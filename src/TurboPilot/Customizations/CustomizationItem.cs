namespace TurboPilot.Customizations;

/// <summary>
/// A single customization item (prompt, skill, instruction or custom
/// agent) discovered on disk. <see cref="Name"/> is a unique short name
/// assigned at scan time: when two items share a name the later one gets
/// an incrementing suffix (doublecheck, doublecheck_2, ...) so both
/// remain addressable.
/// </summary>
public sealed class CustomizationItem
{
	/// <summary>
	/// Full path to the file that defines the item. For a skill this is
	/// the SKILL.md inside the skill folder.
	/// </summary>
	public required string FilePath { get; init; }

	/// <summary>
	/// Stable identity used to carry options across rescans. Ordinary
	/// customizations use their path-based map key. Bundled items use an
	/// application-owned source ID and bundle-relative path so an update
	/// or installation-directory change does not reset their options.
	/// </summary>
	public string Identity { get; set; } = "";

	/// <summary>
	/// Unique short name: the skill folder name for skills, the file stem
	/// for prompts, instructions and agents.
	/// </summary>
	public required string Name { get; set; }

	/// <summary>
	/// Sub-identifier when one file defines several items: the server
	/// name inside an MCP config file. Null for single-item files.
	/// </summary>
	public string? Element { get; set; }

	/// <summary>
	/// Whether the item participates when a session starts.
	/// Items are enabled by default.
	/// </summary>
	public bool Enabled { get; set; } = true;

	/// <summary>
	/// Whether the CLI discovers this instruction without a TurboPilot-specific
	/// customization folder. Current standard instructions refresh when a
	/// saved session resumes.
	/// </summary>
	public bool IsCliStandard { get; set; }

	/// <summary>
	/// Whether this item comes from TurboPilot's application-owned
	/// SessionTools bundle.
	/// </summary>
	public bool IsBuiltIn { get; set; }

	/// <summary>Independent copy, so edits can be staged and discarded.</summary>
	public CustomizationItem Clone() => new()
	{
		FilePath = FilePath,
		Identity = Identity,
		Element = Element,
		Name = Name,
		Enabled = Enabled,
		IsCliStandard = IsCliStandard,
		IsBuiltIn = IsBuiltIn,
	};
}
