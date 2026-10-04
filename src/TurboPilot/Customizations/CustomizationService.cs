using System.IO;
using System.Text.Json;
using TurboPilot.Dialogs;

namespace TurboPilot.Customizations;

/// <summary>
/// Collects prompts, custom agents, skills and instructions from the
/// customization search roots and keeps the resulting lists in memory and
/// on disk.
///
/// Search roots, following the CLI convention plus our own:
///   1. COPILOT_HOME, or %USERPROFILE%\.copilot when it is unset
///   2. The user-defined customization folders (order not significant)
///   3. The workspace .github folder
///
/// Within each root the standard layout is scanned:
///   prompts/*.md, agents/*.md, skills/[name]/SKILL.md,
///   the canonical instruction file, instructions/**/*.instructions.md and
///   *.mcp.json
/// (prompts, agents and MCP files are top-level; a skill is its whole folder,
/// keyed by its SKILL.md, and each server inside an MCP config file is its own
/// entry, keyed by the file path and the server name).
///
/// The CLI's standard workspace agent files and the instruction files in its
/// custom instruction directories are collected as instructions as well.
///
/// A rescan builds fresh lists but transfers the enabled flag from the
/// previous lists for items found at the same file path, so user toggles
/// survive reordering and rescans. The lists persist to disk between runs.
/// Callers rescan when a session starts or when the customization folder
/// list changes.
/// </summary>
public static class CustomizationService
{
	private const string LibraryFileName = "customizations.json";

	private const string CanonicalInstructionsFileName = "copilot-instructions.md";

	private const string InstructionsSuffix = ".instructions.md";

	private static readonly string[] AgentInstructionPaths =
	[
		"AGENTS.md",
		"CLAUDE.md",
		"GEMINI.md",
		Path.Combine(".claude", "CLAUDE.md"),
	];

	private static readonly JsonSerializerOptions SerializerOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	private static string LibraryPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"TurboPilot",
		LibraryFileName);

	/// <summary>
	/// The most recently scanned (or loaded from disk) lists.
	/// </summary>
	public static CustomizationLibrary Current { get; private set; } = Load();

	/// <summary>
	/// Rebuilds all three lists from the search roots, transfers the
	/// enabled flag from the previous lists for items at the same path,
	/// persists the result and makes it current.
	/// </summary>
	/// <param name="workspaceFolder">
	/// The session workspace whose .github folder is searched last.
	/// Null or missing folders are skipped.
	/// </param>
	public static CustomizationLibrary Rescan(string? workspaceFolder)
	{
		CustomizationLibrary library = Preview(
			workspaceFolder, Settings.Load().CustomizationFolders, Current);

		Save(library);
		Current = library;
		return library;
	}

	/// <summary>
	/// Builds a library from the given folder list (plus the personal and
	/// workspace roots) and transfers enabled flags from
	/// <paramref name="previous"/> by key, without touching the live lists
	/// or disk. Used by the Customization dialog to refresh its working
	/// copy after staged folder changes or an options load.
	/// </summary>
	public static CustomizationLibrary Preview(string? workspaceFolder,
		IReadOnlyList<string> folders, CustomizationLibrary previous)
	{
		CustomizationLibrary library = Collect(
			GetSearchRoots(workspaceFolder, folders), workspaceFolder);

		TransferEnabled(previous.Prompts, library.Prompts);
		TransferEnabled(previous.Agents, library.Agents);
		TransferEnabled(previous.Skills, library.Skills);
		TransferEnabled(previous.Instructions, library.Instructions);
		TransferEnabled(previous.McpServers, library.McpServers);

		return library;
	}

	/// <summary>
	/// Keeps a resumed session's TurboPilot-specific customizations while
	/// replacing its standard CLI instruction subset with the files
	/// discovered now.
	/// </summary>
	/// <param name="saved">The customization snapshot stored with the session.</param>
	/// <param name="current">The customization library discovered for the resume.</param>
	/// <param name="workspaceFolder">The workspace used to classify legacy snapshots.</param>
	/// <returns>A copy of the saved library containing the current standard instructions.</returns>
	public static CustomizationLibrary RefreshStandardInstructions(
		CustomizationLibrary saved, CustomizationLibrary current, string? workspaceFolder)
	{
		var merged = saved.Clone();
		foreach (string path in merged.Instructions
			.Where(pair => pair.Value.IsCliStandard
				|| IsStandardInstructionPath(pair.Key, workspaceFolder))
			.Select(pair => pair.Key)
			.ToList())
			merged.Instructions.Remove(path);

		foreach (var (path, item) in current.Instructions
			.Where(pair => pair.Value.IsCliStandard))
			merged.Instructions[path] = item.Clone();

		return merged;
	}

	/// <summary>
	/// Adopts a working copy as the live library and persists it, without
	/// rescanning the roots. A following <see cref="Rescan"/> carries the
	/// copy's enabled flags onto whatever is found on disk.
	/// </summary>
	public static void Commit(CustomizationLibrary workingCopy)
	{
		Current = workingCopy;
		Save(workingCopy);
	}

	// ------------------------------------------------------------------ scan

	private static CustomizationLibrary Collect(IEnumerable<string> roots, string? workspaceFolder)
	{
		var rootList = roots.ToList();
		var standardRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
		{
			SharedConfigurationHome(),
		};
		if (!string.IsNullOrWhiteSpace(workspaceFolder))
			standardRoots.Add(Path.Combine(workspaceFolder.Trim(), ".github"));

		var library = new CustomizationLibrary();
		var usedPromptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var usedAgentNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var usedSkillNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var usedInstructionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var usedMcpNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		foreach (string root in rootList)
		{
			CollectPrompts(root, library, usedPromptNames);
			CollectAgents(root, library, usedAgentNames);
			CollectSkills(root, library, usedSkillNames);
			CollectInstructions(root, library, usedInstructionNames, standardRoots.Contains(root));
			CollectMcpServers(root, library, usedMcpNames);
		}

		CollectCliInstructionDirectories(library, usedInstructionNames);
		CollectWorkspaceAgentInstructions(workspaceFolder, library, usedInstructionNames);

		return library;
	}

	/// <summary>
	/// The roots a scan covers: the personal folder, the given
	/// customization folders, then the workspace .github folder.
	/// </summary>
	public static IEnumerable<string> GetSearchRoots(string? workspaceFolder,
		IReadOnlyList<string> folders)
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		string personal = SharedConfigurationHome();
		if (AddRoot(seen, personal))
			yield return personal;

		foreach (string folder in folders)
		{
			string trimmed = folder.Trim();
			if (AddRoot(seen, trimmed))
				yield return trimmed;
		}

		if (!string.IsNullOrWhiteSpace(workspaceFolder))
		{
			string github = Path.Combine(workspaceFolder.Trim(), ".github");
			if (AddRoot(seen, github))
				yield return github;
		}
	}

	private static bool AddRoot(HashSet<string> seen, string path) =>
		!string.IsNullOrWhiteSpace(path) && Directory.Exists(path) && seen.Add(path);

	private static string SharedConfigurationHome()
	{
		string? configured = Environment.GetEnvironmentVariable("COPILOT_HOME");
		return string.IsNullOrWhiteSpace(configured)
			? DefaultSharedConfigurationHome()
			: Environment.ExpandEnvironmentVariables(configured.Trim().Trim('"'));
	}

	private static string DefaultSharedConfigurationHome() => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".copilot");

	private static void CollectPrompts(string root, CustomizationLibrary library, HashSet<string> usedNames)
	{
		foreach (string file in EnumerateFiles(Path.Combine(root, "prompts"), "*.md"))
		{
			string name = MakeUniqueName(Path.GetFileNameWithoutExtension(file), usedNames);
			library.Prompts[file] = new CustomizationItem { FilePath = file, Name = name };
		}
	}

	private static void CollectAgents(string root, CustomizationLibrary library, HashSet<string> usedNames)
	{
		foreach (string file in EnumerateFiles(Path.Combine(root, "agents"), "*.md"))
		{
			string name = MakeUniqueName(Path.GetFileNameWithoutExtension(file), usedNames);
			library.Agents[file] = new CustomizationItem { FilePath = file, Name = name };
		}
	}

	private static void CollectSkills(string root, CustomizationLibrary library, HashSet<string> usedNames)
	{
		string skillsDir = Path.Combine(root, "skills");
		if (!Directory.Exists(skillsDir))
			return;

		foreach (string folder in Directory.GetDirectories(skillsDir)
			.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
		{
			string skillFile = Path.Combine(folder, "SKILL.md");
			if (!File.Exists(skillFile))
				continue;

			string name = MakeUniqueName(Path.GetFileName(folder), usedNames);
			library.Skills[skillFile] = new CustomizationItem { FilePath = skillFile, Name = name };
		}
	}

	private static void CollectInstructions(string root, CustomizationLibrary library,
		HashSet<string> usedNames, bool isCliStandard)
	{
		AddInstruction(Path.Combine(root, CanonicalInstructionsFileName),
			library, usedNames, isCliStandard);
		foreach (string file in EnumerateFiles(
			Path.Combine(root, "instructions"), "*" + InstructionsSuffix, SearchOption.AllDirectories))
			AddInstruction(file, library, usedNames, isCliStandard);
	}

	private static void CollectCliInstructionDirectories(
		CustomizationLibrary library, HashSet<string> usedNames)
	{
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (string directory in CliInstructionDirectories())
		{
			if (!Directory.Exists(directory) || !seen.Add(directory))
				continue;

			foreach (string file in EnumerateFiles(
				directory, "*" + InstructionsSuffix, SearchOption.AllDirectories))
				AddInstruction(file, library, usedNames, isCliStandard: true);
			foreach (string file in EnumerateFiles(
				directory, "AGENTS.md", SearchOption.AllDirectories))
				AddInstruction(file, library, usedNames, isCliStandard: true);
		}
	}

	private static IEnumerable<string> CliInstructionDirectories()
	{
		string? configured = Environment.GetEnvironmentVariable("COPILOT_CUSTOM_INSTRUCTIONS_DIRS");
		if (string.IsNullOrWhiteSpace(configured))
			yield break;

		foreach (string value in configured.Split(','))
		{
			string path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
			if (!string.IsNullOrWhiteSpace(path))
				yield return path;
		}
	}

	private static void CollectWorkspaceAgentInstructions(string? workspaceFolder,
		CustomizationLibrary library, HashSet<string> usedNames)
	{
		var directories = StandardWorkspaceDirectories(workspaceFolder);
		foreach (string directory in directories)
		{
			AddInstruction(
				Path.Combine(directory, ".github", CanonicalInstructionsFileName),
				library, usedNames, isCliStandard: true);
			foreach (string relativePath in AgentInstructionPaths)
				AddInstruction(Path.Combine(directory, relativePath),
					library, usedNames, isCliStandard: true);
		}

		var endpoints = directories
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();
		IEnumerable<string> modularDirectories = endpoints.Count <= 1
			? endpoints
			: [endpoints[0], endpoints[^1]];
		foreach (string directory in modularDirectories)
		{
			foreach (string file in EnumerateFiles(
				Path.Combine(directory, ".github", "instructions"),
				"*" + InstructionsSuffix, SearchOption.AllDirectories))
				AddInstruction(file, library, usedNames, isCliStandard: true);
		}
	}

	private static IReadOnlyList<string> StandardWorkspaceDirectories(string? workspaceFolder)
	{
		if (string.IsNullOrWhiteSpace(workspaceFolder) || !Directory.Exists(workspaceFolder))
			return [];

		string workspace = Path.GetFullPath(workspaceFolder.Trim());
		string repository = workspace;
		for (var current = new DirectoryInfo(workspace); current is not null; current = current.Parent)
		{
			if (Directory.Exists(Path.Combine(current.FullName, ".git"))
				|| File.Exists(Path.Combine(current.FullName, ".git")))
			{
				repository = current.FullName;
				break;
			}
		}

		var directories = new List<string>();
		for (var current = new DirectoryInfo(workspace); current is not null; current = current.Parent)
		{
			directories.Add(current.FullName);
			if (string.Equals(current.FullName, repository, StringComparison.OrdinalIgnoreCase))
				break;
		}
		directories.Reverse();
		return directories;
	}

	private static bool IsStandardInstructionPath(string path, string? workspaceFolder)
	{
		string fileName = Path.GetFileName(path);
		foreach (string home in new[] { SharedConfigurationHome(), DefaultSharedConfigurationHome() }
			.Distinct(StringComparer.OrdinalIgnoreCase))
		{
			if (PathEquals(path, Path.Combine(home, CanonicalInstructionsFileName))
				|| IsModularInstructionWithin(path, Path.Combine(home, "instructions")))
				return true;
		}

		foreach (string directory in CliInstructionDirectories())
		{
			if (IsPathWithin(path, directory)
				&& (fileName.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
					|| fileName.EndsWith(InstructionsSuffix, StringComparison.OrdinalIgnoreCase)))
				return true;
		}

		var directories = StandardWorkspaceDirectories(workspaceFolder);
		foreach (string directory in directories)
		{
			if (PathEquals(path, Path.Combine(directory, ".github", CanonicalInstructionsFileName))
				|| AgentInstructionPaths.Any(relativePath =>
					PathEquals(path, Path.Combine(directory, relativePath))))
				return true;
		}

		if (directories.Count > 0)
		{
			IEnumerable<string> endpoints = directories.Count == 1
				? directories
				: new[] { directories[0], directories[^1] };
			if (endpoints.Any(directory =>
				IsModularInstructionWithin(path, Path.Combine(directory, ".github", "instructions"))))
				return true;
		}

		return false;
	}

	private static bool IsModularInstructionWithin(string path, string directory) =>
		Path.GetFileName(path).EndsWith(InstructionsSuffix, StringComparison.OrdinalIgnoreCase)
		&& IsPathWithin(path, directory);

	private static bool PathEquals(string left, string right)
	{
		try
		{
			return string.Equals(
				Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static bool IsPathWithin(string path, string directory)
	{
		try
		{
			string fullPath = Path.GetFullPath(path);
			string prefix = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory))
				+ Path.DirectorySeparatorChar;
			return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return false;
		}
	}

	private static void AddInstruction(string file, CustomizationLibrary library,
		HashSet<string> usedNames, bool isCliStandard = false)
	{
		if (!File.Exists(file))
			return;
		if (library.Instructions.TryGetValue(file, out var existing))
		{
			existing.IsCliStandard |= isCliStandard;
			return;
		}

		string name;
		string fileName = Path.GetFileName(file);
		if (fileName.EndsWith(InstructionsSuffix, StringComparison.OrdinalIgnoreCase))
			name = fileName[..^InstructionsSuffix.Length];
		else
			name = Path.GetFileNameWithoutExtension(fileName);

		library.Instructions[file] = new CustomizationItem
		{
			FilePath = file,
			Name = MakeUniqueName(name, usedNames),
			IsCliStandard = isCliStandard,
		};
	}

	private static void CollectMcpServers(string root, CustomizationLibrary library, HashSet<string> usedNames)
	{
		foreach (string file in EnumerateFiles(root, "*.mcp.json"))
		{
			foreach (string serverName in McpConfig.ReadServerNames(file))
			{
				string key = MakeKey(file, serverName);
				string name = MakeUniqueName(serverName, usedNames);
				library.McpServers[key] = new CustomizationItem
				{
					FilePath = file,
					Element = serverName,
					Name = name,
				};
			}
		}
	}

	/// <summary>
	/// Map key for an item defined inside a shared file: the file path
	/// plus the element name, so several servers in one config each get
	/// their own enabled state and rescan identity.
	/// </summary>
	public static string MakeKey(string filePath, string element) => $"{filePath}#{element}";

	private static IEnumerable<string> EnumerateFiles(string directory, string searchPattern,
		SearchOption searchOption = SearchOption.TopDirectoryOnly) =>
		Directory.Exists(directory)
			? Directory.GetFiles(directory, searchPattern, searchOption)
				.OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
			: [];

	/// <summary>
	/// Returns a name not yet in <paramref name="usedNames"/>, appending an
	/// incrementing suffix on collision (base, base_2, base_3, ...).
	/// The chosen name is added to the set.
	/// </summary>
	private static string MakeUniqueName(string baseName, HashSet<string> usedNames)
	{
		if (usedNames.Add(baseName))
			return baseName;

		int suffix = 2;
		while (!usedNames.Add($"{baseName}_{suffix}"))
			suffix++;

		return $"{baseName}_{suffix}";
	}

	// ------------------------------------------------------------------ state

	private static void TransferEnabled(
		Dictionary<string, CustomizationItem> previous,
		Dictionary<string, CustomizationItem> current)
	{
		foreach (var (path, item) in current)
		{
			if (previous.TryGetValue(path, out var old))
				item.Enabled = old.Enabled;
		}
	}

	/// <summary>
	/// Writes the library to disk. Failures are swallowed: the in-memory
	/// lists stay authoritative for the running session.
	/// </summary>
	public static void Save(CustomizationLibrary library)
	{
		try
		{
			string? directory = Path.GetDirectoryName(LibraryPath);
			if (!string.IsNullOrEmpty(directory))
				Directory.CreateDirectory(directory);

			File.WriteAllText(LibraryPath, JsonSerializer.Serialize(library, SerializerOptions));
		}
		catch (IOException)
		{
		}
		catch (UnauthorizedAccessException)
		{
		}
	}

	/// <summary>
	/// Loads the persisted library. Returns empty lists when the file is
	/// missing or unreadable.
	/// </summary>
	public static CustomizationLibrary Load()
	{
		try
		{
			if (!File.Exists(LibraryPath))
				return new CustomizationLibrary();

			var library = JsonSerializer.Deserialize<CustomizationLibrary>(
				File.ReadAllText(LibraryPath), SerializerOptions);
			return RebuildKeys(library ?? new CustomizationLibrary());
		}
		catch (IOException)
		{
			return new CustomizationLibrary();
		}
		catch (UnauthorizedAccessException)
		{
			return new CustomizationLibrary();
		}
		catch (JsonException)
		{
			return new CustomizationLibrary();
		}
	}

	/// <summary>
	/// Writes a library to an arbitrary file, for the dialog's Save
	/// Options. Throws to the caller so it can report the failure.
	/// </summary>
	public static void SaveTo(CustomizationLibrary library, string filePath) =>
		File.WriteAllText(filePath, JsonSerializer.Serialize(library, SerializerOptions));

	/// <summary>
	/// Reads a library saved by <see cref="SaveTo"/>. Throws to the caller
	/// so it can report the failure.
	/// </summary>
	public static CustomizationLibrary LoadFrom(string filePath)
	{
		var library = JsonSerializer.Deserialize<CustomizationLibrary>(
			File.ReadAllText(filePath), SerializerOptions);
		return RebuildKeys(library ?? new CustomizationLibrary());
	}

	/// <summary>
	/// Derives the search root an item path came from, following the
	/// scanned layout (root/prompts, root/agents, root/instructions,
	/// root's canonical instruction file, root/skills/[name]/SKILL.md,
	/// root/*.mcp.json). Returns null when the path does not fit the layout.
	/// </summary>
	public static string? RootFolderFor(string itemPath)
	{
		string? directory = Path.GetDirectoryName(itemPath);
		if (directory is null)
			return null;

		string fileName = Path.GetFileName(itemPath);
		string parentName = Path.GetFileName(directory);
		if (fileName.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase)
			&& parentName.Equals(".claude", StringComparison.OrdinalIgnoreCase))
			return Path.GetDirectoryName(directory);

		if (fileName.Equals(CanonicalInstructionsFileName, StringComparison.OrdinalIgnoreCase)
			|| fileName.Equals("AGENTS.md", StringComparison.OrdinalIgnoreCase)
			|| fileName.Equals("CLAUDE.md", StringComparison.OrdinalIgnoreCase)
			|| fileName.Equals("GEMINI.md", StringComparison.OrdinalIgnoreCase))
			return directory;

		if (fileName.Equals("SKILL.md", StringComparison.OrdinalIgnoreCase))
		{
			// root/skills/[name]/SKILL.md
			string? skills = Path.GetDirectoryName(directory);
			string? root = Path.GetDirectoryName(skills);
			return skills is not null
				&& Path.GetFileName(skills).Equals("skills", StringComparison.OrdinalIgnoreCase)
				? root
				: null;
		}

		if (parentName.Equals("prompts", StringComparison.OrdinalIgnoreCase)
			|| parentName.Equals("agents", StringComparison.OrdinalIgnoreCase))
			return Path.GetDirectoryName(directory);

		if (fileName.EndsWith(InstructionsSuffix, StringComparison.OrdinalIgnoreCase))
		{
			for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
			{
				if (current.Name.Equals("instructions", StringComparison.OrdinalIgnoreCase))
					return current.Parent?.FullName;
			}
		}

		if (fileName.EndsWith(".mcp.json", StringComparison.OrdinalIgnoreCase))
			return directory;

		return null;
	}

	// Deserialized dictionaries get the default ordinal comparer; rebuild
	// them so path lookups stay case-insensitive like the scanner's.
	private static CustomizationLibrary RebuildKeys(CustomizationLibrary library)
	{
		library.Prompts = new(library.Prompts ?? new(), CustomizationLibrary.PathComparer);
		library.Agents = new(library.Agents ?? new(), CustomizationLibrary.PathComparer);
		library.Skills = new(library.Skills ?? new(), CustomizationLibrary.PathComparer);
		library.Instructions = new(library.Instructions ?? new(), CustomizationLibrary.PathComparer);
		library.McpServers = new(library.McpServers ?? new(), CustomizationLibrary.PathComparer);
		return library;
	}
}
