using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TurboPilot.Storage;

namespace TurboPilot.Orchestration;

internal sealed record ExternalAssistantMessage(
	string MessageId,
	string SessionId,
	string Content,
	string ContentSha256);

internal sealed class ExternalAssistantMessageException : Exception
{
	internal ExternalAssistantMessageException(string message, Exception? innerException = null)
		: base(message, innerException)
	{
	}
}

internal sealed class ExternalAssistantMessageMonitor : IDisposable
{
	internal const string CurrentContractVersion = "1.0";
	internal const string MessageKind = "assistant_message";
	internal const string HostFileName = "host.json";

	private const string RequestSuffix = ".request.json";
	private const string AcknowledgmentSuffix = ".ack.json";
	private const long MaximumRequestBytes = 64 * 1024;
	private const int MaximumIdentifierLength = 200;
	private const int MaximumContentLength = 16 * 1024;
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};

	private readonly string _inboxPath;
	private readonly string _sessionId;
	private readonly string _hostId = Guid.NewGuid().ToString("N");
	private readonly Func<ExternalAssistantMessage, Task> _deliver;
	private readonly CancellationTokenSource _lifetime = new();
	private int _started;
	private int _disposed;

	internal ExternalAssistantMessageMonitor(
		string inboxPath,
		string sessionId,
		Func<ExternalAssistantMessage, Task> deliver)
	{
		_inboxPath = Path.GetFullPath(inboxPath);
		_sessionId = RequiredIdentifier(sessionId, "sessionId");
		_deliver = deliver ?? throw new ArgumentNullException(nameof(deliver));
	}

	internal string HostId => _hostId;
	internal string HostPath => Path.Combine(_inboxPath, HostFileName);

	internal void Start()
	{
		ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
		if (Interlocked.Exchange(ref _started, 1) != 0)
			throw new InvalidOperationException("The external assistant message monitor is already running.");

		Directory.CreateDirectory(_inboxPath);
		WriteJson(HostPath, new ExternalAssistantMessageHost
		{
			ContractVersion = CurrentContractVersion,
			SessionId = _sessionId,
			ProcessId = Environment.ProcessId,
			HostId = _hostId,
			StartedAtUtc = DateTimeOffset.UtcNow,
		});
		_ = RunAsync(_lifetime.Token);
	}

	private async Task RunAsync(CancellationToken cancellationToken)
	{
		try
		{
			await InspectAsync(cancellationToken).ConfigureAwait(false);
			using var timer = new PeriodicTimer(PollInterval);
			while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
				await InspectAsync(cancellationToken).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
		{
		}
		finally
		{
			RemoveOwnedHostMarker();
			_lifetime.Dispose();
		}
	}

	private async Task InspectAsync(CancellationToken cancellationToken)
	{
		IReadOnlyList<string> requests;
		try
		{
			requests = Directory.EnumerateFiles(_inboxPath, "*" + RequestSuffix)
				.Order(StringComparer.Ordinal)
				.ToList();
		}
		catch (IOException)
		{
			return;
		}
		catch (UnauthorizedAccessException)
		{
			return;
		}

		foreach (var requestPath in requests)
		{
			cancellationToken.ThrowIfCancellationRequested();
			await ProcessRequestAsync(requestPath, cancellationToken).ConfigureAwait(false);
		}
	}

	private async Task ProcessRequestAsync(string requestPath, CancellationToken cancellationToken)
	{
		var messageId = Path.GetFileName(requestPath)[..^RequestSuffix.Length];
		var acknowledgmentPath = Path.Combine(_inboxPath, messageId + AcknowledgmentSuffix);
		if (File.Exists(acknowledgmentPath))
			return;

		string contentSha256 = "";
		try
		{
			var file = new FileInfo(requestPath);
			if (!file.Exists)
				return;
			if (file.Length > MaximumRequestBytes)
				throw new ExternalAssistantMessageException(
					$"The external assistant message exceeds {MaximumRequestBytes} bytes.");

			var json = await File.ReadAllTextAsync(requestPath, cancellationToken).ConfigureAwait(false);
			var request = Parse(json, messageId);
			contentSha256 = request.ContentSha256;
			await _deliver(new ExternalAssistantMessage(
				request.MessageId,
				request.SessionId,
				request.Content,
				request.ContentSha256)).ConfigureAwait(false);
			WriteAcknowledgment(
				acknowledgmentPath,
				messageId,
				contentSha256,
				delivered: true,
				error: null);
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
		{
			throw;
		}
		catch (Exception ex) when (ex is IOException
			or UnauthorizedAccessException
			or JsonException
			or ExternalAssistantMessageException)
		{
			WriteAcknowledgment(
				acknowledgmentPath,
				messageId,
				contentSha256,
				delivered: false,
				error: ex.Message);
		}
		catch (Exception ex)
		{
			WriteAcknowledgment(
				acknowledgmentPath,
				messageId,
				contentSha256,
				delivered: false,
				error: "TurboPilot could not deliver the assistant message: " + ex.Message);
		}
	}

	private ExternalAssistantMessageRequest Parse(string json, string fileMessageId)
	{
		var request = JsonSerializer.Deserialize<ExternalAssistantMessageRequest>(json, JsonOptions)
			?? throw new ExternalAssistantMessageException("The external assistant message is empty.");
		if (!string.Equals(request.ContractVersion, CurrentContractVersion, StringComparison.Ordinal))
			throw new ExternalAssistantMessageException(
				$"Unsupported external assistant message contract version '{request.ContractVersion}'.");
		if (!string.Equals(request.Kind, MessageKind, StringComparison.Ordinal))
			throw new ExternalAssistantMessageException(
				$"Unsupported external assistant message kind '{request.Kind}'.");

		var messageId = RequiredIdentifier(request.MessageId, "messageId");
		if (!string.Equals(messageId, fileMessageId, StringComparison.Ordinal))
			throw new ExternalAssistantMessageException(
				"The external assistant message ID does not match its file name.");
		if (!string.Equals(request.SessionId, _sessionId, StringComparison.Ordinal))
			throw new ExternalAssistantMessageException(
				"The external assistant message targets another session.");
		if (!string.Equals(request.HostId, _hostId, StringComparison.Ordinal))
			throw new ExternalAssistantMessageException(
				"The external assistant message targets a stale TurboPilot host.");

		var content = request.Content?.Trim();
		if (string.IsNullOrEmpty(content))
			throw new ExternalAssistantMessageException(
				"The external assistant message content is required.");
		if (content.Length > MaximumContentLength)
			throw new ExternalAssistantMessageException(
				$"The external assistant message content must not exceed {MaximumContentLength} characters.");
		var contentSha256 = Sha256(content);
		if (!string.Equals(request.ContentSha256, contentSha256, StringComparison.Ordinal))
			throw new ExternalAssistantMessageException(
				"The external assistant message content digest is invalid.");

		return request with
		{
			MessageId = messageId,
			SessionId = _sessionId,
			HostId = _hostId,
			Content = content,
			ContentSha256 = contentSha256,
		};
	}

	private void WriteAcknowledgment(
		string path,
		string messageId,
		string contentSha256,
		bool delivered,
		string? error)
	{
		WriteJson(path, new ExternalAssistantMessageAcknowledgment
		{
			ContractVersion = CurrentContractVersion,
			MessageId = messageId,
			SessionId = _sessionId,
			HostId = _hostId,
			ContentSha256 = contentSha256,
			Delivered = delivered,
			PresentedToUser = delivered,
			Error = delivered ? null : error,
			AcknowledgedAtUtc = DateTimeOffset.UtcNow,
		});
	}

	private static string RequiredIdentifier(string? value, string field)
	{
		if (string.IsNullOrWhiteSpace(value))
			throw new ExternalAssistantMessageException(
				$"The external assistant message field '{field}' is required.");
		var identifier = value.Trim();
		if (identifier.Length > MaximumIdentifierLength
			|| identifier is "." or ".."
			|| !identifier.All(character =>
				char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
		{
			throw new ExternalAssistantMessageException(
				$"The external assistant message field '{field}' is invalid.");
		}
		return identifier;
	}

	internal static string Sha256(string content) =>
		Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant();

	private static void WriteJson<T>(string path, T value) =>
		AtomicFile.Write(path, stream => JsonSerializer.Serialize(stream, value, JsonOptions));

	public void Dispose()
	{
		if (Interlocked.Exchange(ref _disposed, 1) != 0)
			return;
		_lifetime.Cancel();
		RemoveOwnedHostMarker();
		if (Volatile.Read(ref _started) == 0)
			_lifetime.Dispose();
	}

	private void RemoveOwnedHostMarker()
	{
		try
		{
			if (File.Exists(HostPath))
			{
				var host = JsonSerializer.Deserialize<ExternalAssistantMessageHost>(
					File.ReadAllText(HostPath),
					JsonOptions);
				if (host?.HostId == _hostId)
					File.Delete(HostPath);
			}
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
		}
	}

	private sealed record ExternalAssistantMessageRequest
	{
		public string ContractVersion { get; init; } = "";
		public string Kind { get; init; } = "";
		public string MessageId { get; init; } = "";
		public string SessionId { get; init; } = "";
		public string HostId { get; init; } = "";
		public string Content { get; init; } = "";
		public string ContentSha256 { get; init; } = "";
	}

	private sealed record ExternalAssistantMessageHost
	{
		public string ContractVersion { get; init; } = "";
		public string SessionId { get; init; } = "";
		public int ProcessId { get; init; }
		public string HostId { get; init; } = "";
		public DateTimeOffset StartedAtUtc { get; init; }
	}

	private sealed record ExternalAssistantMessageAcknowledgment
	{
		public string ContractVersion { get; init; } = "";
		public string MessageId { get; init; } = "";
		public string SessionId { get; init; } = "";
		public string HostId { get; init; } = "";
		public string ContentSha256 { get; init; } = "";
		public bool Delivered { get; init; }
		public bool PresentedToUser { get; init; }
		public string? Error { get; init; }
		public DateTimeOffset AcknowledgedAtUtc { get; init; }
	}
}
