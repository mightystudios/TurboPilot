using System.IO;
using GitHub.Copilot;
using TurboPilot.Commands;
using TurboPilot.Customizations;
using YamlDotNet.RepresentationModel;

namespace TurboPilot.Ai;

internal static class SessionConfiguration
{
	public static void Apply(SessionConfigBase config, ChatSessionOptions options, string? applicationInstructionsPath = null)
	{
		config.Model = string.IsNullOrWhiteSpace(options.Model) ? null : options.Model;
		config.ContextTier = options.UseByok ? null : ToSdkContextTier(options.ContextTier);
		config.ReasoningEffort = string.IsNullOrWhiteSpace(options.ReasoningEffort) ? null : options.ReasoningEffort;
		config.WorkingDirectory = options.WorkspaceFolder;
		config.Streaming = true;
		config.IncludeSubAgentStreamingEvents = false;

		// Discovery must not reload items the user explicitly disabled.
		config.EnableConfigDiscovery = false;
		config.SkipCustomInstructions = true;
		config.EnableOnDemandInstructionDiscovery = false;
		config.EnableFileHooks = false;
		config.CustomAgentsLocalOnly = true;
		config.InstructionDirectories = [];
		config.PluginDirectories = [];

		var parts = new List<string>();

		if (options.ApplyInstructions)
		{
			foreach (var item in options.Customizations.Instructions.Values.Where(item => item.Enabled))
			{
				var document = FrontMatter.ReadDocument(item.FilePath);
				var applyTo = Scalar(document.Header, "applyTo");
				var scope = string.IsNullOrWhiteSpace(applyTo)
					? ""
					: $"\r\nApply these instructions only to files matching: {applyTo}";
				parts.Add($"## Instructions: {item.Name}\r\nSource: {item.FilePath}{scope}\r\n\r\n{document.Body}");
			}
		}

		config.EnableSkills = options.PreloadSkills;
		config.SkillDirectories = [];
		config.DisabledSkills = [];
		var skillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		if (options.PreloadSkills)
		{
			foreach (var item in options.Customizations.Skills.Values.Where(item => item.Enabled))
			{
				var document = FrontMatter.ReadDocument(item.FilePath);
				var directory = Path.GetDirectoryName(Path.GetFullPath(item.FilePath))!;
				config.SkillDirectories.Add(directory);
				skillNames.Add(Scalar(document.Header, "name") ?? Path.GetFileName(directory));
				parts.Add($"## Skill: {item.Name}\r\nSource: {item.FilePath}\r\n"
					+ $"Resolve this skill's relative resource paths against: {directory}\r\n\r\n{document.Body}");
			}
		}

		config.CustomAgents = options.Customizations.Agents.Values
			.Where(item => item.Enabled)
			.Select(item => ReadAgent(item, skillNames))
			.ToList();
		if (options.Mode is not ("Standard" or "Plan" or "Autopilot"))
		{
			config.Agent = config.CustomAgents.FirstOrDefault(agent =>
				string.Equals(agent.Name, options.Mode, StringComparison.OrdinalIgnoreCase))?.Name
				?? throw new InvalidOperationException($"Agent '{options.Mode}' is unavailable or disabled. Choose another mode in Settings.");
		}

		config.McpServers = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);
		foreach (var item in options.Customizations.McpServers.Values.Where(item => item.Enabled))
		{
			if (string.IsNullOrWhiteSpace(item.Element))
				throw new InvalidDataException($"The server entry '{item.Name}' has no server name.");
			config.McpServers.Add(item.Name, McpConfig.ReadServerConfiguration(item.FilePath, item.Element));
		}
		config.DisabledMcpServers = options.Customizations.McpServers.Values
			.Where(item => !item.Enabled && !config.McpServers.ContainsKey(item.Name))
			.Select(item => item.Name).ToList();

		parts.Add(PromptReferences.SystemInstructions);
		parts.Add(ApplicationInstructions.Read(applicationInstructionsPath));
		config.SystemMessage = new SystemMessageConfig
		{
			Mode = SystemMessageMode.Append,
			Content = string.Join("\r\n\r\n", parts),
		};

		if (options.UseByok)
		{
			if (string.IsNullOrWhiteSpace(options.Model))
				throw new InvalidOperationException("Select a model for the configured provider.");
			if (!Uri.TryCreate(options.ByokEndpoint, UriKind.Absolute, out var endpoint)
				|| endpoint.Scheme is not ("http" or "https"))
				throw new InvalidOperationException("The provider endpoint must be an HTTP or HTTPS URL.");

			config.Provider = new ProviderConfig
			{
				Type = "openai",
				WireApi = "completions",
				Transport = "http",
				BaseUrl = options.ByokEndpoint.Trim(),
				ApiKey = string.IsNullOrWhiteSpace(options.ByokApiKey) ? "local" : options.ByokApiKey.Trim(),
				ModelId = options.Model,
				MaxPromptTokens = options.ContextWindowTokens > 0 ? options.ContextWindowTokens : null,
			};
		}
	}

	internal static ContextTier? ToSdkContextTier(string? contextTier) => contextTier switch
	{
		null or "" => null,
		AvailableContextTier.DefaultId => ContextTier.Default,
		AvailableContextTier.LongContextId => ContextTier.LongContext,
		_ => throw new InvalidOperationException($"Context tier '{contextTier}' is unavailable."),
	};

	private static CustomAgentConfig ReadAgent(CustomizationItem item, HashSet<string> enabledSkills)
	{
		var document = FrontMatter.ReadDocument(item.FilePath);
		var tools = List(document.Header, "tools");
		if (tools is { Count: 1 } && tools[0] == "*")
			tools = null;
		var inferText = Scalar(document.Header, "infer");
		bool? infer = inferText is null ? null : bool.TryParse(inferText, out var value)
			? value : throw new InvalidDataException($"Invalid infer flag in '{item.FilePath}'.");

		return new CustomAgentConfig
		{
			Name = item.Name,
			DisplayName = Scalar(document.Header, "name") ?? item.Name,
			Description = Scalar(document.Header, "description"),
			Prompt = document.Body,
			Tools = tools,
			Infer = infer,
			Model = Scalar(document.Header, "model"),
			ReasoningEffort = Scalar(document.Header, "reasoning-effort")
				?? Scalar(document.Header, "reasoningEffort"),
			Skills = List(document.Header, "skills")?.Where(enabledSkills.Contains).ToList() ?? [],
		};
	}

	private static YamlNode? Field(YamlMappingNode header, string name) =>
		header.Children.FirstOrDefault(pair => pair.Key is YamlScalarNode key
			&& string.Equals(key.Value, name, StringComparison.OrdinalIgnoreCase)).Value;

	private static string? Scalar(YamlMappingNode header, string name) => Field(header, name) switch
	{
		null => null,
		YamlScalarNode scalar => scalar.Value,
		_ => throw new InvalidDataException($"Header field '{name}' must be a scalar value."),
	};

	private static List<string>? List(YamlMappingNode header, string name) => Field(header, name) switch
	{
		null => null,
		YamlScalarNode scalar => (scalar.Value ?? "").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList(),
		YamlSequenceNode sequence => sequence.Children.Select(node => node is YamlScalarNode scalar
			? scalar.Value ?? ""
			: throw new InvalidDataException($"Header field '{name}' must contain strings.")).ToList(),
		_ => throw new InvalidDataException($"Header field '{name}' must be a list."),
	};
}
