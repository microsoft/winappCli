// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Fake DotNetService that delegates file-based operations to the real DotNetService
/// but fakes CLI-based operations (dotnet add package, dotnet list, etc.).
/// Tracks which packages were added for test assertions.
/// </summary>
internal class FakeDotNetService : IDotNetService
{
    private readonly DotNetService _real = new();

    /// <summary>
    /// Tracks packages added via AddOrUpdatePackageReferenceAsync
    /// </summary>
    public List<(string CsprojPath, string PackageName, string? Version)> AddedPackages { get; } = [];

    /// <summary>
    /// Set this to control what GetPackageListAsync returns.
    /// When null, the method returns null (default behavior).
    /// </summary>
    public DotNetPackageListJson? PackageListResult { get; set; }

    /// <summary>
    /// When true, <see cref="GetPackageListAsync"/> throws, simulating a failure of
    /// <c>dotnet list package --format json</c> (e.g. a project that fails to restore).
    /// </summary>
    public bool ThrowOnGetPackageList { get; set; }

    /// <summary>
    /// Number of times <see cref="GetPackageListAsync"/> has been invoked.
    /// </summary>
    public int GetPackageListCallCount { get; private set; }

    /// <summary>
    /// Packages listed here will cause <see cref="AddOrUpdatePackageReferenceAsync"/> to throw,
    /// simulating failures from <c>dotnet add package</c>.
    /// </summary>
    public HashSet<string> PackagesToThrowOnAdd { get; } = new(StringComparer.OrdinalIgnoreCase);

    // Delegate file-based operations to real implementation
    public IReadOnlyList<FileInfo> FindCsproj(DirectoryInfo directory) => _real.FindCsproj(directory);
    public string? GetTargetFramework(FileInfo csprojPath) => _real.GetTargetFramework(csprojPath);
    public bool IsMultiTargeted(FileInfo csprojPath) => _real.IsMultiTargeted(csprojPath);
    public bool IsTargetFrameworkSupported(string targetFramework) => _real.IsTargetFrameworkSupported(targetFramework);
    public string GetRecommendedTargetFramework(string? currentTargetFramework = null) => _real.GetRecommendedTargetFramework(currentTargetFramework);
    public void SetTargetFramework(FileInfo csprojPath, string newTargetFramework) => _real.SetTargetFramework(csprojPath, newTargetFramework);
    public Task<bool> UpdatePublishProfileAsync(FileInfo csprojPath, CancellationToken cancellationToken = default) => _real.UpdatePublishProfileAsync(csprojPath, cancellationToken);
    public Task<bool> EnsureRuntimeIdentifierAsync(FileInfo csprojPath, CancellationToken cancellationToken = default) => _real.EnsureRuntimeIdentifierAsync(csprojPath, cancellationToken);
    public Task<bool> HasPackageReferenceAsync(FileInfo csprojPath, string packageName, CancellationToken cancellationToken = default)
    {
        if (PackageListResult?.Projects is null)
        {
            return Task.FromResult(false);
        }

        var found = PackageListResult.Projects
            .SelectMany(p => p.Frameworks ?? [])
            .SelectMany(f => f.TopLevelPackages ?? [])
            .Any(pkg => string.Equals(pkg.Id, packageName, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(found);
    }

    // Fake CLI-based operations
    public Task<string> AddOrUpdatePackageReferenceAsync(FileInfo csprojPath, string packageName, string? version, CancellationToken cancellationToken = default)
    {
        if (PackagesToThrowOnAdd.Contains(packageName))
        {
            throw new InvalidOperationException($"Simulated dotnet add package failure for {packageName}");
        }

        AddedPackages.Add((csprojPath.FullName, packageName, version));
        return Task.FromResult(version ?? "1.0.0");
    }

    public Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(DirectoryInfo workingDirectory, string arguments, CancellationToken cancellationToken = default)
    {
        StringInvocations.Add(arguments);
        if (RunDotnetCommandHandler is not null)
        {
            return Task.FromResult(RunDotnetCommandHandler(arguments));
        }
        return Task.FromResult((0, "Fake dotnet command executed successfully.", string.Empty));
    }

    /// <summary>Records every argument list passed to the <see cref="IReadOnlyList{T}"/> overload.</summary>
    public List<IReadOnlyList<string>> ArgumentListInvocations { get; } = [];

    /// <summary>Records every string passed to the string overload.</summary>
    public List<string> StringInvocations { get; } = [];

    /// <summary>
    /// Optional scripted responder for the <see cref="IReadOnlyList{T}"/> overload. When null, the
    /// call is recorded and a success tuple is returned. May throw to simulate a missing executable.
    /// </summary>
    public Func<IReadOnlyList<string>, (int ExitCode, string Output, string Error)>? RunDotnetArgumentListHandler { get; set; }

    /// <summary>Records the environment overrides passed alongside each argument-list invocation.</summary>
    public List<IReadOnlyDictionary<string, string>?> ArgumentListEnvironmentInvocations { get; } = [];

    /// <summary>Records the working directory passed alongside each argument-list invocation.</summary>
    public List<DirectoryInfo> ArgumentListWorkingDirectories { get; } = [];

    public Task<(int ExitCode, string Output, string Error)> RunDotnetCommandAsync(DirectoryInfo workingDirectory, IReadOnlyList<string> arguments, IReadOnlyDictionary<string, string>? environmentOverrides = null, Action<string>? onOutputLine = null, Action<string>? onErrorLine = null, CancellationToken cancellationToken = default)
    {
        ArgumentListInvocations.Add(arguments.ToArray());
        ArgumentListEnvironmentInvocations.Add(environmentOverrides);
        ArgumentListWorkingDirectories.Add(workingDirectory);
        var result = RunDotnetArgumentListHandler?.Invoke(arguments) ?? (0, string.Empty, string.Empty);

        // Mirror the real service: when a caller supplies line callbacks (e.g. the verbose scaffold
        // streaming its post-creation output), replay the scripted stdout/stderr through them so tests
        // can assert the output was surfaced live.
        if (onOutputLine is not null)
        {
            foreach (var line in SplitLines(result.Item2))
            {
                onOutputLine(line);
            }
        }

        if (onErrorLine is not null)
        {
            foreach (var line in SplitLines(result.Item3))
            {
                onErrorLine(line);
            }
        }

        return Task.FromResult(result);
    }

    private static string[] SplitLines(string? text)
        => string.IsNullOrEmpty(text)
            ? []
            : text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n').Split('\n');

    /// <summary>
    /// When set, <see cref="RunDotnetStreamingAsync"/> invokes this handler (given the argument
    /// string plus the stdout/stderr line callbacks) instead of the default no-op success. Lets a
    /// test simulate streamed build output and control the exit code.
    /// </summary>
    public Func<string, Action<string>?, Action<string>?, int>? RunDotnetStreamingHandler { get; set; }

    /// <summary>Records the argument strings passed to <see cref="RunDotnetStreamingAsync"/> (build passes).</summary>
    public List<string> StreamingCalls { get; } = [];

    public Task<int> RunDotnetStreamingAsync(DirectoryInfo workingDirectory, string arguments, Action<string>? onOutputLine, Action<string>? onErrorLine, CancellationToken cancellationToken = default)
    {
        StreamingCalls.Add(arguments);
        if (RunDotnetStreamingHandler is not null)
        {
            return Task.FromResult(RunDotnetStreamingHandler(arguments, onOutputLine, onErrorLine));
        }
        return Task.FromResult(0);
    }

    /// <summary>
    /// When set, <see cref="RunDotnetInheritedAsync"/> returns this handler's result (keyed on the
    /// argument string) instead of the default success. Lets a test control the exit code of the
    /// inherited-stdio (native terminal logger) build path.
    /// </summary>
    public Func<string, int>? RunDotnetInheritedHandler { get; set; }

    /// <summary>Records the argument strings passed to <see cref="RunDotnetInheritedAsync"/> (native-terminal build passes).</summary>
    public List<string> InheritedCalls { get; } = [];

    public Task<int> RunDotnetInheritedAsync(DirectoryInfo workingDirectory, string arguments, CancellationToken cancellationToken = default)
    {
        InheritedCalls.Add(arguments);
        if (RunDotnetInheritedHandler is not null)
        {
            return Task.FromResult(RunDotnetInheritedHandler(arguments));
        }
        return Task.FromResult(0);
    }

    /// <summary>
    /// When set, <see cref="RunDotnetCommandAsync"/> returns this handler's result (keyed on the
    /// argument string) instead of the fixed success tuple. Lets a test feed canned
    /// <c>--getProperty</c> JSON for build/resolve scenarios.
    /// </summary>
    public Func<string, (int ExitCode, string Output, string Error)>? RunDotnetCommandHandler { get; set; }

    /// <summary>Records the <c>noRestore</c> flag from the most recent <see cref="GetPackageListAsync"/> call.</summary>
    public bool? LastGetPackageListNoRestore { get; private set; }

    /// <summary>The <c>projectAssetsFile</c> passed to the most recent <see cref="GetPackageListAsync"/> call.</summary>
    /// <remarks>
    /// Recorded because the command takes no <c>-c</c>/<c>-r</c>/<c>-p</c> and environment properties
    /// cannot substitute for them, so the build's own assets file is the only faithful source of the
    /// graph the binary was built against.
    /// </remarks>
    public string? LastGetPackageListAssetsFile { get; private set; }

    public Task<DotNetPackageListJson?> GetPackageListAsync(FileInfo csprojFile, bool includeTransitive = true, bool noRestore = false, FileInfo? projectAssetsFile = null, CancellationToken cancellationToken = default)
    {
        GetPackageListCallCount++;
        LastGetPackageListNoRestore = noRestore;
        LastGetPackageListAssetsFile = projectAssetsFile?.FullName;

        if (ThrowOnGetPackageList)
        {
            throw new InvalidOperationException("Simulated dotnet list package failure.");
        }

        return Task.FromResult(PackageListResult);
    }

    // Delegate file-based csproj modifications to real implementation
    public Task<bool> EnsureEnableMsixToolingAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
        => _real.EnsureEnableMsixToolingAsync(csprojPath, cancellationToken);

    public Task<bool> RemoveWindowsPackageTypeNoneAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
        => _real.RemoveWindowsPackageTypeNoneAsync(csprojPath, cancellationToken);

    public Task<bool> AnnotatePackageReferencesAsync(FileInfo csprojPath, IReadOnlyDictionary<string, string> packageComments, CancellationToken cancellationToken = default)
        => _real.AnnotatePackageReferencesAsync(csprojPath, packageComments, cancellationToken);

    public Task<bool> EnsureAssetContentItemsAsync(FileInfo csprojPath, CancellationToken cancellationToken = default)
        => _real.EnsureAssetContentItemsAsync(csprojPath, cancellationToken);
}
