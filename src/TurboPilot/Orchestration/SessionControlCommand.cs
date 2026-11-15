using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TurboPilot.Sessions;

namespace TurboPilot.Orchestration;

internal static class SessionControlActions
{
	public const string Replace = "replace";
	public const string Close = "close";
	public const string Continue = "continue";

	internal static bool IsValid(string action) => action is Replace or Close or Continue;
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
	internal const int MaximumPromptLength = 32 * 1024;
	internal const int MaximumAttachments = 32;

	public string ContractVersion { get; init; } = "";
	public string CommandId { get; init; } = "";
	public int TargetProcessId { get; init; }
	public string TargetLaunchId { get; init; } = "";
	public string? TargetSessionId { get; init; }
	public string Action { get; init; } = "";
	public string Disposition { get; init; } = SessionControlDispositions.Abandon;
	public string? LaunchRequestPath { get; init; }
	public string? Prompt { get; init; }
	public IReadOnlyList<string> Attachments { get; init; } = [];

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
		string? targetSessionId = null;
		string? prompt = null;
		IReadOnlyList<string> attachments = [];
		if (action == SessionControlActions.Replace)
		{
			RejectContinuationFields(command, "replace", Fail);
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
		else if (action == SessionControlActions.Close)
		{
			RejectContinuationFields(command, "close", Fail);
			if (!string.IsNullOrWhiteSpace(command.LaunchRequestPath))
				Fail("The session control action 'close' must not include 'launchRequestPath'.");
		}
		else
		{
			if (!string.IsNullOrWhiteSpace(command.LaunchRequestPath))
				Fail("The session control action 'continue' must not include 'launchRequestPath'.");
			if (disposition != SessionControlDispositions.Abandon)
				Fail("The session control action 'continue' does not accept a session disposition.");

			targetSessionId = Required(command.TargetSessionId, "targetSessionId", Fail);
			if (targetSessionId.Length > MaximumIdentifierLength)
			{
				Fail(
					$"The session control field 'targetSessionId' must not exceed "
					+ $"{MaximumIdentifierLength} characters.");
			}

			prompt = command.Prompt?.Trim();
			if (prompt?.Length > SessionControlCommand.MaximumPromptLength)
			{
				Fail(
					$"The session control prompt must not exceed "
					+ $"{SessionControlCommand.MaximumPromptLength} characters.");
			}

			var suppliedAttachments = command.Attachments ?? [];
			if (suppliedAttachments.Count > SessionControlCommand.MaximumAttachments)
			{
				Fail(
					$"The session control action 'continue' supports at most "
					+ $"{SessionControlCommand.MaximumAttachments} attachments.");
			}
			try
			{
				attachments = suppliedAttachments
					.Select((path, index) => CanonicalFile(path, $"Attachment {index + 1}"))
					.ToArray();
			}
			catch (Exception ex) when (ex is ArgumentException
				or NotSupportedException
				or PathTooLongException)
			{
				Fail(ex.Message);
			}
			if (string.IsNullOrWhiteSpace(prompt) && attachments.Count == 0)
				Fail("The session control action 'continue' requires a prompt or attachment.");
		}

		return command with
		{
			ContractVersion = SessionControlCommand.CurrentContractVersion,
			CommandId = commandId,
			TargetLaunchId = targetLaunchId,
			Action = action,
			Disposition = disposition,
			LaunchRequestPath = launchRequestPath,
			TargetSessionId = targetSessionId,
			Prompt = prompt,
			Attachments = attachments,
		};
	}

	internal static bool ContainsPath(string parent, string path)
	{
		var relative = Path.GetRelativePath(parent, path);
		return !Path.IsPathRooted(relative)
			&& !string.Equals(relative, "..", StringComparison.Ordinal)
			&& !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
			&& !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
	}

	internal static string Fingerprint(SessionControlCommand command)
	{
		var text = new StringBuilder()
			.Append(command.TargetProcessId).Append('\n')
			.Append(command.TargetLaunchId).Append('\n')
			.Append(command.TargetSessionId).Append('\n')
			.Append(command.Action).Append('\n')
			.Append(command.Prompt).Append('\n');
		foreach (var attachment in command.Attachments)
			text.Append(attachment).Append('\n');
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))
			.ToLowerInvariant();
	}

	private static void RejectContinuationFields(
		SessionControlCommand command,
		string action,
		Action<string> fail)
	{
		if (!string.IsNullOrWhiteSpace(command.TargetSessionId)
			|| !string.IsNullOrWhiteSpace(command.Prompt)
			|| command.Attachments is { Count: > 0 })
		{
			fail($"The session control action '{action}' must not include continuation fields.");
		}
	}

	private static string Required(string? value, string field, Action<string> fail)
	{
		if (string.IsNullOrWhiteSpace(value))
			fail($"The session control field '{field}' is required.");
		return value!.Trim();
	}

	private static string? EmptyToNull(string? value) =>
		string.IsNullOrWhiteSpace(value) ? null : value.Trim();

	private static string CanonicalFile(string? path, string description)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException(description + " is required.");
		if (!Path.IsPathFullyQualified(path))
			throw new ArgumentException(description + " path must be absolute.");
		var canonical = Path.GetFullPath(path.Trim());
		if (!File.Exists(canonical))
			throw new ArgumentException($"{description} '{canonical}' does not exist.");
		return canonical;
	}
}

internal sealed record SessionControlCommandOutcome(
	string Fingerprint,
	string State,
	string? Error);

internal sealed class SessionControlCommandHistory
{
	private const int MaximumEntries = 256;
	private readonly Dictionary<string, SessionControlCommandOutcome> _entries =
		new(StringComparer.Ordinal);
	private readonly Queue<string> _order = new();

	internal bool TryGet(
		SessionControlCommand command,
		out SessionControlCommandOutcome? outcome)
	{
		if (!_entries.TryGetValue(command.CommandId, out outcome))
			return false;
		if (!string.Equals(
			outcome.Fingerprint,
			SessionControlCommandLoader.Fingerprint(command),
			StringComparison.Ordinal))
		{
			throw new SessionControlCommandException(
				$"The command ID '{command.CommandId}' was already used for different content.",
				command.CommandId);
		}
		return true;
	}

	internal void Record(
		SessionControlCommand command,
		string state,
		string? error = null)
	{
		if (!SessionControlCommandStates.IsValid(state))
			throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown control state.");
		if (state == SessionControlCommandStates.Failed && string.IsNullOrWhiteSpace(error))
			throw new ArgumentException("A failed command requires an error.", nameof(error));

		if (!_entries.ContainsKey(command.CommandId))
		{
			while (_entries.Count >= MaximumEntries)
				_entries.Remove(_order.Dequeue());
			_order.Enqueue(command.CommandId);
		}
		_entries[command.CommandId] = new SessionControlCommandOutcome(
			SessionControlCommandLoader.Fingerprint(command),
			state,
			state == SessionControlCommandStates.Failed ? error!.Trim() : null);
	}
}
