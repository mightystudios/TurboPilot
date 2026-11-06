# Releasing TurboPilot

TurboPilot releases are built from version tags by
`.github/workflows/release.yml`. The first release format is an unsigned,
self-contained Windows x64 ZIP with a SHA-256 checksum and GitHub build
provenance.

## Prerequisites

- The release commit is on the default branch.
- Local IDE builds use Visual Studio 2026 18.0 or later with the .NET desktop
  development workload. `.vsconfig` declares the required components.
- Its TurbolandWPF submodule commit is available from
  `https://github.com/mighty-studios/TurbolandWPF.git`.
- The tag uses SemVer with a leading `v`, such as `v1.0.0-alpha.2`.
- The repository remains public so standard GitHub-hosted runners and public
  artifact attestations are available without runner charges.

The TurbolandWPF commit must be pushed before the TurboPilot commit that points
to it. Otherwise a clean GitHub checkout cannot initialize the submodule.

## Local release check

Run the same build and test configuration before tagging:

```powershell
dotnet restore .\TurboPilot.sln
dotnet build .\TurboPilot.sln -c Release --no-restore
dotnet run --project .\tests\TurboPilot.Tests -c Release --no-build -- --runtime --ui
```

Validate the self-contained publish:

```powershell
dotnet publish .\src\TurboPilot\TurboPilot.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -o .\artifacts\publish\win-x64
```

The publish folder must contain at least:

- `TurboPilot.exe`
- `web\output.html`
- `Assets\ShowHelp.md`
- `SessionTools\README.md`
- `session-launch-capabilities.json`
- `runtimes\win-x64\native\copilot.exe`

## Create a release

Create and push the tag only after both repositories are available remotely:

```powershell
git tag v1.0.0-alpha.2
git push origin main
git push origin v1.0.0-alpha.2
```

The workflow:

1. Checks out TurboPilot and the TurbolandWPF submodule.
2. Installs the SDK selected by `global.json`.
3. Builds and runs the runtime plus WPF/WebView2 checks.
4. Publishes a self-contained `win-x64` application.
5. Adds license and notice files.
6. Creates a ZIP and SHA-256 checksum.
7. Attests the ZIP's build provenance.
8. Creates a prerelease when the version contains a suffix such as `-alpha.2`.

No temporary Actions artifact is uploaded. The ZIP and checksum go directly to
GitHub Releases.

## Next release steps

The ZIP is intentionally unsigned while the first automated builds are proven.
Before promoting TurboPilot beyond alpha:

1. Configure Microsoft Artifact Signing with GitHub OIDC.
2. Sign executable files before creating the archive.
3. Add a signed installer with WebView2 Runtime detection.
4. Submit the stable installer to WinGet and optionally the Microsoft Store.
