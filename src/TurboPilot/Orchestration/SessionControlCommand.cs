using System.IO;
using System.Text.Json;
using TurboPilot.Sessions;

namespace TurboPilot.Orchestration;

internal static class SessionControlActions
{
	public const string Replace = "replace";
	public const string Close = "close";

	internal static bool IsValid(string action) => action is Replace or Close;
}

internal static class SessionControlDispositions
{
	public const string Abandon = "abandon";
	public const string SummarizeAndStore = "summarize_and_store";

	internal static bool IsValid(string disposition) =>
		disposition is Abandon or SummarizeAndStore;

	internal static SessionDisposition ToSessionDisposition(string disposition) =>
		disposition == SummarizeAndStore
			? SessionDisposition.SummarizeAndStore
			: SessionDisposition.Abandon;
}

internal static class SessionControlCommandStates
{
	public const string Received = "received";
	public const string Completed = "completed";
	public const string Failed = "failed";

	internal static bool IsValid(string state) => state is Received or Completed or Failed;
}

/// <summary>
/// One external command targeting a specific externally launched process and session.
/// </summary>
internal sealed record SessionControlCommand
{
	public const string CurrentContractVersion = "1.0";

	public string ContractVersion { get; init; } = "";
	public string CommandId { get; init; } = "";
	public int TargetProcessId { get; init; }
	public string TargetLaunchId { get; init; } = "";
	public string Action { get; init; } = "";
	public string Disposition { get; init; } = SessionControlDispositions.Abandon;
	public string? LaunchRequestPath { get; init; }

	internal SessionDisposition SessionDisposition =>
		SessionControlDispositions.ToSessionDisposition(Disposition);
}

internal sealed class SessionControlCommandException : Exception
{
	internal SessionControlCommandException(
		string message,
		string? commandId = null,
		Exception? innerException = null)
		: base(message, innerException)
	{
		CommandId = commandId;
	}

	internal string? CommandId { get; }
}

internal static class SessionControlCommandLoader
{
	private const int MaximumIdentifierLength = 200;

	internal static SessionControlCommand Parse(string json)
	{
		SessionControlCommand command;
		try
		{
			command = JsonSerializer.Deserialize<SessionControlCommand>(
				json, SessionLaunchRequest.JsonOptions)
				?? throw new SessionControlCommandException("The session control command is empty.");
		}
		catch (SessionControlCommandException)
		{
			throw;
		}
		catch (JsonException ex)
		{
			throw new SessionControlCommandException(
				"The session control command is not valid JSON: " + ex.Message,
				innerException: ex);
		}

		void Fail(string message) =>
			throw new SessionControlCommandException(message, EmptyToNull(command.CommandId));

		if (!string.Equals(
			command.ContractVersion?.Trim(),
			SessionControlCommand.CurrentContractVersion,
			StringComparison.Ordinal))
		{
			Fail($"Unsupported session control contract version '{command.ContractVersion}'. "
				+ $"TurboPilot supports '{SessionControlCommand.CurrentContractVersion}'.");
		}

		var commandId = Required(command.CommandId, "commandId", Fail);
		var targetLaunchId = Required(command.TargetLaunchId, "targetLaunchId", Fail);
		if (commandId.Length > MaximumIdentifierLength)
			Fail($"The session control field 'commandId' must not exceed {MaximumIdentifierLength} characters.");
		if (targetLaunchId.Length > MaximumIdentifierLength)
			Fail($"The session control field 'targetLaunchId' must not exceed {MaximumIdentifierLength} characters.");
		if (command.TargetProcessId <= 0)
			Fail("The session control field 'targetProcessId' must be a positive process ID.");

		var action = Required(command.Action, "action", Fail).ToLowerInvariant();
		if (!SessionControlActions.IsValid(action))
			Fail($"Unsupported session control action '{command.Action}'.");
		var disposition = string.IsNullOrWhiteSpace(command.Disposition)
			? SessionControlDispositions.Abandon
			: command.Disposition.Trim().ToLowerInvariant();
		if (!SessionControlDispositions.IsValid(disposition))
			Fail($"Unsupported session control disposition '{command.Disposition}'.");

		string? launchRequestPath = null;
		if (action == SessionControlActions.Replace)
		{
			var requestedPath = command.LaunchRequestPath;
			if (string.IsNullOrWhiteSpace(requestedPath))
				Fail("The session control action 'replace' requires 'launchRequestPath'.");
			if (!Path.IsPathFullyQualified(requestedPath!))
				Fail("The replacement launch request path must be absolute.");
			launchRequestPath = Path.GetFullPath(requestedPath!.Trim());
			if (!string.Equals(Path.GetExtension(launchRequestPath), ".json", StringComparison.OrdinalIgnoreCase))
				Fail("The replacement launch request path must end in .json.");
			if (!File.Exists(launchRequestPath))
				Fail($"The replacement launch request '{launchRequestPath}' does not exist.");
		}
		else if (!string.IsNullOrWhiteSpace(command.LaunchRequestPath))
		{
			Fail("The session control action 'close' must not include 'launchRequestPath'.");
		}

		return command with
		{
			ContractVersion = SessionControlCommand.CurrentContractVersion,
			CommandId = commandId,
			TargetLaunchId = targetLaunchId,
			Action = action,
			Disposition = disposition,
			LaunchRequestPath = launchRequestPath,
		};
	}

	private static string Required(string? value, string field, Action<string> fail)
	{
		if (string.IsNullOrWhiteSpace(value))
			fail($"The session control field '{field}' is required.");
		return value!.Trim();
	}

	private static string? EmptyToNull(string? value) =>
		string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
