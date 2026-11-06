using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace TurboPilot.Rendering;

/// <summary>
/// Configures a WebView2 control as a locked-down TurboPilot Markdown
/// document surface. Session output and Help use the same host so their
/// styling, navigation rules, diagrams, and code-copy controls stay aligned.
/// </summary>
internal sealed class RenderedWebViewHost : IDisposable
{
	private readonly WebView2 _view;
	private readonly string _dataFolder;
	private readonly Action<string?> _openExternal;
	private string? _documentUri;
	private bool _initialized;
	private bool _disposed;

	internal RenderedWebViewHost(
		WebView2 view,
		string dataFolder,
		Action<string?> openExternal)
	{
		_view = view ?? throw new ArgumentNullException(nameof(view));
		_dataFolder = string.IsNullOrWhiteSpace(dataFolder)
			? throw new ArgumentException("A WebView2 data folder is required.", nameof(dataFolder))
			: dataFolder;
		_openExternal = openExternal ?? throw new ArgumentNullException(nameof(openExternal));
		_view.DefaultBackgroundColor = BorlandVisionTheme.DesktopBackgroundColorGdi;
	}

	/// <summary>Raised after the bundled output document and its scripts are ready.</summary>
	internal event Action? Ready;

	/// <summary>Raised when the bundled output document cannot be loaded.</summary>
	internal event Action<string>? Failed;

	/// <summary>
	/// Raised for application-specific path and action messages. External
	/// links and code-copy requests are handled by this host.
	/// </summary>
	internal event Action<string, string>? MessageReceived;

	/// <summary>
	/// Raised after a code block has been copied. A streaming owner can queue
	/// the visual result with its other renderer commands.
	/// </summary>
	internal event Action<string, bool>? CodeCopyCompleted;

	/// <summary>Whether the bundled document has completed navigation.</summary>
	internal bool IsReady { get; private set; }

	/// <summary>
	/// Creates the WebView2 environment, applies the shared renderer policy,
	/// and navigates to the bundled output document.
	/// </summary>
	internal async Task InitializeAsync()
	{
		ObjectDisposedException.ThrowIf(_disposed, this);
		if (_initialized)
			return;
		_initialized = true;

		Directory.CreateDirectory(_dataFolder);
		var environment = await CoreWebView2Environment.CreateAsync(null, _dataFolder);
		await _view.EnsureCoreWebView2Async(environment);
		await _view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(
			BorlandVisionTheme.BuildBootstrapScript());

		var settings = _view.CoreWebView2.Settings;
		settings.AreDefaultContextMenusEnabled = false;
		settings.AreBrowserAcceleratorKeysEnabled = false;
		settings.IsStatusBarEnabled = false;
		settings.AreDevToolsEnabled = false;

		_view.CoreWebView2.WebMessageReceived += OnWebMessageReceived;
		_view.CoreWebView2.NavigationStarting += OnNavigationStarting;
		_view.CoreWebView2.FrameNavigationStarting += OnFrameNavigationStarting;
		_view.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
		_view.CoreWebView2.NavigationCompleted += OnNavigationCompleted;

		var htmlPath = Path.Combine(AppContext.BaseDirectory, "web", "output.html");
		if (!File.Exists(htmlPath))
			throw new FileNotFoundException("The rendered output page is missing.", htmlPath);

		_documentUri = new Uri(htmlPath).AbsoluteUri;
		_view.CoreWebView2.Navigate(_documentUri);
	}

	/// <summary>
	/// Replaces the rendered document with Markdown and scrolls it to the
	/// beginning. Intended for complete, non-streaming documents such as Help.
	/// </summary>
	internal async Task SetDocumentAsync(string markdown)
	{
		if (!IsReady || _view.CoreWebView2 is null)
			throw new InvalidOperationException("The rendered document is not ready.");

		var text = JsonSerializer.Serialize(markdown ?? string.Empty);
		await _view.CoreWebView2.ExecuteScriptAsync(
			$"setTranscript({text});scrollToTop();");
	}

	private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
	{
		string json;
		try
		{
			json = e.TryGetWebMessageAsString();
		}
		catch (Exception ex) when (ex is InvalidOperationException
			or System.Runtime.InteropServices.COMException)
		{
			return;
		}
		if (string.IsNullOrWhiteSpace(json))
			return;

		try
		{
			using var document = JsonDocument.Parse(json);
			var root = document.RootElement;
			if (!root.TryGetProperty("type", out var typeElement))
				return;

			var type = typeElement.GetString();
			if (type == "copyCode"
				&& root.TryGetProperty("id", out var idElement)
				&& root.TryGetProperty("text", out var textElement))
			{
				var id = idElement.GetString();
				var text = textElement.GetString();
				if (!string.IsNullOrEmpty(id) && text is not null)
					CopyCode(id, text);
				return;
			}

			if (!root.TryGetProperty("path", out var pathElement))
				return;
			var path = pathElement.GetString();
			if (string.IsNullOrEmpty(type) || string.IsNullOrEmpty(path))
				return;

			if (type == "openUri")
				_openExternal(path);
			else
				MessageReceived?.Invoke(type, path);
		}
		catch (Exception ex) when (ex is JsonException or InvalidOperationException)
		{
			// Renderer messages are untrusted input. Ignore malformed JSON.
		}
	}

	private void CopyCode(string id, string text)
	{
		var succeeded = true;
		try
		{
			Clipboard.SetText(text);
		}
		catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException
			or ThreadStateException)
		{
			succeeded = false;
		}

		if (CodeCopyCompleted is { } completed)
			completed(id, succeeded);
		else
			_ = SetCodeCopyResultAsync(id, succeeded);
	}

	private async Task SetCodeCopyResultAsync(string id, bool succeeded)
	{
		try
		{
			if (IsReady && _view.CoreWebView2 is not null)
			{
				await _view.CoreWebView2.ExecuteScriptAsync(
					$"setCodeCopyResult({JsonSerializer.Serialize(id)}, {(succeeded ? "true" : "false")})");
			}
		}
		catch (Exception ex) when (ex is InvalidOperationException
			or System.Runtime.InteropServices.COMException)
		{
			// The window may have closed before the copy result was painted.
		}
	}

	private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
	{
		if (string.IsNullOrEmpty(e.Uri))
			return;
		if (_documentUri is not null
			&& e.Uri.Equals(_documentUri, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
			return;

		e.Cancel = true;
		_openExternal(e.Uri);
	}

	private static void OnFrameNavigationStarting(
		object? sender,
		CoreWebView2NavigationStartingEventArgs e)
	{
		if (string.IsNullOrEmpty(e.Uri)
			|| e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
		{
			return;
		}
		e.Cancel = true;
	}

	private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
	{
		if (_documentUri is null || _disposed)
			return;
		if (!e.IsSuccess)
		{
			if (e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)
			{
				IsReady = false;
				Failed?.Invoke($"Rendered output failed to load: {e.WebErrorStatus}");
			}
			return;
		}

		var currentUri = _view.CoreWebView2.Source;
		if (currentUri is not null
			&& currentUri.Equals(_documentUri, StringComparison.OrdinalIgnoreCase))
		{
			IsReady = true;
			Ready?.Invoke();
			return;
		}

		IsReady = false;
		_view.CoreWebView2.Navigate(_documentUri);
	}

	private void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
	{
		e.Handled = true;
		_openExternal(e.Uri);
	}

	public void Dispose()
	{
		if (_disposed)
			return;
		_disposed = true;
		IsReady = false;

		if (_view.CoreWebView2 is not null)
		{
			_view.CoreWebView2.WebMessageReceived -= OnWebMessageReceived;
			_view.CoreWebView2.NavigationStarting -= OnNavigationStarting;
			_view.CoreWebView2.FrameNavigationStarting -= OnFrameNavigationStarting;
			_view.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
			_view.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
		}
		_view.Dispose();
	}
}
