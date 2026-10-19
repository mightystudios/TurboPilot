using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TurboPilot.Ai;

namespace TurboPilot.Tests;

internal sealed class LocalProvider : IAsyncDisposable
{
	private readonly HttpListener _listener = new();
	private readonly CancellationTokenSource _stopping = new();
	private readonly ConcurrentBag<Task> _requests = [];
	private readonly Task _accepting;

	public string Endpoint { get; }
	public ConcurrentQueue<JsonElement> Requests { get; } = new();
	public ConcurrentQueue<Reply> Replies { get; } = new();
	public ConcurrentQueue<string> Errors { get; } = new();
	public ConcurrentQueue<(string Method, string? Authorization)> McpRequests { get; } = new();
	public int DisabledServerRequests;
	public int WarmupRequests;

	public LocalProvider()
	{
		using var socket = new TcpListener(IPAddress.Loopback, 0);
		socket.Start();
		var port = ((IPEndPoint)socket.LocalEndpoint).Port;
		socket.Stop();
		Endpoint = $"http://127.0.0.1:{port}/v1";
		_listener.Prefixes.Add($"http://127.0.0.1:{port}/");
		_listener.Start();
		_accepting = AcceptAsync();
	}

	private async Task AcceptAsync()
	{
		while (!_stopping.IsCancellationRequested)
		{
			try
			{
				var context = await _listener.GetContextAsync().WaitAsync(_stopping.Token);
				_requests.Add(RespondAsync(context));
			}
			catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { break; }
			catch (HttpListenerException) when (_stopping.IsCancellationRequested) { break; }
		}
	}

	private async Task RespondAsync(HttpListenerContext context)
	{
		Reply? reply = null;
		try
		{
			if (context.Request.Url!.AbsolutePath.EndsWith("/disabled-mcp"))
			{
				Interlocked.Increment(ref DisabledServerRequests);
				context.Response.StatusCode = 500;
				return;
			}
			if (context.Request.Url.AbsolutePath.EndsWith("/mcp"))
			{
				await RespondMcpAsync(context);
				return;
			}
			if (context.Request.Url!.AbsolutePath.EndsWith("/models"))
			{
				context.Response.ContentType = "application/json";
				await WriteAsync(context.Response, """{"data":[{"id":"test-model","max_context_window":32768},{"id":"test-model-two","max_context_window":65536}]}""");
				return;
			}
			if (context.Request.Headers["X-TurboPilot-Keep-Alive"] == "1")
			{
				using var warmupReader = new StreamReader(context.Request.InputStream);
				await warmupReader.ReadToEndAsync();
				Interlocked.Increment(ref WarmupRequests);
				context.Response.ContentType = "application/json";
				await WriteAsync(context.Response, JsonSerializer.Serialize(new
				{
					id = "warmup", @object = "chat.completion",
					created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model = "test-model",
					choices = new[] { new { index = 0, message = new { role = "assistant", content = "" }, finish_reason = "stop" } },
					usage = new { prompt_tokens = 1, completion_tokens = 1, total_tokens = 2 },
				}));
				return;
			}
			using var reader = new StreamReader(context.Request.InputStream);
			using var document = JsonDocument.Parse(await reader.ReadToEndAsync());
			var request = document.RootElement.Clone();
			Requests.Enqueue(request);
			if (!Replies.TryDequeue(out reply))
			{
				// A session end asks the model for one sentence for the
				// archive. Answering that request from its own text spares
				// every check that merely ends a session from scripting a
				// summary it never reads, while any other unscripted
				// streamed turn is still a failure.
				var streamed = request.TryGetProperty("stream", out var streaming) && streaming.ValueKind == JsonValueKind.True;
				if (streamed && !MentionsArchivePrompt(request))
					throw new InvalidOperationException("The provider received an unscripted request.");
				reply = new Reply(streamed ? "The session ran and ended without a scripted summary." : "");
			}
			reply.Started.TrySetResult();
			var id = "fixture-" + Guid.NewGuid().ToString("N");
			// Some runtime requests, such as hand-off summaries, ask for one complete JSON response.
			if (!request.TryGetProperty("stream", out var stream) || stream.ValueKind != JsonValueKind.True)
			{
				if (reply.Hold)
					await reply.Release.Task.WaitAsync(_stopping.Token);
				context.Response.ContentType = "application/json";
				await WriteAsync(context.Response, JsonSerializer.Serialize(new
				{
					id, @object = "chat.completion", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model = "test-model",
					choices = new[] { new { index = 0, message = new { role = "assistant", content = reply.Text }, finish_reason = "stop" } },
					usage = new { prompt_tokens = 2048, completion_tokens = 10, total_tokens = 2058 },
				}));
				return;
			}
			context.Response.ContentType = "text/event-stream";
			context.Response.SendChunked = true;

			async Task Chunk(object delta, string? finishReason = null) => await WriteAsync(context.Response,
				"data: " + JsonSerializer.Serialize(new
				{
					id, @object = "chat.completion.chunk", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model = "test-model",
					choices = new[] { new { index = 0, delta, finish_reason = finishReason } },
				}) + "\n\n");

			if (reply.ToolName is not null)
			{
				var call = new[] { new { index = 0, id = "call-" + Guid.NewGuid().ToString("N"), type = "function", function = new { name = reply.ToolName, arguments = reply.ToolArguments } } };
				// Text before a tool call is interim narration within the same assistant message.
				if (reply.Text.Length > 0)
				{
					await Chunk(new { role = "assistant", content = reply.Text });
					await Chunk(new { tool_calls = call });
				}
				else
					await Chunk(new { role = "assistant", tool_calls = call });
				await Chunk(new { }, "tool_calls");
			}
			else
			{
				var split = Math.Max(1, reply.Text.Length / 2);
				await Chunk(new { role = "assistant", content = reply.Text[..split] });
				if (reply.Hold)
				{
					while (!reply.Release.Task.IsCompleted)
					{
						await Task.Delay(100, _stopping.Token);
						await WriteAsync(context.Response, ": heartbeat\n\n");
					}
				}
				await Task.Delay(30, _stopping.Token);
				await Chunk(new { content = reply.Text[split..] });
				await Chunk(new { }, "stop");
			}
			await WriteAsync(context.Response, "data: " + JsonSerializer.Serialize(new
			{
				id, @object = "chat.completion.chunk", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model = "test-model",
				choices = Array.Empty<object>(), usage = new { prompt_tokens = 2048, completion_tokens = 10, total_tokens = 2058 },
			}) + "\n\ndata: [DONE]\n\n");
		}
		catch (OperationCanceledException) when (_stopping.IsCancellationRequested) { }
		catch (Exception ex) when (reply?.Hold == true && ex is HttpListenerException or IOException or ObjectDisposedException)
		{
			reply.Disconnected.TrySetResult();
		}
		catch (Exception ex)
		{
			Errors.Enqueue(ex.Message);
			context.Response.StatusCode = 500;
		}
		finally
		{
			// The provider or an abandoned session may close the connection
			// before this response cleanup reaches the listener.
			try { context.Response.Close(); }
			catch (Exception ex) when (ex is ObjectDisposedException or HttpListenerException or IOException) { }
		}
	}

	private async Task RespondMcpAsync(HttpListenerContext context)
	{
		if (context.Request.HttpMethod != "POST")
		{
			context.Response.StatusCode = context.Request.HttpMethod == "DELETE" ? 200 : 405;
			return;
		}
		using var document = await JsonDocument.ParseAsync(context.Request.InputStream);
		var request = document.RootElement;
		var method = request.GetProperty("method").GetString()!;
		McpRequests.Enqueue((method, context.Request.Headers["Authorization"]));
		if (!request.TryGetProperty("id", out var id))
		{
			context.Response.StatusCode = 202;
			return;
		}
		object result = method switch
		{
			"initialize" => new
			{
				protocolVersion = request.GetProperty("params").GetProperty("protocolVersion").GetString(),
				capabilities = new { tools = new { } },
				serverInfo = new { name = "fixture", version = "1.0.0" },
			},
			"tools/list" => new
			{
				tools = new[]
				{
					new { name = "fixture_echo", description = "Echo a fixture value",
						inputSchema = new { type = "object", properties = new { text = new { type = "string" } } } },
				},
			},
			"ping" => new { },
			"server/discover" => new { servers = Array.Empty<object>() },
			_ => throw new InvalidOperationException("Unexpected fixture server method: " + method),
		};
		context.Response.ContentType = "application/json";
		context.Response.SendChunked = true;
		context.Response.Headers["Mcp-Session-Id"] = "fixture";
		await WriteAsync(context.Response, JsonSerializer.Serialize(new { jsonrpc = "2.0", id, result }));
	}

	/// <summary>
	/// Whether a request carries the sentence the archive asks for at the
	/// end of a session. Matched on a distinctive phrase of the prompt
	/// rather than the whole of it, because the runtime wraps what it
	/// sends in instructions of its own.
	/// </summary>
	private static bool MentionsArchivePrompt(JsonElement request) =>
		request.GetRawText().Contains(ChatService.ArchiveMarker, StringComparison.OrdinalIgnoreCase);

	private static async Task WriteAsync(HttpListenerResponse response, string text)
	{
		await response.OutputStream.WriteAsync(Encoding.UTF8.GetBytes(text));
		await response.OutputStream.FlushAsync();
	}

	public async ValueTask DisposeAsync()
	{
		_stopping.Cancel();
		_listener.Close();
		await _accepting;
		await Task.WhenAll(_requests);
		_stopping.Dispose();
	}

	internal sealed record Reply(string Text, bool Hold = false, string? ToolName = null, string? ToolArguments = null)
	{
		public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
		public TaskCompletionSource Disconnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
	}
}
