namespace TurboPilot.Orchestration;

internal static class SessionStatusStates
{
	public const string Starting = "starting";
	public const string Connecting = "connecting";
	public const string Running = "running";
	public const string WaitingUser = "waiting_user";
	public const string Idle = "idle";
	public const string Failed = "failed";
	public const string Closing = "closing";
	public const string Closed = "closed";

	private static readonly HashSet<string> All =
	[
		Starting,
		Connecting,
		Running,
		WaitingUser,
		Idle,
		Failed,
		Closing,
		Closed,
	];

	public static bool IsValid(string state) => All.Contains(state);
}

/// <summary>
/// Machine-readable health record owned by one TurboPilot process.
/// </summary>
internal sealed record SessionStatusRecord
{
	public string ContractVersion { get; init; } = SessionLaunchRequest.CurrentContractVersion;
	public string LaunchId { get; init; } = "";
	public string SessionLabel { get; init; } = "";
	public int ProcessId { get; init; }
	public string? SessionId { get; init; }
	public string Workspace { get; init; } = "";
	public string State { get; init; } = SessionStatusStates.Starting;
	public DateTimeOffset StartedAt { get; init; }
	public DateTimeOffset UpdatedAt { get; init; }
	public DateTimeOffset LastActivityAt { get; init; }
	public bool IsWorking { get; init; }
	public bool HasPendingQuestion { get; init; }
	public string? Error { get; init; }
	public string? LastControlCommandId { get; init; }
	public string? LastControlCommandState { get; init; }
	public string? LastControlCommandError { get; init; }
}
