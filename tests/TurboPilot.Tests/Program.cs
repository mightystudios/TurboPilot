namespace TurboPilot.Tests;

internal static class Program
{
	public static async Task<int> Main(string[] args)
	{
		try
		{
			await CoreChecks.RunAsync();
			RenderingChecks.Run();
			await SessionLaunchChecks.RunAsync(args.Contains("--runtime"));
			if (args.Contains("--runtime"))
			{
				await RuntimeChecks.RunAsync();
				await SessionFeatureChecks.RunAsync();
			}
			if (args.Contains("--ui") || args.Contains("--ui-close"))
				await UiChecks.RunAsync(closingOnly: !args.Contains("--ui"));
			if (args.Contains("--cloud"))
				await RuntimeChecks.RunCloudAsync();
			if (args.Contains("--session-features") && !args.Contains("--runtime"))
				await SessionFeatureChecks.RunAsync();
			Console.WriteLine("All requested checks passed.");
			return 0;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine(ex);
			return 1;
		}
	}
}

internal static class Check
{
	public static void True(bool condition, string message)
	{
		if (!condition)
			throw new InvalidOperationException(message);
	}

	public static void Equal<T>(T expected, T actual, string message)
	{
		if (!EqualityComparer<T>.Default.Equals(expected, actual))
			throw new InvalidOperationException($"{message}: expected '{expected}', got '{actual}'.");
	}

	public static void Throws<T>(Action action) where T : Exception
	{
		try { action(); }
		catch (T) { return; }
		throw new InvalidOperationException($"Expected {typeof(T).Name}.");
	}

	public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
	{
		try { await action(); }
		catch (T) { return; }
		throw new InvalidOperationException($"Expected {typeof(T).Name}.");
	}

	public static async Task UntilAsync(Func<bool> condition, string message, int timeoutSeconds = 15)
	{
		using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
		while (!condition())
		{
			if (timeout.IsCancellationRequested)
				throw new TimeoutException(message);
			await Task.Delay(25);
		}
	}
}
