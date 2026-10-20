using System.Text.Json;
using TurboPilot.Storage;

namespace TurboPilot.Orchestration;

internal sealed class SessionStatusWriter
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
	};

	private readonly object _sync = new();
	private readonly SessionLaunchRequest _request;
	private readonly Func<DateTimeOffset> _utcNow;
	private readonly int _processId;
	private readonly DateTimeOffset _startedAt;

	internal SessionStatusWriter(
		SessionLaunchRequest request,
		Func<DateTimeOffset>? utcNow = null,
		int? processId = null)
	{
		_request = request;
		_utcNow = utcNow ?? (() => DateTimeOffset.UtcNow);
		_processId = processId ?? Environment.ProcessId;
		_startedAt = _utcNow().ToUniversalTime();
	}

	internal DateTimeOffset StartedAt => _startedAt;

	internal SessionStatusRecord Write(
		string state,
		string? sessionId = null,
		bool isWorking = false,
		bool hasPendingQuestion = false,
		DateTimeOffset? lastActivityAt = null,
		string? error = null)
	{
		if (!SessionStatusStates.IsValid(state))
			throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown session status state.");
		if (state == SessionStatusStates.Failed && string.IsNullOrWhiteSpace(error))
			throw new ArgumentException("A failed session status requires an actionable error.", nameof(error));

		lock (_sync)
		{
			var updatedAt = _utcNow().ToUniversalTime();
			var record = new SessionStatusRecord
			{
				ContractVersion = SessionLaunchRequest.CurrentContractVersion,
				LaunchId = _request.LaunchId,
				SessionLabel = _request.SessionLabel,
				ProcessId = _processId,
				SessionId = string.IsNullOrWhiteSpace(sessionId) ? null : sessionId,
				Workspace = _request.Workspace,
				State = state,
				StartedAt = _startedAt,
				UpdatedAt = updatedAt,
				LastActivityAt = (lastActivityAt ?? _startedAt).ToUniversalTime(),
				IsWorking = isWorking,
				HasPendingQuestion = hasPendingQuestion,
				Error = state == SessionStatusStates.Failed ? error!.Trim() : null,
			};
			AtomicFile.Write(_request.SessionStatusPath, stream =>
				JsonSerializer.Serialize(stream, record, JsonOptions));
			return record;
		}
	}
}
