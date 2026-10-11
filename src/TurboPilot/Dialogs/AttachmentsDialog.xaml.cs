using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using TurbolandTheme.Wpf.Controls;

namespace TurboPilot.Dialogs;

/// <summary>
/// Dialog for managing the files that ride along with the next prompt.
/// It opens with a copy of the current attachment list; Add picks files
/// and Remove drops the selected one. The caller's list is only replaced
/// when the user clicks OK: Cancel and the close box discard the edits.
/// Shown as a floating dialog window owned by the main window, so it sorts
/// above the WebView2 content and against other windows by OS rule.
/// </summary>
public partial class AttachmentsDialog : TurbolandFloatingDialog
{
	// Working copy of the attachment list. Paths compare case-insensitively
	// because this is NTFS: the same file picked twice under different
	// casing must not attach twice.
	private readonly List<string> _paths;
	private readonly Func<string?>? _takeInitialDirectory;

	/// <summary>
	/// The edited attachment list. Only meaningful after the dialog
	/// returns true.
	/// </summary>
	public IReadOnlyList<string> Attachments => _paths;

	public AttachmentsDialog(IEnumerable<string> current, Func<string?>? takeInitialDirectory = null)
	{
		InitializeComponent();

		_takeInitialDirectory = takeInitialDirectory;
		_paths = current
			.Where(p => !string.IsNullOrWhiteSpace(p))
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		ReloadList();
		UpdateButtonStates();
	}

	protected override void OnInitialized(EventArgs e)
	{
		base.OnInitialized(e);

		// The floating dialog sizes to its content, which overrides any
		// Width/Height set in the designer. Capture the designer's size
		// as the minimum so an empty list does not shrink the dialog to a
		// postage stamp, while still growing if the content needs more.
		if (!double.IsNaN(Width))
			MinWidth = Width;
		if (!double.IsNaN(Height))
			MinHeight = Height;
		Width = double.NaN;
		Height = double.NaN;
	}

	// ------------------------------------------------------------------ list

	private void ReloadList()
	{
		int prevIndex = listBox.SelectedIndex;
		listBox.Items.Clear();
		foreach (var path in _paths)
			listBox.Items.Add(path);

		if (listBox.Items.Count > 0)
			listBox.SelectedIndex = Math.Clamp(prevIndex, 0, listBox.Items.Count - 1);
	}

	private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		UpdateButtonStates();
	}

	private void UpdateButtonStates()
	{
		buttonRemove.IsEnabled = listBox.SelectedItem is not null;
	}

	// ---------------------------------------------------------------- actions

	private void OnAdd(object sender, RoutedEventArgs e)
	{
		var dialog = CreateFilePicker(_takeInitialDirectory?.Invoke());
		if (dialog.ShowDialog(this) != true) return;

		int before = _paths.Count;
		foreach (var path in dialog.FileNames)
		{
			if (!_paths.Contains(path, StringComparer.OrdinalIgnoreCase))
				_paths.Add(path);
		}

		ReloadList();

		// Land the newest addition at the end of the list in view.
		if (_paths.Count > before)
			listBox.SelectedIndex = _paths.Count - 1;

		UpdateButtonStates();
	}

	internal static OpenFileDialog CreateFilePicker(string? initialDirectory)
	{
		var dialog = new OpenFileDialog
		{
			Title = "Attach File(s) to Prompt",
			Multiselect = true,
			CheckFileExists = true,
		};
		if (!string.IsNullOrWhiteSpace(initialDirectory))
			dialog.InitialDirectory = initialDirectory;
		return dialog;
	}

	private void OnRemove(object sender, RoutedEventArgs e)
	{
		if (listBox.SelectedItem is not string path) return;

		_paths.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
		ReloadList();
		UpdateButtonStates();
	}

	private void OnOk(object sender, RoutedEventArgs e) => Close(true);

	private void OnCancel(object sender, RoutedEventArgs e) => Close(false);
}
