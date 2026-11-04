namespace TurboPilot.Sessions;

public sealed class OutputPageRecord
{
	public required string Id { get; init; }
	public int RawStart { get; set; }
	public int RenderedStart { get; set; }
}

public sealed record OutputPageContent(string Id, string Raw, string Rendered);

internal static class OutputPagePersistence
{
	internal const int CurrentFormatVersion = 1;

	internal static List<OutputPageContent> Load(
		SessionRecord record,
		string raw,
		string rendered,
		out List<OutputPageRecord> pageRecords)
	{
		if (record.OutputPageFormatVersion == CurrentFormatVersion
			&& TryValidate(record.OutputPages, raw, rendered, out pageRecords))
		{
			return Slice(pageRecords, raw, rendered);
		}

		if (TryInfer(record, raw, rendered, out pageRecords))
			return Slice(pageRecords, raw, rendered);

		pageRecords =
		[
			new OutputPageRecord
			{
				Id = PageId(record.SessionId, 0),
				RawStart = 0,
				RenderedStart = 0,
			},
		];
		return Slice(pageRecords, raw, rendered);
	}

	internal static List<OutputPageRecord> Clone(IReadOnlyList<OutputPageRecord> records) =>
		records.Select(record => new OutputPageRecord
		{
			Id = record.Id,
			RawStart = record.RawStart,
			RenderedStart = record.RenderedStart,
		}).ToList();

	internal static string PageId(string sessionId, int index) => $"{sessionId}-page-{index:D6}";

	private static bool TryValidate(
		IReadOnlyList<OutputPageRecord>? records,
		string raw,
		string rendered,
		out List<OutputPageRecord> validated)
	{
		validated = [];
		if (records is not { Count: > 0 }
			|| records[0].RawStart != 0
			|| records[0].RenderedStart != 0)
		{
			return false;
		}

		var ids = new HashSet<string>(StringComparer.Ordinal);
		var rawStart = 0;
		var renderedStart = 0;
		for (var index = 0; index < records.Count; index++)
		{
			var record = records[index];
			if (string.IsNullOrWhiteSpace(record.Id)
				|| !ids.Add(record.Id)
				|| record.RawStart < rawStart
				|| record.RenderedStart < renderedStart
				|| record.RawStart > raw.Length
				|| record.RenderedStart > rendered.Length
				|| (index > 0
					&& (!raw.AsSpan(record.RawStart).StartsWith("\r\n**You:** ", StringComparison.Ordinal)
						|| !rendered.AsSpan(record.RenderedStart).StartsWith(
							"\r\n<!--kp-user-prompt-start-->\r\n",
							StringComparison.Ordinal))))
			{
				validated = [];
				return false;
			}

			validated.Add(new OutputPageRecord
			{
				Id = record.Id,
				RawStart = record.RawStart,
				RenderedStart = record.RenderedStart,
			});
			rawStart = record.RawStart;
			renderedStart = record.RenderedStart;
		}
		return true;
	}

	private static bool TryInfer(
		SessionRecord record,
		string raw,
		string rendered,
		out List<OutputPageRecord> pageRecords)
	{
		pageRecords = [];
		if (record.Prompts.Count == 0)
			return false;

		var rawStarts = new List<int>(record.Prompts.Count);
		var renderedStarts = new List<int>(record.Prompts.Count);
		var rawCursor = 0;
		var renderedCursor = 0;
		foreach (var prompt in record.Prompts)
		{
			var rawEcho = $"\r\n**You:** {prompt}\r\n\r\n";
			var renderedEcho = $"\r\n<!--kp-user-prompt-start-->\r\n\r\n**You:** {prompt}"
				+ "\r\n\r\n<!--kp-user-prompt-end-->\r\n\r\n";
			var rawStart = raw.IndexOf(rawEcho, rawCursor, StringComparison.Ordinal);
			var renderedStart = rendered.IndexOf(renderedEcho, renderedCursor, StringComparison.Ordinal);
			if (rawStart < 0 || renderedStart < 0)
			{
				pageRecords = [];
				return false;
			}

			rawStarts.Add(rawStart);
			renderedStarts.Add(renderedStart);
			rawCursor = rawStart + rawEcho.Length;
			renderedCursor = renderedStart + renderedEcho.Length;
		}

		var pagePromptIndexes = RequestPromptIndexes(record);
		if (pagePromptIndexes is null || pagePromptIndexes.Count == 0)
			return false;

		var firstPrompt = pagePromptIndexes[0];
		var index = 0;
		if (rawStarts[firstPrompt] > 0 || renderedStarts[firstPrompt] > 0)
		{
			pageRecords.Add(new OutputPageRecord
			{
				Id = PageId(record.SessionId, index++),
				RawStart = 0,
				RenderedStart = 0,
			});
		}

		foreach (var promptIndex in pagePromptIndexes)
		{
			var rawStart = rawStarts[promptIndex];
			var renderedStart = renderedStarts[promptIndex];
			if (pageRecords.LastOrDefault() is { } previous
				&& previous.RawStart == rawStart
				&& previous.RenderedStart == renderedStart)
			{
				continue;
			}
			pageRecords.Add(new OutputPageRecord
			{
				Id = PageId(record.SessionId, index++),
				RawStart = rawStart,
				RenderedStart = renderedStart,
			});
		}
		return pageRecords.Count > 0;
	}

	private static List<int>? RequestPromptIndexes(SessionRecord record)
	{
		if (record.Requests.Count == 0)
			return Enumerable.Range(0, record.Prompts.Count).ToList();

		var matches = new List<List<int>>(2);
		FindMatches(0, 0, []);
		return matches.Count == 1 ? matches[0] : null;

		void FindMatches(int promptStart, int requestIndex, List<int> current)
		{
			if (matches.Count > 1)
				return;
			if (requestIndex == record.Requests.Count)
			{
				matches.Add([.. current]);
				return;
			}

			var remainingRequests = record.Requests.Count - requestIndex;
			for (var promptIndex = promptStart;
				promptIndex <= record.Prompts.Count - remainingRequests;
				promptIndex++)
			{
				if (!string.Equals(
					record.Prompts[promptIndex],
					record.Requests[requestIndex],
					StringComparison.Ordinal))
				{
					continue;
				}
				current.Add(promptIndex);
				FindMatches(promptIndex + 1, requestIndex + 1, current);
				current.RemoveAt(current.Count - 1);
			}
		}
	}

	private static List<OutputPageContent> Slice(
		IReadOnlyList<OutputPageRecord> records,
		string raw,
		string rendered)
	{
		var result = new List<OutputPageContent>(records.Count);
		for (var index = 0; index < records.Count; index++)
		{
			var record = records[index];
			var rawEnd = index + 1 < records.Count ? records[index + 1].RawStart : raw.Length;
			var renderedEnd = index + 1 < records.Count ? records[index + 1].RenderedStart : rendered.Length;
			result.Add(new OutputPageContent(
				record.Id,
				raw[record.RawStart..rawEnd],
				Rendering.NoticeFormatter.Repair(rendered[record.RenderedStart..renderedEnd])));
		}
		return result;
	}
}
