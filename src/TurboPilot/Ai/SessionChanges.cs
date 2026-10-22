using TurboPilot.Permissions;

namespace TurboPilot.Ai;

public enum SessionChange
{
	// Nothing to do: the running session already has these settings.
	None,
	// A new session without carried context: no running session, or another workspace.
	Fresh,
	// Model, context tier, reasoning effort, built-in mode, and file linking change on the running session.
	Live,
	// A setting fixed when the session was created changed; the replacement can carry a hand-off.
	Restart,
}

public static class SessionChanges
{
	/// <summary>
	/// What starting <paramref name="next"/> would do to the running
	/// session described by <paramref name="current"/>: leave it alone,
	/// replace it outright, change it in place, or restart it with a
	/// hand-off on offer.
	/// </summary>
	public static SessionChange Classify(ChatSessionOptions current, ChatSessionOptions next)
	{
		if (!SameWorkspace(current.WorkspaceFolder, next.WorkspaceFolder) || string.IsNullOrWhiteSpace(current.WorkspaceFolder))
			return SessionChange.Fresh;
		// Custom agents are configured at creation. A BYOK provider also receives the context
		// window then, and a smaller window must not be applied to a running session.
		if (current.UseByok != next.UseByok
			|| (next.UseByok && (current.ByokEndpoint != next.ByokEndpoint || current.ByokApiKey != next.ByokApiKey))
			|| current.ApplyInstructions != next.ApplyInstructions || current.PreloadSkills != next.PreloadSkills
			|| (current.Mode != next.Mode && !(IsBuiltInMode(current.Mode) && IsBuiltInMode(next.Mode)))
			|| (current.UseByok && current.ContextWindowTokens != next.ContextWindowTokens))
			return SessionChange.Restart;
		return current.Model != next.Model || (!next.UseByok && current.ContextTier != next.ContextTier)
			|| (current.ReasoningEffort ?? "") != (next.ReasoningEffort ?? "")
			|| current.Mode != next.Mode || current.LinkFiles != next.LinkFiles
			|| current.ContextWindowTokens != next.ContextWindowTokens
			? SessionChange.Live : SessionChange.None;
	}

	public static bool IsBuiltInMode(string mode) => mode is "Standard" or "Plan" or "Autopilot";

	public static bool SameWorkspace(string? first, string? second) =>
		!string.IsNullOrWhiteSpace(first) && !string.IsNullOrWhiteSpace(second)
			? PermissionService.MakeKey(first) == PermissionService.MakeKey(second)
			: string.IsNullOrWhiteSpace(first) && string.IsNullOrWhiteSpace(second);
}
