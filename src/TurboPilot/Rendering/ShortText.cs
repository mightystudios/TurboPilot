namespace TurboPilot.Rendering;

/// <summary>
/// Shortens the identifiers that appear in fixed-width furniture: the
/// session badge over the output area and the Past Sessions list. Model
/// names and session IDs are written for machines and routinely run past
/// any width a dialog can honor, so what is shown is cut down and the
/// whole value is offered in a tooltip instead.
/// </summary>
internal static class ShortText
{
	/// <summary>Ellipsis glyph used wherever text is cut. One cell wide in the bundled face.</summary>
	private const string Ellipsis = "...";

	/// <summary>
	/// Cuts <paramref name="text"/> to <paramref name="max"/> characters,
	/// ellipsis included, so the result never exceeds the budget it was
	/// given. Text already within budget is returned unchanged.
	/// </summary>
	internal static string Clip(string? text, int max)
	{
		text = (text ?? "").Trim();
		if (max <= 0) return "";
		if (text.Length <= max) return text;
		return max <= Ellipsis.Length ? text[..max] : text[..(max - Ellipsis.Length)] + Ellipsis;
	}

	/// <summary>
	/// The readable part of a model identifier. Vendors qualify their
	/// models with a publisher, a registry and a version
	/// ("Qwen3-Coder-30B-A3B-Instruct-GGUF/qwen3-coder-30b:4") and the
	/// leading segments repeat across every entry, so the last path
	/// segment carries what actually distinguishes one model from
	/// another. The result is still clipped, because that segment alone
	/// can be long.
	/// </summary>
	internal static string Model(string? model, int max = 34)
	{
		var text = (model ?? "").Trim();
		if (text.Length == 0) return "";
		var slash = text.LastIndexOfAny(['/', '\\']);
		if (slash >= 0 && slash < text.Length - 1)
			text = text[(slash + 1)..];
		return Clip(text, max);
	}

	/// <summary>
	/// A session ID cut to a recognizable stub. IDs are unique in their
	/// opening characters, so the head is kept and the tail dropped.
	/// </summary>
	internal static string SessionId(string? sessionId, int max = 14) => Clip(sessionId, max);

	/// <summary>
	/// The folder a workspace is known by. A full path is mostly the
	/// part every project on the machine has in common, so the leaf
	/// carries what tells one session's workspace from another's. A
	/// trailing separator is ignored, and a drive root has no leaf to
	/// take, so it stands for itself.
	/// </summary>
	internal static string Workspace(string? path, int max = 24)
	{
		var text = (path ?? "").Trim();
		if (text.Length == 0) return "";
		var trimmed = text.TrimEnd('\\', '/');
		if (trimmed.Length == 0) return Clip(text, max);
		var slash = trimmed.LastIndexOfAny(['/', '\\']);
		var leaf = slash >= 0 ? trimmed[(slash + 1)..] : trimmed;
		return Clip(leaf.Length == 0 ? trimmed : leaf, max);
	}

	/// <summary>
	/// A workspace path shown whole when it fits and cut from the front
	/// otherwise. The tail of a path - a project's own folders - is what
	/// tells one workspace from another, so the head is dropped and
	/// marked with a leading ellipsis when the path runs past its budget.
	/// </summary>
	internal static string WorkspacePath(string? path, int max = 48)
	{
		var text = (path ?? "").Trim();
		if (max <= 0) return "";
		if (text.Length <= max) return text;
		return max <= Ellipsis.Length ? text[^max..] : Ellipsis + text[^(max - Ellipsis.Length)..];
	}

	/// <summary>
	/// A compact elapsed time for a narrow status line. Seconds are useful
	/// until the first minute; longer operations use whole minutes and
	/// hours so progress never crowds out the state or usage figures.
	/// </summary>
	internal static string Elapsed(TimeSpan elapsed)
	{
		if (elapsed < TimeSpan.Zero)
			elapsed = TimeSpan.Zero;
		if (elapsed.TotalHours >= 1)
			return $"{(int)elapsed.TotalHours}h {elapsed.Minutes}m";
		if (elapsed.TotalMinutes >= 1)
			return $"{(int)elapsed.TotalMinutes}m";
		return $"{Math.Max(1, (int)elapsed.TotalSeconds)}s";
	}
}
