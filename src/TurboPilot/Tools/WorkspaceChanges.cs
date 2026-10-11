using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;

namespace TurboPilot.Tools;

/// <summary>One file the agent touched, and how.</summary>
internal sealed record WorkspaceChange(string Path, string Kind)
{
	/// <summary>The letter the card shows, in the shape a diff uses.</summary>
	internal string Mark => Kind switch
	{
		"added" => "+",
		"deleted" => "-",
		_ => "~",
	};
}

/// <summary>Cheap file state used to find possible loose-file changes.</summary>
internal readonly record struct FileStamp(long Length, long LastWriteTimeUtcTicks);

/// <summary>Verified file content at one point in time.</summary>
internal sealed record FileContentState(bool Exists, string Hash);

/// <summary>
/// One repository a turn is measured against: its top folder, where
/// that sits in the workspace, and the commit object holding its files
/// as they were when the turn began.
/// </summary>
internal sealed record RepositoryAnchor(string Root, string Prefix, string Commit, IReadOnlySet<string> Untracked)
{
	/// <summary>
	/// A listed path as the repository names it. Listed paths are
	/// relative to the workspace, which is the repository's parent when
	/// the workspace is a folder holding checkouts.
	/// </summary>
	internal string Inner(string path) => path[Prefix.Length..];
}

/// <summary>
/// Where the workspace stood when a turn began, so what the turn did to
/// it can be worked out when the turn ends.
/// </summary>
internal sealed class ChangeAnchor(
	string workspace,
	IReadOnlyList<RepositoryAnchor> repositories,
	IReadOnlyDictionary<string, FileStamp>? stamps,
	IReadOnlySet<string>? completeFolders = null,
	int scanLimit = WorkspaceChanges.MaxFiles)
{
	internal string Workspace { get; } = workspace;

	/// <summary>
	/// The repositories whose history the turn is measured against: the
	/// workspace's own, or the ones found inside it.
	/// </summary>
	internal IReadOnlyList<RepositoryAnchor> Repositories { get; } = repositories;

	/// <summary>
	/// Size and write time for each file in no repository, which is all
	/// that can be collected cheaply across a large tree. Null when the
	/// workspace is itself a repository.
	/// </summary>
	internal IReadOnlyDictionary<string, FileStamp>? Stamps { get; } = stamps;

	/// <summary>
	/// Folders whose direct file list was captured in full. Null is used
	/// by synthetic anchors in tests and means the supplied list is
	/// complete.
	/// </summary>
	internal IReadOnlySet<string>? CompleteFolders { get; } = completeFolders;

	/// <summary>The loose-file scan bound used for both sides.</summary>
	internal int ScanLimit { get; } = scanLimit;

	/// <summary>
	/// Content captured before an approved built-in file write. These
	/// paths can be reconciled by hash without hashing the whole tree.
	/// </summary>
	internal ConcurrentDictionary<string, FileContentState> WriteBaselines { get; } =
		new(StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// The repository holding a listed path, or null when the file is in
	/// none. Repositories found inside a workspace never nest, because
	/// the search does not look inside one it has found, so the first
	/// match is the only one.
	/// </summary>
	internal RepositoryAnchor? RepositoryFor(string path) =>
		Repositories.FirstOrDefault(repository => path.StartsWith(repository.Prefix, StringComparison.OrdinalIgnoreCase));

	/// <summary>
	/// Whether an earlier copy of the file was kept, so it can be
	/// diffed, compared and put back.
	/// </summary>
	internal bool CanDiff(string path) => RepositoryFor(path) is not null;
}

/// <summary>
/// What a turn did to the files on disk.
///
/// The list is taken from the workspace itself rather than from the
/// tools the agent reported running. A tool can write a file the user
/// was never told about, and a reported edit can fail; only the disk
/// knows what actually happened.
///
/// In a repository the anchor is a commit object built by `git stash
/// create`, which records the working tree without altering it or the
/// index. Diffing against that gives exactly what the turn changed,
/// even for a file that was already modified before the turn began.
///
/// A workspace that is not a repository is often the folder above one
/// or more checkouts, so the repositories inside it are found and each
/// is anchored the same way: a file in any of them keeps its history.
/// Files in no repository use a bounded metadata snapshot to find
/// candidates. A capped scan never treats an unseen file as created or
/// deleted, and approved built-in writes are reconciled by content hash.
/// </summary>
internal static class WorkspaceChanges
{
	internal const int MaxFiles = 20_000;
	internal const int MaxDiffLines = 400;

	/// <summary>
	/// Repositories anchored inside one workspace. Each costs a few git
	/// runs at every turn, so a folder of many checkouts is bounded, and
	/// the ones past the bound are watched by stamp.
	/// </summary>
	internal const int MaxRepositories = 16;
	private const int GitRunsAtOnce = 4;
	private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
	private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
		{ ".git", ".vs", ".vscode", ".idea", "bin", "obj", "node_modules", "packages", "TestResults" };

	/// <summary>
	/// Runs a git command in a folder, the workspace or a repository
	/// inside it, or returns null.
	/// </summary>
	internal static Func<string, string, string?>? Runner { get; set; }

	/// <summary>
	/// Records the state of the workspace before a turn runs. Returns
	/// null when there is no workspace to watch.
	/// </summary>
	internal static ChangeAnchor? Begin(string? workspace) => Begin(workspace, MaxFiles);

	/// <summary>
	/// Records the state with a specific loose-file bound. The overload
	/// exists so cutoff behavior can be covered without creating tens of
	/// thousands of fixture files.
	/// </summary>
	internal static ChangeAnchor? Begin(string? workspace, int scanLimit)
	{
		if (string.IsNullOrWhiteSpace(workspace) || !Directory.Exists(workspace)) return null;
		if (scanLimit <= 0) throw new ArgumentOutOfRangeException(nameof(scanLimit));
		workspace = Path.GetFullPath(workspace);

		if (Git(workspace, "rev-parse --is-inside-work-tree") is "true" && Anchor(workspace, workspace) is { } own)
			return new ChangeAnchor(workspace, [own], null, scanLimit: scanLimit);

		var found = new List<string>();
		var snapshot = Snapshot(workspace, Roots([]), found, scanLimit);
		found.Sort(StringComparer.OrdinalIgnoreCase);
		var repositories = found.AsParallel().AsOrdered().WithDegreeOfParallelism(GitRunsAtOnce)
			.Select(root => Anchor(workspace, root)).OfType<RepositoryAnchor>().ToList();
		// A folder that looked like a repository but has nothing to
		// anchor to, such as one before its first commit, is watched by
		// stamp like any other folder.
		if (repositories.Count < found.Count)
			snapshot = Snapshot(workspace, Roots(repositories), null, scanLimit);
		return new ChangeAnchor(workspace, repositories, snapshot.Files, snapshot.CompleteFolders, scanLimit);
	}

	/// <summary>
	/// Records one repository as it stands. Null when there is no commit
	/// to diff from, as before the first one is made.
	/// </summary>
	private static RepositoryAnchor? Anchor(string workspace, string root)
	{
		// An empty result means the tree matched the index and the
		// head, so the head itself is the right thing to diff from.
		var stash = Git(root, "stash create");
		var commit = string.IsNullOrWhiteSpace(stash) ? Git(root, "rev-parse --verify -q HEAD") : stash;
		if (string.IsNullOrWhiteSpace(commit)) return null;
		var prefix = string.Equals(root, workspace, StringComparison.OrdinalIgnoreCase)
			? string.Empty
			: Path.GetRelativePath(workspace, root).Replace('\\', '/') + "/";
		return new RepositoryAnchor(root, prefix, commit.Trim(), Untracked(root));
	}

	/// <summary>
	/// What has changed since the anchor was taken, newest listing
	/// first. Deletions are included: a file the agent removed is the
	/// change a user most wants to be told about.
	/// </summary>
	internal static IReadOnlyList<WorkspaceChange> Since(ChangeAnchor? anchor)
	{
		if (anchor is null) return [];
		var changes = anchor.Stamps is null ? new List<WorkspaceChange>() : StampChanges(anchor);
		changes.AddRange(anchor.Repositories.AsParallel().AsOrdered().WithDegreeOfParallelism(GitRunsAtOnce)
			.SelectMany(GitChanges));
		return changes;
	}

	/// <summary>
	/// What changed in one repository, named from the workspace so the
	/// list reads the same whichever repository a file is in.
	/// </summary>
	private static List<WorkspaceChange> GitChanges(RepositoryAnchor repository)
	{
		var changes = new List<WorkspaceChange>();
		var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		// Git names a change from the top of the checkout, but reads a
		// path handed to it from the folder it runs in. A workspace
		// below the top would be listed paths it cannot act on, and
		// changes above it, so the list is kept relative to that folder.
		foreach (var line in Lines(Git(repository.Root, $"diff --name-status --relative {repository.Commit} --")))
		{
			var tab = line.IndexOf('\t');
			if (tab <= 0) continue;
			var path = line[(tab + 1)..].Trim();
			// A rename reports both names; the new one is what exists now.
			var lastTab = path.LastIndexOf('\t');
			if (lastTab >= 0) path = path[(lastTab + 1)..];
			if (path.Length == 0 || !seen.Add(path)) continue;
			changes.Add(new WorkspaceChange(repository.Prefix + path, line[0] switch
			{
				'A' => "added",
				'D' => "deleted",
				_ => "modified",
			}));
		}

		// A file created during the turn is untracked, so it is absent
		// from a commit object and has to be found by comparison.
		foreach (var path in Untracked(repository.Root))
			if (!repository.Untracked.Contains(path) && seen.Add(path))
				changes.Add(new WorkspaceChange(repository.Prefix + path, "added"));
		return changes;
	}

	private static List<WorkspaceChange> StampChanges(ChangeAnchor anchor)
	{
		var before = anchor.Stamps ?? new Dictionary<string, FileStamp>();
		var after = Snapshot(anchor.Workspace, Roots(anchor.Repositories), null, anchor.ScanLimit);
		var changes = new List<WorkspaceChange>();
		foreach (var (path, stamp) in after.Files)
		{
			if (!before.TryGetValue(path, out var was))
			{
				if (FolderWasComplete(anchor, path))
					changes.Add(new WorkspaceChange(path, "added"));
			}
			else if (was != stamp)
				changes.Add(new WorkspaceChange(path, "modified"));
		}
		foreach (var path in before.Keys)
			if (!after.Files.ContainsKey(path) && FilePresence(FullPath(anchor, path)) is false)
				changes.Add(new WorkspaceChange(path, "deleted"));
		ReconcileWrites(anchor, changes);
		return changes;
	}

	/// <summary>
	/// Captures the content immediately before an approved built-in file
	/// write. Git already provides this baseline inside repositories; a
	/// loose file needs its own hash.
	/// </summary>
	internal static void RecordWriteIntent(ChangeAnchor? anchor, string? path, bool requireSnapshotMatch = false)
	{
		if (anchor is null || string.IsNullOrWhiteSpace(path)) return;
		if (ListedPath(anchor, path) is not { } listed || anchor.RepositoryFor(listed.Path) is not null)
			return;
		if (anchor.WriteBaselines.ContainsKey(listed.Path))
			return;
		if (!BaselineStillCurrent(anchor, listed.Path, listed.FullPath, allowUnknown: !requireSnapshotMatch))
			return;
		if (ContentState(listed.FullPath) is { } baseline)
			anchor.WriteBaselines.TryAdd(listed.Path, baseline);
	}

	private static void ReconcileWrites(ChangeAnchor anchor, List<WorkspaceChange> changes)
	{
		foreach (var (path, before) in anchor.WriteBaselines)
		{
			if (ContentState(FullPath(anchor, path)) is not { } after)
				continue;
			changes.RemoveAll(change => string.Equals(change.Path, path, StringComparison.OrdinalIgnoreCase));
			var kind = before.Exists switch
			{
				false when after.Exists => "added",
				true when !after.Exists => "deleted",
				true when after.Exists && !string.Equals(before.Hash, after.Hash, StringComparison.Ordinal) => "modified",
				_ => null,
			};
			if (kind is not null)
				changes.Add(new WorkspaceChange(path, kind));
		}
	}

	private static bool BaselineStillCurrent(ChangeAnchor anchor, string path, string fullPath, bool allowUnknown)
	{
		if (!IsInsideWorkspace(path))
			return allowUnknown;
		if (anchor.Stamps is null)
			return false;
		if (anchor.Stamps.TryGetValue(path, out var baseline))
			return Stamp(fullPath) is { } current && current == baseline;
		return FolderWasComplete(anchor, path)
			? FilePresence(fullPath) is false
			: allowUnknown;
	}

	private static bool FolderWasComplete(ChangeAnchor anchor, string path) =>
		anchor.CompleteFolders is null || anchor.CompleteFolders.Contains(ParentFolder(path));

	private static string ParentFolder(string path)
	{
		var slash = path.LastIndexOf('/');
		return slash < 0 ? string.Empty : path[..slash];
	}

	private static bool IsInsideWorkspace(string path) =>
		!Path.IsPathFullyQualified(path)
		&& path != ".."
		&& !path.StartsWith("../", StringComparison.Ordinal);

	private static (string Path, string FullPath)? ListedPath(ChangeAnchor anchor, string path)
	{
		try
		{
			var fullPath = Path.GetFullPath(Path.IsPathFullyQualified(path)
				? path
				: Path.Combine(anchor.Workspace, path));
			var listed = Path.GetRelativePath(anchor.Workspace, fullPath).Replace('\\', '/');
			return (listed, fullPath);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			return null;
		}
	}

	private static FileContentState? ContentState(string path)
	{
		if (FilePresence(path) is not { } exists)
			return null;
		if (!exists)
			return new FileContentState(false, string.Empty);
		try
		{
			using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
			return new FileContentState(true, Convert.ToHexString(SHA256.HashData(stream)));
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static bool? FilePresence(string path)
	{
		try
		{
			return !File.GetAttributes(path).HasFlag(FileAttributes.Directory);
		}
		catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
		{
			return false;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	private static FileStamp? Stamp(string path)
	{
		try
		{
			var file = new FileInfo(path);
			return file.Exists ? new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks) : null;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
		{
			return null;
		}
	}

	/// <summary>
	/// The change made to one file, as a unified diff. Empty when the
	/// file is in no repository, since there is nothing to compare it
	/// against.
	/// </summary>
	internal static string Diff(ChangeAnchor? anchor, string path)
	{
		if (anchor?.RepositoryFor(path) is not { } repository) return string.Empty;
		var inner = repository.Inner(path);
		var diff = Git(repository.Root, $"diff --no-color {repository.Commit} -- \"{inner}\"");
		// An untracked file has no counterpart in the commit object, so
		// git compares it against nothing and reports nothing.
		if (string.IsNullOrWhiteSpace(diff))
			diff = Git(repository.Root, $"diff --no-color --no-index -- /dev/null \"{inner}\"");
		return Bound(diff);
	}

	/// <summary>
	/// The command handing one file to the user's diff tool, run in the
	/// repository holding the file so git finds the earlier copy in that
	/// repository's history. Null when the file is in no repository.
	/// </summary>
	internal static ProcessStartInfo? DiffToolCommand(ChangeAnchor? anchor, string path) =>
		anchor?.RepositoryFor(path) is { } repository
			? new ProcessStartInfo
			{
				FileName = "git",
				Arguments = $"difftool --no-prompt {repository.Commit} -- \"{repository.Inner(path)}\"",
				WorkingDirectory = repository.Root,
				UseShellExecute = true,
			}
			: null;

	/// <summary>
	/// Opens the user's configured diff tool on one file. Whatever they
	/// already set up for `git difftool` is what opens, because a tool
	/// chosen once should not have to be chosen again here.
	/// </summary>
	internal static bool OpenDiffTool(ChangeAnchor? anchor, string path)
	{
		if (DiffToolCommand(anchor, path) is not { } command) return false;
		try
		{
			Process.Start(command)?.Dispose();
			return true;
		}
		catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
		{
			return false;
		}
	}

	/// <summary>Puts one file back as it was when the turn began.</summary>
	internal static bool Revert(ChangeAnchor? anchor, string path)
	{
		if (anchor?.RepositoryFor(path) is not { } repository) return false;
		return Git(repository.Root, $"checkout {repository.Commit} -- \"{repository.Inner(path)}\"") is not null;
	}

	/// <summary>The full path of a listed change, for opening it.</summary>
	internal static string FullPath(ChangeAnchor anchor, string path) =>
		Path.GetFullPath(Path.Combine(anchor.Workspace, path.Replace('/', Path.DirectorySeparatorChar)));

	private static HashSet<string> Untracked(string root)
	{
		var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		foreach (var line in Lines(Git(root, "ls-files --others --exclude-standard")))
			if (line.Trim() is { Length: > 0 } path)
				paths.Add(path);
		return paths;
	}

	private static HashSet<string> Roots(IEnumerable<RepositoryAnchor> repositories) =>
		new(repositories.Select(repository => repository.Root), StringComparer.OrdinalIgnoreCase);

	/// <summary>
	/// Size and write time per file, bounded so a very large tree costs
	/// a fixed amount rather than an unbounded one. Direct file lists
	/// are marked complete per folder, so reaching the bound cannot turn
	/// an unchanged file into a false creation or deletion. The
	/// repositories in
	/// <paramref name="anchored"/> are left out, since their history
	/// already answers for their files. When <paramref name="found"/> is
	/// given, the walk is also a search: a folder at the top of a
	/// repository is listed there and not walked into.
	/// </summary>
	private static LooseSnapshot Snapshot(
		string workspace,
		IReadOnlySet<string> anchored,
		List<string>? found,
		int scanLimit)
	{
		var files = new Dictionary<string, FileStamp>(StringComparer.OrdinalIgnoreCase);
		var completeFolders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
		var folders = new Queue<string>();
		folders.Enqueue(workspace);
		while (folders.Count > 0 && files.Count < scanLimit)
		{
			var folder = folders.Dequeue();
			FileInfo[] directFiles;
			try
			{
				directFiles = new DirectoryInfo(folder).EnumerateFiles()
					.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
					.ThenBy(file => file.Name, StringComparer.Ordinal)
					.ToArray();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				continue;
			}

			var complete = true;
			foreach (var file in directFiles)
			{
				if (files.Count >= scanLimit)
				{
					complete = false;
					break;
				}
				try
				{
					files[Path.GetRelativePath(workspace, file.FullName).Replace('\\', '/')] =
						new FileStamp(file.Length, file.LastWriteTimeUtc.Ticks);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
					complete = false;
				}
			}
			if (complete)
			{
				var relative = Path.GetRelativePath(workspace, folder).Replace('\\', '/');
				completeFolders.Add(relative == "." ? string.Empty : relative);
			}

			DirectoryInfo[] children;
			try
			{
				children = new DirectoryInfo(folder).EnumerateDirectories()
					.OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
					.ThenBy(child => child.Name, StringComparer.Ordinal)
					.ToArray();
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
			{
				continue;
			}
			foreach (var child in children)
			{
				try
				{
					if (SkippedFolders.Contains(child.Name) || child.Attributes.HasFlag(FileAttributes.ReparsePoint)
						|| anchored.Contains(child.FullName))
						continue;
					// A submodule or an extra worktree marks its top with a
					// .git file rather than a folder, so either counts.
					if (found is { Count: < MaxRepositories } && Path.Exists(Path.Combine(child.FullName, ".git")))
						found.Add(child.FullName);
					else if (files.Count < scanLimit)
						folders.Enqueue(child.FullName);
				}
				catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
				{
				}
			}
		}
		return new LooseSnapshot(files, completeFolders);
	}

	private sealed record LooseSnapshot(
		Dictionary<string, FileStamp> Files,
		HashSet<string> CompleteFolders);

	private static string Bound(string? diff)
	{
		if (string.IsNullOrWhiteSpace(diff)) return string.Empty;
		var lines = diff.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
		return lines.Length <= MaxDiffLines
			? string.Join("\r\n", lines)
			: string.Join("\r\n", lines[..MaxDiffLines]) + "\r\n... " + (lines.Length - MaxDiffLines) + " more lines";
	}

	private static string[] Lines(string? text) =>
		string.IsNullOrWhiteSpace(text) ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');

	/// <summary>
	/// Runs one git command and returns its output, or null when git is
	/// missing or the command failed. Nothing here is important enough
	/// to interrupt a session over.
	/// </summary>
	private static string? Git(string folder, string arguments)
	{
		if (Runner is { } runner) return runner(folder, arguments);
		try
		{
			using var process = Process.Start(new ProcessStartInfo
			{
				FileName = "git",
				Arguments = arguments,
				WorkingDirectory = folder,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
				UseShellExecute = false,
				CreateNoWindow = true,
			});
			if (process is null) return null;
			var output = process.StandardOutput.ReadToEnd();
			process.StandardError.ReadToEnd();
			if (!process.WaitForExit(Patience))
			{
				process.Kill(entireProcessTree: true);
				return null;
			}
			// A diff reports a difference with exit code 1, which is not
			// a failure, so output is preferred over the code.
			return output.Length > 0 || process.ExitCode == 0 ? output.TrimEnd('\r', '\n') : null;
		}
		catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
		{
			return null;
		}
	}
}
