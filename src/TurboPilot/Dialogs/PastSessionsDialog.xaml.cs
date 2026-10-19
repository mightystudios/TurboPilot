using System.IO;
using System.Windows;
using System.Windows.Controls;
using TurbolandTheme.Wpf.Controls;
using TurboPilot.Sessions;

namespace TurboPilot.Dialogs;

public partial class PastSessionsDialog : TurbolandFloatingDialog
{
	private readonly List<SessionRecord> _sessions;
	private readonly SessionStore? _store;
	private readonly string? _currentSessionId;

	public SessionRecord? SelectedSession { get; private set; }
	public bool ResumeRequested { get; private set; }

	/// <summary>
	/// The dialog deletes in place rather than reporting a list back to
	/// the caller. A removed session is gone from the list the moment it
	/// is gone from disk, which is the only way the two can be seen to
	/// agree while the dialog stays open.
	/// </summary>
	/// <param name="store">Supply to offer deletion; omit to list only.</param>
	/// <param name="currentSessionId">
	/// The running session, which cannot be deleted from under itself.
	/// </param>
	public PastSessionsDialog(IReadOnlyList<SessionRecord> sessions, SessionStore? store = null, string? currentSessionId = null)
	{
		_sessions = [.. sessions];
		_store = store;
		_currentSessionId = currentSessionId;
		InitializeComponent();
		buttonDelete.Visibility = store is null ? Visibility.Collapsed : Visibility.Visible;
		RefreshList();
	}

	private void OnSearchChanged(object sender, TextChangedEventArgs e)
	{
		if (listSessions is not null)
			RefreshList();
	}

	private void RefreshList()
	{
		var search = textSearch.Text.Trim();
		listSessions.ItemsSource = _sessions.Where(session =>
			session.SessionId.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| session.ArchiveSentence.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| session.Description.Contains(search, StringComparison.OrdinalIgnoreCase)
			|| (session.Options.WorkspaceFolder?.Contains(search, StringComparison.OrdinalIgnoreCase) ?? false))
			.ToList();
		listSessions.SelectedIndex = listSessions.Items.Count > 0 ? 0 : -1;
		UpdateSelection();
	}

	private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSelection();

	private bool IsCurrent(SessionRecord record) =>
		_currentSessionId is not null
		&& string.Equals(record.SessionId, _currentSessionId, StringComparison.Ordinal);

	private List<SessionRecord> Deletable() =>
		[.. listSessions.SelectedItems.OfType<SessionRecord>().Where(record => !IsCurrent(record))];

	/// <summary>
	/// The details pane carries what the row left out. The model and the
	/// settings live here rather than in the row because they are the
	/// same across most sessions, so they tell one row from another
	/// least; they still matter once a row has been picked.
	///
	/// Resume and View act on one session, so they want exactly one row.
	/// Delete reads the whole selection, minus the running session, which
	/// cannot be removed from under itself.
	/// </summary>
	private void UpdateSelection()
	{
		var picked = listSessions.SelectedItems.OfType<SessionRecord>().ToList();
		var selected = picked.Count == 1 ? picked[0] : null;
		var deletable = Deletable();

		buttonView.IsEnabled = selected is not null && !IsCurrent(selected);
		buttonResume.IsEnabled = selected is not null && !IsCurrent(selected);
		buttonDelete.IsEnabled = _store is not null && deletable.Count > 0;

		textCount.Text = picked.Count switch
		{
			0 => $"{listSessions.Items.Count} of {_sessions.Count} shown",
			1 when IsCurrent(picked[0]) => "The running session is already open and cannot be resumed or deleted",
			_ when picked.Count != deletable.Count => $"{picked.Count} selected, running session excluded",
			1 => "",
			_ => $"{picked.Count} selected",
		};

		if (selected is null)
		{
			textDetails.Text = picked.Count > 1
				? string.Join("\r\n", picked.Select(record => record.DisplayLabel))
				: "No matching sessions.";
			return;
		}

		var lines = new List<string>
		{
			$"ID: {selected.SessionId}",
			$"Workspace: {selected.Options.WorkspaceFolder}",
			$"Model: {(string.IsNullOrWhiteSpace(selected.Options.Model) ? "default" : selected.Options.Model)}",
			$"Mode: {selected.Options.Mode} | Service: {(selected.Options.UseByok ? selected.Options.ByokEndpoint : "Cloud")}",
		};
		// The row already shows the summary clipped. The opening prompt is
		// only worth the space when there is no summary to have shown.
		var sentence = selected.ArchiveSentence;
		if (sentence.Length > 0)
			lines.Add($"Summary: {sentence}");
		else if (!string.IsNullOrWhiteSpace(selected.Description))
			lines.Add($"Opening prompt: {selected.Description}");

		textDetails.Text = string.Join("\r\n", lines);
	}

	/// <summary>
	/// Removes the selected sessions from disk and from the list. Each is
	/// deleted on its own so one unreadable file does not strand the
	/// rest, and what could not be removed is named rather than counted.
	/// </summary>
	private void OnDelete(object sender, RoutedEventArgs e)
	{
		if (_store is null)
			return;
		var doomed = Deletable();
		if (doomed.Count == 0)
			return;

		var what = doomed.Count == 1 ? "this session" : $"these {doomed.Count} sessions";
		if (!YesNoDialog.Ask(this, $"Permanently delete {what} and their transcripts? This cannot be undone.", "Delete Sessions"))
			return;

		var failed = new List<string>();
		foreach (var record in doomed)
		{
			try
			{
				_store.Delete(record.SessionId);
				_sessions.Remove(record);
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
			{
				failed.Add($"{record.SessionId}: {ex.Message}");
			}
		}

		RefreshList();
		if (failed.Count > 0)
			MessageDialog.Ok(this, string.Join("\r\n", failed), "Delete Sessions");
	}

	private void OnView(object sender, RoutedEventArgs e) => Choose(resume: false);
	private void OnResume(object sender, RoutedEventArgs e) => Choose(resume: true);
	private void OnCancel(object sender, RoutedEventArgs e) => Close(false);

	private void Choose(bool resume)
	{
		var picked = listSessions.SelectedItems.OfType<SessionRecord>().ToList();
		if (picked.Count != 1)
			return;
		if (resume && IsCurrent(picked[0]))
			return;
		SelectedSession = picked[0];
		ResumeRequested = resume;
		Close(true);
	}
}
