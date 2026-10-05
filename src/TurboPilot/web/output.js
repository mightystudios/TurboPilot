/* TurboPilot Output - Rendering Engine
 *
 * The Rendered tab follows the Raw tab: the host appends to both and
 * this file renders its copy as a single markdown document (markdown,
 * Mermaid diagrams, inline images). Every render rebuilds the document,
 * but a diagram already drawn is carried into the new one rather than
 * drawn again (see placeDiagrams). Application notices and file links
 * are marked up on the way in, so the two tabs read the same without
 * being character for character identical. Nothing here is
 * user-editable. Called from MainWindow.xaml.cs via
 * WebView2.ExecuteScriptAsync().
 *
 * Transcript API (primary; keeps Rendered in sync with Raw):
 *   setTranscript(text)                    - replace the whole transcript
 *                                            and render it immediately
 *   appendTranscript(text)                 - append text and re-render
 *                                            (rapid appends are coalesced)
 *   clearAll()                             - clear all output
 *   scrollToTop()                          - show the first line and stop
 *                                            following the bottom
 *   scrollToEnd()                          - show the last line and follow
 *                                            new output
 *
 * Application notices (questions, permission requests, tool calls,
 * errors and session banners) arrive in that same stream as small
 * blocks of literal HTML written by NoticeFormatter.cs: .kp-card with
 * .kp-card-title/.kp-card-detail/.kp-card-choices/.kp-card-hint,
 * .kp-tool rows, .kp-status with .kp-status-tag/.kp-status-text, and
 * .kp-banner. Markdown inside a card is parsed normally because an HTML
 * block ends at the following blank line. The only scripting they get
 * runs after each render: a run of consecutive tool rows is folded into
 * one .kp-tools line, and any disclosure the reader had open is opened
 * again (see foldToolRuns and restoreDisclosures). Their styling is in
 * output.css and BorlandVisionTheme.cs.
 *
 * Block API (structured rendering, reserved for future use):
 *   appendBlock(id, kind, label, content, isMarkdown)
 *                                          - add a new block
 *   updateBlock(id, content)               - update an existing block's content (streaming)
 *   finalizeBlock(id)                      - mark a block as complete (triggers Mermaid)
 *   appendToolStatus(id, html)             - append status HTML to a tool/subagent block
 *   appendSection(id, sectionKind, summaryText, defaultOpen)
 *                                          - add a collapsible <details> section
 *                                            (sectionKind: "reasoning" | "tools")
 *   setSectionContent(id, content, isMarkdown)
 *                                          - replace the section body. If
 *                                            isMarkdown is true, content is
 *                                            run through marked first.
 *   appendSectionLine(sectionId, lineId, html)
 *                                          - append a tool line (<div class="kp-tool-line">)
 *                                            inside a tool group section's body
 *   updateSectionLine(lineId, html)        - replace an existing tool line's innerHTML
 *   markSectionLineFailed(lineId)          - add the failure styling to a tool line
 *   closeSection(id, summaryText, collapse, hasFailure)
 *                                          - rewrite the summary and (optionally) collapse
 *                                            and clear the streaming pulse
 *   appendThinking(id)                     - emit the animated "Thinking..." pill
 *   removeThinking(id)                     - remove the thinking pill
 */

(function () {
	"use strict";

	var outputEl = document.getElementById("output");
	var blocks = {};
	var sections = {};   // id -> { element, bodyEl, kind }
	var lines    = {};   // lineId -> element
	var thinking = {};   // id -> element
	var mermaidLoaded = false;
	var mermaidLoading = false;
	var pendingMermaid = [];

	// Transcript state: the full verbatim text mirrored from the Raw tab.
	var transcriptText = "";
	var transcriptRenderTimer = 0;
	var transcriptDiagramTimer = 0;
	var TRANSCRIPT_RENDER_MS = 40;   // coalesce rapid appends while streaming
	var TRANSCRIPT_DIAGRAM_MS = 300; // convert diagrams once the stream pauses

	// Drawn diagrams, each in its zoom frame, by the key placeDiagrams gives
	// its fenced block. See placeDiagrams.
	var drawnDiagrams = Object.create(null);

	// Debounce timer for streaming renders
	var renderTimers = {};
	var RENDER_DEBOUNCE_MS = 40;

	// Hide broken thumbnails (e.g. when the model mentions a path that
	// doesn't exist yet, or a pasted-image data URI fails to decode).
	// 'error' doesn't bubble, so listen in capture phase.
	document.addEventListener("error", function (e) {
		var t = e.target;
		if (t && t.tagName === "IMG" && t.closest
			&& (t.closest(".block-assistant") || t.closest(".block-user"))) {
			t.classList.add("kp-img-broken");
		}
	}, true);

	// ── File-path link bridge ──────────────────────────────────────
	//
	// FilePathLinkTransformer (C# side) rewrites each resolvable file
	// path as a markdown link of the form [path](kp-path:encoded). After
	// marked.js renders, those become standard <a href="kp-path:..."> tags.
	// These listeners forward left- and right-click intents to the host
	// so it can shell-execute the file or reveal it in Explorer. Anchors
	// are inserted dynamically on every block re-render, so a single
	// document-level delegated listener is the simplest way to keep
	// streaming updates working without rebinding handlers.

	var KP_PATH_SCHEME = "kp-path:";
	var KP_ACT_SCHEME = "kp-act:";

	// Change-card actions. The href carries a verb and a path joined by
	// a bar, so one scheme covers viewing a diff, opening the user's
	// diff tool, reverting a file, and reviewing everything at once.
	function actionFromAnchor(a) {
		if (!a || !a.getAttribute) return null;
		var href = a.getAttribute("href");
		if (!href) return null;
		var lower = href.toLowerCase();
		var prefix = lower.indexOf(KP_ACT_SCHEME) === 0 ? KP_ACT_SCHEME.length
			: lower.indexOf("kp-act%3a") === 0 ? "kp-act%3a".length
				: -1;
		if (prefix < 0) return null;
		var payload = href.substring(prefix);
		try { return decodeURIComponent(payload); }
		catch (_) { return payload; }
	}

	function pathFromAnchor(a) {
		if (!a) return null;
		var href = a.getAttribute("href");
		if (!href) return null;
		// marked may URL-encode the percent prefix when it normalises
		// the href (encodeURI re-encodes existing escapes), so the
		// scheme prefix can show up either bare or as "kp-path%3A".
		// Compare case-insensitively for both forms.
		var lower = href.toLowerCase();
		if (lower.indexOf(KP_PATH_SCHEME) === 0) {
			try { return decodeURIComponent(href.substring(KP_PATH_SCHEME.length)); }
			catch (_) { return href.substring(KP_PATH_SCHEME.length); }
		}
		var encScheme = "kp-path%3a";
		if (lower.indexOf(encScheme) === 0) {
			try { return decodeURIComponent(href.substring(encScheme.length)); }
			catch (_) { return href.substring(encScheme.length); }
		}
		return null;
	}

	function isPathAnchor(a) {
		if (!a || !a.getAttribute) return false;
		var href = a.getAttribute("href");
		if (!href) return false;
		var lower = href.toLowerCase();
		return lower.indexOf(KP_PATH_SCHEME) === 0
			|| lower.indexOf("kp-path%3a") === 0;
	}

	function postPathMessage(type, path) {
		if (!path) return;
		if (!window.chrome || !window.chrome.webview
			|| typeof window.chrome.webview.postMessage !== "function") {
			return;
		}
		try {
			window.chrome.webview.postMessage(JSON.stringify({
				type: type,
				path: path
			}));
		} catch (_) { /* ignore */ }
	}

	document.addEventListener("click", function (e) {
		var a = e.target && e.target.closest && e.target.closest("a[href]");
		var action = actionFromAnchor(a);
		if (action === null) return;
		e.preventDefault();
		e.stopPropagation();
		postPathMessage("action", action);
	});

	document.addEventListener("click", function (e) {
		var a = e.target && e.target.closest && e.target.closest("a[href]");
		if (!isPathAnchor(a)) return;
		e.preventDefault();
		e.stopPropagation();
		postPathMessage("openPath", pathFromAnchor(a));
	});

	// Catch-all: prevent default navigation for ALL anchor clicks.
	// Non-kp-path links (http, https, mailto, etc.) are handled by the
	// C# NavigationStarting handler which opens them in the system
	// browser. This preventDefault ensures the WebView never even
	// attempts a top-level navigation from a click.
	document.addEventListener("click", function (e) {
		var a = e.target && e.target.closest && e.target.closest("a[href]");
		if (!a) return;
		// kp-path links are already fully handled above
		if (isPathAnchor(a) || actionFromAnchor(a) !== null) return;
		e.preventDefault();
		e.stopPropagation();
	});

	document.addEventListener("contextmenu", function (e) {
		var a = e.target && e.target.closest && e.target.closest("a[href]");
		if (!isPathAnchor(a)) return;
		// Suppress the default WebView2 context menu so the user gets
		// the reveal-in-Explorer action without competing UI.
		e.preventDefault();
		e.stopPropagation();
		postPathMessage("revealPath", pathFromAnchor(a));
	});

	// ── Marked configuration ───────────────────────────────────────

	// marked dropped its highlight hook, so coloring happens on the live DOM
	// after each render. See highlightCodeIn().
	marked.setOptions({
		breaks: true,
		gfm: true
	});

	// ── Mermaid lazy loader ────────────────────────────────────────

	function loadMermaid(callback) {
		if (mermaidLoaded) { callback(); return; }
		if (mermaidLoading) { pendingMermaid.push(callback); return; }
		mermaidLoading = true;
		var script = document.createElement("script");
		script.src = "mermaid.min.js";
		script.onload = function () {
			// Colors come from the host theme table (window.BV_THEME is
			// published by the bootstrap script before this page runs), so a
			// diagram can never drift from the text around it.
			var bv = window.BV_THEME || {};
			var themeVariables = Object.assign({}, bv);
			delete themeVariables.theme;
			mermaid.initialize({
				startOnLoad: false,
				theme: bv.theme || "base",
				themeVariables: themeVariables,
				sequence: {
					wrap: true
				}
			});
			mermaidLoaded = true;
			mermaidLoading = false;
			callback();
			for (var i = 0; i < pendingMermaid.length; i++) {
				pendingMermaid[i]();
			}
			pendingMermaid = [];
		};
		script.onerror = function () {
			mermaidLoading = false;
			console.warn("Failed to load mermaid.min.js");
		};
		document.head.appendChild(script);
	}

	// ── Rendering helpers ──────────────────────────────────────────

	function renderMarkdown(content) {
		return marked.parse(content || "");
	}

	function styleUserPromptEchoes(root) {
		var withinPrompt = false;
		for (var node = root.firstChild; node;) {
			var next = node.nextSibling;
			if (node.nodeType === Node.COMMENT_NODE) {
				var marker = node.nodeValue ? node.nodeValue.trim() : "";
				if (marker === "kp-user-prompt-start") {
					withinPrompt = true;
					root.removeChild(node);
				} else if (marker === "kp-user-prompt-end") {
					withinPrompt = false;
					root.removeChild(node);
				}
			} else if (withinPrompt && node.nodeType === Node.ELEMENT_NODE) {
				node.classList.add("kp-user-prompt");
			}
			node = next;
		}
	}

	/**
	 * Runs highlight.js over every fenced block under root. Mermaid fences
	 * are skipped: they are diagrams in waiting, not code, and coloring
	 * their source would fight the renderer.
	 */
	function highlightCodeIn(root) {
		if (!root || !window.hljs) return;
		var codes = root.querySelectorAll("pre code");
		for (var i = 0; i < codes.length; i++) {
			var el = codes[i];
			if (el.classList.contains("language-mermaid")) continue;
			try { hljs.highlightElement(el); } catch (_) { /* leave it plain */ }
		}
	}

	function processMermaidBlocks(containerEl) {
		var codeBlocks = containerEl.querySelectorAll("pre > code.language-mermaid");
		if (codeBlocks.length === 0) return;

		loadMermaid(function () {
			var nodes = [];
			var keys = [];
			for (var i = 0; i < codeBlocks.length; i++) {
				var preEl = codeBlocks[i].parentElement;
				// A render since this pass began has replaced the block.
				if (!containerEl.contains(preEl)) continue;
				var container = document.createElement("div");
				container.className = "mermaid-container";
				container.textContent = codeBlocks[i].textContent;
				preEl.replaceWith(container);
				// The frame goes on first, so the diagram is drawn, and
				// measured, where it will stay. The frame itself measures
				// nothing until the reader pans or zooms.
				makeZoomable(container, "mermaid");
				nodes.push(container);
				keys.push(preEl.getAttribute("data-kp-diagram"));
			}
			if (nodes.length === 0) return;
			var drawing = null;
			try {
				drawing = mermaid.run({ nodes: nodes });
			} catch (e) {
				console.warn("Mermaid render error:", e);
			}
			Promise.resolve(drawing).catch(function (e) {
				console.warn("Mermaid render error:", e);
			}).then(function () {
				// A diagram is kept only if it stayed in the document while
				// it was drawn. One taken out by a later render was measured
				// out of place, and the next pass draws it again.
				for (var j = 0; j < nodes.length; j++) {
					var frame = nodes[j].closest(".kp-zoom-wrap");
					if (keys[j] && frame && nodes[j].isConnected && nodes[j].querySelector("svg"))
						drawnDiagrams[keys[j]] = frame;
				}
				// The drawings have grown the document, so re-anchor to the
				// bottom for a reader who is still pinned there.
				scrollToBottom();
			});
		});
	}

	// Every render rebuilds the document from the transcript text, and
	// drawing a diagram is slow and done out of turn. So a drawn diagram is
	// kept, in its zoom frame, and put back in place of the fenced block the
	// next render makes of the same source. Without that, a streaming render
	// showed every diagram in the transcript as source text, and each tool
	// call that finished drew them all again. Moving the frame rather than a
	// copy keeps its pan and zoom and its handlers. A source that appears
	// twice is kept once per appearance, since an element stands in one
	// place, and one no longer in the transcript is let go.
	function placeDiagrams(root) {
		var codeBlocks = root.querySelectorAll("pre > code.language-mermaid");
		var seen = Object.create(null);
		var present = Object.create(null);
		for (var i = 0; i < codeBlocks.length; i++) {
			var source = codeBlocks[i].textContent;
			seen[source] = (seen[source] || 0) + 1;
			var key = seen[source] + ":" + source;
			present[key] = true;
			var preEl = codeBlocks[i].parentElement;
			if (drawnDiagrams[key]) preEl.replaceWith(drawnDiagrams[key]);
			else preEl.setAttribute("data-kp-diagram", key);
		}
		for (var kept in drawnDiagrams) {
			if (!present[kept]) delete drawnDiagrams[kept];
		}
	}

	function processOversizedBlocks(containerEl) {
		// Pan/zoom is reserved for graphical content (Mermaid SVGs); see
		// processMermaidBlocks(). Text content -- code blocks (<pre>) and
		// tables -- must remain natively scrollable so the user can select,
		// copy, and paste with the wheel and mouse.
		//
		// <pre> blocks: handled entirely in CSS (overflow: auto + max-height
		// on .block-content pre), so no JS wrapping is required.
		//
		// <table>: wrap in a non-intercepting scroll container only when
		// the table actually overflows its available space.
		var MAX_H = 480;
		var tables = containerEl.querySelectorAll("table");
		for (var k = 0; k < tables.length; k++) {
			var tbl = tables[k];
			if (!tbl.parentElement) continue;
			if (tbl.parentElement.classList.contains("kp-scroll-wrap")) continue;
			if (tbl.parentElement.classList.contains("kp-zoom-wrap")) continue;
			if (tbl.scrollWidth > tbl.clientWidth + 1 || tbl.offsetHeight > MAX_H) {
				wrapInScroller(tbl);
			}
		}
	}

	function wrapInScroller(targetEl) {
		if (!targetEl || !targetEl.parentElement) return;
		var wrap = document.createElement("div");
		wrap.className = "kp-scroll-wrap";
		targetEl.parentElement.insertBefore(wrap, targetEl);
		wrap.appendChild(targetEl);
	}

	// ── Pan / Zoom wrapper ─────────────────────────────────────────

	function makeZoomable(targetEl, kind) {
		if (!targetEl || !targetEl.parentElement) return;
		if (targetEl.parentElement.classList.contains("kp-zoom-wrap")) return;

		var wrap = document.createElement("div");
		wrap.className = "kp-zoom-wrap kp-zoom-" + kind;

		var viewport = document.createElement("div");
		viewport.className = "kp-zoom-viewport";

		var inner = document.createElement("div");
		inner.className = "kp-zoom-inner";

		targetEl.parentElement.insertBefore(wrap, targetEl);
		inner.appendChild(targetEl);
		viewport.appendChild(inner);
		wrap.appendChild(viewport);

		// Toolbar
		var toolbar = document.createElement("div");
		toolbar.className = "kp-zoom-toolbar";
		toolbar.innerHTML =
			'<button type="button" class="kp-zb" data-act="in"  title="Zoom in">+</button>' +
			'<button type="button" class="kp-zb" data-act="out" title="Zoom out">-</button>' +
			'<button type="button" class="kp-zb" data-act="fit" title="Reset">Fit</button>' +
			'<button type="button" class="kp-zb" data-act="full" title="Fullscreen">Full</button>';
		wrap.appendChild(toolbar);

		var state = { scale: 1, tx: 0, ty: 0 };

		function apply() {
			inner.style.transform =
				"translate(" + state.tx + "px," + state.ty + "px) scale(" + state.scale + ")";
		}

		function reset() {
			state.scale = 1;
			state.tx = 0;
			state.ty = 0;
			apply();
		}

		function zoomAt(factor, cx, cy) {
			var newScale = Math.max(0.1, Math.min(8, state.scale * factor));
			var k = newScale / state.scale;
			// Keep the point under (cx, cy) stationary in viewport coords.
			state.tx = cx - k * (cx - state.tx);
			state.ty = cy - k * (cy - state.ty);
			state.scale = newScale;
			apply();
		}

		viewport.addEventListener("wheel", function (e) {
			e.preventDefault();
			var rect = viewport.getBoundingClientRect();
			var cx = e.clientX - rect.left;
			var cy = e.clientY - rect.top;
			var factor = e.deltaY < 0 ? 1.15 : 1 / 1.15;
			zoomAt(factor, cx, cy);
		}, { passive: false });

		// Drag-to-pan
		var dragging = false, lastX = 0, lastY = 0;
		viewport.addEventListener("mousedown", function (e) {
			if (e.button !== 0) return;
			dragging = true;
			lastX = e.clientX;
			lastY = e.clientY;
			viewport.classList.add("kp-zoom-dragging");
			e.preventDefault();
		});
		window.addEventListener("mousemove", function (e) {
			if (!dragging) return;
			state.tx += e.clientX - lastX;
			state.ty += e.clientY - lastY;
			lastX = e.clientX;
			lastY = e.clientY;
			apply();
		});
		window.addEventListener("mouseup", function () {
			if (!dragging) return;
			dragging = false;
			viewport.classList.remove("kp-zoom-dragging");
		});

		// Double-click resets
		viewport.addEventListener("dblclick", function (e) {
			e.preventDefault();
			reset();
		});

		// Toolbar handlers
		toolbar.addEventListener("click", function (e) {
			var btn = e.target.closest(".kp-zb");
			if (!btn) return;
			e.stopPropagation();
			var act = btn.getAttribute("data-act");
			var rect = viewport.getBoundingClientRect();
			var cx = rect.width / 2;
			var cy = rect.height / 2;
			if (act === "in") zoomAt(1.25, cx, cy);
			else if (act === "out") zoomAt(1 / 1.25, cx, cy);
			else if (act === "fit") reset();
			else if (act === "full") toggleFullscreen(wrap, reset);
		});
	}

	function toggleFullscreen(wrap, resetFn) {
		var isFull = wrap.classList.toggle("kp-zoom-full");
		if (isFull) {
			document.body.classList.add("kp-zoom-body-locked");
			if (!wrap._kpEsc) {
				wrap._kpEsc = function (e) {
					if (e.key === "Escape") toggleFullscreen(wrap, resetFn);
				};
				document.addEventListener("keydown", wrap._kpEsc);
			}
		} else {
			document.body.classList.remove("kp-zoom-body-locked");
			if (wrap._kpEsc) {
				document.removeEventListener("keydown", wrap._kpEsc);
				wrap._kpEsc = null;
			}
		}
		if (resetFn) resetFn();
	}

	// -- Sticky-scroll state ----------------------------------------
	//
	// Track whether the user is pinned near the bottom of the transcript.
	// The flag is updated ONLY on real scroll events (and on our own
	// programmatic scrolls, which also fire one), so it stays accurate
	// even when the document grows underneath the viewport during
	// streaming.
	//
	// The previous implementation re-evaluated the pinned check AFTER
	// each DOM mutation. That meant a single content addition taller
	// than the slop pushed the apparent bottom out of reach, so the
	// check failed and auto-scroll silently stopped -- forcing the
	// user to scroll down by hand to see new output.
	var stickToBottom = true;
	var SCROLL_SLOP = 80;

	function getScrollHeight() {
		var doc = document.scrollingElement || document.documentElement;
		return doc.scrollHeight;
	}

	function isAtBottom() {
		return (window.innerHeight + window.scrollY)
		     >= (getScrollHeight() - SCROLL_SLOP);
	}

	window.addEventListener("scroll", function () {
		stickToBottom = isAtBottom();
	}, { passive: true });

	// Late-arriving images (markdown thumbnails, pasted-image previews)
	// extend the document height after scrollToBottom() has already
	// run. Re-anchor when each image finishes loading so the user
	// stays at the bottom while content settles. 'load' doesn't bubble,
	// so listen in the capture phase.
	document.addEventListener("load", function (e) {
		var t = e.target;
		if (t && t.tagName === "IMG") {
			scrollToBottom();
		}
	}, true);

	function scrollToBottom() {
		if (!stickToBottom) return;
		window.scrollTo(0, getScrollHeight());
	}

	function escapeHtml(str) {
		var div = document.createElement("div");
		div.textContent = str;
		return div.innerHTML;
	}

	// ── Public API ─────────────────────────────────────────────────

	// ── Transcript API ─────────────────────────────────────────────
	//
	// The host treats the Raw tab as the source of truth and mirrors
	// every append/clear here so both tabs always show the same text,
	// this one rendered for human viewing.

	function renderTranscript(withDiagrams) {
		var shown = disclosureState(outputEl);
		outputEl.innerHTML = renderMarkdown(transcriptText);
		styleUserPromptEchoes(outputEl);
		foldToolRuns(outputEl);
		restoreDisclosures(outputEl, shown);
		highlightCodeIn(outputEl);
		placeDiagrams(outputEl);
		if (withDiagrams) {
			processMermaidBlocks(outputEl);
			processOversizedBlocks(outputEl);
		}
		scrollToBottom();
	}

	// -- Tool rows ---------------------------------------------------
	//
	// NoticeFormatter.cs writes each tool call as a one-line row
	// (.kp-tool). Rows with nothing between them are one burst of work
	// between two passages of the reply, so they are folded under a
	// single line that counts the calls by name. A lone call is left as
	// it is. The fold exists only on screen: the transcript text keeps
	// one row per call, so the Raw tab and a saved session are unchanged.

	function foldToolRuns(root) {
		var runs = [];
		var run = null;
		for (var node = root.firstElementChild; node; node = node.nextElementSibling) {
			if (node.classList.contains("kp-tool")) {
				if (!run) { run = []; runs.push(run); }
				run.push(node);
			} else {
				run = null;
			}
		}
		for (var i = 0; i < runs.length; i++) {
			if (runs[i].length > 1) foldToolRun(runs[i]);
		}
	}

	function foldToolRun(rows) {
		var counts = Object.create(null);
		var names = [];
		var failed = 0;
		var running = false;
		for (var i = 0; i < rows.length; i++) {
			var label = rows[i].querySelector(".kp-tool-name");
			var name = label ? label.textContent : "tool";
			if (!(name in counts)) { counts[name] = 0; names.push(name); }
			counts[name]++;
			if (rows[i].classList.contains("kp-tool-failed")) failed++;
			if (rows[i].classList.contains("kp-tool-running")) running = true;
		}
		var tally = [];
		for (var j = 0; j < names.length; j++) {
			tally.push(counts[names[j]] > 1 ? names[j] + " x" + counts[names[j]] : names[j]);
		}

		var fold = document.createElement("details");
		fold.className = running ? "kp-tools kp-tools-running" : "kp-tools";
		var summary = document.createElement("summary");
		summary.appendChild(textSpan("kp-tools-tag", "tools"));
		summary.appendChild(textSpan("kp-tools-text", rows.length + " calls: " + tally.join(", ")));
		if (failed) summary.appendChild(textSpan("kp-tools-failed", failed + " failed"));
		var body = document.createElement("div");
		body.className = "kp-tools-body";
		fold.appendChild(summary);
		fold.appendChild(body);
		rows[0].parentNode.insertBefore(fold, rows[0]);
		for (var k = 0; k < rows.length; k++) body.appendChild(rows[k]);
	}

	function textSpan(className, text) {
		var span = document.createElement("span");
		span.className = className;
		span.textContent = text;
		return span;
	}

	// -- Open disclosures ---------------------------------------------
	//
	// Every render rebuilds the document, so a disclosure the reader had
	// opened would snap shut the moment more output arrived. Each one is
	// named by its place in the transcript, which later output does not
	// change, and the ones that were open are opened again. A tool row
	// is named by its count among the rows and a fold by its first row,
	// so a fold that gains a row is still the same fold.

	function keyDisclosures(root) {
		var all = root.querySelectorAll("details");
		var rows = 0;
		var others = 0;
		var folds = [];
		for (var i = 0; i < all.length; i++) {
			var item = all[i];
			if (item.classList.contains("kp-tools")) { folds.push(item); continue; }
			var parent = item.parentElement;
			item.setAttribute("data-kp-key", parent && parent.classList.contains("kp-tool")
				? "tool-" + (rows++)
				: "details-" + (others++));
		}
		for (var j = 0; j < folds.length; j++) {
			var first = folds[j].querySelector(".kp-tool > details");
			folds[j].setAttribute("data-kp-key", "tools-" + (first ? first.getAttribute("data-kp-key") : j));
		}
	}

	function disclosureState(root) {
		var state = { open: Object.create(null), seen: Object.create(null) };
		var all = root.querySelectorAll("details[data-kp-key]");
		for (var i = 0; i < all.length; i++) {
			var key = all[i].getAttribute("data-kp-key");
			state.seen[key] = true;
			if (all[i].open) state.open[key] = true;
		}
		return state;
	}

	function restoreDisclosures(root, state) {
		keyDisclosures(root);
		var all = root.querySelectorAll("details[data-kp-key]");
		for (var i = 0; i < all.length; i++) {
			if (state.open[all[i].getAttribute("data-kp-key")]) all[i].open = true;
		}
		// A fold that has just formed around a row the reader had open
		// opens with it, rather than hiding the row it took in.
		var folds = root.querySelectorAll("details.kp-tools");
		for (var j = 0; j < folds.length; j++) {
			if (state.seen[folds[j].getAttribute("data-kp-key")]) continue;
			if (folds[j].querySelector(".kp-tool > details[open]")) folds[j].open = true;
		}
	}

	/**
	 * Replace the entire transcript and render it immediately.
	 * Used for the full re-sync once the page has (re)loaded.
	 */
	window.setTranscript = function (text) {
		if (transcriptRenderTimer) { clearTimeout(transcriptRenderTimer); transcriptRenderTimer = 0; }
		if (transcriptDiagramTimer) { clearTimeout(transcriptDiagramTimer); transcriptDiagramTimer = 0; }
		transcriptText = text || "";
		renderTranscript(true);
	};

	/**
	 * Jump to the top of the transcript and release the bottom pin. Used
	 * once at startup: a document that opens with the app is meant to be
	 * read from its first line, not its last. Scrolling back down re-pins
	 * automatically, so streaming resumes following on its own.
	 */
	window.scrollToTop = function () {
		stickToBottom = false;
		window.scrollTo(0, 0);
	};

	/**
	 * Jump to the end of the transcript and pin later output there. Used
	 * when a saved session resumes as a live conversation.
	 */
	window.scrollToEnd = function () {
		stickToBottom = true;
		scrollToBottom();
	};

	/**
	 * Append text to the transcript. Rapid appends (streaming tokens)
	 * are coalesced: markdown re-renders within ~40ms of the first
	 * delta, and the Mermaid/table pass runs once the stream pauses.
	 */
	window.appendTranscript = function (text) {
		if (!text) return;
		transcriptText += text;
		if (!transcriptRenderTimer) {
			transcriptRenderTimer = setTimeout(function () {
				transcriptRenderTimer = 0;
				renderTranscript(false);
			}, TRANSCRIPT_RENDER_MS);
		}
		if (transcriptDiagramTimer) clearTimeout(transcriptDiagramTimer);
		transcriptDiagramTimer = setTimeout(function () {
			transcriptDiagramTimer = 0;
			renderTranscript(true);
		}, TRANSCRIPT_DIAGRAM_MS);
	};

	/**
	 * Append one or more inline images to an existing block (used for the
	 * user echo of pasted clipboard images, so the user sees a thumbnail
	 * of what they sent). Images are appended into a dedicated sibling
	 * <div class="block-images"> so subsequent updateBlock() calls -- which
	 * regenerate ".block-content" -- never wipe them.
	 *
	 * The images are display-only: nothing here changes what was sent to
	 * the Copilot SDK. The picture bytes still travel as a binary blob
	 * attachment via AttachmentBlob; this function only
	 * mirrors them visually in the transcript.
	 *
	 * @param {string} id     - Block ID previously returned by appendBlock.
	 * @param {Array<{src:string, alt:string, title?:string}>} items
	 *                          One entry per pasted image. "src" is
	 *                          expected to be a data: URI built by the
	 *                          C# side from the on-disk bytes.
	 */
	window.appendBlockImages = function (id, items) {
		var block = blocks[id];
		if (!block || !items || !items.length) return;

		var container = block.element.querySelector(".block-images");
		if (!container) {
			container = document.createElement("div");
			container.className = "block-images";
			block.element.appendChild(container);
		}

		for (var i = 0; i < items.length; i++) {
			var it = items[i] || {};
			if (!it.src) continue;
			var img = document.createElement("img");
			img.className = "kp-pasted-image";
			img.src = it.src;
			if (it.alt)   img.alt = it.alt;
			if (it.title) img.title = it.title;
			container.appendChild(img);
		}

		scrollToBottom();
	};

	/**
	 * Append a new message block.
	 * @param {string} id      - Unique block ID
	 * @param {string} kind    - CSS class suffix (user, assistant, tool, subagent, error, status, reasoning)
	 * @param {string} label   - Header label (e.g. "Assistant:", "Tool: edit_file")
	 * @param {string} content - Initial content (Markdown for assistant, plain text for others)
	 * @param {boolean} isMarkdown - Whether to render content as Markdown
	 */
	window.appendBlock = function (id, kind, label, content, isMarkdown) {
		var blockEl = document.createElement("div");
		blockEl.className = "block block-" + kind;
		blockEl.id = "block-" + id;

		var html = "";
		if (label) {
			html += '<div class="block-label">' + escapeHtml(label) + '</div>';
		}

		html += '<div class="block-content">';
		if (isMarkdown && content) {
			html += renderMarkdown(content);
		} else if (content) {
			html += escapeHtml(content);
		}
		html += '</div>';

		blockEl.innerHTML = html;
		highlightCodeIn(blockEl);
		outputEl.appendChild(blockEl);

		blocks[id] = {
			element: blockEl,
			kind: kind,
			isMarkdown: isMarkdown,
			rawContent: content || ""
		};

		scrollToBottom();
	};

	/**
	 * Update an existing block's content (used for streaming deltas).
	 * Debounced to avoid excessive re-renders during rapid streaming.
	 */
	window.updateBlock = function (id, content) {
		var block = blocks[id];
		if (!block) return;

		block.rawContent = content;

		// Debounce rendering
		if (renderTimers[id]) {
			clearTimeout(renderTimers[id]);
		}
		renderTimers[id] = setTimeout(function () {
			delete renderTimers[id];
			var contentEl = block.element.querySelector(".block-content");
			if (!contentEl) return;

			if (block.isMarkdown) {
				contentEl.innerHTML = renderMarkdown(block.rawContent);
				highlightCodeIn(contentEl);
			} else {
				contentEl.textContent = block.rawContent;
			}
			scrollToBottom();
		}, RENDER_DEBOUNCE_MS);
	};

	/**
	 * Mark a block as complete. Triggers Mermaid rendering for assistant blocks.
	 */
	window.finalizeBlock = function (id) {
		var block = blocks[id];
		if (!block) return;

		// Flush any pending debounced render
		if (renderTimers[id]) {
			clearTimeout(renderTimers[id]);
			delete renderTimers[id];
		}

		var contentEl = block.element.querySelector(".block-content");
		if (!contentEl) return;

		if (block.isMarkdown) {
			contentEl.innerHTML = renderMarkdown(block.rawContent);
			highlightCodeIn(contentEl);
			processMermaidBlocks(contentEl);
			processOversizedBlocks(contentEl);
		}

		scrollToBottom();
	};

	/**
	 * Append raw HTML to an existing block (for tool status, sub-agent updates).
	 */
	window.appendToolStatus = function (id, html) {
		var block = blocks[id];
		if (!block) return;

		var contentEl = block.element.querySelector(".block-content");
		if (!contentEl) return;

		contentEl.insertAdjacentHTML("beforeend", html);
		scrollToBottom();
	};

	/**
	 * Clear all output blocks.
	 */
	window.clearAll = function () {
		outputEl.innerHTML = "";
		blocks = {};
		sections = {};
		lines = {};
		thinking = {};
		for (var key in renderTimers) {
			clearTimeout(renderTimers[key]);
		}
		renderTimers = {};
		transcriptText = "";
		drawnDiagrams = Object.create(null);
		if (transcriptRenderTimer) { clearTimeout(transcriptRenderTimer); transcriptRenderTimer = 0; }
		if (transcriptDiagramTimer) { clearTimeout(transcriptDiagramTimer); transcriptDiagramTimer = 0; }
		// Reset sticky-scroll: an empty document is trivially "at bottom"
		// and the user expects new output to follow.
		stickToBottom = true;
	};

	// ── Collapsible sections (Reasoning / Tool group) ──────────────────

	/**
	 * Append a new collapsible section block.
	 * @param {string} id          - Unique section id
	 * @param {string} sectionKind - "reasoning" | "tools"
	 * @param {string} summaryText - Initial summary line (e.g. "Reasoning...")
	 * @param {boolean} defaultOpen - If true, render with the open attribute
	 */
	window.appendSection = function (id, sectionKind, summaryText, defaultOpen) {
		var details = document.createElement("details");
		details.id = "sec-" + id;
		details.className = "kp-section kp-section-" + sectionKind + " kp-section-active";
		if (defaultOpen) details.open = true;

		var summary = document.createElement("summary");
		summary.className = "kp-section-summary";
		summary.textContent = summaryText || "";

		var body = document.createElement("div");
		body.className = "kp-section-body";

		details.appendChild(summary);
		details.appendChild(body);

		// Re-run scroll/zoom wrapping on any tables or Mermaid diagrams that
		// become visible when the user expands a previously-collapsed section.
		// Code blocks (<pre>) handle their own scrolling via CSS so they need
		// no special treatment here.
		details.addEventListener("toggle", function () {
			if (details.open) {
				try {
					processOversizedBlocks(body);
					processMermaidBlocks(body);
				} catch (_) { /* tolerate; section content may be transient */ }
			}
		});

		outputEl.appendChild(details);
		sections[id] = { element: details, bodyEl: body, kind: sectionKind };
		scrollToBottom();
	};

	/**
	 * Replace a section body's content. When isMarkdown is true the supplied
	 * content is parsed by marked first; otherwise it's treated as raw HTML.
	 * Re-applies scroll/zoom wrapping if the section is currently open so
	 * oversized tables and Mermaid diagrams remain navigable. Code blocks
	 * use native scrolling via CSS, so they are unaffected.
	 */
	window.setSectionContent = function (id, content, isMarkdown) {
		var sec = sections[id];
		if (!sec) return;
		sec.bodyEl.innerHTML = isMarkdown ? renderMarkdown(content) : (content || "");
		if (isMarkdown) highlightCodeIn(sec.bodyEl);
		if (sec.element.open) {
			try {
				processOversizedBlocks(sec.bodyEl);
				processMermaidBlocks(sec.bodyEl);
			} catch (_) { /* see toggle handler */ }
		}
		scrollToBottom();
	};

	/**
	 * Append a new tool line inside a section's body.
	 * @param {string} sectionId - Section id created via appendSection
	 * @param {string} lineId    - Unique line id (typically the tool call id)
	 * @param {string} html      - HTML content for the line (already escaped)
	 */
	window.appendSectionLine = function (sectionId, lineId, html) {
		var sec = sections[sectionId];
		if (!sec) return;
		var line = document.createElement("div");
		line.className = "kp-tool-line";
		line.id = "ln-" + lineId;
		line.innerHTML = html || "";
		sec.bodyEl.appendChild(line);
		lines[lineId] = line;
		scrollToBottom();
	};

	/**
	 * Update an existing tool line's HTML in place (used for ToolProgress
	 * and ToolComplete to keep the line a single tidy entry).
	 */
	window.updateSectionLine = function (lineId, html) {
		var line = lines[lineId];
		if (!line) return;
		line.innerHTML = html || "";
		scrollToBottom();
	};

	/**
	 * Mark a tool line as failed so it stands out when the user expands
	 * a closed group.
	 */
	window.markSectionLineFailed = function (lineId) {
		var line = lines[lineId];
		if (!line) return;
		line.classList.add("kp-tool-line-failed");
	};

	/**
	 * Close a streaming section: rewrite the summary and (optionally)
	 * collapse the body. Always removes the active-pulse class.
	 * @param {string} id          - Section id
	 * @param {string} summaryText - New summary line (e.g. "Read 3 files (8s)")
	 * @param {boolean} collapse   - If true, remove the open attribute
	 * @param {boolean} hasFailure - If true, apply failure-colour styling
	 */
	window.closeSection = function (id, summaryText, collapse, hasFailure) {
		var sec = sections[id];
		if (!sec) return;
		var summary = sec.element.querySelector(".kp-section-summary");
		if (summary) {
			summary.textContent = summaryText || "";
			if (hasFailure) summary.classList.add("kp-summary-failure");
			else            summary.classList.remove("kp-summary-failure");
		}
		sec.element.classList.remove("kp-section-active");
		if (collapse) sec.element.open = false;
		scrollToBottom();
	};

	// ── Thinking indicator ──────────────────────────────────────────

	/**
	 * Emit the animated "Thinking..." pill. Shown for the dead air between
	 * the user pressing Send and the first event of the turn.
	 */
	window.appendThinking = function (id) {
		if (thinking[id]) return;
		var el = document.createElement("div");
		el.className = "kp-thinking";
		el.id = "thk-" + id;
		el.innerHTML =
			'<span class="kp-thinking-dots">' +
				'<span></span><span></span><span></span>' +
			'</span>' +
			'<span>Thinking...</span>';
		outputEl.appendChild(el);
		thinking[id] = el;
		scrollToBottom();
	};

	/**
	 * Remove the thinking pill (called when the first event arrives).
	 */
	window.removeThinking = function (id) {
		var el = thinking[id];
		if (!el) return;
		if (el.parentElement) el.parentElement.removeChild(el);
		delete thinking[id];
	};

})();
