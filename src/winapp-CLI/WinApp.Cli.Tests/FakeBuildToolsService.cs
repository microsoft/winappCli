// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;
using WinApp.Cli.Tools;
using Microsoft.Extensions.Logging.Abstractions;

namespace WinApp.Cli.Tests;

/// <summary>
/// Shared fake <see cref="IBuildToolsService"/> that records every invocation and never runs a real tool.
/// Tests observe calls through <see cref="Invocations"/> and customize behavior with the optional
/// <see cref="OnRun"/> side-effect hook and/or the <see cref="Handler"/> result hook.
/// </summary>
/// <remarks>
/// <para><see cref="OnRun"/> receives just the raw arguments and is convenient for simulating a side
/// effect (e.g. writing an output file) without caring about the return value.</para>
/// <para><see cref="Handler"/> receives the <see cref="Tool"/> and arguments and returns the
/// <c>(stdout, stderr)</c> the call should produce; it can also throw to simulate a tool failure.
/// When no <see cref="Handler"/> is set, the call returns empty stdout/stderr.</para>
/// </remarks>
internal sealed class FakeBuildToolsService : IBuildToolsService
{
    /// <summary>Records (tool executable name, raw arguments) for every RunBuildToolAsync call, in order.</summary>
    public List<(string ToolName, string Arguments)> Invocations { get; } = [];

    /// <summary>Optional side-effect hook invoked with the raw arguments before the result is produced.</summary>
    public Action<string>? OnRun { get; set; }

    /// <summary>Optional hook that produces the (stdout, stderr) result for a given tool + arguments.</summary>
    public Func<Tool, string, (string stdout, string stderr)>? Handler { get; set; }

    /// <summary>
    /// Result returned from <see cref="EnsureBuildToolsAsync"/>. Defaults to null (build-tools
    /// directory not resolved); set to a directory to exercise the "BuildTools ready" branch.
    /// </summary>
    public DirectoryInfo? BuildToolsResult { get; set; }

    /// <summary>
    /// Records the <c>forceLatest</c> argument passed to each <see cref="EnsureBuildToolsAsync"/>
    /// call, in order, so tests can assert whether a pinned build-tools version suppressed the
    /// force-latest path (pinned =&gt; <c>false</c>, no pin =&gt; <c>true</c>).
    /// </summary>
    public List<bool> EnsureBuildToolsForceLatest { get; } = [];

    public FileInfo? GetBuildToolPath(string toolName) => new(Path.Combine(Path.GetTempPath(), toolName));

    public VerifiedTool OpenVerifiedTool(FileInfo toolPath)
        => VerifiedTool.Open(toolPath, static (_, _) => true, NullLogger.Instance);

    public Task<FileInfo> EnsureBuildToolAvailableAsync(string toolName, TaskContext taskContext, CancellationToken cancellationToken = default)
        => Task.FromResult(new FileInfo(Path.Combine(Path.GetTempPath(), toolName)));

    public Task<DirectoryInfo?> EnsureBuildToolsAsync(TaskContext taskContext, bool forceLatest = false, CancellationToken cancellationToken = default)
    {
        EnsureBuildToolsForceLatest.Add(forceLatest);
        return Task.FromResult(BuildToolsResult);
    }

    public Task<(string stdout, string stderr)> RunBuildToolAsync(Tool tool, string arguments, TaskContext taskContext, bool printErrors = true, FileInfo? toolPathOverride = null, IReadOnlyDictionary<string, string>? environment = null, string? workingDirectory = null, CancellationToken cancellationToken = default)
    {
        Invocations.Add((tool.ExecutableName, arguments));
        OnRun?.Invoke(arguments);
        var result = Handler?.Invoke(tool, arguments) ?? (string.Empty, string.Empty);
        return Task.FromResult(result);
    }

    // ---- Opt-in SDK-tool output emulation ----

    private static readonly Regex OutputPathRegex = new("(?:^|\\s)/p\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ConfigFileRegex = new("/cf\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex OutputFileRegex = new("/of\\s+\"(?<path>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Ready-made <see cref="Handler"/> that emulates the on-disk side effects of the real SDK tools so
    /// packaging flows can run without them: makeappx writes its <c>/p</c> output package and makepri
    /// writes its <c>createconfig</c> config / <c>new</c> output. Other tools (e.g. signtool) are no-ops.
    /// Wire it via <c>new FakeBuildToolsService { Handler = FakeBuildToolsService.EmulateSdkToolOutput }</c>.
    /// </summary>
    public static (string stdout, string stderr) EmulateSdkToolOutput(Tool tool, string arguments)
    {
        // Only create fake output files for makeappx (not signtool or other tools)
        if (tool.ExecutableName.Contains("makeappx", StringComparison.OrdinalIgnoreCase))
        {
            var match = OutputPathRegex.Match(arguments);
            if (match.Success)
            {
                var outputPath = NormalizeLongPath(match.Groups["path"].Value);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, $"fake {tool.ExecutableName} output");
            }
        }
        else if (tool.ExecutableName.Contains("makepri", StringComparison.OrdinalIgnoreCase))
        {
            EmulateMakePri(arguments);
        }

        return (string.Empty, string.Empty);
    }

    private static void EmulateMakePri(string arguments)
    {
        // 'createconfig' must produce a priconfig.xml that PriService then loads and rewrites.
        if (arguments.Contains("createconfig", StringComparison.OrdinalIgnoreCase))
        {
            var match = ConfigFileRegex.Match(arguments);
            if (match.Success)
            {
                var configPath = NormalizeLongPath(match.Groups["path"].Value);
                Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
                File.WriteAllText(configPath, """
                    <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
                    <resources targetOsVersion="10.0.0" majorVersion="1">
                      <index root="\" startIndexAt="\">
                        <indexer-config type="folder" foldernameAsQualifier="true" filenameAsQualifier="true" qualifierDelimiter="." />
                        <indexer-config type="PRI" />
                      </index>
                    </resources>
                    """);
            }
        }
        else if (arguments.Contains("new", StringComparison.OrdinalIgnoreCase))
        {
            // 'new' emits resources.pri; PriService only parses stdout, but write the file for realism.
            var match = OutputFileRegex.Match(arguments);
            if (match.Success)
            {
                var outputPath = NormalizeLongPath(match.Groups["path"].Value);
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
                File.WriteAllText(outputPath, "fake pri");
            }
        }
    }

    private static string NormalizeLongPath(string path)
    {
        return path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
    }
}
