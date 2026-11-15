using TurboPilot.Ai;

namespace TurboPilot.Orchestration;

internal static class SessionLaunch
{
	internal static Task SubmitInitialPromptAsync(
		ChatService chat,
		SessionLaunchRequest request,
		CancellationToken cancellationToken = default) =>
		chat.SendAsync(
			request.InitialPrompt,
			request.Attachments.Count == 0 ? null : request.Attachments,
			cancellationToken);

	internal static Task SubmitContinuationAsync(
		ChatService chat,
		SessionControlCommand command,
		CancellationToken cancellationToken = default) =>
		chat.SendAsync(
			command.Prompt ?? "",
			command.Attachments.Count == 0 ? null : command.Attachments,
			cancellationToken);
}
