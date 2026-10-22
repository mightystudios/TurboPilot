using System.IO;
using System.Text.Json;

namespace TurboPilot.Dialogs;

/// <summary>
/// Application settings persisted to disk between runs.
/// Stored in the user's local application data folder.
/// </summary>
public sealed class Settings
{
	private const string SettingsFileName = "turbopilot-settings.json";

	private static string SettingsPath => Path.Combine(
		Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
		"TurboPilot",
		SettingsFileName);

	/// <summary>
	/// The last workspace folder path used by the user.
	/// </summary>
	public string? LastWorkspacePath { get; set; }

	/// <summary>
	/// List of folders searched for customization items (prompts,
	/// agents, skills, instructions and MCP servers). Order is not
	/// significant; duplicate short names coexist with suffixes.
	/// </summary>
	public List<string> CustomizationFolders { get; set; } = new();

	/// <summary>
	/// Model hosting service for new sessions: "CopilotCli" or "Byok".
	/// </summary>
	public string ModelProvider { get; set; } = "CopilotCli";

	/// <summary>
	/// Composed base URL of the BYOK OpenAI-compatible server
	/// (e.g. http://10.0.0.234:13305/api/v1). Unused for the CLI provider.
	/// </summary>
	public string ByokEndpoint { get; set; } = "";

	/// <summary>API key sent to the BYOK server. Unused for the CLI provider.</summary>
	public string ByokApiKey { get; set; } = "";

	/// <summary>Model id selected in the Settings dialog, if any.</summary>
	public string SelectedModel { get; set; } = "";

	/// <summary>Context tier selected in the Settings dialog, if any.</summary>
	public string SelectedContextTier { get; set; } = "";

	/// <summary>Reasoning effort selected in the Settings dialog, if any.</summary>
	public string SelectedEffort { get; set; } = "";

	/// <summary>Session mode: Standard, Plan, Autopilot, or a custom agent name.</summary>
	public string SelectedMode { get; set; } = "Standard";

	/// <summary>Whether instruction files load into the session context.</summary>
	public bool ApplyInstructions { get; set; } = true;

	/// <summary>Whether enabled skills preload into the session context.</summary>
	public bool PreloadSkills { get; set; } = true;

	/// <summary>Whether Rendered output links mentioned local files and previews images.</summary>
	public bool LinkFiles { get; set; } = true;

	/// <summary>
	/// Whether audio cues play when a prompt is sent, a turn finishes, or
	/// the model needs an answer.
	/// </summary>
	public bool PlaySounds { get; set; } = true;

	/// <summary>
	/// Loads settings from disk. Returns a default instance if the file
	/// doesn't exist or fails to parse.
	/// </summary>
	public static Settings Load()
	{
		try
		{
			if (!File.Exists(SettingsPath))
				return new Settings();

			var json = File.ReadAllText(SettingsPath);
			var settings = JsonSerializer.Deserialize<Settings>(json);
			return settings ?? new Settings();
		}
		catch
		{
			// Corrupted or unreadable settings file: return defaults
			return new Settings();
		}
	}

	/// <summary>
	/// Saves settings to disk, creating the directory structure if needed.
	/// </summary>
	public void Save()
	{
		var directory = Path.GetDirectoryName(SettingsPath);
		if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var options = new JsonSerializerOptions
		{
			WriteIndented = true,
		};
		var json = JsonSerializer.Serialize(this, options);
		File.WriteAllText(SettingsPath, json);
	}
}
