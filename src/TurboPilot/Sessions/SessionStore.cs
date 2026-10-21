using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using TurboPilot.Ai;
using TurboPilot.Customizations;
using TurboPilot.Permissions;
using TurboPilot.Storage;

namespace TurboPilot.Sessions;

// Context carried into a replacement session: the previous model's hand-off summary plus the
// user's own requests, quoted exactly so their constraints cannot be summarized away.
public sealed record SummaryBootstrap(string SourceSessionId, string Workspace, string Summary)
{
	public IReadOnlyList<string> RecentRequests { get; init; } = [];
}

public sealed class SessionRecord
{
	public required string SessionId { get; init; }
	public required ChatSessionOptions Options { get; set; }
	public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
	public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
	public string Description { get; set; } = "";

	/// <summary>
	/// One sentence naming the session's most recent completed work,
	/// written by the model as the session ends. An archive is read long
	/// after the work is forgotten, so what it needs is where the work
	/// finished, which is not knowable when the opening prompt is typed.
	/// Empty until the session has ended at least once, and empty for a
	/// session that ended before this was recorded.
	/// </summary>
	public string Summary { get; set; } = "";

	/// <summary>
	/// How many requests had been sent when the summary was written. A
	/// session reopened and closed again without being asked anything
	/// has nothing new to say, and asking anyway would spend a model
	/// call to rewrite the same sentence.
	/// </summary>
	public int SummaryRequests { get; set; }

	public List<string> Prompts { get; set; } = [];
	// Prompts also holds answers to model questions; Requests holds only the user's own prompts.
	public List<string> Requests { get; set; } = [];
	public int ContextUsedTokens { get; set; }
	public int ContextWindowTokens { get; set; }
	public long AicNano { get; set; }
	public bool UsesApiKey { get; set; }
	/// <summary>
	/// The customization items the session ran with, snapshotted when it
	/// connected. A resume uses these rather than whatever the lists hold
	/// now, so the session comes back with the instructions, skills,
	/// agents and servers it actually had.
	/// </summary>
	public CustomizationLibrary Customizations { get; set; } = new();
	/// <summary>
	/// The permissions in force for the workspace when the session
	/// connected: every folder grant that applied, and the operations
	/// pre-approved. Restored with the session so a resume is not
	/// silently more or less trusted than the run it continues.
	/// </summary>
	public PermissionScope Permissions { get; set; } = new();
	public SummaryBootstrap? Bootstrap { get; set; }
	public bool BootstrapPending { get; set; }

	/// <summary>
	/// The one sentence a row may show, or nothing. The check is repeated
	/// here rather than trusted from the file: a record written before
	/// the distiller was corrected can hold a fragment, and a stored
	/// value is not worth a migration when reading it can simply decline
	/// to show it.
	/// </summary>
	[JsonIgnore]
	public string ArchiveSentence =>
		!string.IsNullOrWhiteSpace(Summary) && !Summary.AsSpan().ContainsAny('\r', '\n')
			&& Ai.ChatService.IsSentence(Summary.Trim())
			? Summary.Trim() : "";

	/// <summary>
	/// The Past Sessions row: when the session last ran, the folder it
	/// ran against, and what it was about. The model and the settings are
	/// left to the details pane, because they are the same across most
	/// rows and so tell one row from another least. Each part is clipped
	/// to a fixed budget so the row stays a predictable width.
	///
	/// The summary is preferred, then the opening prompt for a session
	/// that ended before summaries were recorded, then the ID, which
	/// always exists.
	/// </summary>
	[JsonIgnore]
	public string DisplayLabel
	{
		get
		{
			var said = ArchiveSentence is { Length: > 0 } sentence ? Rendering.ShortText.Clip(sentence, 96)
				: !string.IsNullOrWhiteSpace(Description) ? Rendering.ShortText.Clip(Description, 96)
				: Rendering.ShortText.SessionId(SessionId, 24);
			var workspace = Rendering.ShortText.Workspace(Options.WorkspaceFolder);
			return $"{UpdatedAt.ToLocalTime():g} | {(workspace.Length == 0 ? "no workspace" : workspace)} | {said}";
		}
	}
}

public sealed class SessionStore
{
	private static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		PropertyNameCaseInsensitive = true,
		RespectNullableAnnotations = true,
	};

	private readonly string _directory;

	public SessionStore(string? directory = null)
	{
		_directory = directory ?? Path.Combine(
			Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
			"TurboPilot", "sessions");
	}

	public SessionRecord Create(string sessionId, ChatSessionOptions options, string transcript)
	{
		var metadataPath = FilePath(sessionId, ".json");
		var transcriptPath = FilePath(sessionId, ".md");
		Directory.CreateDirectory(_directory);
		if (File.Exists(metadataPath) || File.Exists(transcriptPath))
			throw new IOException($"Session '{sessionId}' already has saved history.");

		var record = new SessionRecord
		{
			SessionId = sessionId,
			Options = options,
			UsesApiKey = options.UseByok && !string.IsNullOrWhiteSpace(options.ByokApiKey),
		};
		File.WriteAllText(transcriptPath, transcript);
		File.WriteAllText(FilePath(sessionId, ".rendered.md"), transcript);
		Save(record);
		return record;
	}

	public SessionRecord Load(string sessionId)
	{
		var record = JsonSerializer.Deserialize<SessionRecord>(
			File.ReadAllText(FilePath(sessionId, ".json")), JsonOptions)
			?? throw new InvalidDataException($"Session '{sessionId}' has no metadata.");
		if (record.SessionId != sessionId || record.Options is null || record.Prompts is null)
			throw new InvalidDataException($"Session '{sessionId}' has invalid metadata.");
		return record;
	}

	public IReadOnlyList<SessionRecord> List(out List<string> errors)
	{
		errors = [];
		var records = new List<SessionRecord>();
		if (!Directory.Exists(_directory))
			return records;

		foreach (var file in Directory.EnumerateFiles(_directory, "*.json"))
		{
			try
			{
				records.Add(Load(Path.GetFileNameWithoutExtension(file)));
			}
			catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
			{
				errors.Add($"Cannot read '{Path.GetFileName(file)}': {ex.Message}");
			}
		}
		return records.OrderByDescending(record => record.UpdatedAt).ToList();
	}

	/// <summary>
	/// Removes a saved session: its metadata and both transcripts. A file
	/// that is already gone is not an error, since the point of the call
	/// is that none of them remain afterward.
	/// </summary>
	public void Delete(string sessionId)
	{
		foreach (var extension in new[] { ".json", ".md", ".rendered.md" })
		{
			var path = FilePath(sessionId, extension);
			if (File.Exists(path))
				File.Delete(path);
		}
	}

	public string ReadTranscript(string sessionId) => File.ReadAllText(FilePath(sessionId, ".md"));
	public string ReadRenderedTranscript(string sessionId)
	{
		var path = FilePath(sessionId, ".rendered.md");
		return File.Exists(path) ? Rendering.NoticeFormatter.Repair(File.ReadAllText(path)) : ReadTranscript(sessionId);
	}

	public void AppendTranscript(string sessionId, string text) =>
		File.AppendAllText(FilePath(sessionId, ".md"), text);
	public void AppendRenderedTranscript(string sessionId, string text) =>
		File.AppendAllText(FilePath(sessionId, ".rendered.md"), text);

	public void WriteTranscript(string sessionId, string text) =>
		WriteText(FilePath(sessionId, ".md"), text);

	public void WriteRenderedTranscript(string sessionId, string text) =>
		WriteText(FilePath(sessionId, ".rendered.md"), text);

	private static void WriteText(string path, string text) =>
		WriteAtomic(path, stream =>
		{
			using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
			writer.Write(text);
		});

	public void Save(SessionRecord record)
	{
		var path = FilePath(record.SessionId, ".json");
		record.UpdatedAt = DateTimeOffset.UtcNow;
		WriteAtomic(path, stream => JsonSerializer.Serialize(stream, record, JsonOptions));
	}

	private static void WriteAtomic(string path, Action<Stream> write) => AtomicFile.Write(path, write);

	private string FilePath(string sessionId, string extension)
	{
		if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > 160
			|| sessionId is "." or ".."
			|| !sessionId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
			throw new ArgumentException("Invalid session ID.", nameof(sessionId));
		return Path.Combine(_directory, sessionId + extension);
	}
}
