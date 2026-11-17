using System.Text;
using GitHub.Copilot.Rpc;

namespace TurboPilot.Ai;

#pragma warning disable GHCP001
internal sealed record BackgroundTaskSnapshot(TaskInfo Task, GitHub.Copilot.Rpc.TaskProgress? Progress);

internal static class BackgroundTaskDiagnostics
{
	internal static string Describe(IReadOnlyList<BackgroundTaskSnapshot> snapshots)
	{
		if (snapshots.Count == 0)
			return "No background tasks are tracked.";

		var output = new StringBuilder();
		foreach (var snapshot in snapshots.OrderBy(item => StartedAt(item.Task)))
		{
			var (id, kind, status, title) = Summary(snapshot.Task);
			output.Append(id).Append("  ").Append(kind).Append("  ").Append(status).Append("\r\n");
			if (!string.IsNullOrWhiteSpace(title))
				output.Append("  ").Append(OneLine(title, 240)).Append("\r\n");
			if (ProgressText(snapshot.Progress) is { Length: > 0 } progress)
				output.Append("  latest: ").Append(OneLine(progress, 400)).Append("\r\n");
		}
		return output.ToString().TrimEnd();
	}

	internal static string? Id(TaskInfo task) => task switch
	{
		TaskInfoAgent agent => agent.Id,
		TaskInfoShell shell => shell.Id,
		TaskInfoClient client => client.Id,
		_ => null,
	};

	private static DateTimeOffset StartedAt(TaskInfo task) => task switch
	{
		TaskInfoAgent agent => agent.StartedAt,
		TaskInfoShell shell => shell.StartedAt,
		TaskInfoClient client => client.StartedAt,
		_ => DateTimeOffset.MaxValue,
	};

	private static (string Id, string Kind, string Status, string Title) Summary(TaskInfo task) => task switch
	{
		TaskInfoAgent agent => (
			agent.Id,
			"agent",
			agent.Status.Value ?? "unknown",
			agent.DisplayName ?? agent.Description ?? agent.AgentType ?? ""),
		TaskInfoShell shell => (
			shell.Id,
			"shell",
			shell.Status.Value ?? "unknown",
			shell.Description ?? shell.Command ?? ""),
		TaskInfoClient client => (
			client.Id,
			"client",
			client.Status.Value ?? "unknown",
			client.DisplayName ?? client.Description ?? ""),
		_ => ("(unknown)", task.Type ?? "task", "unknown", ""),
	};

	private static string ProgressText(GitHub.Copilot.Rpc.TaskProgress? progress) => progress switch
	{
		TaskProgressAgent agent => agent.LatestIntent
			?? agent.RecentActivity?.LastOrDefault()?.Message
			?? "",
		TaskProgressShell shell => shell.RecentOutput ?? "",
		TaskProgressClient client => client.LastMessage
			?? client.RecentActivity?.LastOrDefault()?.Message
			?? client.Phase
			?? "",
		_ => "",
	};

	private static string OneLine(string value, int length) =>
		Rendering.ShortText.Clip(string.Join(" ", value.Split((char[]?)null,
			StringSplitOptions.RemoveEmptyEntries)), length);
}
#pragma warning restore GHCP001
