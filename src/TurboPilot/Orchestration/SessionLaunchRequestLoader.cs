using System.IO;
using System.Text.Json;
using TurboPilot.Customizations;

namespace TurboPilot.Orchestration;

internal sealed record ValidatedSessionLaunchRequest(
	SessionLaunchRequest Request,
	CustomizationLibrary Customizations);

internal sealed class SessionLaunchRequestException : Exception
{
	public SessionLaunchRequestException(string message, SessionLaunchRequest? request = null, Exception? innerException = null)
		: base(message, innerException)
	{
		Request = request;
	}

	public SessionLaunchRequest? Request { get; }
}

internal static class SessionLaunchRequestLoader
{
	private static readonly string[] BuiltInModes = ["Standard", "Plan", "Autopilot"];

	internal static string? GetLaunchRequestPath(IReadOnlyList<string> arguments)
	{
		if (arguments.Count == 0)
			return null;
		if (arguments.Count == 2
			&& string.Equals(arguments[0], "--launch-request", StringComparison.Ordinal)
			&& !string.IsNullOrWhiteSpace(arguments[1]))
			return arguments[1];

		throw new SessionLaunchRequestException(
			"Invalid startup arguments. Use TurboPilot.exe --launch-request <absolute-json-path>.");
	}

	internal static ValidatedSessionLaunchRequest Load(
		string launchRequestPath,
		Func<string, CustomizationLibrary>? collectCustomizations = null)
	{
		var requestPath = CanonicalJsonFile(
			launchRequestPath, "The launch request path", mustExist: true);

		SessionLaunchRequest request;
		try
		{
			request = JsonSerializer.Deserialize<SessionLaunchRequest>(
				File.ReadAllText(requestPath), SessionLaunchRequest.JsonOptions)
				?? throw new SessionLaunchRequestException("The launch request is empty.");
		}
		catch (SessionLaunchRequestException)
		{
			throw;
		}
		catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
		{
			throw new SessionLaunchRequestException(
				$"Cannot read launch request '{requestPath}': {ex.Message}", innerException: ex);
		}

		string statusPath;
		string controlPath;
		try
		{
			statusPath = CanonicalJsonFile(
				request.SessionStatusPath, "The session status path", mustExist: false);
			controlPath = string.IsNullOrWhiteSpace(request.SessionControlPath)
				? ""
				: CanonicalJsonFile(
					request.SessionControlPath, "The session control path", mustExist: false);
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			throw new SessionLaunchRequestException(ex.Message, innerException: ex);
		}

		request = request with
		{
			SessionStatusPath = statusPath,
			SessionControlPath = controlPath,
		};

		void Fail(string message, Exception? innerException = null) =>
			throw new SessionLaunchRequestException(message, request, innerException);

		if (!string.Equals(
			request.ContractVersion?.Trim(),
			SessionLaunchRequest.CurrentContractVersion,
			StringComparison.Ordinal))
		{
			Fail($"Unsupported launch contract version '{request.ContractVersion}'. "
				+ $"TurboPilot supports '{SessionLaunchRequest.CurrentContractVersion}'.");
		}

		var launchId = Required(request.LaunchId, "launchId", Fail);
		var sessionLabel = Required(request.SessionLabel, "sessionLabel", Fail);
		var mode = Required(request.Mode, "mode", Fail);
		if (string.IsNullOrWhiteSpace(request.InitialPrompt))
			Fail("The launch request field 'initialPrompt' must not be empty.");

		string workspace;
		List<string> attachments;
		try
		{
			workspace = CanonicalDirectory(request.Workspace, "The workspace");
			attachments = (request.Attachments ?? [])
				.Select((path, index) => CanonicalFile(path, $"Attachment {index + 1}"))
				.ToList();
		}
		catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
		{
			Fail(ex.Message, ex);
			throw;
		}

		foreach (var attachment in attachments)
		{
			if (!ContainsPath(workspace, attachment))
				Fail($"Attachment '{attachment}' must be contained by workspace '{workspace}'.");
		}
		if (string.Equals(requestPath, statusPath, StringComparison.OrdinalIgnoreCase)
			|| attachments.Contains(statusPath, StringComparer.OrdinalIgnoreCase))
			Fail("The session status path must be different from the launch request and attachment paths.");
		if (controlPath.Length > 0
			&& (string.Equals(requestPath, controlPath, StringComparison.OrdinalIgnoreCase)
				|| string.Equals(statusPath, controlPath, StringComparison.OrdinalIgnoreCase)
				|| attachments.Contains(controlPath, StringComparer.OrdinalIgnoreCase)))
		{
			Fail("The session control path must be different from the launch request, status, and attachment paths.");
		}

		request = request with
		{
			ContractVersion = SessionLaunchRequest.CurrentContractVersion,
			LaunchId = launchId,
			SessionLabel = sessionLabel,
			Workspace = workspace,
			Attachments = attachments,
		};

		CustomizationLibrary customizations;
		try
		{
			customizations = (collectCustomizations ?? CustomizationService.Rescan)(workspace);
		}
		catch (Exception ex)
		{
			Fail("Cannot rescan workspace customizations: " + ex.Message, ex);
			throw;
		}

		var canonicalMode = BuiltInModes.FirstOrDefault(
			item => string.Equals(item, mode, StringComparison.OrdinalIgnoreCase));
		if (canonicalMode is null)
		{
			canonicalMode = customizations.Agents.Values
				.Where(item => item.Enabled)
				.Select(item => item.Name)
				.FirstOrDefault(name => string.Equals(name, mode, StringComparison.OrdinalIgnoreCase));
		}
		if (canonicalMode is null)
			Fail($"Mode '{mode}' is unavailable or disabled in the current customization library.");

		request = request with { Mode = canonicalMode! };
		return new ValidatedSessionLaunchRequest(request, customizations.Clone());
	}

	private static string Required(
		string? value,
		string field,
		Action<string, Exception?> fail)
	{
		if (string.IsNullOrWhiteSpace(value))
			fail($"The launch request field '{field}' is required.", null);
		return value!.Trim();
	}

	private static string CanonicalJsonFile(string path, string description, bool mustExist)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException(description + " is required.");
		if (!Path.IsPathFullyQualified(path))
			throw new ArgumentException(description + " must be absolute.");
		var canonical = Path.GetFullPath(path.Trim());
		if (!string.Equals(Path.GetExtension(canonical), ".json", StringComparison.OrdinalIgnoreCase))
			throw new ArgumentException(description + " must end in .json.");
		if (mustExist && !File.Exists(canonical))
			throw new ArgumentException($"{description} '{canonical}' does not exist.");
		return canonical;
	}

	private static string CanonicalDirectory(string path, string description)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException(description + " is required.");
		if (!Path.IsPathFullyQualified(path))
			throw new ArgumentException(description + " path must be absolute.");
		var canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path.Trim()));
		if (!Directory.Exists(canonical))
			throw new ArgumentException($"{description} '{canonical}' does not exist.");
		return canonical;
	}

	private static string CanonicalFile(string path, string description)
	{
		if (string.IsNullOrWhiteSpace(path))
			throw new ArgumentException(description + " is required.");
		if (!Path.IsPathFullyQualified(path))
			throw new ArgumentException(description + " path must be absolute.");
		var canonical = Path.GetFullPath(path.Trim());
		if (!File.Exists(canonical))
			throw new ArgumentException($"{description} '{canonical}' does not exist.");
		return canonical;
	}

	private static bool ContainsPath(string parent, string path)
	{
		var relative = Path.GetRelativePath(parent, path);
		return !Path.IsPathRooted(relative)
			&& !string.Equals(relative, "..", StringComparison.Ordinal)
			&& !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
			&& !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);
	}
}
