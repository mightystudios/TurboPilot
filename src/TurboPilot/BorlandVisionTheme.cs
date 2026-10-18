using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Media;

namespace TurboPilot;

/// <summary>
/// Borland Vision (Turbo Vision) theme for the output views.
///
/// This file is the single source of truth for the colors and font
/// parameters used by the Rendered (WebView2) tab and the Raw tab.
///
/// HOW TO RESTYLE
///   1. Change a hue once in <see cref="Palette"/> (the fixed VGA 16 set)
///      or in <see cref="Semantic"/> (names that describe a use rather
///      than a hue).
///   2. Change what an element looks like in <see cref="Roles"/>: one row
///      per element, with ink, face, size, weight, slant and decoration.
///   3. Change code coloring in <see cref="SyntaxRoles"/>.
///   4. Diagrams pick their colors up from <see cref="MermaidTheme"/>.
///
/// The tables compile into one stylesheet at startup, injected into the
/// WebView2 before the page paints. The Raw tab reads the same palette for
/// its face, ink and selection.
/// </summary>
public static class BorlandVisionTheme
{
	/// <summary>Face name of the bundled DOS bitmap font.</summary>
	public const string FontFaceName = "Px437 IBM VGA 9x16";

	/// <summary>Font file, relative to web/output.html.</summary>
	public const string FontFileUrl = "fonts/Px437_IBM_VGA_9x16.ttf";

	/// <summary>
	/// CSS fallback chain. Segoe UI Emoji supplies the glyphs code page 437
	/// never had, so emoji still render in color.
	/// </summary>
	public const string FontStack =
		"'Px437 IBM VGA 9x16', 'Consolas', 'Segoe UI Emoji', 'Courier New', monospace";

	/// <summary>Base body size in CSS pixels. 16 puts the face on its native 9x16 cell.</summary>
	public const double BaseFontSizePx = 14;

	/// <summary>Leading for prose. 1.0 is the raw text-mode cell; 1.25 reads better in long output.</summary>
	public const double LineHeight = 1.25;

	/// <summary>Font size of the Raw tab, which mirrors the Rendered base size.</summary>
	public const double RawFontSize = 16;

	/// <summary>
	/// The hardware VGA 16 palette. These are fixed values, not taste:
	/// every other color in the app resolves to one of them.
	/// </summary>
	public static class Palette
	{
		public const string Black = "#000000";        // 0
		public const string Blue = "#0000AA";         // 1
		public const string Green = "#00AA00";        // 2
		public const string Cyan = "#00AAAA";         // 3
		public const string Red = "#AA0000";          // 4
		public const string Magenta = "#AA00AA";      // 5
		public const string Brown = "#AA5500";        // 6
		public const string LightGray = "#AAAAAA";    // 7
		public const string DarkGray = "#555555";     // 8
		public const string LightBlue = "#5555FF";    // 9
		public const string LightGreen = "#55FF55";   // 10
		public const string LightCyan = "#55FFFF";    // 11
		public const string LightRed = "#FF5555";     // 12
		public const string Pink = "#FF55FF";         // 13
		public const string Yellow = "#F0F055";       // 14
		public const string White = "#F0F0F0";        // 15

		/// <summary>
		/// The one non-hardware entry: a shaded desktop blue used for zebra
		/// stripes and diagram clusters, where plain blue cannot separate two
		/// adjacent faces.
		/// </summary>
		public const string MidBlue = "#000080";
		public const string DeepBlue = "#000055";
	}

	/// <summary>
	/// Named uses. The tables below refer to these rather than to a hue, so
	/// a whole surface can be retuned without editing a rule.
	/// </summary>
	public static class Semantic
	{
		/// <summary>The editor desktop: a blue field carrying light gray ink.</summary>
		public const string DesktopBackground = Palette.MidBlue;
		public const string DesktopForeground = Palette.LightGray;

		/// <summary>
		/// The Raw tab. It shows the transcript source, so it is drawn the way
		/// the IDE drew a source window: yellow on blue. It deliberately does
		/// not match the Rendered body ink, which is gray, so the two tabs are
		/// never mistaken for one another.
		/// </summary>
		public const string RawBackground = Palette.MidBlue;
		public const string RawForeground = Palette.Yellow;

		/// <summary>The WPF named yellow used by the User Prompt editor.</summary>
		public const string UserPromptForeground = "#FFFF00";

		/// <summary>Recessed panels: code, diagrams, tool output.</summary>
		public const string PanelBackground = Palette.Black;
		public const string PanelBorder = Palette.DarkGray;
		public const string PanelBorderStrong = Palette.LightGray;

		/// <summary>Reverse video: the one selection treatment in the app.</summary>
		public const string SelectionBackground = Palette.LightGray;
		public const string SelectionForeground = Palette.Black;

		/// <summary>State colors.</summary>
		public const string Accent = Palette.Yellow;
		public const string Success = Palette.LightGreen;
		public const string Failure = Palette.LightRed;
		public const string Warning = Palette.Pink;
		public const string Info = Palette.LightCyan;
		public const string Dim = Palette.Brown;
	}

	/// <summary>
	/// One row of the styling table.
	/// </summary>
	/// <param name="Selector">CSS selector. Rendered content always lives under #output.</param>
	/// <param name="Label">What the row is for, for whoever edits it next.</param>
	/// <param name="Foreground">Hex ink, or empty to inherit.</param>
	/// <param name="Background">Hex face, or empty to stay transparent.</param>
	/// <param name="SizeEm">Size relative to the base face; 1.0 leaves it alone.</param>
	/// <param name="Bold">Bold weight.</param>
	/// <param name="Italic">Slanted.</param>
	/// <param name="Decoration">A text-decoration value, or empty for none.</param>
	public sealed record Role(
		string Selector,
		string Label,
		string Foreground = "",
		string Background = "",
		double SizeEm = 1.0,
		bool Bold = false,
		bool Italic = false,
		string Decoration = "");

	/// <summary>
	/// The markdown element table. Edit a row to restyle that element.
	///
	/// Headings step down the classic intensity ladder (yellow, white,
	/// cyan, green, pink, gray). Emphasis is pink, strong is white, links
	/// are light cyan and flip to reverse video under the pointer. Code
	/// drops into a black well, which is how the Turbo editor drew a
	/// nested window on the blue desktop.
	/// </summary>
	public static readonly Role[] Roles =
	{
		new("html, body", "Body copy on the desktop",
			Semantic.DesktopForeground, Semantic.DesktopBackground),

		// Headings
		new("#output h1", "Heading 1: the title of a response",
			Palette.Yellow, "", 1.2, Bold: false),
		new("#output h2", "Heading 2",
			Palette.White, "", 1.2, Bold: false),
		new("#output h3", "Heading 3",
			Palette.LightCyan, "", 1.01, Bold: false),
		new("#output h4", "Heading 4",
			Palette.LightGreen, "", 1.01, Bold: false),
		new("#output h5", "Heading 5",
			Palette.Pink, "", 1.01, Bold: false),
		new("#output h6", "Heading 6: the quietest rung, still legible on blue",
			Palette.LightGray, "", 1.01, Bold: false),

		// Emphasis
		new("#output strong", "Bold",
			Palette.White, "", 1.01, Bold: true),
		new("#output em", "Italic: pink is the emphasis color of the help screens",
			Palette.Pink, "", 1.01, Italic: true),
		new("#output del", "Struck through",
			Palette.LightRed, "", 1.01, Decoration: "line-through"),
		new("#output mark", "Marked text: reverse video, no new hue needed",
			Semantic.SelectionForeground, Semantic.Accent),

		// Links
		new("#output a", "Hyperlink",
			Palette.LightCyan, "", 1.01, Decoration: "underline"),
		// Visited must precede hover, or it wins the tie and a hovered link
		// never lights up.
		new("#output a:visited", "Already opened: dimmed rather than purple",
			Palette.LightGray),
		new("#output a:hover", "Hyperlink under the pointer: full reverse video",
			Palette.Blue, Palette.Yellow, 1.01, Decoration: "underline"),
		new("#output a[href^=\"kp-path:\" i], #output a[href^=\"kp-path%3A\" i]",
			"File-path link: green, the way the editor drew a filename",
			Palette.LightGreen, "", 1.0, Decoration: "underline"),

		// Code. The inline rule deliberately excludes code inside a block so
		// the highlighter keeps control there.
		new("#output :not(pre) > code", "Inline code: yellow ink in a black well",
			Palette.Yellow, Semantic.PanelBackground),
		new("#output pre", "Fenced block: a black editor window on the blue desktop",
			Palette.LightGray, Semantic.PanelBackground),
		new(".kp-code-copy", "Code copy button",
			Palette.LightGray, Semantic.PanelBackground),
		new(".kp-code-copy:hover, .kp-code-copy:focus-visible", "Code copy button under the pointer or keyboard focus",
			Palette.Blue, Palette.Yellow),
		new(".kp-code-copy.kp-code-copy-copied", "Successful code copy",
			Palette.LightGreen, Semantic.PanelBackground),
		new(".kp-code-copy.kp-code-copy-failed", "Failed code copy",
			Palette.LightRed, Semantic.PanelBackground),
		new("#output kbd", "Keycap",
			Palette.White, Palette.DarkGray),

		// Quotations
		new("#output blockquote", "Quote: green, the color of the help text",
			Palette.LightGreen, "", 1.0, Italic: true),

		// Lists
		new("#output li", "List item",
			Semantic.DesktopForeground),
		new("#output li::marker", "Bullet and counter: yellow markers on the blue field",
			Palette.Yellow),

		// Tables
		new("#output th", "Table header: reversed light gray face",
			Semantic.SelectionForeground, Semantic.SelectionBackground, 1.0, Bold: true),
		new("#output td", "Table cell",
			Semantic.DesktopForeground),
		new("#output tbody tr:nth-child(even) td", "Zebra stripe",
			"", Palette.DeepBlue),

		// Media
		new("#output img", "Inline image sits in a black well",
			"", Semantic.PanelBackground),
		new("#output .mermaid-container", "Rendered diagram well",
			Semantic.DesktopForeground, Semantic.PanelBackground),

		// Transcript block kinds
		new(".block-user .block-label", "User prompt label",
			Palette.Yellow),
		new(".block-user .block-content", "User prompt text",
			Palette.White),
		new("#output .kp-user-prompt", "User prompt echo: match the editor's font size and yellow",
			Semantic.UserPromptForeground, "", RawFontSize / BaseFontSizePx),
		new("#output .kp-user-prompt *", "User prompt echo formatting: keep the editor's yellow",
			Semantic.UserPromptForeground),
		new(".block-assistant .block-label", "Assistant label",
			Palette.LightGreen),
		new(".block-assistant .block-content", "Assistant text",
			Semantic.DesktopForeground),
		new(".block-reasoning .block-label, .block-reasoning .block-content",
			"Reasoning: dim and slanted, because it is not the answer",
			Palette.Pink, "", 1.0, Italic: true),
		new(".block-tool", "Tool activity",
			Palette.Yellow, Semantic.PanelBackground),
		new(".block-subagent", "Sub-agent activity",
			Palette.LightCyan, Semantic.PanelBackground),
		new(".tool-success, .subagent-complete", "Step succeeded",
			Palette.LightGreen),
		new(".tool-failure, .subagent-failed", "Step failed",
			Palette.LightRed),
		new(".tool-dim", "Secondary detail inside a tool line",
			Palette.Brown),
		new(".block-error", "Error",
			Palette.LightRed),
		new(".block-status", "Status and meta",
			Semantic.DesktopForeground, "", 1.0, Italic: true),

		// Collapsible sections
		new(".kp-section", "Section shell",
			Semantic.DesktopForeground, Palette.DeepBlue),
		new(".kp-section-summary", "Section summary",
			Palette.LightGray),
		new(".kp-section-reasoning > .kp-section-summary", "Reasoning summary",
			Palette.Pink),
		new(".kp-section-tools > .kp-section-summary", "Tool group summary",
			Palette.Yellow),
		new(".kp-section-summary.kp-summary-failure", "Summary of a group that failed",
			Palette.LightRed),
		new(".kp-section-body", "Section body",
			Semantic.DesktopForeground, Semantic.PanelBackground),

		// Chrome
		new(".kp-zoom-toolbar", "Diagram zoom toolbar",
			"", Semantic.PanelBackground),
		new(".kp-zb", "Zoom button",
			Palette.LightGray, Semantic.PanelBackground),
		new(".kp-zb:hover", "Zoom button under the pointer",
			Palette.Blue, Palette.Yellow),
		new(".kp-thinking", "Waiting-for-first-token pill",
			Palette.Pink, Semantic.PanelBackground),

		// Application notices
		new(".kp-card", "Prompt card: a black panel the reply cannot be mistaken for",
			Semantic.DesktopForeground, Semantic.PanelBackground),
		new(".kp-card-title", "Prompt card heading",
			Semantic.Accent, "", 1.0, Bold: true),
		new(".kp-permission .kp-card-title", "Heading of a card that asks to allow an operation",
			Semantic.Warning, "", 1.0, Bold: true),
		new(".kp-card-detail", "The operation a permission card is about",
			Palette.White),
		new(".kp-card-hint", "How to answer a prompt card",
			Semantic.Dim, "", 1.0, Italic: true),
		new(".kp-plan-done .kp-plan-box, .kp-plan-done .kp-plan-title", "A finished step of the plan",
			Semantic.Dim),
		new(".kp-plan-running .kp-plan-box, .kp-plan-running .kp-plan-title", "The step the agent is on",
			Semantic.Accent, "", 1.0, Bold: true),
		new(".kp-plan-blocked .kp-plan-box, .kp-plan-blocked .kp-plan-title", "A step that cannot proceed",
			Semantic.Warning),
		new(".kp-plan-pending .kp-plan-box, .kp-plan-pending .kp-plan-title", "A step not started yet",
			Semantic.DesktopForeground),
		new(".kp-listing-body", "A listing the program wrote, such as the command list",
			Semantic.DesktopForeground),
		new(".kp-change-added .kp-change-mark", "A file the turn created",
			Semantic.Success),
		new(".kp-change-deleted .kp-change-mark", "A file the turn removed",
			Semantic.Failure),
		new(".kp-change-modified .kp-change-mark", "A file the turn edited",
			Semantic.Accent),
		new(".kp-change-path", "The name of a changed file",
			Semantic.Info),
		new(".kp-change-act, .kp-change-more a", "An action offered on a changed file",
			Semantic.Dim),
		new(".kp-diff-add", "A line the change added",
			Semantic.Success),
		new(".kp-diff-del", "A line the change removed",
			Semantic.Failure),
		new(".kp-diff-hunk", "The position of a change within a file",
			Semantic.Accent),
		new(".kp-diff-meta", "The heading of a diff",
			Semantic.Dim),
		new(".kp-diff-ctx", "A line the change left alone",
			Semantic.DesktopForeground),
		new(".kp-tool-name", "The name of a tool that ran",
			Semantic.Accent, "", 1.0, Bold: true),
		new(".kp-tool-head", "What the tool was asked to do",
			Semantic.DesktopForeground),
		new(".kp-tool > details > summary::before, .kp-tools > summary::before",
			"The open and shut marker on a tool call or a run of them",
			Semantic.Info),
		new(".kp-tools-tag", "The tag on a run of tool calls, as a status line has one",
			Semantic.Accent),
		new(".kp-tools-text", "The calls in a run, counted by name",
			Semantic.DesktopForeground),
		new(".kp-tools-failed", "How many calls in a run failed",
			Semantic.Failure),
		new(".kp-tool-running > details > summary::after, .kp-tools-running > summary::after",
			"The blinking cursor on a call still under way",
			Semantic.Accent),
		// The detail rows name #output so they outrank the fenced block
		// rule, since each is drawn as a pre.
		new("#output .kp-tool-body", "The arguments a tool was given, in a black well",
			Semantic.DesktopForeground, Semantic.PanelBackground),
		new("#output .kp-tool-outcome", "What a tool reported back",
			Semantic.Info, Semantic.PanelBackground),
		new(".kp-tool-failed .kp-tool-name, #output .kp-tool-failed .kp-tool-outcome", "A tool that failed",
			Semantic.Failure),
		new("#output .kp-tool-stopped .kp-tool-outcome", "A tool call that never reported back",
			Semantic.Dim),
		new(".kp-status-tag", "Status tag, such as tool or error",
			Semantic.Accent),
		new(".kp-status-text", "Status text",
			Semantic.DesktopForeground),
		new(".kp-status-error .kp-status-tag, .kp-status-error .kp-status-text", "Reported error",
			Semantic.Failure),
		new(".kp-status-warning .kp-status-tag, .kp-status-warning .kp-status-text", "Reported warning",
			Semantic.Warning),
		new(".kp-status-tool .kp-status-text", "Name of a running tool",
			Semantic.Info),
		new(".kp-status-compacted .kp-status-tag, .kp-status-compacted .kp-status-text", "Context compaction result",
			Semantic.Success),
		new(".kp-status-reset .kp-status-tag, .kp-status-reset .kp-status-text", "Context cleared on request",
			Semantic.Warning),
		new(".kp-status-permissions .kp-status-tag, .kp-status-permissions .kp-status-text", "Permissions restored with a session",
			Semantic.Info),
		new(".kp-status-update .kp-status-tag, .kp-status-update .kp-status-text", "An available tool update",
			Semantic.Accent),
		new(".kp-banner", "Session banner",
			Palette.LightGray, Palette.DeepBlue),

		// Selection
		new("::selection", "Selected text: reverse video",
			Semantic.SelectionForeground, Semantic.SelectionBackground),
	};

	/// <summary>
	/// highlight.js token table: the classic Turbo C and Pascal editor
	/// scheme mapped onto the token classes the bundler emits.
	/// </summary>
	public static readonly Role[] SyntaxRoles =
	{
		new(".hljs", "Code face", Palette.LightGray, Semantic.PanelBackground),
		new(".hljs-keyword, .hljs-built_in, .hljs-literal, .hljs-type",
			"Keywords: white and bold, as the Turbo editor drew them",
			Palette.White, "", 1.0, Bold: true),
		new(".hljs-string, .hljs-regexp, .hljs-addition",
			"Strings", Palette.LightGreen),
		new(".hljs-number, .hljs-symbol, .hljs-bullet",
			"Numbers and bullets", Palette.Pink),
		new(".hljs-comment, .hljs-quote, .hljs-meta",
			"Comments", Palette.Green, "", 1.0, Italic: true),
		new(".hljs-title, .hljs-title.function_, .hljs-title.class_",
			"Function and class names", Palette.Yellow),
		new(".hljs-attr, .hljs-attribute, .hljs-property",
			"Attributes", Palette.LightCyan),
		new(".hljs-variable, .hljs-template-variable, .hljs-selector-tag",
			"Variables and selectors", Palette.LightCyan),
		new(".hljs-name, .hljs-tag, .hljs-selector-id, .hljs-selector-class",
			"Markup names", Palette.LightGreen),
		new(".hljs-deletion", "Removed line", Palette.LightRed),
		new(".hljs-emphasis", "Emphasis inside code", "", "", 1.0, Italic: true),
		new(".hljs-strong", "Strong inside code", "", "", 1.0, Bold: true),
	};

	/// <summary>
	/// Mermaid variables, built from the same palette so a diagram cannot
	/// drift from the text around it. The renderer reads this off
	/// window.BV_THEME when it initializes Mermaid.
	/// </summary>
	public static IReadOnlyDictionary<string, string> MermaidTheme { get; } =
		new Dictionary<string, string>
		{
			["theme"] = "base",
			["background"] = Semantic.PanelBackground,
			["fontFamily"] = FontStack,
			["fontSize"] = "14px",
			["primaryColor"] = Palette.Blue,
			["primaryTextColor"] = Palette.White,
			["primaryBorderColor"] = Palette.LightGray,
			["secondaryColor"] = Palette.Cyan,
			["secondaryTextColor"] = Palette.Black,
			["secondaryBorderColor"] = Palette.LightGray,
			["tertiaryColor"] = Palette.Magenta,
			["tertiaryTextColor"] = Palette.White,
			["tertiaryBorderColor"] = Palette.LightGray,
			["lineColor"] = Palette.LightCyan,
			["textColor"] = Palette.LightGray,
			["mainBkg"] = Palette.Blue,
			["nodeBorder"] = Palette.LightGray,
			["nodeTextColor"] = Palette.White,
			["clusterBkg"] = Palette.DeepBlue,
			["clusterBorder"] = Palette.DarkGray,
			["edgeLabelBackground"] = Semantic.PanelBackground,
			["titleColor"] = Palette.Yellow,
			["errorColor"] = Palette.LightRed,

			// Sequence diagrams
			["actorBkg"] = Palette.Blue,
			["actorTextColor"] = Palette.White,
			["actorBorder"] = Palette.LightGray,
			["actorLineColor"] = Palette.DarkGray,
			["signalColor"] = Palette.LightGray,
			["signalTextColor"] = Palette.Yellow,
			["labelBoxBkgColor"] = Semantic.PanelBackground,
			["labelBoxBorderColor"] = Palette.DarkGray,
			["labelTextColor"] = Palette.LightCyan,
			["loopTextColor"] = Palette.Pink,
			["noteBkgColor"] = Palette.Yellow,
			["noteTextColor"] = Palette.Black,
			["noteBorderColor"] = Palette.Yellow,
			["activationBkgColor"] = Palette.Cyan,
			["activationBorderColor"] = Palette.LightCyan,

			// State diagrams
			["labelColor"] = Palette.White,
			["compositTitleColor"] = Palette.Yellow,
			["dividerColor"] = Palette.DarkGray,

			// Gantt
			["sectionBkgColor"] = Palette.DeepBlue,
			["altSectionBkgColor"] = Semantic.PanelBackground,
			["taskBkgColor"] = Palette.Cyan,
			["taskTextColor"] = Palette.Black,
			["taskTextLightColor"] = Palette.Black,
			["taskTextOutsideColor"] = Palette.LightGray,
			["taskBorderColor"] = Palette.LightGray,
			["activeTaskBkgColor"] = Palette.DarkGray,
			["doneTaskBkgColor"] = Palette.Brown,
			["critBkgColor"] = Palette.Red,
			["gridColor"] = Palette.DarkGray,
			["todayLineColor"] = Palette.LightRed,

			// Pie
			["pie1"] = Palette.Yellow,
			["pie2"] = Palette.LightCyan,
			["pie3"] = Palette.LightGreen,
			["pie4"] = Palette.Pink,
			["pie5"] = Palette.Cyan,
			["pie6"] = Palette.LightRed,
			["pie7"] = Palette.LightGray,
			["pieOuterStrokeColor"] = Palette.Black,
			["pieStrokeColor"] = Palette.Black,
			["pieTitleTextSize"] = "16px",
			["pieSectionTextSize"] = "14px",
			["pieSectionTextColor"] = Palette.Black,
		};

	/// <summary>
	/// The complete stylesheet: font face, palette variables, the role
	/// table and the syntax table.
	/// </summary>
	public static string BuildCss()
	{
		var sb = new StringBuilder();

		sb.Append("@font-face{font-family:'").Append(FontFaceName)
			.Append("';src:url('").Append(FontFileUrl)
			.Append("') format('truetype');font-display:swap;}\n");

		sb.Append(":root{\n");
		foreach (var (name, hex) in PaletteVariables())
		{
			sb.Append("  --bv-").Append(name).Append(':').Append(hex).Append(";\n");
		}
		sb.Append("  --bv-font:").Append(FontStack).Append(";\n");
		sb.Append("  --bv-base-size:").Append(Format(BaseFontSizePx)).Append("px;\n");
		sb.Append("  --bv-line-height:").Append(Format(LineHeight)).Append(";\n");
		sb.Append("}\n");

		sb.Append("html,body{font-family:var(--bv-font);")
			.Append("font-size:var(--bv-base-size);line-height:var(--bv-line-height);}\n");

		AppendScrollBarArrows(sb);

		foreach (var role in Roles)
		{
			AppendRule(sb, role);
		}
		foreach (var role in SyntaxRoles)
		{
			AppendRule(sb, role);
		}

		return sb.ToString();
	}

	/// <summary>
	/// Script that runs before the page's own scripts on every document
	/// creation. It publishes the diagram palette and installs the
	/// stylesheet, so the first paint is already themed.
	/// </summary>
	public static string BuildBootstrapScript()
	{
		var css = JsonSerializer.Serialize(BuildCss());
		var theme = JsonSerializer.Serialize(MermaidTheme);

		var sb = new StringBuilder();
		sb.Append("(function(){\n");
		sb.Append("\twindow.BV_THEME=").Append(theme).Append(";\n");
		sb.Append("\tvar css=").Append(css).Append(";\n");
		sb.Append("\tfunction apply(){\n");
		sb.Append("\t\tvar target=document.head||document.documentElement;\n");
		sb.Append("\t\tif(!target){return false;}\n");
		sb.Append("\t\tvar style=document.createElement('style');\n");
		sb.Append("\t\tstyle.id='bv-theme';\n");
		sb.Append("\t\tstyle.textContent=css;\n");
		sb.Append("\t\ttarget.appendChild(style);\n");
		sb.Append("\t\treturn true;\n");
		sb.Append("\t}\n");
		sb.Append("\tif(!apply()){\n");
		sb.Append("\t\tvar mo=new MutationObserver(function(){if(apply()){mo.disconnect();}});\n");
		sb.Append("\t\tmo.observe(document,{childList:true});\n");
		sb.Append("\t}\n");
		sb.Append("})();\n");
		return sb.ToString();
	}

	/// <summary>Desktop face behind the Raw tab and the WebView2 surface.</summary>
	public static Color DesktopBackgroundColor => ToMediaColor(Semantic.DesktopBackground);

	/// <summary>Body ink of the Rendered tab.</summary>
	public static Color DesktopForegroundColor => ToMediaColor(Semantic.DesktopForeground);

	/// <summary>Face of the Raw tab.</summary>
	public static Color RawBackgroundColor => ToMediaColor(Semantic.RawBackground);

	/// <summary>Ink of the Raw tab.</summary>
	public static Color RawForegroundColor => ToMediaColor(Semantic.RawForeground);

	/// <summary>
	/// The desktop face as a GDI color, used for the WebView2 background so
	/// there is no white flash before the stylesheet lands.
	/// </summary>
	public static System.Drawing.Color DesktopBackgroundColorGdi =>
		ToGdiColor(Semantic.DesktopBackground);

	/// <summary>
	/// Arrow glyphs for the web scroll bar's arrow buttons. The WPF theme
	/// draws CP437 triangles in the stipple color on a solid base face;
	/// Chromium paints no default arrow once a scroll bar button is
	/// styled, so the triangles are baked into data URIs here, where the
	/// palette lives. The button face, edge and hover come from
	/// output.css. The tile is the button's content area: 14x14 for a
	/// vertical button (the 16x16 bar width and row minus the 1px edge)
	/// and 7x14 for a horizontal one (the 9x16 cell minus the edge).
	/// </summary>
	private static void AppendScrollBarArrows(StringBuilder sb)
	{
		string fill = "%23" + Palette.Black.TrimStart('#');

		static string Arrow(int w, int h, string points, string fillColor) =>
			"url(\"data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' width='" + w + "' height='" + h + "'"
			+ "%3E%3Cpolygon points='" + points + "' fill='" + fillColor + "'/%3E%3C/svg%3E\")";

		sb.Append("::-webkit-scrollbar-button:vertical:decrement{background-image:")
			.Append(Arrow(14, 14, "7,4 2,10 12,10", fill)).Append(";}\n");
		sb.Append("::-webkit-scrollbar-button:vertical:increment{background-image:")
			.Append(Arrow(14, 14, "7,10 2,4 12,4", fill)).Append(";}\n");
		sb.Append("::-webkit-scrollbar-button:horizontal:decrement{background-image:")
			.Append(Arrow(7, 14, "1,7 6,3.5 6,10.5", fill)).Append(";}\n");
		sb.Append("::-webkit-scrollbar-button:horizontal:increment{background-image:")
			.Append(Arrow(7, 14, "6,7 1,3.5 1,10.5", fill)).Append(";}\n");
	}

	private static void AppendRule(StringBuilder sb, Role role)
	{
		var body = new StringBuilder();
		if (role.Foreground.Length > 0) body.Append("color:").Append(role.Foreground).Append(';');
		if (role.Background.Length > 0) body.Append("background:").Append(role.Background).Append(';');
		if (Math.Abs(role.SizeEm - 1.0) > 0.001) body.Append("font-size:").Append(Format(role.SizeEm)).Append("em;");
		if (role.Bold) body.Append("font-weight:bold;");
		if (role.Italic) body.Append("font-style:italic;");
		if (role.Decoration.Length > 0) body.Append("text-decoration:").Append(role.Decoration).Append(';');
		if (body.Length == 0) return;

		sb.Append("/* ").Append(role.Label).Append(" */\n")
			.Append(role.Selector).Append('{').Append(body).Append("}\n");
	}

	/// <summary>
	/// Palette variables emitted into :root. Names are kebab-case so they
	/// read as --bv-light-cyan in the inspector.
	/// </summary>
	private static IEnumerable<(string Name, string Hex)> PaletteVariables()
	{
		yield return ("black", Palette.Black);
		yield return ("blue", Palette.Blue);
		yield return ("green", Palette.Green);
		yield return ("cyan", Palette.Cyan);
		yield return ("red", Palette.Red);
		yield return ("magenta", Palette.Magenta);
		yield return ("brown", Palette.Brown);
		yield return ("light-gray", Palette.LightGray);
		yield return ("dark-gray", Palette.DarkGray);
		yield return ("light-blue", Palette.LightBlue);
		yield return ("light-green", Palette.LightGreen);
		yield return ("light-cyan", Palette.LightCyan);
		yield return ("light-red", Palette.LightRed);
		yield return ("pink", Palette.Pink);
		yield return ("yellow", Palette.Yellow);
		yield return ("white", Palette.White);
		yield return ("deep-blue", Palette.DeepBlue);
		yield return ("desktop-bg", Semantic.DesktopBackground);
		yield return ("desktop-fg", Semantic.DesktopForeground);
		yield return ("panel-bg", Semantic.PanelBackground);
		yield return ("panel-border", Semantic.PanelBorder);
		yield return ("panel-border-strong", Semantic.PanelBorderStrong);
		yield return ("selection-bg", Semantic.SelectionBackground);
		yield return ("selection-fg", Semantic.SelectionForeground);
		yield return ("accent", Semantic.Accent);
		yield return ("success", Semantic.Success);
		yield return ("failure", Semantic.Failure);
		yield return ("warning", Semantic.Warning);
		yield return ("info", Semantic.Info);
		yield return ("dim", Semantic.Dim);
	}

	private static string Format(double value) =>
		value.ToString("0.###", CultureInfo.InvariantCulture);

	private static Color ToMediaColor(string hex) =>
		(Color)ColorConverter.ConvertFromString(hex);

	private static System.Drawing.Color ToGdiColor(string hex) =>
		System.Drawing.Color.FromArgb(
			Convert.ToByte(hex.Substring(1, 2), 16),
			Convert.ToByte(hex.Substring(3, 2), 16),
			Convert.ToByte(hex.Substring(5, 2), 16));
}
