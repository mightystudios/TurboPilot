using System.Text;
using System.Text.RegularExpressions;
using TurboPilot.Tools;

namespace TurboPilot.Rendering;

/// <summary>
/// Builds the two forms of every transcript notice the application writes
/// itself: the plain line shown in the Raw tab, and the marked-up form
/// shown in the Rendered tab.
///
/// The Rendered form is a small block of literal HTML embedded in the
/// markdown stream. A div on its own line opens an HTML block that ends at
/// the following blank line, so any markdown between the opening and
/// closing tags is still parsed normally; that matters for questions,
/// whose text can carry a whole plan. Everything else is emitted as
/// escaped HTML so a file path or a shell command can never be read as
/// markup. Escaping covers line breaks too: they are written as character
/// references, because a blank line in a tool's output would otherwise end
/// the block early and leave the notice's closing tags to the markdown
/// parser. Styling lives in web/output.css and BorlandVisionTheme.cs.
/// </summary>
internal static class NoticeFormatter
{
	// Escaped text inside a notice holds no tag, so it runs from its
	// opening tag to the next one.
	private static readonly Regex SavedText = new(
		"""(?<open><pre class="kp-(?:tool-body|tool-outcome|listing-body)">|<span class="kp-status-text">|<div class="kp-card-detail">[^<]*<code>)(?<text>[^<]*)""",
		RegexOptions.Compiled);

	// A tool row's opening tag, in any form a saved transcript can hold.
	private static readonly Regex SavedToolRow = new(
		"""<div class="(?:kp-card )?kp-tool kp-tool-(?<state>running|ok|failed|stopped)">""",
		RegexOptions.Compiled);

	/// <summary>
	/// Brings a Rendered transcript saved by an earlier version to the
	/// current form. Those versions kept the line breaks in escaped text,
	/// so a blank line in a tool's output ended the card's HTML block and
	/// the rest was read as markdown. When that rest was a table or a code
	/// block it swallowed the closing tags, and the card, left open, hid
	/// everything written after it. They also framed every tool call as a
	/// card; it is now a row. A row saved while its call was running is
	/// marked stopped, because nothing read back from disk can still be
	/// running. Anything else already in the current form is returned
	/// unchanged.
	/// </summary>
	public static string Repair(string rendered) =>
		SavedToolRow.Replace(
			SavedText.Replace(rendered, match => match.Groups["open"].Value + EncodeLineBreaks(match.Groups["text"].Value)),
			match => ToolRowTag(match.Groups["state"].Value == "running" ? "stopped" : match.Groups["state"].Value));

	/// <summary>A question with its answer choices.</summary>
	public static (string Text, string Rendered) Question(string question, IReadOnlyList<string> choices, bool allowFreeform)
	{
		var hint = allowFreeform
			? "Reply with an option number, its text, or your own answer and press Send."
			: "Reply with an option number or its text and press Send.";
		var text = new StringBuilder("Question: ").Append(question);
		if (choices.Count > 0)
			text.Append("\r\n").Append(string.Join("  |  ", choices.Select((choice, index) => $"{index + 1}. {choice}")));
		text.Append("\r\n(").Append(hint).Append(')');
		return (text.ToString(), Card("question", "Question", question, null, choices, hint));
	}

	/// <summary>A permission request for one operation.</summary>
	public static (string Text, string Rendered) Permission(string kind, string? detail)
	{
		var label = string.IsNullOrWhiteSpace(detail) ? kind : $"{kind}: {detail}";
		var text = $"Permission requested - {label}\r\n1. yes (allow)  |  2. no (deny)\r\nReply with an option and press Send.";
		var rendered = Card("permission", "Permission requested", null,
			string.IsNullOrWhiteSpace(detail)
				? Escape(kind)
				: Escape(kind) + ": <code>" + Escape(detail) + "</code>",
			["yes (allow)", "no (deny)"],
			"Reply with an option and press Send.");
		return (text, rendered);
	}

	/// <summary>A tagged status line, such as a tool step or an error.</summary>
	public static (string Text, string Rendered) Status(string tag, string text)
	{
		var rendered = new StringBuilder("<div class=\"kp-status kp-status-").Append(ClassOf(tag)).Append("\">")
			.Append("<span class=\"kp-status-tag\">").Append(Escape(tag)).Append("</span>")
			.Append("<span class=\"kp-status-text\">").Append(Escape(text)).Append("</span>")
			.Append("</div>");
		return ($"[{tag}] {text}", rendered.ToString());
	}

	/// <summary>A session banner separating one run of the transcript from the next.</summary>
	public static (string Text, string Rendered) Banner(string text) =>
		($"--- {text} ---", "<div class=\"kp-banner\">" + Escape(text) + "</div>");

	/// <summary>
	/// The agent's plan as a checklist. Each step carries its state in a
	/// box drawn from ASCII, so the Raw tab reads the same as the
	/// Rendered one and neither depends on a font that has the glyphs.
	/// </summary>
	public static (string Text, string Rendered) Checklist(IReadOnlyList<PlanStep> steps)
	{
		var text = new StringBuilder("Plan:");
		var rendered = new StringBuilder("<div class=\"kp-card kp-plan\">")
			.Append("<div class=\"kp-card-title\">Plan</div>")
			.Append("<div class=\"kp-plan-steps\">");
		foreach (var step in steps)
		{
			var box = step.IsDone ? "[x]" : step.IsRunning ? "[>]" : step.IsBlocked ? "[!]" : "[ ]";
			var state = step.IsDone ? "done" : step.IsRunning ? "running" : step.IsBlocked ? "blocked" : "pending";
			text.Append("\r\n").Append(box).Append(' ').Append(step.Title);
			rendered.Append("<div class=\"kp-plan-step kp-plan-").Append(state).Append("\">")
				.Append("<span class=\"kp-plan-box\">").Append(box).Append("</span>")
				.Append("<span class=\"kp-plan-title\">").Append(Escape(step.Title)).Append("</span>")
				.Append("</div>");
		}
		rendered.Append("</div></div>");
		return (text.ToString(), rendered.ToString());
	}

	/// <summary>
	/// A tool call, as one row of the transcript. The headline is the one
	/// line worth seeing without asking; everything else is behind a
	/// disclosure, shut by default, because a turn can run twenty tools
	/// and a transcript that showed all of them in full would be
	/// unreadable. The row is unframed on purpose: a tool call is a step
	/// in the work, not a message, and the reply around it is what the
	/// transcript is for. The Rendered view folds a run of rows into a
	/// single line (see web/output.js); the text keeps one row per call.
	///
	/// The disclosure is plain HTML rather than script, so it works the
	/// same in a saved copy of the transcript as it does on screen.
	/// </summary>
	/// <param name="state">ok, failed or stopped; anything else is still running.</param>
	public static (string Text, string Rendered) Tool(string name, string headline, string body,
		string? state = null, string? outcome = null)
	{
		var text = new StringBuilder("[tool] ").Append(name);
		if (headline.Length > 0)
			text.Append("  ").Append(headline);

		var rendered = new StringBuilder(ToolRowTag(state))
			.Append("<details><summary>")
			.Append("<span class=\"kp-tool-name\">").Append(Escape(name)).Append("</span>")
			.Append("<span class=\"kp-tool-head\">").Append(Escape(headline)).Append("</span>")
			.Append("</summary>");
		if (body.Length > 0)
			rendered.Append("<pre class=\"kp-tool-body\">").Append(Escape(body)).Append("</pre>");
		if (!string.IsNullOrEmpty(outcome))
			rendered.Append("<pre class=\"kp-tool-outcome\">").Append(Escape(outcome)).Append("</pre>");
		if (body.Length == 0 && string.IsNullOrEmpty(outcome))
			rendered.Append("<div class=\"kp-tool-outcome\">No detail was reported.</div>");
		rendered.Append("</details></div>");
		return (text.ToString(), rendered.ToString());
	}

	private static string ToolRowTag(string? state) => "<div class=\"kp-tool kp-tool-" + (state switch
	{
		"ok" or "failed" or "stopped" => state,
		_ => "running",
	}) + "\">";

	/// <summary>
	/// What a turn did to the files on disk. Capped, because a card
	/// listing two hundred files is a wall, not a summary; the rest are
	/// reachable through the review link.
	/// </summary>
	public static (string Text, string Rendered) Changes(
		IReadOnlyList<WorkspaceChange> changes, ChangeAnchor anchor, int shown = 10)
	{
		var visible = changes.Count <= shown ? changes : changes.Take(shown).ToList();
		var title = "Changes (" + changes.Count + ")";
		var text = new StringBuilder(title);
		var rendered = new StringBuilder("<div class=\"kp-card kp-changes\">")
			.Append("<div class=\"kp-card-title\">").Append(title).Append("</div>")
			.Append("<div class=\"kp-change-rows\">");
		foreach (var change in visible)
		{
			text.Append("\r\n").Append(change.Mark).Append(' ').Append(change.Path);
			var link = "kp-path:" + Uri.EscapeDataString(
				WorkspaceChanges.FullPath(anchor, change.Path));
			rendered.Append("<div class=\"kp-change-row kp-change-").Append(change.Kind).Append("\">")
				.Append("<span class=\"kp-change-mark\">").Append(Escape(change.Mark)).Append("</span>")
				.Append("<a class=\"kp-change-path\" href=\"").Append(link).Append("\">")
				.Append(Escape(change.Path)).Append("</a>")
				.Append("<span class=\"kp-change-acts\">")
				.Append(Action("tool", change.Path, "compare"))
				.Append(Action("revert", change.Path, "undo"))
				.Append("</span></div>");
		}
		rendered.Append("</div>");
		if (changes.Count > visible.Count)
			text.Append("\r\n... ").Append(changes.Count - visible.Count).Append(" more");
		rendered.Append("<div class=\"kp-change-more\"><a href=\"kp-act:all\">Review all ")
			.Append(changes.Count).Append(" files</a></div></div>");
		return (text.ToString(), rendered.ToString());
	}

	/// <summary>
	/// One file's change as a unified diff, colored a line at a time so
	/// an addition and a removal do not have to be told apart by
	/// counting leading characters.
	/// </summary>
	public static (string Text, string Rendered) Diff(string path, string diff)
	{
		if (string.IsNullOrWhiteSpace(diff))
			return Status("diff", "No change to show for " + path + ".");

		var rendered = new StringBuilder("<div class=\"kp-card kp-diffcard\">")
			.Append("<div class=\"kp-card-title\">").Append(Escape(path)).Append("</div>")
			.Append("<pre class=\"kp-diff\">");
		foreach (var line in diff.Replace("\r\n", "\n").Split('\n'))
		{
			var kind = line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal)
				|| line.StartsWith("diff ", StringComparison.Ordinal) || line.StartsWith("index ", StringComparison.Ordinal)
					? "meta"
				: line.StartsWith('@') ? "hunk"
				: line.StartsWith('+') ? "add"
				: line.StartsWith('-') ? "del"
				: "ctx";
			rendered.Append("<span class=\"kp-diff-").Append(kind).Append("\">")
				.Append(Escape(line)).Append("</span>\n");
		}
		rendered.Append("</pre></div>");
		return ("--- " + path + " ---\r\n" + diff, rendered.ToString());
	}

	private static string Action(string verb, string path, string label) =>
		"<a class=\"kp-change-act\" href=\"kp-act:" + Uri.EscapeDataString(verb + "|" + path) + "\">["
		+ Escape(label) + "]</a>";

	/// <summary>
	/// A block of already-aligned text under a heading, for listings the
	/// program writes itself. The body keeps its own spacing: it is laid
	/// out in columns before it gets here, and reflowing it would undo
	/// the only thing making it readable.
	/// </summary>
	public static (string Text, string Rendered) Listing(string title, string body)
	{
		var rendered = new StringBuilder("<div class=\"kp-card kp-listing\">")
			.Append("<div class=\"kp-card-title\">").Append(Escape(title)).Append("</div>")
			.Append("<pre class=\"kp-listing-body\">").Append(Escape(body)).Append("</pre>")
			.Append("</div>");
		return (title + ":\r\n" + body, rendered.ToString());
	}

	private static string Card(string kind, string title, string? body, string? detail,		IReadOnlyList<string> choices, string hint)
	{
		var sb = new StringBuilder("<div class=\"kp-card kp-").Append(kind).Append("\">")
			.Append("<div class=\"kp-card-title\">").Append(Escape(title)).Append("</div>");
		if (detail is not null)
			sb.Append("<div class=\"kp-card-detail\">").Append(detail).Append("</div>");
		// A blank line closes the HTML block so the body is parsed as markdown.
		sb.Append("\r\n\r\n");
		if (!string.IsNullOrWhiteSpace(body))
			sb.Append(body).Append("\r\n\r\n");
		if (choices.Count > 0)
		{
			sb.Append("<div class=\"kp-card-choices\"><ol>");
			foreach (var choice in choices)
				sb.Append("<li>").Append(Escape(choice)).Append("</li>");
			sb.Append("</ol></div>");
		}
		sb.Append("<div class=\"kp-card-hint\">").Append(Escape(hint)).Append("</div>")
			.Append("</div>");
		return sb.ToString();
	}

	/// <summary>Reduces a tag to the letters that can safely name a style class.</summary>
	private static string ClassOf(string tag)
	{
		var sb = new StringBuilder(tag.Length);
		foreach (var character in tag)
		{
			if (char.IsAsciiLetter(character))
				sb.Append(char.ToLowerInvariant(character));
		}
		return sb.Length == 0 ? "note" : sb.ToString();
	}

	private static string Escape(string text) => EncodeLineBreaks(text
		.Replace("&", "&amp;")
		.Replace("<", "&lt;")
		.Replace(">", "&gt;")
		.Replace("\"", "&quot;"));

	private static string EncodeLineBreaks(string text) =>
		text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "&#10;");
}
