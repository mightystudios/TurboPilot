using System.Windows;
using GitHub.Copilot;
using TurbolandTheme.Wpf.Controls;

namespace TurboPilot.Dialogs;

/// <summary>
/// Collects a shell credential or confirmation without writing the answer
/// into the transcript or exposing it to the model.
/// </summary>
public partial class ShellAuthenticationDialog : TurbolandFloatingDialog
{
	private readonly ShellAuthenticationInputKind _inputKind;
	private bool _cancelRequested;

	internal ShellAuthenticationDialog(ShellAuthenticationRequest request)
	{
		ArgumentNullException.ThrowIfNull(request);
		InitializeComponent();
		_inputKind = request.InputKind;
		textContext.Text = string.Join(" | ",
			new[] { request.Program, request.Cwd }
				.Where(value => !string.IsNullOrWhiteSpace(value)));
		textPrompt.Text = string.IsNullOrWhiteSpace(request.Prompt)
			? "The command requests authentication."
			: request.Prompt;
		textCommand.Text = string.IsNullOrWhiteSpace(request.Command)
			? ""
			: "Command: " + request.Command;

		if (_inputKind == ShellAuthenticationInputKind.Secret)
		{
			passwordAnswer.Visibility = Visibility.Visible;
		}
		else if (_inputKind == ShellAuthenticationInputKind.Confirm)
		{
			buttonAccept.Content = "Yes";
		}
		else
		{
			textAnswer.Visibility = Visibility.Visible;
		}
	}

	internal string? Answer { get; private set; }

	internal static string? Ask(
		Window owner,
		ShellAuthenticationRequest request,
		CancellationToken cancellationToken)
	{
		if (cancellationToken.IsCancellationRequested)
			return null;
		var dialog = new ShellAuthenticationDialog(request);
		using var registration = cancellationToken.Register(dialog.RequestCancellation);
		var accepted = dialog.ShowDialog(owner) == true;
		var answer = accepted ? dialog.Answer : null;
		dialog.Answer = null;
		dialog.textAnswer.Clear();
		dialog.passwordAnswer.Clear();
		return answer;
	}

	private void OnLoaded(object sender, RoutedEventArgs e)
	{
		if (_cancelRequested)
		{
			Close(false);
			return;
		}
		if (passwordAnswer.Visibility == Visibility.Visible)
			passwordAnswer.Focus();
		else if (textAnswer.Visibility == Visibility.Visible)
			textAnswer.Focus();
		else
			buttonAccept.Focus();
	}

	private void OnAccept(object sender, RoutedEventArgs e)
	{
		Answer = _inputKind == ShellAuthenticationInputKind.Secret
			? passwordAnswer.Password
			: _inputKind == ShellAuthenticationInputKind.Confirm
				? "yes"
				: textAnswer.Text;
		Close(true);
	}

	private void OnCancel(object sender, RoutedEventArgs e) => Close(false);

	private void RequestCancellation()
	{
		_cancelRequested = true;
		if (!Dispatcher.CheckAccess())
		{
			Dispatcher.BeginInvoke(RequestCancellation);
			return;
		}
		if (IsVisible)
			Close(false);
	}
}
