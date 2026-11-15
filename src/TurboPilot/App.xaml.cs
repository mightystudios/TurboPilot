using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TurboPilot.Customizations;
using TurboPilot.Orchestration;
using Theme = TurbolandTheme.Wpf.TurbolandTheme;

namespace TurboPilot;

public partial class App : System.Windows.Application
{
	protected override void OnStartup(StartupEventArgs e)
	{
		base.OnStartup(e);

		CustomizationService.ConfigureProcessEnvironment();
		Theme.Apply(this, TurbolandTheme.Core.ThemeMode.Authentic);
		DisableAutomaticWordSelection(this);

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

	internal static void DisableAutomaticWordSelection(Application application)
	{
		DisableFor(typeof(TextBox));
		DisableFor(typeof(RichTextBox));

		void DisableFor(Type targetType)
		{
			if (application.Resources.Contains(targetType)
				&& application.Resources[targetType] is Style configured
				&& configured.Setters.OfType<Setter>().Any(setter =>
					setter.Property == TextBoxBase.AutoWordSelectionProperty
					&& setter.Value is false))
			{
				return;
			}

			var style = new Style(targetType, application.TryFindResource(targetType) as Style);
			style.Setters.Add(new Setter(TextBoxBase.AutoWordSelectionProperty, false));
			application.Resources[targetType] = style;
		}
	}

	private void FailStartup(Exception exception)
	{
		Console.Error.WriteLine("TurboPilot startup failed: " + exception.Message);
		Shutdown(2);
	}
}
