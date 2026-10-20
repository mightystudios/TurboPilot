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
}
