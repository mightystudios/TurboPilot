using TurboPilot.Ai;

namespace TurboPilot.Orchestration;

internal sealed class SessionStatusController : IDisposable
{
	private readonly record struct ChatStatusSnapshot(
		ChatService? Source,
		string? SessionId,
		bool IsWorking,
		bool HasPendingQuestion,
		DateTimeOffset? LastActivityAt,
		string? FailureMessage);

	private readonly object _sync = new();
	private readonly SessionStatusWriter _writer;
	private readonly Action? _requestAttention;
	private ChatService? _chat;
	private string _state = SessionStatusStates.Starting;
	private bool _connected;
	private bool _closing;
	private bool _failed;
	private bool _closed;
	private bool _attentionRequested;
	private string? _failureMessage;
	private string? _lastControlCommandId;
	private string? _lastControlCommandState;
	private string? _lastControlCommandError;

	internal SessionStatusController(
		SessionLaunchRequest request,
		Action? requestAttention = null,
		SessionStatusWriter? writer = null)
	{
		_writer = writer ?? new SessionStatusWriter(request);
		_requestAttention = requestAttention;
		_writer.Write(SessionStatusStates.Starting);
	}

	internal string State
	{
		get { lock (_sync) return _state; }
	}

	internal void Connecting()
	{
		lock (_sync)
		{
			if (_closed)
				return;
			_state = SessionStatusStates.Connecting;
			WriteCurrent(default);
		}
	}

	internal void Attach(ChatService chat)
	{
		lock (_sync)
		{
			if (_chat is not null)
				_chat.StateChanged -= OnChatStateChanged;
			_chat = chat;
			chat.StateChanged += OnChatStateChanged;
		}
	}

	internal void Connected()
	{
		lock (_sync)
		{
			if (_closed || _failed)
				return;
			_connected = true;
		}
		UpdateFromChat(force: true);
	}

	internal void Failed(string error)
	{
		var chat = CaptureChatStatus();
		lock (_sync)
		{
			if (_closed || _closing)
				return;
			_failed = true;
			_failureMessage = error;
			_state = SessionStatusStates.Failed;
			WriteCurrent(chat, error);
		}
	}

	internal void Closing()
	{
		var chat = CaptureChatStatus();
		lock (_sync)
		{
			if (_closed)
				return;
			_closing = true;
			_state = SessionStatusStates.Closing;
			WriteCurrent(chat);
		}
	}

	internal void CancelClosing()
	{
		var chat = CaptureChatStatus();
		lock (_sync)
		{
			if (_closed)
				return;
			_closing = false;
			if (_failed)
			{
				_state = SessionStatusStates.Failed;
				WriteCurrent(chat);
				return;
			}
		}
		if (_connected)
			UpdateFromChat(force: true);
		else
			Connecting();
	}

	internal void Closed()
	{
		var chat = CaptureChatStatus();
		lock (_sync)
		{
			if (_closed)
				return;
			_closed = true;
			_closing = false;
			_state = SessionStatusStates.Closed;
			WriteCurrent(chat);
			Detach();
		}
	}

	internal void ControlReceived(string commandId) =>
		UpdateControl(commandId, SessionControlCommandStates.Received);

	internal void ControlCompleted(string commandId) =>
		UpdateControl(commandId, SessionControlCommandStates.Completed);

	internal void ControlFailed(string? commandId, string error) =>
		UpdateControl(commandId, SessionControlCommandStates.Failed, error);

	private void OnChatStateChanged()
	{
		var failure = CaptureChatStatus().FailureMessage;
		if (!string.IsNullOrWhiteSpace(failure))
		{
			Failed(failure);
			return;
		}
		lock (_sync)
		{
			if (!_connected || _closing || _failed || _closed)
				return;
		}
		UpdateFromChat(force: false);
	}

	private void UpdateFromChat(bool force)
	{
		var chat = CaptureChatStatus();
		var requestAttention = false;
		lock (_sync)
		{
			if (chat.Source is null
				|| !ReferenceEquals(_chat, chat.Source)
				|| _closing
				|| _failed
				|| _closed)
				return;

			var next = chat.HasPendingQuestion
				? SessionStatusStates.WaitingUser
				: chat.IsWorking ? SessionStatusStates.Running : SessionStatusStates.Idle;
			if (!force && next == _state && next != SessionStatusStates.Running)
				return;

			_state = next;
			WriteCurrent(chat);
			if (next == SessionStatusStates.WaitingUser && !_attentionRequested)
			{
				_attentionRequested = true;
				requestAttention = true;
			}
		}
		if (requestAttention)
			_requestAttention?.Invoke();
	}

	private ChatStatusSnapshot CaptureChatStatus()
	{
		ChatService? chat;
		lock (_sync)
			chat = _chat;
		return chat is null
			? default
			: new ChatStatusSnapshot(
				chat,
				chat.SessionId,
				chat.IsWorking,
				chat.HasPendingQuestion,
				chat.LastSubstantiveActivityAt,
				chat.FailureMessage);
	}

	private void WriteCurrent(ChatStatusSnapshot chat, string? error = null)
	{
		_writer.Write(
			_state,
			chat.SessionId,
			chat.IsWorking,
			chat.HasPendingQuestion,
			chat.LastActivityAt,
			error ?? (_state == SessionStatusStates.Failed
				? _failureMessage ?? chat.FailureMessage ?? "The externally launched session failed."
				: null),
			_lastControlCommandId,
			_lastControlCommandState,
			_lastControlCommandError);
	}

	private void UpdateControl(string? commandId, string state, string? error = null)
	{
		var chat = CaptureChatStatus();
		lock (_sync)
		{
			if (_closed)
				return;
			_lastControlCommandId = string.IsNullOrWhiteSpace(commandId) ? null : commandId.Trim();
			_lastControlCommandState = state;
			_lastControlCommandError = state == SessionControlCommandStates.Failed
				? error?.Trim()
				: null;
			WriteCurrent(chat);
		}
	}

	private void Detach()
	{
		if (_chat is not null)
			_chat.StateChanged -= OnChatStateChanged;
		_chat = null;
	}

	public void Dispose()
	{
		lock (_sync)
			Detach();
	}
}
