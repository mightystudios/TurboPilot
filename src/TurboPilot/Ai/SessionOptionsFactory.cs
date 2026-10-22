using System.IO;
using TurboPilot.Customizations;
using TurboPilot.Dialogs;

namespace TurboPilot.Ai;

internal static class SessionOptionsFactory
{
	internal static ChatSessionOptions Create(
		string workspace,
		AvailableModel? model,
		AvailableContextTier? contextTier,
		string? reasoningEffort,
		string mode,
		bool useByok,
		string byokEndpoint,
		string byokApiKey,
		bool applyInstructions,
		bool preloadSkills,
		bool linkFiles,
		CustomizationLibrary? customizations = null) =>
		new()
		{
			WorkspaceFolder = workspace,
			Model = model?.Id ?? "",
			ContextTier = contextTier?.Id ?? "",
			ContextWindowTokens = contextTier?.ContextWindowTokens
				?? model?.LargestContextTier?.ContextWindowTokens
				?? model?.ContextWindowTokens
				?? 0,
			ReasoningEffort = reasoningEffort ?? "",
			Mode = mode,
			UseByok = useByok,
			ByokEndpoint = useByok ? byokEndpoint.Trim() : "",
			ByokApiKey = useByok ? byokApiKey.Trim() : "",
			ApplyInstructions = applyInstructions,
			PreloadSkills = preloadSkills,
			LinkFiles = linkFiles,
			Customizations = customizations?.Clone() ?? new CustomizationLibrary(),
		};

	internal static async Task<ChatSessionOptions> ResolveSavedAsync(
		string workspace,
		string mode,
		CustomizationLibrary customizations,
		Settings? settings = null,
		CancellationToken cancellationToken = default)
	{
		settings ??= Settings.Load();
		if (!Directory.Exists(workspace))
			throw new DirectoryNotFoundException($"Workspace '{workspace}' does not exist.");
		if (string.IsNullOrWhiteSpace(settings.SelectedModel))
			throw new InvalidOperationException("No saved model is available. Choose a model in Session Settings first.");

		var useByok = string.Equals(settings.ModelProvider, "Byok", StringComparison.OrdinalIgnoreCase);
		if (!useByok
			&& !string.Equals(settings.ModelProvider, "CopilotCli", StringComparison.OrdinalIgnoreCase))
			throw new InvalidOperationException($"Saved model provider '{settings.ModelProvider}' is not supported.");

		IReadOnlyList<AvailableModel> models;
		if (useByok)
		{
			if (!Uri.TryCreate(settings.ByokEndpoint, UriKind.Absolute, out var endpoint)
				|| endpoint.Scheme is not ("http" or "https"))
				throw new InvalidOperationException("The saved BYOK endpoint must be an HTTP or HTTPS URL.");
			models = await ModelService.QueryOpenAiCompatibleAsync(
				settings.ByokEndpoint, settings.ByokApiKey, cancellationToken);
		}
		else
		{
			models = await ModelService.QueryCopilotCliAsync(workspace, cancellationToken);
		}

		var model = models.FirstOrDefault(item =>
			string.Equals(item.Id, settings.SelectedModel, StringComparison.OrdinalIgnoreCase))
			?? throw new InvalidOperationException(
				$"Saved model '{settings.SelectedModel}' is unavailable from the configured provider.");
		var contextTier = string.IsNullOrWhiteSpace(settings.SelectedContextTier)
			? model.LargestContextTier
			: model.ContextTiers.FirstOrDefault(tier =>
				string.Equals(tier.Id, settings.SelectedContextTier, StringComparison.OrdinalIgnoreCase))
				?? throw new InvalidOperationException(
					$"Saved context tier '{settings.SelectedContextTier}' is unavailable for model '{model.Id}'.");

		var reasoningEffort = "";
		if (model.SupportsReasoningEffort)
		{
			reasoningEffort = string.IsNullOrWhiteSpace(settings.SelectedEffort)
				? model.DefaultReasoningEffort ?? ""
				: model.ReasoningEfforts.FirstOrDefault(effort =>
					string.Equals(effort, settings.SelectedEffort, StringComparison.OrdinalIgnoreCase))
					?? throw new InvalidOperationException(
						$"Saved reasoning effort '{settings.SelectedEffort}' is unavailable for model '{model.Id}'.");
		}

		return Create(
			workspace,
			model,
			contextTier,
			reasoningEffort,
			mode,
			useByok,
			settings.ByokEndpoint,
			settings.ByokApiKey,
			settings.ApplyInstructions,
			settings.PreloadSkills,
			settings.LinkFiles,
			customizations);
	}
}
