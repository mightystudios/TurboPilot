namespace TurboPilot.Commands;

/// <summary>
/// One typed command: the word the user types and the line describing it.
/// </summary>
internal readonly record struct SlashCommand(string Name, string Summary);

/// <summary>
/// The commands the prompt box answers itself instead of sending on.
///
/// Every one of these is also a menu item. The menus stay, because a menu
/// is how a user finds a feature they do not know the name of; the
/// commands exist because once the name is known, reaching for the mouse
/// is the slow way to do it, and the hands are already on the prompt.
///
/// The rule for recognizing one is deliberately narrow: the prompt must
/// contain the command and nothing but the command and its argument. A
/// message that merely happens to mention a path starting with a slash
/// is a message, and goes to the model untouched. An unrecognized
/// command is left alone for the same reason.
/// </summary>
internal static class SlashCommands
{
	internal const string Prefix = "/";

	internal static readonly IReadOnlyList<SlashCommand> All =
	[
		new("/help", "List these commands"),
		new("/plan", "Show the agent's current plan"),
		new("/attach", "Choose files to send with the next prompt"),
		new("/compact", "Summarize the conversation to reclaim context"),
		new("/reset", "Start the conversation over, keeping the settings"),
		new("/session", "Open settings to begin or change the session"),
		new("/details", "List what the session is running with"),
		new("/tasks", "Inspect background agents and shell commands"),
		new("/save", "Save the transcript to a file"),
		new("/past", "Reopen an earlier session"),
		new("/terminal", "Open a shell in the workspace"),
		new("/files", "Open the workspace folder"),
		new("/editor", "Open the workspace in the code editor"),
	];

	/// <summary>
	/// Splits a prompt into a known command and its argument, or returns
	/// null when the prompt is an ordinary message.
	/// </summary>
	internal static (SlashCommand Command, string Argument)? Parse(string? text)
	{
		var trimmed = (text ?? string.Empty).Trim();
		if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal)) return null;
		if (trimmed.Contains('\n') || trimmed.Contains('\r')) return null;

		var split = trimmed.IndexOfAny([' ', '\t']);
		var name = split < 0 ? trimmed : trimmed[..split];
		var argument = split < 0 ? string.Empty : trimmed[(split + 1)..].Trim();
		foreach (var command in All)
			if (string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase))
				return (command, argument);
		return null;
	}

	/// <summary>
	/// Commands worth offering for what has been typed so far. Empty
	/// whenever the prompt has stopped being a bare command: once there
	/// is a space, the user has committed to one and is typing its
	/// argument, and a list of alternatives is only in the way.
	/// </summary>
	internal static IReadOnlyList<SlashCommand> Suggest(string? text)
	{
		var trimmed = (text ?? string.Empty).TrimStart();
		if (!trimmed.StartsWith(Prefix, StringComparison.Ordinal)) return [];
		foreach (var letter in trimmed)
			if (char.IsWhiteSpace(letter))
				return [];

		var matches = new List<SlashCommand>();
		foreach (var command in All)
			if (command.Name.StartsWith(trimmed, StringComparison.OrdinalIgnoreCase))
				matches.Add(command);

		// The only match being what is already typed in full means there
		// is nothing left to complete.
		if (matches.Count == 1 && string.Equals(matches[0].Name, trimmed, StringComparison.OrdinalIgnoreCase))
			return [];
		return matches;
	}

	/// <summary>
	/// The command list as the transcript shows it, padded into columns.
	/// </summary>
	internal static string HelpText()
	{
		var width = 0;
		foreach (var command in All)
			width = Math.Max(width, command.Name.Length);

		var lines = new List<string>();
		foreach (var command in All)
			lines.Add(command.Name.PadRight(width) + "  " + command.Summary);
		lines.Add("");
		lines.Add("[f".PadRight(width) + "  Reference a pending attachment as [file:name]");
		lines.Add("[s".PadRight(width) + "  Request a loaded skill as [skill:name]");
		return string.Join(Environment.NewLine, lines);
	}
}
