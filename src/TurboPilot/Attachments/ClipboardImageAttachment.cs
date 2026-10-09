using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace TurboPilot.Attachments;

internal static class ClipboardImageAttachment
{
	private static readonly string[] ImageFormats =
	[
		DataFormats.Bitmap,
		"PNG",
		DataFormats.Dib,
		"DeviceIndependentBitmap",
	];

	internal static bool Contains(IDataObject data) =>
		ImageFormats.Any(format => data.GetDataPresent(format, autoConvert: true));

	internal static BitmapSource Read(IDataObject data)
	{
		if (data.GetDataPresent(DataFormats.Bitmap, autoConvert: true)
			&& data.GetData(DataFormats.Bitmap, autoConvert: true) is BitmapSource bitmap)
		{
			return Freeze(bitmap);
		}

		if (data.GetDataPresent("PNG", autoConvert: true)
			&& data.GetData("PNG", autoConvert: true) is Stream stream)
		{
			var position = stream.CanSeek ? stream.Position : 0;
			try
			{
				if (stream.CanSeek)
					stream.Position = 0;
				using var copy = new MemoryStream();
				stream.CopyTo(copy);
				copy.Position = 0;
				return Freeze(BitmapFrame.Create(
					copy,
					BitmapCreateOptions.PreservePixelFormat,
					BitmapCacheOption.OnLoad));
			}
			finally
			{
				if (stream.CanSeek)
					stream.Position = position;
			}
		}

		if (Clipboard.GetImage() is { } clipboardImage)
			return Freeze(clipboardImage);

		throw new InvalidDataException("The clipboard image format could not be read.");
	}

	internal static string SavePng(BitmapSource image, string? directory = null)
	{
		directory ??= Path.Combine(Path.GetTempPath(), "TurboPilot");
		Directory.CreateDirectory(directory);
		var path = Path.Combine(directory, $"clip-image-{Guid.NewGuid():N}.png");

		try
		{
			var encoder = new PngBitmapEncoder();
			encoder.Frames.Add(BitmapFrame.Create(image));
			using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
			encoder.Save(output);
			return path;
		}
		catch
		{
			TryDelete(path);
			throw;
		}
	}

	internal static void TryDelete(string path)
	{
		try
		{
			if (File.Exists(path))
				File.Delete(path);
		}
		catch
		{
			// Temporary-file cleanup is best effort.
		}
	}

	private static BitmapSource Freeze(BitmapSource source)
	{
		var copy = source.CloneCurrentValue();
		if (copy.CanFreeze)
			copy.Freeze();
		return copy;
	}
}
