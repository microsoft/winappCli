# `winapp run` architecture matrix

This suite runs `winapp run` against generated .NET projects that cover how the target architecture reaches MSBuild, then checks that each app:

1. builds and registers (or launches unpackaged) without errors,
2. conveys the architecture the expected way: `-p:Platform` alone, or `-r win-<arch>` for fixtures marked `ExpectRid` (projects that set their own `RuntimeIdentifier` or enable `EnableDynamicPlatformResolution`),
3. starts and shows a window (windowed apps, when this machine can run the architecture), and
4. is a PE image for the requested `--arch`.

It guards the failure class behind [FluentStore-style](https://github.com/yoshiask/FluentStore) build errors such as `APPX1101: Payload contains two or more files with the same destination path`, `PRI175`/`PRI252` and `MSB3030`, which depend on how project references, packaging targets and `Platform`/`RuntimeIdentifier` interact.

## Run it

Build the CLI first (`.\scripts\build-cli.ps1`, or `dotnet build src\winapp-CLI\WinApp.Cli -c Debug -o <dir>`), then:

```powershell
# Every fixture for the host architecture, using artifacts\cli\win-<arch>\winapp.exe
Invoke-Pester -Path .\tests\project-arch\ProjectArch.Tests.ps1 -Output Detailed

# A specific CLI build, some fixtures, another architecture
$container = New-PesterContainer -Path .\tests\project-arch\ProjectArch.Tests.ps1 -Data @{
    WinappPath = '.\artifacts\cli\win-x64'
    Fixture    = 'R*', 'P08*'
    Architecture = 'x64'
}
Invoke-Pester -Container $container -Output Detailed
```

| Parameter | Default | Meaning |
|---|---|---|
| `WinappPath` | `artifacts\cli\win-<host>\winapp.exe`, then `winapp` on `PATH` | `winapp.exe` or its folder |
| `Architecture` | host architecture | value passed to `--arch` |
| `Fixture` | `*` | wildcard patterns over fixture IDs |
| `Shard` | all | `N/M`: every M-th fixture starting at N, used by CI |
| `WorkRoot` | `%TEMP%\winapp-project-arch` | where fixtures are generated |
| `TimeoutMinutes` | `15` | per-fixture limit for `winapp run` |
| `SkipCleanup` | off | keep generated fixtures and `winapp-run.log` |

Requirements: Windows with Developer Mode, the .NET 10 SDK plus the .NET 8 runtime (some fixtures target `net8.0`), Pester 5, and access to nuget.org. Fixtures run one at a time; each generated package is unregistered and its folder deleted afterwards. An architecture the machine can't run (for example `arm64` on an x64 host) is built and registered with `--no-launch`, and only the executable architecture is checked.

## Fixtures

`ProjectArch.psm1` defines the catalog in `Get-ProjectArchFixtures`. Each entry states what it guards. The axes are:

- **App:** packaged WinUI, Windows App SDK self-contained, unpackaged, WPF, console; `<Platforms>` in the project, in `Directory.Build.props`, absent, or missing the target architecture.
- **References:** `netstandard2.0`, WinUI libraries with and without `<Platforms>`/`<RuntimeIdentifiers>`, libraries that enable MSIX tooling, multi-targeted, diamond, `GlobalPropertiesToRemove="RuntimeIdentifier"`, `AnyCPU`-only, and a solution.
- **Project settings:** .NET `SelfContained`, `PublishAot` built without `--aot`, a hard-coded or `win-$(Platform)` `RuntimeIdentifier`, Visual Studio publish profiles, a trimmed build whose `$(Platform)` profile makes it self-contained, and `EnableDynamicPlatformResolution`.
- **Windows App SDK:** 1.6, 1.8 and 2.x.

To cover a new shape, add an entry with a short `Why`, run it against the current CLI, and confirm it fails without the fix it protects.
