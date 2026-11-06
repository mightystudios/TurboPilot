using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using TurbolandTheme.Wpf.Controls;
using TurboPilot.Rendering;

namespace TurboPilot.Dialogs;

/// <summary>
/// Modeless Help window rendered with the same Markdown, Mermaid, syntax
/// highlighting, link handling, and theme as the main Rendered output.
/// </summary>
public partial class HelpWindow : TurbolandWindow
{
	private readonly string _markdown;
	private readonly Action<ProcessStartInfo> _launchExternal;
	private readonly RenderedWebViewHost _renderer;
	private bool _closed;

	internal HelpWindow(
		string markdown,
		string webViewDataFolder,
		Action<ProcessStartInfo>? launchExternal = null)
	{
		_markdown = markdown ?? throw new ArgumentNullException(nameof(markdown));
		_launchExternal = launchExternal ?? (startInfo => Process.Start(startInfo)?.Dispose());
		InitializeComponent();
		_renderer = new RenderedWebViewHost(webViewHelp, webViewDataFolder, OpenExternal);
		_renderer.Ready += Renderer_Ready;
		_renderer.Failed += ShowFailure;
		_ = InitializeRendererAsync();
	}

	internal bool IsRendererReady => _renderer.IsReady;

	private async Task InitializeRendererAsync()
	{
		try
		{
			await _renderer.InitializeAsync();
		}
		catch (Exception ex)
		{
			if (!_closed)
				ShowFailure($"Help could not start its rendered view.\r\n\r\n{ex.Message}");
		}
	}

	private void Renderer_Ready()
	{
		if (!_closed)
			_ = ShowDocumentAsync();
	}

	private async Task ShowDocumentAsync()
	{
		try
		{
			await _renderer.SetDocumentAsync(_markdown);
		}
		catch (Exception ex) when (ex is InvalidOperationException
			or System.Runtime.InteropServices.COMException)
		{
			ShowFailure($"Help could not render its document.\r\n\r\n{ex.Message}");
		}
	}

	private void ShowFailure(string message)
	{
		if (_closed)
			return;
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(new Action<string>(ShowFailure), message);
			return;
		}
		errorText.Text = message;
		errorPanel.Visibility = Visibility.Visible;
	}

	private void OpenExternal(string? url)
	{
		var startInfo = MainWindow.ExternalLinkCommand(url);
		if (startInfo is null)
			return;

		try
		{
			_launchExternal(startInfo);
		}
		catch (Exception ex) when (ex is Win32Exception
			or InvalidOperationException
			or ArgumentException
			or NotSupportedException
			or IOException
			or UnauthorizedAccessException
			or System.Security.SecurityException)
		{
			MessageDialog.Ok(this,
				$"Could not open this link:\r\n{url}\r\n\r\n{ex.Message}",
				"Help");
		}
	}

	protected override void OnClosed(EventArgs e)
	{
		_closed = true;
		_renderer.Dispose();
		base.OnClosed(e);
	}
}
