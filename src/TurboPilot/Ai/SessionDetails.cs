using System.Text;
using TurboPilot.Permissions;

namespace TurboPilot.Ai;

/// <summary>
/// Describes what a running session is actually configured with.
///
/// The settings dialog shows what will be asked for; this shows what was
/// granted. They are usually the same, but a session resumed from a
/// saved record, a mode that failed to resolve, or a customization list
/// edited since the session began can pull them apart, and a user
/// debugging a surprising answer needs to know which one they are
/// looking at.
///
/// It is a listing rather than a dialog because the window is a narrow
/// column: written into the transcript it can be scrolled, searched,
/// copied and saved with everything else, at no cost in width.
/// </summary>
public static class SessionDetails
{
	/// <summary>
	/// The listing body. Lists are named rather than counted: the point
	/// of reading this is to find the one item that should not be there.
	/// </summary>
	public static string Describe(ChatSessionOptions options, string? sessionId,
		string? applicationInstructions, string? modelLabel = null)
	{
		var sb = new StringBuilder();

		Section(sb, "Session");
		Field(sb, "Id", sessionId ?? "none");
		Field(sb, "Workspace", options.WorkspaceFolder ?? "none");
		Field(sb, "Model", modelLabel is { Length: > 0 } ? modelLabel : Or(options.Model, "default"));
		Field(sb, "Context tier", options.ContextTier switch
		{
			AvailableContextTier.DefaultId => "default",
			AvailableContextTier.LongContextId => "long",
			_ => "provider default",
		});
		Field(sb, "Context", options.ContextWindowTokens > 0
			? options.ContextWindowTokens.ToString("N0") + " tokens" : "not reported");
		Field(sb, "Reasoning", Or(options.ReasoningEffort, "default"));
		Field(sb, "Mode", options.Mode);
		if (options.UseByok)
		{
			Field(sb, "Provider", Or(options.ByokEndpoint, "not set"));
		}
		Field(sb, "File links", options.LinkFiles ? "on" : "off");

		Section(sb, "Instructions");
		if (!options.ApplyInstructions)
			sb.Append("  Apply Instructions is off; no customization instructions are loaded.\r\n");
		else
			Items(sb, options.Customizations.Instructions.Values
				.Where(item => item.Enabled)
				.Select(item => $"{item.Name}  ({item.FilePath})"),
				"None are enabled.");
		Field(sb, "Presentation", applicationInstructions ?? "not loaded");

		Section(sb, "Skills");
		if (!options.PreloadSkills)
			sb.Append("  Preload Skills is off; no skills are loaded.\r\n");
		else
			Items(sb, options.Customizations.Skills.Values
				.Where(item => item.Enabled)
				.Select(item => $"{item.Name}  ({item.FilePath})"),
				"None are enabled.");

		Section(sb, "Agents");
		Items(sb, options.Customizations.Agents.Values
			.Where(item => item.Enabled)
			.Select(item => item.Name + (string.Equals(item.Name, options.Mode, StringComparison.OrdinalIgnoreCase)
				? "  (in use)" : "")),
			"None are enabled.");

		Section(sb, "MCP servers");
		Items(sb, options.Customizations.McpServers.Values
			.Where(item => item.Enabled)
			.Select(item => $"{item.Name}  ({item.FilePath})"),
			"None are enabled.");

		Section(sb, "Permissions");
		if (options.Mode == "Autopilot")
			sb.Append("  Autopilot approves every request without asking.\r\n");
		var operations = PermissionService.OperationsFor(options.WorkspaceFolder);
		Field(sb, "Pre-approved", operations.Count == 0 ? "nothing; every operation asks" : string.Join(", ", operations));
		Items(sb, PermissionService.EffectiveEntries(options.WorkspaceFolder)
			.Select(entry => $"{entry.Access}  {entry.FolderPath}"),
			"No folder grants; only the workspace is reachable.");

		return sb.ToString().TrimEnd();
	}

	private static void Section(StringBuilder sb, string name)
	{
		if (sb.Length > 0) sb.Append("\r\n");
		sb.Append(name).Append("\r\n");
	}

	private static void Field(StringBuilder sb, string name, string value) =>
		sb.Append("  ").Append(name.PadRight(13)).Append(value).Append("\r\n");

	private static void Items(StringBuilder sb, IEnumerable<string> values, string empty)
	{
		var any = false;
		foreach (var value in values)
		{
			any = true;
			sb.Append("  - ").Append(value).Append("\r\n");
		}
		if (!any) sb.Append("  ").Append(empty).Append("\r\n");
	}

	private static string Or(string? value, string fallback) =>
		string.IsNullOrWhiteSpace(value) ? fallback : value;
}
