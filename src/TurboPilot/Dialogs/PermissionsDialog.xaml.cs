using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using TurboPilot.Permissions;
using TurbolandTheme.Wpf.Controls;

namespace TurboPilot.Dialogs;

/// <summary>
/// Dialog for editing the permissions of one scope: the active workspace
/// when a session is running, or the application defaults when none is.
///
/// Two kinds of setting live here. Folder grants name places outside the
/// workspace whose files may be read or written; they add to the
/// application defaults, which apply to every workspace. Operation toggles
/// decide which actions run without asking; a workspace inherits those from
/// the defaults until it sets its own, at which point it can be stricter or
/// looser than everyone else.
///
/// Edits happen on a working copy held by the dialog. The store is only
/// touched when the user clicks OK; Cancel discards everything. Save
/// Options and Load Options move the settings to and from a file the user
/// picks, so a permission set can be kept and reused.
///
/// Shown as a floating dialog window owned by the main window, so it sorts
/// above the WebView2 content and against other windows by OS rule.
/// </summary>
public partial class PermissionsDialog : TurbolandFloatingDialog
{
	// The workspace whose scope is being edited, or null for the
	// application defaults.
	private readonly string? _workspace;

	private readonly ObservableCollection<PermissionRow> _rows = new();

	// Staged operation toggles, and whether the scope is still inheriting
	// them from the application defaults.
	private readonly HashSet<string> _approved = new(StringComparer.OrdinalIgnoreCase);
	private readonly List<CheckBox> _operationChecks = new();

	// The caption without the unsaved-changes marker.
	private readonly string _baseTitle;

	private bool _inherited;
	private bool _operationsTouched;
	private bool _dirty;
	private bool _loading;

	// True while the discard confirmation is on screen. The modal dialog
	// blocks mouse input, but a programmatic close attempt (or a stray
	// accelerator) must not call Close again mid-close.
	private bool _confirming;

	public PermissionsDialog()
		: this(null)
	{
	}

	public PermissionsDialog(string? workspaceFolder)
	{
		InitializeComponent();

		_workspace = string.IsNullOrWhiteSpace(workspaceFolder) ? null : workspaceFolder.Trim();
		_baseTitle = Title;

		labelScope.Content = _workspace is null
			? "Application defaults (no active session)"
			: $"Workspace: {_workspace}";

		comboAccess.SelectedIndex = 0;

		listBoxEntries.ItemsSource = _rows;
		LoadRows(PermissionService.EntriesFor(_workspace));

		BuildOperationChecks();
		_inherited = _workspace is not null && !PermissionService.OwnsOperations(_workspace);
		LoadOperations(PermissionService.OperationsFor(_workspace));
		UpdateOperationSourceNote();

		UpdateButtonStates();
	}

	protected override void OnInitialized(EventArgs e)
	{
		base.OnInitialized(e);

		// The floating dialog sizes to its content, which overrides any
		// Width/Height set in the designer. Capture the designer's size
		// as the minimum so the dialog never opens smaller than designed,
		// while still growing if the content ever needs more room.
		if (!double.IsNaN(Width))
			MinWidth = Width;
		if (!double.IsNaN(Height))
			MinHeight = Height;
		Width = double.NaN;
		Height = double.NaN;
	}

	// ------------------------------------------------------------------ folders

	private void LoadRows(IEnumerable<PermissionEntry> entries)
	{
		_loading = true;
		try
		{
			_rows.Clear();
			foreach (PermissionEntry entry in entries)
				_rows.Add(new PermissionRow(entry.FolderPath, entry.Access));
		}
		finally
		{
			_loading = false;
		}
	}

	private void UpdateButtonStates()
	{
		buttonRemove.IsEnabled = listBoxEntries.SelectedIndex >= 0;
	}

	private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
		=> UpdateButtonStates();

	/// <summary>
	/// A row's access level was changed through its drop-down. The binding
	/// has already written the new value into the row by the time the
	/// selection event arrives.
	/// </summary>
	private void OnAccessChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_loading)
			return;

		MarkDirty();
	}

	/// <summary>
	/// Adds the typed path to the list. A path may name a folder exactly,
	/// use wildcards, or end with an ellipsis to reach into subfolders.
	/// </summary>
	private void OnAdd(object sender, RoutedEventArgs e)
	{
		string path = textBoxFolder.Text?.Trim().Trim('"') ?? string.Empty;
		if (path.Length == 0)
		{
			Notify("Type a folder path, or use Browse to pick one.");
			textBoxFolder.Focus();
			return;
		}

		if (_rows.Any(r => string.Equals(r.FolderPath, path, StringComparison.OrdinalIgnoreCase)))
		{
			Notify("That path is already in the list.");
			return;
		}

		// The workspace needs no grant: everything inside it is accessible.
		if (_workspace is not null && PermissionMatcher.IsUnder(path, _workspace))
		{
			Notify("That folder is inside the workspace, which is always accessible.");
			return;
		}

		var option = comboAccess.SelectedItem as PermissionOption;
		PermissionAccess access = option?.Access ?? PermissionAccess.Read;

		_rows.Add(new PermissionRow(path, access));
		textBoxFolder.Clear();
		listBoxEntries.SelectedItem = _rows[^1];
		listBoxEntries.ScrollIntoView(_rows[^1]);
		MarkDirty();
		UpdateButtonStates();
	}

	private void OnFolderKeyDown(object sender, KeyEventArgs e)
	{
		if (e.Key == Key.Enter)
		{
			OnAdd(sender, e);
			e.Handled = true;
		}
	}

	private void OnBrowse(object sender, RoutedEventArgs e)
	{
		var folderDialog = new System.Windows.Forms.FolderBrowserDialog
		{
			Description = "Select a folder to grant file access to",
			UseDescriptionForTitle = true,
		};

		string typed = textBoxFolder.Text?.Trim() ?? string.Empty;
		string anchor = PermissionMatcher.AnchorFolder(typed);
		if (anchor.Length > 0 && Directory.Exists(anchor))
			folderDialog.InitialDirectory = anchor;

		// Own the native folder picker with this dialog's HWND so it stays
		// above the floating dialog instead of floating loose on the desktop.
		if (folderDialog.ShowDialog(new Win32Window(new WindowInteropHelper(this).Handle))
			!= System.Windows.Forms.DialogResult.OK)
			return;

		textBoxFolder.Text = folderDialog.SelectedPath;
		textBoxFolder.Focus();
	}

	private void OnRemove(object sender, RoutedEventArgs e)
	{
		int i = listBoxEntries.SelectedIndex;
		if (i < 0)
			return;

		var row = _rows[i];
		if (!YesNoDialog.Ask(this,
			$"Remove {row.FolderPath}, are you sure?",
			"Permissions"))
			return;

		_rows.RemoveAt(i);
		if (_rows.Count > 0)
			listBoxEntries.SelectedIndex = Math.Min(i, _rows.Count - 1);
		MarkDirty();
		UpdateButtonStates();
	}

	// ------------------------------------------------------------------ operations

	/// <summary>
	/// Builds one check box per known operation, laid out in two columns.
	/// The list is fixed, so the boxes are created once and only their
	/// checked state changes afterwards.
	/// </summary>
	private void BuildOperationChecks()
	{
		for (int i = 0; i < PermissionOperations.All.Count; i++)
		{
			PermissionOperation operation = PermissionOperations.All[i];
			var check = new CheckBox
			{
				Content = operation.Label,
				Tag = operation.Kind,
				Margin = new Thickness(0, 3, 16, 3),
			};
			check.Checked += OnOperationToggled;
			check.Unchecked += OnOperationToggled;

			(i % 2 == 0 ? panelOperationsLeft : panelOperationsRight).Children.Add(check);
			_operationChecks.Add(check);
		}
	}

	private void LoadOperations(IEnumerable<string> approvedKinds)
	{
		_approved.Clear();
		foreach (string kind in approvedKinds)
			_approved.Add(kind);

		_loading = true;
		try
		{
			foreach (CheckBox check in _operationChecks)
				check.IsChecked = _approved.Contains((string)check.Tag!);
		}
		finally
		{
			_loading = false;
		}
	}

	/// <summary>
	/// An operation toggle moved. Taking ownership of the toggles is part of
	/// the edit: a workspace that inherits cannot also be changed.
	/// </summary>
	private void OnOperationToggled(object sender, RoutedEventArgs e)
	{
		if (_loading || sender is not CheckBox check)
			return;

		string kind = (string)check.Tag!;
		if (check.IsChecked == true)
			_approved.Add(kind);
		else
			_approved.Remove(kind);

		_inherited = false;
		_operationsTouched = true;
		UpdateOperationSourceNote();
		MarkDirty();
	}

	private void OnUseDefaults(object sender, RoutedEventArgs e)
	{
		_inherited = true;
		_operationsTouched = true;
		LoadOperations(PermissionService.OperationsFor(null));
		UpdateOperationSourceNote();
		MarkDirty();
	}

	private void UpdateOperationSourceNote()
	{
		if (_workspace is null)
		{
			textOperationSource.Text = "These are the application defaults every workspace starts from.";
			buttonUseDefaults.IsEnabled = false;
			return;
		}

		textOperationSource.Text = _inherited
			? "Inherited from the application defaults. Change one to give this workspace its own set."
			: "This workspace has its own set of allowed operations.";
		buttonUseDefaults.IsEnabled = !_inherited;
	}

	// ------------------------------------------------------------------ results

	private void OnOk(object sender, RoutedEventArgs e)
	{
		// The writes below are the save; the close guard must not ask twice.
		_dirty = false;

		PermissionService.SetEntries(_workspace,
			_rows.Select(r => new PermissionEntry(r.FolderPath, r.Access)));

		// Untouched toggles stay as they were: a workspace that has never
		// owned its own set must not acquire one just because the dialog
		// was opened and OK clicked.
		if (_operationsTouched)
		{
			if (_inherited)
				PermissionService.ClearOperations(_workspace);
			else
				PermissionService.SetOperations(_workspace, _approved);
		}

		Close(true);
	}

	private void OnCancel(object sender, RoutedEventArgs e)
	{
		if (_confirming)
			return;

		Close(false);
	}

	/// <summary>
	/// Guards every close path (Cancel, the close box, Alt+F4) the same
	/// way: staged edits are discarded only after the user confirms, and
	/// nothing is ever written to the store on the way out. Cancel means
	/// no net change.
	/// </summary>
	protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
	{
		if (_dirty && !_confirming)
		{
			_confirming = true;
			try
			{
				if (!YesNoDialog.Ask(this,
					"You have unsaved changes. Discard them?",
					"Permissions"))
				{
					e.Cancel = true;
				}
			}
			finally
			{
				_confirming = false;
			}
		}

		base.OnClosing(e);
	}

	private void MarkDirty()
	{
		if (_dirty)
			return;

		_dirty = true;
		Title = $"{_baseTitle} *";
	}

	private void Notify(string message)
		=> MessageDialog.Ok(this, message, "Permissions");

	// ------------------------------------------------------------------ options file

	/// <summary>
	/// Writes the working settings to a file the user picks. The scope is
	/// not written: the same settings can be loaded into any workspace.
	/// </summary>
	private void OnSaveOptions(object sender, RoutedEventArgs e)
	{
		var dialog = new Microsoft.Win32.SaveFileDialog
		{
			Title = "Save Permission Options",
			Filter = "Permission options (*.json)|*.json|All files (*.*)|*.*",
			DefaultExt = ".json",
			FileName = "permission-options.json",
		};

		if (dialog.ShowDialog(this) != true)
			return;

		try
		{
			PermissionService.SaveOptions(dialog.FileName,
				_rows.Select(r => new PermissionEntry(r.FolderPath, r.Access)),
				_approved);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
			or System.Security.SecurityException)
		{
			Notify($"Could not save the options file:\r\n{ex.Message}");
		}
	}

	private void OnLoadOptions(object sender, RoutedEventArgs e)
	{
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Load Permission Options",
			Filter = "Permission options (*.json)|*.json|All files (*.*)|*.*",
			CheckFileExists = true,
		};

		if (dialog.ShowDialog(this) != true)
			return;

		PermissionOptionsFile loaded;
		try
		{
			loaded = PermissionService.LoadOptions(dialog.FileName);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
			or System.Security.SecurityException or System.Text.Json.JsonException)
		{
			Notify($"Could not read the options file:\r\n{ex.Message}");
			return;
		}

		MergeLoaded(loaded);
	}

	/// <summary>
	/// Folds a loaded option set into the working state. Folder grants are
	/// merged: paths already present keep their place and take the loaded
	/// access level, new paths are appended, and loading never removes
	/// anything, so a partial option file can be layered onto an existing
	/// list. Operation toggles are a set rather than a list, so a file that
	/// carries them replaces them wholesale.
	/// </summary>
	private void MergeLoaded(PermissionOptionsFile loaded)
	{
		bool changed = false;

		foreach (PermissionEntry entry in loaded.Folders ?? [])
		{
			var existing = _rows.FirstOrDefault(r =>
				string.Equals(r.FolderPath, entry.FolderPath, StringComparison.OrdinalIgnoreCase));
			if (existing is not null)
				existing.Access = entry.Access;
			else
				_rows.Add(new PermissionRow(entry.FolderPath, entry.Access));

			changed = true;
		}

		if (loaded.Operations is not null)
		{
			LoadOperations(loaded.Operations);
			_inherited = false;
			_operationsTouched = true;
			UpdateOperationSourceNote();
			changed = true;
		}

		if (changed)
			MarkDirty();
	}

	/// <summary>
	/// Adapts a raw HWND to <see cref="System.Windows.Forms.IWin32Window"/> so the
	/// WinForms folder picker can be owned by this WPF window.
	/// </summary>
	private sealed class Win32Window(nint handle) : System.Windows.Forms.IWin32Window
	{
		public nint Handle { get; } = handle;
	}
}

/// <summary>
/// One row of the permissions list: the entry's path pattern, the access
/// granted there, and whether the folder the pattern is anchored to is
/// actually on disk. The missing-folder flag drives the amber warning in
/// the row template.
/// </summary>
public sealed class PermissionRow(string folderPath, PermissionAccess access)
{
	public string FolderPath { get; } = folderPath;

	public PermissionAccess Access { get; set; } = access;

	public bool Exists => PermissionMatcher.AnchorExists(FolderPath);
}
