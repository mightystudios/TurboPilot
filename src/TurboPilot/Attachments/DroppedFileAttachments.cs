using System.IO;
using System.Text;
using System.Windows;

namespace TurboPilot.Attachments;

internal static class DroppedFileAttachments
{
	private const string FileNameW = "FileNameW";
	private const string VsReferenceProjectItems = "CF_VSREFPROJECTITEMS";
	private const string VsStorageProjectItems = "CF_VSSTGPROJECTITEMS";

	private static readonly string[] SupportedFormats =
	[
		DataFormats.FileDrop,
		FileNameW,
		VsReferenceProjectItems,
		VsStorageProjectItems,
	];

	internal static bool ContainsSupportedFormat(IDataObject data) =>
		SupportedFormats.Any(format => data.GetDataPresent(format, autoConvert: false));

	internal static IReadOnlyList<string> ExtractFiles(IDataObject data)
	{
		IEnumerable<string> paths = ReadFileDrop(data);
		if (!paths.Any())
			paths = ReadVisualStudioItems(data);
		if (!paths.Any() && ReadSingleFileName(data) is { } single)
			paths = [single];

		return paths
			.Where(File.Exists)
			.Select(Path.GetFullPath)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToArray();
	}

	internal static IReadOnlyList<string> Formats(IDataObject data)
	{
		try { return data.GetFormats(autoConvert: false); }
		catch { return []; }
	}

	private static IReadOnlyList<string> ReadFileDrop(IDataObject data)
	{
		if (!data.GetDataPresent(DataFormats.FileDrop, autoConvert: false))
			return [];
		try
		{
			return data.GetData(DataFormats.FileDrop, autoConvert: false) as string[] ?? [];
		}
		catch
		{
			return [];
		}
	}

	private static IReadOnlyList<string> ReadVisualStudioItems(IDataObject data)
	{
		if (!data.GetDataPresent(VsReferenceProjectItems, autoConvert: false))
			return [];

		byte[] bytes;
		try
		{
			bytes = data.GetData(VsReferenceProjectItems, autoConvert: false) switch
			{
				MemoryStream stream => stream.ToArray(),
				byte[] value => value,
				_ => [],
			};
		}
		catch
		{
			return [];
		}
		if (bytes.Length == 0)
			return [];

		var paths = new List<string>();
		var text = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
		foreach (var entry in text.Split('\0', StringSplitOptions.RemoveEmptyEntries))
		{
			var parts = entry.Split('|');
			if (parts.Length < 3)
				continue;

			var projectFile = parts[1].Trim();
			var itemPath = parts[2].Trim();
			if (itemPath.Length == 0)
				continue;

			if (!Path.IsPathRooted(itemPath) && Path.GetDirectoryName(projectFile) is { Length: > 0 } projectDirectory)
			{
				try { itemPath = Path.GetFullPath(Path.Combine(projectDirectory, itemPath)); }
				catch { continue; }
			}
			paths.Add(itemPath);
		}
		return paths;
	}

	private static string? ReadSingleFileName(IDataObject data)
	{
		if (!data.GetDataPresent(FileNameW, autoConvert: false))
			return null;
		try
		{
			return data.GetData(FileNameW, autoConvert: false) switch
			{
				string value => value.Trim().TrimEnd('\0'),
				MemoryStream stream => DecodeFileName(stream.ToArray()),
				byte[] bytes => DecodeFileName(bytes),
				_ => null,
			};
		}
		catch
		{
			return null;
		}
	}

	private static string? DecodeFileName(byte[] bytes)
	{
		if (bytes.Length == 0)
			return null;
		var value = Encoding.Unicode.GetString(bytes);
		var terminator = value.IndexOf('\0');
		if (terminator >= 0)
			value = value[..terminator];
		return value.Trim();
	}
}
