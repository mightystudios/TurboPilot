using System.Text.Json.Serialization;

namespace TurboPilot.Customizations;

/// <summary>
/// The collected customization items, one map per type, keyed by the full
/// file path of the item (case-insensitive, since this is Windows). Each
/// item also carries a stable identity for option transfer.
/// </summary>
public sealed class CustomizationLibrary
{
	/// <summary>Key comparer used for all path-keyed maps and lookups.</summary>
	[JsonIgnore]
	public static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

	/// <summary>Prompt templates from prompts/*.md under each search root.</summary>
	public Dictionary<string, CustomizationItem> Prompts { get; set; } = new(PathComparer);

	/// <summary>Custom agent definitions from agents/*.md under each search root.</summary>
	public Dictionary<string, CustomizationItem> Agents { get; set; } = new(PathComparer);

	/// <summary>Skills from skills/[name]/SKILL.md under each search root.</summary>
	public Dictionary<string, CustomizationItem> Skills { get; set; } = new(PathComparer);

	/// <summary>Instructions from instructions/*.instructions.md under each root.</summary>
	public Dictionary<string, CustomizationItem> Instructions { get; set; } = new(PathComparer);

	/// <summary>
	/// MCP server definitions from *.mcp.json files at each root. One
	/// entry per server, keyed by file path and server name.
	/// </summary>
	public Dictionary<string, CustomizationItem> McpServers { get; set; } = new(PathComparer);

	/// <summary>
	/// True when the library found anything at all. A saved session with
	/// an empty library predates customization snapshots and has nothing
	/// to restore.
	/// </summary>
	[JsonIgnore]
	public bool HasItems => Prompts.Count > 0 || Agents.Count > 0 || Skills.Count > 0
		|| Instructions.Count > 0 || McpServers.Count > 0;

	/// <summary>
	/// Deep copy of all five maps and their items. Callers (such as the
	/// Customization dialog) edit the copy freely; the original is untouched
	/// until the copy is committed.
	/// </summary>
	public CustomizationLibrary Clone() => new()
	{
		Prompts = CloneMap(Prompts),
		Agents = CloneMap(Agents),
		Skills = CloneMap(Skills),
		Instructions = CloneMap(Instructions),
		McpServers = CloneMap(McpServers),
	};

	private static Dictionary<string, CustomizationItem> CloneMap(
		Dictionary<string, CustomizationItem> source)
	{
		var copy = new Dictionary<string, CustomizationItem>(PathComparer);
		foreach (var (key, item) in source)
			copy[key] = item.Clone();
		return copy;
	}
}
