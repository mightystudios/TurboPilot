using System.Text.Json;

namespace TurboPilot.Orchestration;

/// <summary>
/// Versioned request supplied by an external session launcher.
/// </summary>
internal sealed record SessionLaunchRequest
{
	public const string CurrentContractVersion = "1.0";

	public string ContractVersion { get; init; } = "";
	public string LaunchId { get; init; } = "";
	public string SessionLabel { get; init; } = "";
	public string Workspace { get; init; } = "";
	public IReadOnlyList<string> Attachments { get; init; } = [];
	public string InitialPrompt { get; init; } = "";
	public string Mode { get; init; } = "";
	public string SessionStatusPath { get; init; } = "";
	public string SessionControlPath { get; init; } = "";

	internal static JsonSerializerOptions JsonOptions { get; } = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
	};
}
