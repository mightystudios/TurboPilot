using System.Windows;
using TurboPilot.Orchestration;
using Theme = TurbolandTheme.Wpf.TurbolandTheme;

namespace TurboPilot;

public partial class App : System.Windows.Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		Theme.Apply(this, TurbolandTheme.Core.ThemeMode.Authentic);

		string? launchRequestPath;
		try
		{
			launchRequestPath = SessionLaunchRequestLoader.GetLaunchRequestPath(e.Args);
		}
		catch (SessionLaunchRequestException ex)
		{
			FailStartup(ex);
			return;
		}

		if (launchRequestPath is null)
		{
			var interactiveWindow = CreateMainWindow();
			interactiveWindow.Show();
			return;
		}

		try
		{
			var launch = SessionLaunchRequestLoader.Load(launchRequestPath);
			var window = CreateMainWindow();
			var status = new SessionStatusController(
				launch.Request, window.RequestLaunchAttention);
			RoutedEventHandler? loaded = null;
			loaded = async (_, _) =>
			{
				window.Loaded -= loaded;
				await window.StartFromLaunchRequestAsync(launch, status);
			};
			window.Loaded += loaded;
			window.Show();
		}
		catch (Exception ex)
		{
			if (ex is SessionLaunchRequestException { Request: { } request })
			{
				try
				{
					new SessionStatusWriter(request).Write(
						SessionStatusStates.Failed, error: ex.Message);
				}
				catch (Exception statusError)
				{
					Console.Error.WriteLine("Cannot write session launch failure status: " + statusError.Message);
				}
			}
			FailStartup(ex);
		}
	}

	private static MainWindow CreateMainWindow()
	{
		var window = new MainWindow();
		Theme.ApplyTo(window);
		return window;
	}

	private void FailStartup(Exception exception)
	{
		Console.Error.WriteLine("TurboPilot startup failed: " + exception.Message);
		Shutdown(2);
	}
}
