using System.IO;
using System.Text;
using TurboPilot.Commands;

namespace TurboPilot.Rendering;

/// <summary>Loads the in-application Help document and its live command table.</summary>
internal static class HelpContent
{
	private const string CommandTableMarker = "<!-- slash-command-table -->";

	internal static string Load(string? baseDirectory = null)
	{
		var path = Path.Combine(
			baseDirectory ?? AppContext.BaseDirectory,
			"Assets",
			"ShowHelp.md");
		var markdown = File.ReadAllText(path);
		if (!markdown.Contains(CommandTableMarker, StringComparison.Ordinal))
			throw new InvalidDataException($"The Help document is missing {CommandTableMarker}.");

		markdown = markdown.Replace(
			CommandTableMarker,
			BuildCommandTable(),
			StringComparison.Ordinal);
		return markdown.Replace("\r\n", "\n").Replace("\n", "\r\n");
	}

	private static string BuildCommandTable()
	{
		var table = new StringBuilder();
		table.AppendLine("| Command | Action |");
		table.AppendLine("| --- | --- |");
		foreach (var command in SlashCommands.All)
		{
			table.Append("| `")
				.Append(command.Name)
				.Append("` | ")
				.Append(command.Summary.Replace("|", "\\|", StringComparison.Ordinal))
				.AppendLine(" |");
		}
		return table.ToString().TrimEnd();
	}
}
