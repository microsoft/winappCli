# Winsdk.CLI Tests

This test project provides comprehensive unit tests for the Winsdk CLI application, focusing on the `SignCommand` functionality and related certificate services.

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
- ✅ Running `winsdk init` to setup workspace
- ✅ Running `winsdk package` to create MSIX packages
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

1. **Certificate Generation**: Uses the actual `CertificateService.GenerateDevCertificateAsync()` method to create real test certificates via PowerShell.

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
dotnet build src\winsdk-CLI\Winsdk.Cli.Tests\Winsdk.Cli.Tests.csproj

# Run all tests
dotnet test src\winsdk-CLI\Winsdk.Cli.Tests\Winsdk.Cli.Tests.csproj

# Run with verbose output
dotnet test src\winsdk-CLI\Winsdk.Cli.Tests\Winsdk.Cli.Tests.csproj --verbosity normal

# Run specific tests by name pattern
dotnet test src\winsdk-CLI\Winsdk.Cli.Tests\Winsdk.Cli.Tests.csproj --filter "FullyQualifiedName~E2E"
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

## Framework Used

- **MSTest** - Microsoft's testing framework for .NET
- **System.CommandLine** - For command parsing testing
- **Temporary Files** - Each test uses isolated temporary directories
- **Real Certificate Generation** - Uses actual PowerShell-based certificate creation

This provides a solid foundation for testing CLI functionality while being practical about the limitations of testing code signing operations in a unit test environment.
