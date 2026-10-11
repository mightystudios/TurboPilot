using System.Windows;
using System.Windows.Controls;
using TurbolandTheme.Wpf.Controls;
using TurboPilot.Tools;

namespace TurboPilot.Dialogs;

/// <summary>
/// Everything the session has changed so far.
///
/// The per-turn cards in the transcript answer "what did that do". This
/// answers "what have we done", which is the question asked before
/// committing, and it is a dialog rather than a panel because the main
/// window is a narrow column that has to stay one.
///
/// Showing a diff is left to the transcript: the output pane already
/// renders one, and a second diff viewer inside a dialog would be a
/// worse copy of it. Selecting a file and choosing View Diff closes the
/// dialog and prints the diff where the user is already reading.
/// </summary>
public partial class SessionChangesDialog : TurbolandFloatingDialog
{
	/// <summary>A row in the list: the change and the line shown for it.</summary>
	private sealed record Row(WorkspaceChange Change)
	{
		public string Label => Change.Mark + " " + Change.Path;
	}

	private readonly ChangeAnchor _anchor;

	/// <summary>The file the user asked to see, once the dialog closes.</summary>
	internal string? Diffed { get; private set; }

	internal SessionChangesDialog(ChangeAnchor anchor)
	{
		InitializeComponent();
		_anchor = anchor;
		Reload();
	}

	private void Reload()
	{
		var changes = WorkspaceChanges.Since(_anchor);
		listChanges.ItemsSource = changes.Select(change => new Row(change)).ToList();
		labelSummary.Content = changes.Count switch
		{
			0 => "Nothing has changed this session",
			1 => "1 file changed this session",
			_ => changes.Count + " files changed this session",
		};
		// A file in no repository has no earlier copy to compare against
		// or restore from, so for that file the list is all that can be
		// offered honestly.
		buttonCompare.IsEnabled = buttonRevert.IsEnabled = false;
		OnSelectionChanged(this, null!);
	}

	private Row? Selected => listChanges.SelectedItem as Row;

	private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		buttonView.IsEnabled = Selected is not null;
		buttonCompare.IsEnabled = buttonRevert.IsEnabled = Selected is { } row && _anchor.CanDiff(row.Change.Path);
	}

	private void OnView(object sender, RoutedEventArgs e)
	{
		if (Selected is not { } row) return;
		Diffed = row.Change.Path;
		DialogResult = true;
		Close();
	}

	private void OnCompare(object sender, RoutedEventArgs e)
	{
		if (Selected is not { } row) return;
		if (!WorkspaceChanges.OpenDiffTool(_anchor, row.Change.Path))
			MessageDialog.Ok(this,
				"Git could not be started to compare that file.",
				"Session Changes");
	}

	private void OnRevert(object sender, RoutedEventArgs e)
	{
		if (Selected is not { } row) return;
		if (!YesNoDialog.Ask(this,
			$"Discard the changes to this file and put it back as it was?\r\n\r\n{row.Change.Path}",
			"Session Changes"))
			return;
		if (!WorkspaceChanges.Revert(_anchor, row.Change.Path))
			MessageDialog.Ok(this, "Could not put that file back.", "Session Changes");
		Reload();
	}

	private void OnClose(object sender, RoutedEventArgs e)
	{
		DialogResult = false;
		Close();
	}
}
