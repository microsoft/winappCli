# WinApp.CLI Tests

This test project provides comprehensive unit tests for the winapp CLI application, focusing on the `SignCommand` functionality and related certificate services.

## Test Structure

### Test Files

- **`SignCommandTests.cs`** - Main test class testing the `sign` command functionality
- **`ManifestCommandTests.cs`** - Tests for manifest generation and manipulation
- **`PackageCommandTests.cs`** - Tests for MSIX package creation
- **`EndToEndTests.cs`** - End-to-end integration tests simulating complete workflows
- **`GlobalTestSetup.cs`** - Global test initialization and cleanup
- **`BaseCommandTests.cs`** - Base class for command tests with service provider setup

### Key Features Tested

#### SignCommand Tests
- ✅ Command argument parsing and validation
- ✅ File path validation (both absolute and relative paths)
- ✅ Certificate file validation
- ✅ Password validation
- ✅ Timestamp URL parameter handling
- ✅ Error handling for missing files/certificates
- ✅ Integration with BuildToolsService and CertificateService

#### Certificate Services Tests
- ✅ Certificate generation using PowerShell
- ✅ Certificate validation and loading
- ✅ Password protection verification
- ✅ Integration with signing operations

#### End-to-End Integration Tests
- ✅ Complete WinForms app creation using `dotnet new winforms`
- ✅ Building .NET applications with `dotnet build`
- ✅ Running `winapp init` to setup workspace
- ✅ Running `winapp package` to create MSIX packages
- ✅ Verification of complete packaging workflow
- ✅ MSIX package content validation

#### Test Infrastructure
- ✅ Temporary directory creation and cleanup
- ✅ Fake executable file creation for testing
- ✅ Test certificate generation during setup
- ✅ Environment isolation using `InternalsVisibleTo`
- ✅ Dotnet CLI integration for E2E tests

## Test Approach

### Realistic Testing Strategy

The tests use a pragmatic approach that acknowledges the complexities of testing code signing operations:

1. **Certificate Generation**: Uses the actual `CertificateService.GenerateDevCertificateAsync()` method to create real test certificates. `BaseCommandTests` turns off the service's `PersistToCurrentUserStore` seam, so generated certificates use an in-memory key and are never added to `Cert:\CurrentUser\My`. Tests that need the real store path must remove exactly what they added (by thumbprint).

2. **File Validation**: Tests file existence, path resolution, and basic validation without requiring real executables.

3. **Command Integration**: Validates the complete command pipeline from argument parsing through to signtool execution.

4. **Error Handling**: Ensures graceful failure handling for various error conditions (missing files, wrong passwords, invalid file formats).

### What The Tests Verify

#### ✅ **Working Components:**
- Command-line argument parsing
- Certificate generation via PowerShell
- File and certificate validation
- BuildTools service integration
- Error handling and user feedback

#### ⚠️ **Expected Limitations:**
- Actual code signing requires real PE executables (our fake files are rejected by signtool)
- BuildTools installation may not be available in test environments
- Network-dependent features (timestamp servers) may be unreliable in CI

## Running the Tests

```bash
# Build the test project
dotnet build src\winapp-CLI\WinApp.Cli.Tests\WinApp.Cli.Tests.csproj

# Run all tests
dotnet test src\winapp-CLI\WinApp.Cli.Tests\WinApp.Cli.Tests.csproj

# Run with verbose output
dotnet test src\winapp-CLI\WinApp.Cli.Tests\WinApp.Cli.Tests.csproj --verbosity normal

# Run specific tests by name pattern
dotnet test src\winapp-CLI\WinApp.Cli.Tests\WinApp.Cli.Tests.csproj --filter "FullyQualifiedName~E2E"
```

## Test Results Summary

Current test coverage includes comprehensive testing across multiple areas:

- **Command parsing and validation** - Sign, Init, Package, Manifest commands
- **Certificate generation and validation** - PowerShell-based cert creation and signing
- **File path handling** - Both absolute and relative paths
- **Error scenarios** - Missing files, wrong passwords, invalid inputs
- **Service integration** - BuildTools, MSIX, Certificate, Config services
- **End-to-end workflows** - Complete app creation → build → init → package flows
- **MSIX package validation** - Package creation and content verification

The E2E tests provide comprehensive coverage of real-world scenarios, ensuring the CLI works correctly for typical developer workflows.

## Code coverage

We gate coverage on the **testable surface** of the CLI, not the raw line count. The raw
`--coverage` denominator is dominated by generated interop (CsWin32 `NativeMethods.g.cs`,
`ComInterfaceGenerator` COM shims for D3D11/UI Automation, `RegexGenerator`) emitted into
`obj\`, which — together with Release-build brace under-counting (see **Why a Debug build**
below) — made the reported number misleading (~18% raw vs. ~59% real over hand-written
source). See issue #630.

### Measuring

```powershell
# Whole CLI, per-directory + top uncovered files, overall %:
pwsh scripts\coverage-report.ps1

# Focus one area and fail under a threshold (what sub-agents use):
pwsh scripts\coverage-report.ps1 -Area Services -Filter "FullyQualifiedName~MsixService" -Threshold 95
```

`build-cli.ps1` runs the suite the same way — a **Debug** build with
`src\winapp-CLI\coverage.runsettings` — so the coverage number CI posts on your PR matches
what `coverage-report.ps1` prints locally.

### Why a Debug build

Coverage is collected on a **Debug** build, not Release. On optimized Release builds the C#
compiler often drops or merges the sequence points for standalone block braces (and duplicate
`return` statements), so the Microsoft coverage engine reports many `{`/`}` lines as `hits=0`
**even when the method fully executes** — systematically under-counting line coverage and
capping control-flow-heavy files around 70–80%. Debug builds map every line faithfully, so the
number reflects what the tests actually exercised. This is the standard configuration for
coverage measurement.

The shipped CLI is still the Release `dotnet publish`, and it's exercised end-to-end by the
sample and npm E2E suites — so moving the C# unit suite to Debug for coverage doesn't drop
Release-artifact validation. The Debug solution build passes `-p:TreatWarningsAsErrors=true`
so it keeps the warning-as-error gate that `Directory.Build.props` otherwise applies only to
Release.

### What's excluded (and why)

**Only generated code is excluded** — via `coverage.runsettings` (`obj\**`, `*.g.cs`,
`*.Designer.cs`, plus the `[GeneratedCode]` attribute). This is the CsWin32 P/Invoke thunks,
`ComInterfaceGenerator` COM shims, and `RegexGenerator` state machines. It isn't hand-written,
so it doesn't belong in the denominator. That change is the biggest single correction to the
raw ~18%; the Debug build (above) closes the remaining brace-undercount gap.

**Hardware / COM / GPU code is _not_ excluded.** `UiAutomationService`, `WgcCapture`, and the
keyboard/mouse input helpers are real product code and stay in the denominator. This foundation
PR sets up the measurement; the tests that cover them land in follow-up PRs, two ways:

1. **Unit tests** for their pure-logic seams (selector parsing, element tree → JSON,
   property/value formatting, foreground classification in `ForegroundGuard`, gesture
   targeting) — these need no live desktop.
2. **A real, in-process UI test** that launches the WinUI sample app and drives the genuine
   `UiAutomationService` — inspect / search / invoke / set-value / wait-for / screenshot, plus
   real type/click through the input helpers. Because it runs in-process in the MSTest host,
   `--coverage` instruments those COM/input paths automatically — no separate collector or
   coverage-merge step. It will be gated to **skip** when no interactive desktop is available,
   so it never blocks a run; its coverage counts when the environment can host it.

> `coverage.runsettings` intentionally contains **no XML comments** — the `--coverage-settings`
> parser rejects them. Document rationale here, not in that file.

### Policy

- **Don't exclude services (or any logic) to grow the number.** The only exclusion is generated
  code. Everything hand-written — including the hardware/COM/GPU interop — stays in the
  denominator and is covered by real tests.
- Write meaningful use-case tests first, then unit tests to close the remaining gaps.

## Framework Used

- **MSTest** - Microsoft's testing framework for .NET
- **System.CommandLine** - For command parsing testing
- **Temporary Files** - Each test uses isolated temporary directories
- **Real Certificate Generation** - Uses actual PowerShell-based certificate creation

This provides a solid foundation for testing CLI functionality while being practical about the limitations of testing code signing operations in a unit test environment.
