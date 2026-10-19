namespace TurboPilot.Sessions;

/// <summary>
/// Determines what happens to a session's persisted history when its live
/// connection ends.
/// </summary>
public enum SessionDisposition
{
	/// <summary>
	/// Ask the session model for an archive label and retain the session in
	/// Past Sessions.
	/// </summary>
	SummarizeAndStore,

	/// <summary>
	/// Skip archive summarization and delete the session's saved history.
	/// </summary>
	Abandon,
}
