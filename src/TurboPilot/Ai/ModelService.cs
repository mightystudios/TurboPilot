using System.Net.Http;
using System.Text.Json;
using GitHub.Copilot;

namespace TurboPilot.Ai;

public sealed record AvailableContextTier
{
	public const string DefaultId = "default";
	public const string LongContextId = "long_context";

	public required string Id { get; init; }
	public required string Name { get; init; }
	public int ContextWindowTokens { get; init; }

	public string DisplayLabel => ContextWindowTokens > 0
		? $"{Name} - {AvailableModel.FormatTokensShort(ContextWindowTokens)}"
		: Name;
}

/// <summary>
/// One model advertised by a hosting service, with the option data the
/// Settings dialog needs: its context window and, where supported, the
/// reasoning effort levels it accepts.
/// </summary>
public sealed class AvailableModel
{
	public required string Id { get; init; }

	public string Name { get; init; } = "";

	/// <summary>Prompt/context window in tokens; 0 when the service reports none.</summary>
	public int ContextWindowTokens { get; init; }

	public IReadOnlyList<AvailableContextTier> ContextTiers { get; init; } = Array.Empty<AvailableContextTier>();

	public bool SupportsReasoningEffort { get; init; }

	public IReadOnlyList<string> ReasoningEfforts { get; init; } = Array.Empty<string>();

	public string? DefaultReasoningEffort { get; init; }

	public AvailableContextTier? LargestContextTier => ContextTiers
		.OrderByDescending(tier => tier.ContextWindowTokens)
		.ThenByDescending(tier => tier.Id == AvailableContextTier.LongContextId)
		.FirstOrDefault();

	/// <summary>Combo label containing only model identity; context is selected separately.</summary>
	public string DisplayLabel
	{
		get
		{
			return string.IsNullOrWhiteSpace(Name) || Name == Id ? Id : $"{Name} ({Id})";
		}
	}

	/// <summary>Renders a token count compactly: 128000 becomes "128K", 1048576 becomes "1M".</summary>
	public static string FormatTokensShort(double tokens)
	{
		if (tokens >= 1_000_000)
		{
			var m = tokens / 1_000_000;
			return (m == Math.Floor(m) ? m.ToString("0") : m.ToString("0.0")) + "M";
		}
		if (tokens >= 1000)
		{
			var k = tokens / 1000;
			return (k == Math.Floor(k) ? k.ToString("0") : k.ToString("0.0")) + "K";
		}
		return tokens.ToString("0");
	}
}

/// <summary>
/// Contacts a model hosting service and enumerates the models it offers.
/// Two service kinds are supported:
///
///  - Copilot CLI: the bundled GitHub Copilot runtime is started and asked
///    for its model catalog through the SDK's list-models call. The client
///    is spun up per query and disposed immediately; no session is created.
///  - BYOK (OpenAI-compatible): the endpoint's GET /models route is queried
///    directly over HTTP, so no CLI or GitHub authentication is involved.
///
/// Failures surface as exceptions for the caller to present in the
/// dialog's status line.
/// </summary>
public static class ModelService
{
	private static readonly HttpClient Http = new()
	{
		// Local servers may still be loading a model. The dialog owns
		// cancellation and reports elapsed progress instead of turning an
		// arbitrary HTTP deadline into a false failure.
		Timeout = Timeout.InfiniteTimeSpan,
	};

	/// <summary>
	/// Starts a throwaway Copilot CLI client and returns its model catalog,
	/// sorted by id. The client is stopped and disposed before returning.
	/// </summary>
	/// <param name="workspaceFolder">
	/// Working directory handed to the CLI so any workspace-scoped model
	/// policy applies. Null when no workspace is selected yet.
	/// </param>
	public static async Task<IReadOnlyList<AvailableModel>> QueryCopilotCliAsync(
		string? workspaceFolder, CancellationToken cancellationToken = default)
	{
		await using var client = new CopilotClient(new CopilotClientOptions
		{
			WorkingDirectory = string.IsNullOrWhiteSpace(workspaceFolder)
				? null
				: workspaceFolder,
		});

		await client.StartAsync(cancellationToken);
		try
		{
			var models = await client.ListModelsAsync(cancellationToken);
			var result = new List<AvailableModel>();

			foreach (var m in models)
			{
				if (string.IsNullOrEmpty(m.Id)) continue;

				var contextTiers = ContextTiersFor(m);

				result.Add(new AvailableModel
				{
					Id = m.Id,
					Name = string.IsNullOrEmpty(m.Name) ? m.Id : m.Name,
					ContextWindowTokens = contextTiers.Max(tier => tier.ContextWindowTokens),
					ContextTiers = contextTiers,
					SupportsReasoningEffort = m.Capabilities?.Supports?.ReasoningEffort == true,
					ReasoningEfforts = m.SupportedReasoningEfforts is { Count: > 0 } efforts
						? efforts.ToList()
						: Array.Empty<string>(),
					DefaultReasoningEffort = m.DefaultReasoningEffort,
				});
			}

			result.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
			return result;
		}
		finally
		{
			await client.StopAsync();
		}
	}

	/// <summary>
	/// Queries an OpenAI-compatible service's GET {baseUrl}/models route and
	/// parses the advertised models. The Authorization header is sent only
	/// when an API key is supplied; local servers usually need none.
	/// </summary>
	public static async Task<IReadOnlyList<AvailableModel>> QueryOpenAiCompatibleAsync(
		string baseUrl, string? apiKey, CancellationToken cancellationToken = default)
	{
		var url = baseUrl.TrimEnd('/') + "/models";
		using var request = new HttpRequestMessage(HttpMethod.Get, url);
		if (!string.IsNullOrWhiteSpace(apiKey))
			request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());

		using var response = await Http.SendAsync(request, cancellationToken);
		response.EnsureSuccessStatusCode();
		var json = await response.Content.ReadAsStringAsync(cancellationToken);

		var models = ParseOpenAiModels(json);
		models.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
		return models;
	}

	/// <summary>
	/// Parses an OpenAI /models response body into id plus optional context
	/// window. Tolerates the field names used by the common local servers:
	/// Lemonade reports max_context_window, vLLM max_model_len, and
	/// llama.cpp nests the training context under meta.n_ctx_train.
	/// Reasoning effort is not advertised by these endpoints, so models
	/// from this path never claim to support it.
	/// </summary>
	private static List<AvailableModel> ParseOpenAiModels(string json)
	{
		var result = new List<AvailableModel>();
		try
		{
			using var doc = JsonDocument.Parse(json);
			var root = doc.RootElement;
			var data = root.ValueKind == JsonValueKind.Array
				? root
				: root.TryGetProperty("data", out var d) ? d : default;
			if (data.ValueKind != JsonValueKind.Array) return result;

			foreach (var item in data.EnumerateArray())
			{
				if (item.ValueKind != JsonValueKind.Object) continue;

				var id = item.TryGetProperty("id", out var idEl) ? idEl.GetString()
					: item.TryGetProperty("name", out var nameEl) ? nameEl.GetString()
					: null;
				if (string.IsNullOrWhiteSpace(id)) continue;

				var ctx = ReadContextField(item);

				// llama.cpp nests the model's training context under "meta".
				if (ctx <= 0
					&& item.TryGetProperty("meta", out var metaEl)
					&& metaEl.ValueKind == JsonValueKind.Object)
				{
					ctx = ReadContextField(metaEl);
				}

				result.Add(new AvailableModel
				{
					Id = id,
					Name = id,
					ContextWindowTokens = ctx,
					ContextTiers = BuildContextTiers(ctx),
				});
			}
		}
		catch (JsonException)
		{
			// Unparseable body: return whatever was collected so far.
		}
		return result;
	}

	internal static IReadOnlyList<AvailableContextTier> ContextTiersFor(ModelInfo model)
	{
		var limits = model.Capabilities?.Limits;
		var advertisedContext = limits is null
			? 0
			: limits.MaxPromptTokens ?? (limits.MaxContextWindowTokens > 0 ? limits.MaxContextWindowTokens : 0);
#pragma warning disable GHCP001
		return BuildContextTiers(
			advertisedContext,
			model.Billing?.TokenPrices?.MaxPromptTokens,
			model.Billing?.TokenPrices?.LongContext?.MaxPromptTokens);
#pragma warning restore GHCP001
	}

	internal static IReadOnlyList<AvailableContextTier> BuildContextTiers(
		int advertisedContextWindowTokens,
		long? defaultPromptTokens = null,
		long? longContextPromptTokens = null)
	{
		var defaultTokens = TokenCount(defaultPromptTokens);
		var longTokens = TokenCount(longContextPromptTokens);
		if (longTokens > 0 && longTokens != defaultTokens)
		{
			var tiers = new List<AvailableContextTier>(2);
			if (defaultTokens > 0)
			{
				tiers.Add(new AvailableContextTier
				{
					Id = AvailableContextTier.DefaultId,
					Name = "Default",
					ContextWindowTokens = defaultTokens,
				});
			}
			tiers.Add(new AvailableContextTier
			{
				Id = AvailableContextTier.LongContextId,
				Name = "Long",
				ContextWindowTokens = longTokens,
			});
			return tiers;
		}

		var fixedTokens = Math.Max(advertisedContextWindowTokens, Math.Max(defaultTokens, longTokens));
		return
		[
			new AvailableContextTier
			{
				Id = "",
				Name = "Default",
				ContextWindowTokens = fixedTokens,
			},
		];
	}

	private static int TokenCount(long? value) =>
		value is > 0 ? (int)Math.Min(value.Value, int.MaxValue) : 0;

	// Context-window field names seen across Lemonade, llama.cpp, vLLM and
	// Ollama-compatible endpoints, most specific first.
	private static readonly string[] ContextFieldNames =
	[
		"max_context_window", "max_context_length", "context_length",
		"max_model_len", "n_ctx", "n_ctx_train", "ctx", "ctx_size",
		"resolved_ctx_size", "max_prompt_tokens",
	];

	/// <summary>
	/// Returns the first recognized context-window value on the object as a
	/// whole number of tokens, or 0 when it carries none. Accepts both JSON
	/// numbers and numeric strings.
	/// </summary>
	private static int ReadContextField(JsonElement element)
	{
		foreach (var name in ContextFieldNames)
		{
			if (!element.TryGetProperty(name, out var value)) continue;

			if (value.ValueKind == JsonValueKind.Number)
			{
				if (value.TryGetInt32(out var n) && n > 0) return n;
				var d = value.GetDouble();
				if (d > 0) return (int)d;
			}
			else if (value.ValueKind == JsonValueKind.String
				&& int.TryParse(value.GetString(), out var s) && s > 0)
			{
				return s;
			}
		}
		return 0;
	}
}
