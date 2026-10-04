using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using TurboPilot.Customizations;
using TurbolandTheme.Wpf.Controls;
using PromptReferenceMarkers = TurboPilot.Commands.PromptReferences;

namespace TurboPilot.Dialogs;

/// <summary>
/// Dialog for editing the list of folders the application searches
/// for customization items (prompts, agents, skills, instructions and
/// MCP servers) and for toggling which collected items participate.
/// All edits happen on working copies held by the dialog: the folder
/// list, the item enabled flags and the settings file are only touched
/// when the user clicks OK, and Cancel discards everything. On OK the
/// staged flags are committed and the roots are rescanned.
/// Shown as a floating dialog window owned by the main window, so it sorts
/// above the WebView2 content and against other windows by OS rule.
/// </summary>
public partial class CustomizeDialog : TurbolandFloatingDialog
{
	private readonly List<string> _folders;
	private readonly string? _workspaceFolder;

	// Deep copy of the collected library; the live lists stay untouched
	// until Commit on OK. Replaced (not mutated) when a load rebuilds it
	// after staged folder additions.
	private CustomizationLibrary _library;

	// The caption without the unsaved-changes marker.
	private readonly string _baseTitle;

	private bool _dirty;

	// True while the discard confirmation is on screen, so a second close
	// attempt cannot call Close while the window is already closing.
	private bool _confirming;

	/// <summary>
	/// The edited list of search folders. Only meaningful after the dialog
	/// returns true.
	/// </summary>
	public IReadOnlyList<string> Folders => _folders;

	// Items the user chose to mention in the next prompt. Staged like
	// everything else in this dialog: Cancel throws them away.
	private readonly List<string> _promptReferences = [];

	/// <summary>
	/// Lines to put in the prompt box, naming the items the user picked.
	/// They are a suggestion left in the box to be edited, not a message
	/// sent on the user's behalf.
	/// </summary>
	public IReadOnlyList<string> PromptReferences => _promptReferences;

	public CustomizeDialog(string? workspaceFolder = null)
		: this(workspaceFolder, Settings.Load().CustomizationFolders, CustomizationService.Current)
	{
	}

	internal CustomizeDialog(string? workspaceFolder, IReadOnlyList<string> folders,
		CustomizationLibrary previous)
	{
		InitializeComponent();

		_baseTitle = Title;
		_workspaceFolder = workspaceFolder;

		_folders = folders
			.Where(f => !string.IsNullOrWhiteSpace(f))
			.Select(f => f.Trim())
			.ToList();

		_library = CustomizationService.Preview(workspaceFolder, _folders, previous);

		ReloadList();
		UpdateButtonStates();
		RefreshFoundItems();
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

	// ------------------------------------------------------------------ list

	private void ReloadList()
	{
		int prevIndex = listBox.SelectedIndex;
		listBox.Items.Clear();
		foreach (var folder in _folders)
			listBox.Items.Add(folder);

		if (listBox.Items.Count > 0)
			listBox.SelectedIndex = Math.Clamp(prevIndex, 0, listBox.Items.Count - 1);

		UpdateButtonStates();
	}

	private void UpdateButtonStates()
	{
		buttonDelete.IsEnabled = listBox.SelectedIndex >= 0;
	}

	private void OnListSelectionChanged(object sender, SelectionChangedEventArgs e)
		=> UpdateButtonStates();

	// ------------------------------------------------------------------ editing

	private void OnAdd(object sender, RoutedEventArgs e)
	{
		var folderDialog = new System.Windows.Forms.FolderBrowserDialog
		{
			Description = "Select a folder to search for customization items",
			UseDescriptionForTitle = true,
		};

		// Own the native folder picker with this dialog's HWND so it stays
		// above the floating dialog instead of floating loose on the desktop.
		if (folderDialog.ShowDialog(new Win32Window(new WindowInteropHelper(this).Handle))
			!= System.Windows.Forms.DialogResult.OK)
			return;

		AddFolder(folderDialog.SelectedPath);
	}

	private void AddFolder(string path)
	{
		var trimmed = path.Trim();
		if (string.IsNullOrEmpty(trimmed))
			return;

		if (IsDuplicate(trimmed))
		{
			MessageBox.Show(this, "That folder is already in the list.",
				"Customization", MessageBoxButton.OK, MessageBoxImage.Information);
			return;
		}

		_folders.Add(trimmed);
		ReloadList();
		listBox.SelectedIndex = _folders.Count - 1;
		MarkDirty();
	}

	private void OnDelete(object sender, RoutedEventArgs e)
	{
		int i = listBox.SelectedIndex;
		if (i < 0)
			return;

		var path = _folders[i];
		var answer = MessageBox.Show(this, $"Delete {path}, are you sure?",
			"Customization", MessageBoxButton.YesNo, MessageBoxImage.Question);
		if (answer != MessageBoxResult.Yes)
			return;

		_folders.RemoveAt(i);
		ReloadList();
		if (_folders.Count > 0)
			listBox.SelectedIndex = Math.Min(i, _folders.Count - 1);
		MarkDirty();
	}

	private bool IsDuplicate(string candidate) =>
		_folders.Any(f => string.Equals(f, candidate, StringComparison.OrdinalIgnoreCase));

	// ------------------------------------------------------------------ found items

	/// <summary>
	/// Fills the per-type tabs from the collected library. With the
	/// Only Show Enabled filter on, disabled items are left out of the
	/// lists; the tab headers always report the full counts.
	/// </summary>
	private void RefreshFoundItems()
	{
		var library = _library;
		bool onlyEnabled = radioOnlyEnabled.IsChecked == true;
		PopulateTab(listPrompts, tabPrompts, "Prompts", library.Prompts, onlyEnabled);
		PopulateTab(listAgents, tabAgents, "Agents", library.Agents, onlyEnabled);
		PopulateTab(listSkills, tabSkills, "Skills", library.Skills, onlyEnabled);
		PopulateTab(listInstructions, tabInstructions, "Instructions", library.Instructions, onlyEnabled);
		PopulateTab(listMcpServers, tabMcpServers, "MCP Servers", library.McpServers, onlyEnabled);
		textBoxDetails.Text = string.Empty;
	}

	private static void PopulateTab(ListBox list, TabItem tab, string label,
		IReadOnlyDictionary<string, CustomizationItem> items, bool onlyEnabled)
	{
		list.ItemsSource = items.Values
			.Where(i => !onlyEnabled || i.Enabled)
			.OrderBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
			.ToList();
		tab.Header = $"{label} ({items.Count})";
	}

	/// <summary>
	/// A checkbox in one of the found-items lists was toggled. The flag
	/// lands on the staged copy only; nothing is persisted until OK. The
	/// lists are deliberately not rebuilt here: the row the user just
	/// clicked must not vanish under the filter.
	/// </summary>
	private void OnItemToggled(object sender, RoutedEventArgs e)
	{
		if (e.OriginalSource is not CheckBox checkBox
			|| checkBox.DataContext is not CustomizationItem item)
			return;

		// Write the flag explicitly rather than trusting the binding's
		// update order relative to this event.
		item.Enabled = checkBox.IsChecked == true;
		MarkDirty();

		// Keep the details pane honest if the toggled item is selected.
		if (listPrompts.SelectedItem == item || listAgents.SelectedItem == item
			|| listSkills.SelectedItem == item || listInstructions.SelectedItem == item
			|| listMcpServers.SelectedItem == item)
		{
			ShowDetails(item);
		}
	}

	private void OnOnlyEnabledChanged(object sender, RoutedEventArgs e) => RefreshFoundItems();

	// A lone radio button stays checked when clicked, but this one is a
	// filter, not part of a choice group: clicking it while checked must
	// clear it. Remember the state before the toggle happens, then act on
	// it in Click.
	private bool _onlyEnabledWasChecked;

	private void OnOnlyEnabledBeforeToggle(object sender, RoutedEventArgs e)
		=> _onlyEnabledWasChecked = radioOnlyEnabled.IsChecked == true;

	private void OnOnlyEnabledClick(object sender, RoutedEventArgs e)
	{
		if (_onlyEnabledWasChecked)
			radioOnlyEnabled.IsChecked = false;
	}

	/// <summary>
	/// A row in one of the found-items lists was selected: show its
	/// details. Selection never changes the enabled flag; only the
	/// checkmark does, through OnItemToggled.
	/// </summary>
	private void OnFoundItemSelected(object sender, SelectionChangedEventArgs e)
	{
		if (sender is not ListBox list)
			return;

		if (list.SelectedItem is not CustomizationItem item)
		{
			textBoxDetails.Text = string.Empty;
			buttonAddToPrompt.IsEnabled = false;
			return;
		}

		buttonAddToPrompt.IsEnabled = true;
		ShowDetails(item);
	}

	/// <summary>
	/// The compact marker that names one item to the model. Prompt and
	/// instruction files are attached when the marker is sent.
	/// </summary>
	internal string ReferenceFor(CustomizationItem item) => TypeNameFor(item) switch
	{
		"Skill" => PromptReferenceMarkers.SkillMarker(item.Name),
		"Agent" => $"Use the \"{item.Name}\" agent.",
		"MCP Server" => $"Use the \"{item.Name}\" MCP server.",
		_ => PromptReferenceMarkers.FileMarker(Path.GetFileName(item.FilePath)),
	};

	/// <summary>
	/// Stages a mention of the selected item for the prompt box. The
	/// same item twice is still one mention: repeating it would only
	/// make the prompt longer.
	/// </summary>
	private void OnAddToPrompt(object sender, RoutedEventArgs e)
	{
		if (SelectedFoundItem() is not { } item) return;
		var reference = ReferenceFor(item);
		if (!_promptReferences.Contains(reference, StringComparer.Ordinal))
			_promptReferences.Add(reference);
		buttonAddToPrompt.Content = _promptReferences.Count == 1
			? "Added To Prompt" : $"Added To Prompt ({_promptReferences.Count})";
	}

	/// <summary>The item selected on whichever tab is showing.</summary>
	private CustomizationItem? SelectedFoundItem()
	{
		foreach (var list in new[] { listPrompts, listAgents, listSkills, listInstructions, listMcpServers })
			if (list.IsVisible && list.SelectedItem is CustomizationItem item)
				return item;
		return null;
	}

	private void ShowDetails(CustomizationItem item)
	{
		var lines = new List<string>
		{
			$"Type: {TypeNameFor(item)}",
			$"Name: {item.Name}",
			$"Path: {item.FilePath}",
			$"State: {(item.Enabled ? "Enabled" : "Disabled")}",
		};

		if (_library.McpServers.ContainsKey(CustomizationService.MakeKey(item.FilePath, item.Element ?? string.Empty)))
			AppendMcpDetails(lines, item);
		else
			AppendFrontMatter(lines, item.FilePath);

		textBoxDetails.Text = string.Join(Environment.NewLine, lines);
	}

	/// <summary>
	/// Adds the server's own definition from the MCP config file. Values
	/// under env and headers are credential material, so McpConfig renders
	/// only their key names.
	/// </summary>
	private static void AppendMcpDetails(List<string> lines, CustomizationItem item)
	{
		if (item.Element is null)
			return;

		var fields = McpConfig.ReadServerFields(item.FilePath, item.Element);
		if (fields is null || fields.Count == 0)
			return;

		string? description = null;
		var others = new List<KeyValuePair<string, string>>();
		foreach (var field in fields)
		{
			if (description is null && string.Equals(field.Key, "description", StringComparison.OrdinalIgnoreCase))
				description = field.Value;
			else
				others.Add(field);
		}

		if (!string.IsNullOrWhiteSpace(description))
		{
			lines.Add(string.Empty);
			lines.Add($"Description: {description}");
		}

		lines.Add(string.Empty);
		lines.Add("Metadata:");
		foreach (var (key, value) in others)
			lines.Add($"  {key}: {value}");
	}

	/// <summary>
	/// Adds the item's description and remaining front matter fields to
	/// the details lines. Sections are omitted when the file has no
	/// front matter or cannot be read.
	/// </summary>
	private static void AppendFrontMatter(List<string> lines, string filePath)
	{
		var frontMatter = FrontMatter.Read(filePath);
		if (frontMatter.Fields.Count == 0)
			return;

		string? description = frontMatter.Get("description");
		if (!string.IsNullOrWhiteSpace(description))
		{
			lines.Add(string.Empty);
			lines.Add($"Description: {description}");
		}

		var others = frontMatter.Fields
			.Where(f => !string.Equals(f.Key, "description", StringComparison.OrdinalIgnoreCase))
			.ToList();
		if (others.Count > 0)
		{
			lines.Add(string.Empty);
			lines.Add("Metadata:");
			foreach (var (key, value) in others)
				lines.Add($"  {key}: {value}");
		}
	}

	private string TypeNameFor(CustomizationItem item)
	{
		var library = _library;
		if (library.Prompts.ContainsKey(item.FilePath)) return "Prompt";
		if (library.Agents.ContainsKey(item.FilePath)) return "Agent";
		if (library.Skills.ContainsKey(item.FilePath)) return "Skill";
		if (library.Instructions.ContainsKey(item.FilePath)) return "Instruction";
		if (library.McpServers.ContainsKey(CustomizationService.MakeKey(item.FilePath, item.Element ?? string.Empty))) return "MCP Server";
		return "Item";
	}

	// ------------------------------------------------------------------ results

	private void OnOk(object sender, RoutedEventArgs e)
	{
		// The commit below is the save; the close guard must not ask twice.
		bool changed = _dirty;
		_dirty = false;

		var settings = Settings.Load();
		settings.CustomizationFolders = new List<string>(_folders);
		settings.Save();

		// Apply the staged enabled flags, then rebuild from the (possibly
		// changed) roots; the rescan carries the committed flags across by
		// key so nothing the user just chose is lost.
		CustomizationService.Commit(_library);
		CustomizationService.Rescan(_workspaceFolder);

		// The rescan replaces the library the next session reads, but a
		// session already running took its instructions, skills and agent
		// modes into itself when it started and keeps them. Say so rather
		// than leave the user looking for the effect of a change that is
		// real but not yet in force.
		if (changed)
		{
			MessageDialog.Ok(this,
				"Customization changes take effect on the next session.",
				"Customization");
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
	/// nothing is ever written on the way out. Cancel means no net change.
	/// </summary>
	protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
	{
		if (_dirty && !_confirming)
		{
			_confirming = true;
			try
			{
				if (MessageBox.Show(this, "You have unsaved changes. Discard them?",
					"Customization", MessageBoxButton.YesNo, MessageBoxImage.Question)
					!= MessageBoxResult.Yes)
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

	// ------------------------------------------------------------------ options file

	/// <summary>
	/// Saves the staged item options (enabled flags) to a file the user
	/// picks. The folder list is not written: those are application-level
	/// globals.
	/// </summary>
	private void OnSaveOptions(object sender, RoutedEventArgs e)
	{
		var dialog = new Microsoft.Win32.SaveFileDialog
		{
			Title = "Save Customization Options",
			Filter = "Customization options (*.json)|*.json|All files (*.*)|*.*",
			DefaultExt = ".json",
			FileName = "customization-options.json",
		};

		if (dialog.ShowDialog(this) != true)
			return;

		try
		{
			CustomizationService.SaveTo(_library, dialog.FileName);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
			or System.Security.SecurityException)
		{
			MessageBox.Show(this, $"Could not save the options file:\r\n{ex.Message}",
				"Customization", MessageBoxButton.OK, MessageBoxImage.Warning);
		}
	}

	private void OnLoadOptions(object sender, RoutedEventArgs e)
	{
		var dialog = new Microsoft.Win32.OpenFileDialog
		{
			Title = "Load Customization Options",
			Filter = "Customization options (*.json)|*.json|All files (*.*)|*.*",
			CheckFileExists = true,
		};

		if (dialog.ShowDialog(this) != true)
			return;

		CustomizationLibrary loaded;
		try
		{
			loaded = CustomizationService.LoadFrom(dialog.FileName);
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
			or System.Security.SecurityException or System.Text.Json.JsonException)
		{
			MessageBox.Show(this, $"Could not read the options file:\r\n{ex.Message}",
				"Customization", MessageBoxButton.OK, MessageBoxImage.Warning);
			return;
		}

		ApplyLoadedOptions(loaded);
	}

	/// <summary>
	/// Folds a loaded options file into the staged state: folders the
	/// loaded paths live in are added when missing (and exist on disk),
	/// the staged library is rebuilt so those folders' items appear, and
	/// finally the loaded enabled flags are transferred onto every item
	/// matched by key.
	/// </summary>
	private void ApplyLoadedOptions(CustomizationLibrary loaded)
	{
		string? workspace = Settings.Load().LastWorkspacePath;
		var roots = new HashSet<string>(
			CustomizationService.GetSearchRoots(workspace, _folders), StringComparer.OrdinalIgnoreCase);

		bool foldersAdded = false;
		foreach (var item in LoadedItems(loaded))
		{
			string? root = CustomizationService.RootFolderFor(item.FilePath);
			if (root is not null && Directory.Exists(root) && roots.Add(root))
			{
				_folders.Add(root);
				foldersAdded = true;
			}
		}

		if (foldersAdded)
			ReloadList();

		// Rebuild first so items from newly added folders exist, then let
		// the loaded flags win over the staged ones for matching keys.
		_library = CustomizationService.Preview(workspace, _folders, _library);
		TransferEnabledInto(loaded.Prompts, _library.Prompts);
		TransferEnabledInto(loaded.Agents, _library.Agents);
		TransferEnabledInto(loaded.Skills, _library.Skills);
		TransferEnabledInto(loaded.Instructions, _library.Instructions);
		TransferEnabledInto(loaded.McpServers, _library.McpServers);
		RefreshFoundItems();
		MarkDirty();
	}

	private static IEnumerable<CustomizationItem> LoadedItems(CustomizationLibrary loaded) =>
		loaded.Prompts.Values
			.Concat(loaded.Agents.Values)
			.Concat(loaded.Skills.Values)
			.Concat(loaded.Instructions.Values)
			.Concat(loaded.McpServers.Values);

	private static void TransferEnabledInto(
		Dictionary<string, CustomizationItem> loaded,
		Dictionary<string, CustomizationItem> staged)
	{
		foreach (var (key, item) in loaded)
		{
			if (staged.TryGetValue(key, out var target))
				target.Enabled = item.Enabled;
		}
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
