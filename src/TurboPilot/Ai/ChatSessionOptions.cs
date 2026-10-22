using System.Text.Json.Serialization;
using TurboPilot.Customizations;

namespace TurboPilot.Ai;

public sealed record ChatSessionOptions
{
	public string? WorkspaceFolder { get; init; }
	public string Model { get; init; } = "";
	public string ContextTier { get; init; } = "";
	public string? ReasoningEffort { get; init; }
	public string Mode { get; init; } = "Standard";
	public int ContextWindowTokens { get; init; }
	public bool UseByok { get; init; }
	public string ByokEndpoint { get; init; } = "";
	public bool ApplyInstructions { get; init; } = true;
	public bool PreloadSkills { get; init; } = true;
	public bool LinkFiles { get; init; } = true;

	[JsonIgnore]
	public string ByokApiKey { get; init; } = "";

	[JsonIgnore]
	public CustomizationLibrary Customizations { get; init; } = new();
}
