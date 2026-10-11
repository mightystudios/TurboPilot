using System.Net;
using System.Net.Http;
using System.Net.Http.Json;

namespace TurboPilot.Ai;

internal enum ProviderWarmupResult
{
	Skipped,
	Warmed,
	Unsupported,
	Failed,
}

/// <summary>
/// Keeps the two local parts of a BYOK session ready: the SDK runtime
/// connection and, when the provider is on this machine, the selected
/// model. The provider request is deliberately outside the conversation,
/// so it cannot add messages or consume the session context.
/// </summary>
internal static class SessionLiveness
{
	internal static readonly TimeSpan ProgressInterval = TimeSpan.FromSeconds(15);
	internal static readonly TimeSpan KeepAliveInterval = TimeSpan.FromMinutes(4);

	private static readonly HttpClient Http = new()
	{
		// A local model may need several minutes to load. The caller owns
		// cancellation; an HTTP deadline would only report that ongoing
		// load as a failure while the server continued doing the work.
		Timeout = Timeout.InfiniteTimeSpan,
	};

	internal static Uri? WarmupUri(ChatSessionOptions options)
	{
		if (!options.UseByok
			|| string.IsNullOrWhiteSpace(options.Model)
			|| !Uri.TryCreate(options.ByokEndpoint, UriKind.Absolute, out var endpoint)
			|| !endpoint.IsLoopback)
			return null;

		var builder = new UriBuilder(endpoint)
		{
			Path = endpoint.AbsolutePath.TrimEnd('/') + "/chat/completions",
			Query = "",
			Fragment = "",
		};
		return builder.Uri;
	}

	internal static async Task<ProviderWarmupResult> WarmProviderAsync(
		ChatSessionOptions options, CancellationToken cancellationToken)
	{
		var uri = WarmupUri(options);
		if (uri is null)
			return ProviderWarmupResult.Skipped;

		using var request = new HttpRequestMessage(HttpMethod.Post, uri)
		{
			Content = JsonContent.Create(new
			{
				model = options.Model,
				messages = new[] { new { role = "user", content = "." } },
				max_tokens = 1,
				stream = false,
			}),
		};
		request.Headers.TryAddWithoutValidation("X-TurboPilot-Keep-Alive", "1");
		if (!string.IsNullOrWhiteSpace(options.ByokApiKey))
			request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + options.ByokApiKey.Trim());

		try
		{
			using var response = await Http.SendAsync(request, cancellationToken).ConfigureAwait(false);
			if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed)
				return ProviderWarmupResult.Unsupported;
			return response.IsSuccessStatusCode
				? ProviderWarmupResult.Warmed
				: ProviderWarmupResult.Failed;
		}
		catch (HttpRequestException)
		{
			return ProviderWarmupResult.Failed;
		}
	}
}
