using System.Windows;
using TurbolandTheme.Wpf.Controls;
using TurboPilot.Sessions;

namespace TurboPilot.Dialogs;

/// <summary>
/// Lets the user retain a session in Past Sessions or abandon and delete it.
/// </summary>
public partial class SessionDispositionDialog : TurbolandFloatingDialog
{
	private SessionDisposition? _selection;

	/// <summary>
	/// Initializes the session disposition dialog.
	/// </summary>
	public SessionDispositionDialog()
	{
		InitializeComponent();
	}

	/// <summary>
	/// Asks how the current session should end.
	/// </summary>
	/// <param name="owner">Window that owns the modal dialog.</param>
	/// <param name="context">Explanation of why the session is ending.</param>
	/// <returns>The selected disposition, or null when the operation was canceled.</returns>
	public static SessionDisposition? Choose(Window owner, string context)
	{
		var dialog = new SessionDispositionDialog
		{
			Message =
			{
				Text = context + "\r\n\r\n"
					+ "Summarize and Store creates a short summary and keeps the history in Past Sessions.\r\n\r\n"
					+ "Abandon skips summarization and deletes the saved history."
			}
		};

		return dialog.ShowDialog(owner) == true ? dialog._selection : null;
	}

	private void OnStore(object sender, RoutedEventArgs e)
	{
		_selection = SessionDisposition.SummarizeAndStore;
		Close(true);
	}

	private void OnAbandon(object sender, RoutedEventArgs e)
	{
		_selection = SessionDisposition.Abandon;
		Close(true);
	}

	private void OnCancel(object sender, RoutedEventArgs e) => Close(false);
}
