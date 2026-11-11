[CmdletBinding()]
param(
	[string]$Destination = "",

	[switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\TurboPilot\TurboPilot.csproj"
$solution = Join-Path $repoRoot "TurboPilot.sln"
$testProject = Join-Path $repoRoot "tests\TurboPilot.Tests\TurboPilot.Tests.csproj"
if ([string]::IsNullOrWhiteSpace($Destination)) {
	$Destination = Join-Path (Split-Path -Parent $repoRoot) "Published"
}
$Destination = [IO.Path]::GetFullPath($Destination)
$destinationRoot = [IO.Path]::GetPathRoot($Destination)
if ($Destination -eq $repoRoot -or $Destination -eq $destinationRoot) {
	throw "Refusing unsafe publish destination: $Destination"
}

$running = @(
	Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
		Where-Object {
			$_.ExecutablePath -and
			[IO.Path]::GetFullPath($_.ExecutablePath) -eq
				(Join-Path $Destination "TurboPilot.exe")
		}
)
if ($running.Count -gt 0) {
	throw (
		"TurboPilot is running from '$Destination' (PID(s): " +
		(($running.ProcessId | Sort-Object) -join ", ") +
		"). Close it before publishing.")
}

if (-not $SkipTests) {
	& dotnet restore $solution
	if ($LASTEXITCODE -ne 0) {
		throw "dotnet restore failed with exit code $LASTEXITCODE."
	}
	& dotnet build $solution -c Release --no-restore
	if ($LASTEXITCODE -ne 0) {
		throw "dotnet build failed with exit code $LASTEXITCODE."
	}
	& dotnet run --project $testProject -c Release --no-build
	if ($LASTEXITCODE -ne 0) {
		throw "TurboPilot checks failed with exit code $LASTEXITCODE."
	}
	& python -m unittest discover -s (Join-Path $repoRoot "SessionTools\tests") -v
	if ($LASTEXITCODE -ne 0) {
		throw "SessionTools checks failed with exit code $LASTEXITCODE."
	}
}

$suffix = "{0}-{1}" -f $PID, [Guid]::NewGuid().ToString("N")
$staging = "$Destination.staging-$suffix"
$backup = "$Destination.backup-$suffix"
try {
	& dotnet publish $project `
		-c Release `
		-r win-x64 `
		--self-contained true `
		-p:PublishSingleFile=false `
		-o $staging
	if ($LASTEXITCODE -ne 0) {
		throw "dotnet publish failed with exit code $LASTEXITCODE."
	}

	Copy-Item `
		-LiteralPath (Join-Path $repoRoot "LICENSE.md") `
		-Destination $staging
	Copy-Item `
		-LiteralPath (Join-Path $repoRoot "NOTICE.md") `
		-Destination $staging
	Copy-Item `
		-LiteralPath (Join-Path $repoRoot "licenses.json") `
		-Destination $staging

	$required = @(
		"TurboPilot.exe",
		"TurboPilot.dll",
		"web\output.html",
		"Assets\StartupGreeting.md",
		"Assets\ShowHelp.md",
		"SessionTools\README.md",
		"SessionTools\src\TurboPilotPhaseRunner\phase-runner.py",
		"session-launch-capabilities.json",
		"runtimes\win-x64\native\copilot.exe",
		"LICENSE.md",
		"NOTICE.md",
		"licenses.json"
	)
	foreach ($relativePath in $required) {
		if (-not (Test-Path -LiteralPath (Join-Path $staging $relativePath))) {
			throw "Published output is missing '$relativePath'."
		}
	}
	$capabilities = Get-Content `
		-LiteralPath (Join-Path $staging "session-launch-capabilities.json") `
		-Raw |
		ConvertFrom-Json
	if (
		$capabilities.supportsExternalAssistantMessages -ne $true -or
		$capabilities.externalAssistantMessageContractVersion -cne "1.0"
	) {
		throw "Published output does not advertise external assistant messages."
	}

	$destinationParent = Split-Path -Parent $Destination
	New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
	if (Test-Path -LiteralPath $Destination) {
		Move-Item -LiteralPath $Destination -Destination $backup
	}
	try {
		Move-Item -LiteralPath $staging -Destination $Destination
	}
	catch {
		if (
			-not (Test-Path -LiteralPath $Destination) -and
			(Test-Path -LiteralPath $backup)
		) {
			Move-Item -LiteralPath $backup -Destination $Destination
		}
		throw
	}
	if (Test-Path -LiteralPath $backup) {
		Remove-Item -LiteralPath $backup -Recurse -Force
	}
}
finally {
	if (Test-Path -LiteralPath $staging) {
		Remove-Item -LiteralPath $staging -Recurse -Force
	}
}

Write-Output "Published the validated TurboPilot build to $Destination"
