using System.IO;

namespace TurboPilot.Orchestration;

internal sealed class SessionControlMonitor : IDisposable
{
	private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);
	private const long MaximumCommandBytes = 64 * 1024;

	private readonly string _path;
	private readonly Func<SessionControlCommand, Task> _execute;
	private readonly Func<SessionControlCommandException, Task> _reject;
	private readonly CancellationTokenSource _lifetime = new();
	private string? _lastFileIdentity;
	private int _started;

	internal SessionControlMonitor(
		string path,
		Func<SessionControlCommand, Task> execute,
		Func<SessionControlCommandException, Task> reject)
	{
		_path = Path.GetFullPath(path);
		_execute = execute ?? throw new ArgumentNullException(nameof(execute));
		_reject = reject ?? throw new ArgumentNullException(nameof(reject));
	}

	internal void Start()
	{
		if (Interlocked.Exchange(ref _started, 1) != 0)
			throw new InvalidOperationException("The session control monitor is already running.");
		_ = RunAsync();
	}

	private async Task RunAsync()
	{
		try
		{
			await InspectAsync(_lifetime.Token).ConfigureAwait(false);
			using var timer = new PeriodicTimer(PollInterval);
			while (await timer.WaitForNextTickAsync(_lifetime.Token).ConfigureAwait(false))
				await InspectAsync(_lifetime.Token).ConfigureAwait(false);
		}
		catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
		{
		}
		catch (Exception ex)
		{
			await RejectAsync(new SessionControlCommandException(
				"Session control monitoring stopped: " + ex.Message,
				innerException: ex)).ConfigureAwait(false);
		}
	}

	private async Task InspectAsync(CancellationToken cancellationToken)
	{
		FileInfo file;
		try
		{
			file = new FileInfo(_path);
			if (!file.Exists)
				return;
			file.Refresh();
		}
		catch (IOException)
		{
			return;
		}

		if (file.Length > MaximumCommandBytes)
		{
			var oversizedIdentity = $"oversized:{file.Length}:{file.LastWriteTimeUtc.Ticks}";
			if (oversizedIdentity == _lastFileIdentity)
				return;
			_lastFileIdentity = oversizedIdentity;
			await RejectAsync(new SessionControlCommandException(
				$"The session control command exceeds {MaximumCommandBytes} bytes.")).ConfigureAwait(false);
			return;
		}

		string json;
		try
		{
			json = await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false);
		}
		catch (IOException)
		{
			return;
		}
		catch (UnauthorizedAccessException ex)
		{
			var deniedIdentity = $"denied:{file.LastWriteTimeUtc.Ticks}";
			if (deniedIdentity == _lastFileIdentity)
				return;
			_lastFileIdentity = deniedIdentity;
			await RejectAsync(new SessionControlCommandException(
				"Cannot read the session control command: " + ex.Message,
				innerException: ex)).ConfigureAwait(false);
			return;
		}

		if (json == _lastFileIdentity)
			return;
		_lastFileIdentity = json;

		SessionControlCommand command;
		try
		{
			command = SessionControlCommandLoader.Parse(json);
		}
		catch (SessionControlCommandException ex)
		{
			await RejectAsync(ex).ConfigureAwait(false);
			return;
		}

		try
		{
			await _execute(command).ConfigureAwait(false);
		}
		catch (Exception ex)
		{
			await RejectAsync(new SessionControlCommandException(
				"Cannot execute the session control command: " + ex.Message,
				command.CommandId,
				ex)).ConfigureAwait(false);
		}
	}

	private async Task RejectAsync(SessionControlCommandException exception)
	{
		try
		{
			await _reject(exception).ConfigureAwait(false);
		}
		catch (Exception)
		{
			// The owning window may already be closed.
		}
	}

	public void Dispose()
	{
		_lifetime.Cancel();
	}
}
