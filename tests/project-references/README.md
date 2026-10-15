# Project-reference build tests

These tests build small packaged WinUI apps that reference other projects, using the real `winapp` CLI. They guard against packaging failures such as `APPX1101: Payload contains two or more files with the same destination path` and `PRI175`/`PRI252`, which happened when Windows App SDK packaging built a referenced library a second time.

Each case checks that:

- `winapp run --no-launch` builds and registers the app,
- `winapp package` works on a clean checkout (selected cases),
- nothing is written into the project, and
- a build that already failed this way recovers without a clean.

The cases cover Windows App SDK 1.6, 1.8 and 2.x, a FluentStore-style reference graph, and a library that sets `CustomAfterDirectoryBuildTargets` itself.

## Run it

Build the CLI first (`.\scripts\build-cli.ps1`), then:

```powershell
Invoke-Pester -Path .\tests\project-references\ProjectReferences.Tests.ps1 -Output Detailed
```

Pass `-WinappPath` (through `New-PesterContainer -Data`) to test another `winapp.exe`. Requires Windows with Developer Mode, the .NET 10 SDK, Pester 5, and access to nuget.org. The suite is skipped when Developer Mode is off.

CI runs it in the `e2e-test-ui` job.
