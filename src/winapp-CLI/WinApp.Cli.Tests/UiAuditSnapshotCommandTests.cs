// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class UiAuditSnapshotCommandTests : IDisposable
{
    private ServiceProvider _services = null!;
    private TestConsole _console = null!;
    private StringWriter _stderr = null!;
    private SnapshotFake _snapshot = null!;
    private ResolverFake _resolver = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        _console = new TestConsole();
        _stderr = new StringWriter();
        _snapshot = new SnapshotFake();
        _resolver = new ResolverFake();
        _services = new ServiceCollection()
            .ConfigureCommands()
            .AddSingleton<IAnsiConsole>(_console)
            .AddSingleton<IUiAuditSnapshotService>(_snapshot)
            .AddSingleton<IUiTargetResolver>(_resolver)
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.None))
            .BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _services?.Dispose();
        _console?.Dispose();
        _stderr?.Dispose();
    }

    [TestMethod]
    public void CommandIsWiredWithoutInputOrFocusOptions()
    {
        var command = _services.GetRequiredService<UiAuditSnapshotCommand>();
        Assert.IsTrue(_services.GetRequiredService<UiCommand>().Subcommands.Contains(command));
        Assert.IsTrue(command.Options.Contains(SharedUiOptions.AppOption));
        Assert.IsTrue(command.Options.Contains(SharedUiOptions.WindowOption));
        Assert.IsFalse(command.Options.Contains(SharedUiOptions.FocusOption));
        Assert.IsFalse(command.Options.Contains(SharedUiOptions.CaptureScreenOption));
        foreach (var option in new[] { "--focus", "--capture-screen", "--include-injected", "--capture-text", "--via" })
        {
            Assert.IsNotEmpty(command.Parse(["--window", "42", option]).Errors);
        }
        Assert.IsNotNull(command.Description);
        StringAssert.Contains(command.Description, "partial");
        StringAssert.Contains(command.Description, "Value properties are never collected");
    }

    [TestMethod]
    public async Task ExplicitTargetIsRequiredBeforeAnyResolution()
    {
        var result = await InvokeAsync(["--json"]);

        AssertFailure(result, "missing_app");
        Assert.AreEqual(0, _resolver.Calls);
        Assert.AreEqual(0, _snapshot.Calls);
    }

    [TestMethod]
    [DataRow("--depth=0")]
    [DataRow("--depth=51")]
    [DataRow("--max-elements=0")]
    [DataRow("--max-elements=10001")]
    [DataRow("--timeout=0")]
    [DataRow("--timeout=120001")]
    [DataRow("--window=0")]
    [DataRow("--window=-1")]
    public async Task InvalidBoundsAreRejectedBeforeResolving(string option)
    {
        var result = await InvokeAsync(["--app", "Fixture", "--json", option]);

        AssertFailure(result, "invalid_arguments");
        Assert.AreEqual(0, _resolver.Calls);
        Assert.AreEqual(0, _snapshot.Calls);
    }

    [TestMethod]
    public async Task DefaultsAndResolvedTargetArePassedToDedicatedSnapshotService()
    {
        var result = await InvokeAsync(["--app", "Fixture", "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(1, _snapshot.Calls);
        Assert.AreSame(_resolver.Target, _snapshot.Target);
        Assert.AreEqual("Fixture", _resolver.App);
        Assert.IsNotNull(_snapshot.Options);
        Assert.AreEqual(20, _snapshot.Options.MaxDepth);
        Assert.AreEqual(2000, _snapshot.Options.MaxElements);
        Assert.AreEqual(16, _snapshot.Options.MaxTextRunsPerNode);
        Assert.AreEqual(5000, _snapshot.Options.TimeoutMs);
    }

    [TestMethod]
    public async Task WindowAndCustomBoundsArePreservedWithoutSearchingAnotherWindow()
    {
        var result = await InvokeAsync(["--window", "42", "--depth", "50", "--max-elements", "10000", "--timeout", "120000", "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual(42L, _resolver.Window);
        Assert.IsNull(_resolver.App);
        Assert.IsNotNull(_snapshot.Options);
        Assert.AreEqual(50, _snapshot.Options.MaxDepth);
        Assert.AreEqual(10000, _snapshot.Options.MaxElements);
        Assert.AreEqual(120000, _snapshot.Options.TimeoutMs);
        Assert.AreSame(_resolver.Target, _snapshot.Target);
    }

    [TestMethod]
    [DataRow(0L, 123)]
    [DataRow(42L, 0)]
    [DataRow(43L, 123)]
    public async Task EmptyOrMismatchedResolverTargetsDoNotStartSnapshot(long hwnd, int processId)
    {
        _resolver.Target.WindowHandle = hwnd;
        _resolver.Target.ProcessId = processId;
        var result = await InvokeAsync(["--window", "42", "--json"]);

        AssertFailure(result, "invalid_target");
        Assert.AreEqual(0, _snapshot.Calls);
    }

    [TestMethod]
    public async Task JsonContainsTheWholeTypedDocumentIncludingExplicitNulls()
    {
        var result = await InvokeAsync(["--window", "42", "--json"]);

        Assert.AreEqual(0, result.ExitCode);
        Assert.AreEqual("", result.Stderr);
        using var json = JsonDocument.Parse(result.Stdout);
        var root = json.RootElement;
        Assert.AreEqual(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.AreEqual(123, root.GetProperty("processId").GetInt32());
        Assert.AreEqual(42L, root.GetProperty("windowHandle").GetInt64());
        Assert.AreEqual("captured", root.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("rootRuntimeId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("title").ValueKind);
        Assert.AreEqual(_snapshot.Result.CapturedAtUtc, root.GetProperty("capturedAtUtc").GetDateTimeOffset());
        Assert.AreEqual(20, root.GetProperty("maxDepth").GetInt32());
        Assert.AreEqual(2000, root.GetProperty("maxElements").GetInt32());
        Assert.IsFalse(root.GetProperty("depthLimitReached").GetBoolean());
        Assert.IsFalse(root.GetProperty("elementLimitReached").GetBoolean());

        var node = root.GetProperty("nodes")[0];
        Assert.AreEqual("n1", node.GetProperty("id").GetString());
        Assert.AreEqual(JsonValueKind.Null, node.GetProperty("parentId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, node.GetProperty("automationId").ValueKind);
        Assert.AreEqual("Accessible evidence", node.GetProperty("name").GetString());
        Assert.IsFalse(node.GetProperty("isPassword").GetBoolean());
        Assert.IsFalse(node.GetProperty("isEnabled").GetBoolean());
        Assert.AreEqual(JsonValueKind.Null, node.GetProperty("isKeyboardFocusable").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, node.GetProperty("hasKeyboardFocus").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, node.GetProperty("isOffscreen").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, node.GetProperty("bounds").ValueKind);
        Assert.AreEqual(JsonValueKind.Array, node.GetProperty("patterns").ValueKind);
        Assert.AreEqual(JsonValueKind.Array, node.GetProperty("issues").ValueKind);

        var run = node.GetProperty("textRuns")[0];
        var color = run.GetProperty("foregroundColor");
        Assert.AreEqual("value", color.GetProperty("status").GetString());
        Assert.AreEqual(0x00112233, color.GetProperty("number").GetInt32());
        Assert.AreEqual(JsonValueKind.Null, color.GetProperty("boolean").ValueKind);
        Assert.AreEqual("VT_I4", color.GetProperty("nativeType").GetString());
        Assert.AreEqual(JsonValueKind.Null, color.GetProperty("error").ValueKind);
        Assert.AreEqual("unsupported", run.GetProperty("backgroundColor").GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, run.GetProperty("backgroundColor").GetProperty("number").ValueKind);
        Assert.IsFalse(run.GetProperty("isHidden").GetProperty("boolean").GetBoolean());
        Assert.IsFalse(node.TryGetProperty("value", out _));
        Assert.IsFalse(run.TryGetProperty("text", out _));
    }

    [TestMethod]
    public void IndeterminatePrivacyAndWithheldFieldsRemainExplicitInJson()
    {
        var snapshot = new UiAuditSnapshotResult
        {
            Status = "partial",
            Nodes =
            [
                new UiAuditNode
                {
                    Id = "n1",
                    TextRuns = [new UiAuditNodeTextRun { Id = "n1.t1", ForegroundColor = new UiAuditField { Status = "withheld" } }],
                    Issues = [new UiAuditSnapshotIssue("privacy_unavailable", "Password status could not be verified.")],
                },
            ],
        };
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(snapshot, UiJsonContext.Default.UiAuditSnapshotResult));
        var node = json.RootElement.GetProperty("nodes")[0];

        Assert.AreEqual(JsonValueKind.Null, node.GetProperty("isPassword").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, node.GetProperty("name").ValueKind);
        Assert.AreEqual("withheld", node.GetProperty("textRuns")[0].GetProperty("foregroundColor").GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, node.GetProperty("textRuns")[0].GetProperty("foregroundColor").GetProperty("number").ValueKind);
    }

    [TestMethod]
    public async Task ExcludedWindowBoundariesHaveOptionalMachineReadableProvenance()
    {
        _snapshot.Result = _snapshot.Result with
        {
            Issues =
            [
                new UiAuditSnapshotIssue("privacy_notice", "Accessible names can contain user data."),
                new UiAuditSnapshotIssue("window_boundary_excluded", "Another native top-level window was excluded.") { ExcludedWindowHandle = 77 },
            ],
        };
        var result = await InvokeAsync(["--window", "42", "--json"]);

        Assert.AreEqual(0, result.ExitCode, "Intentional exclusion of a different window does not itself make the selected-window snapshot partial.");
        using var json = JsonDocument.Parse(result.Stdout);
        var issues = json.RootElement.GetProperty("issues");
        Assert.IsFalse(issues[0].TryGetProperty("excludedWindowHandle", out _));
        Assert.AreEqual("window_boundary_excluded", issues[1].GetProperty("code").GetString());
        Assert.AreEqual(77L, issues[1].GetProperty("excludedWindowHandle").GetInt64());
    }

    [TestMethod]
    public async Task PartialSnapshotStaysMachineReadableOnStdoutWithNonzeroExit()
    {
        _snapshot.Result = _snapshot.Result with
        {
            Status = "partial",
            ElementLimitReached = true,
            Issues = [new UiAuditSnapshotIssue("element_limit_reached", "Additional nodes were omitted.")],
        };
        var result = await InvokeAsync(["--window", "42", "--json"]);

        Assert.AreEqual(1, result.ExitCode);
        using var snapshot = JsonDocument.Parse(result.Stdout);
        Assert.AreEqual("partial", snapshot.RootElement.GetProperty("status").GetString());
        Assert.AreEqual(1, snapshot.RootElement.GetProperty("nodes").GetArrayLength());
        Assert.IsTrue(snapshot.RootElement.GetProperty("elementLimitReached").GetBoolean());
        Assert.IsFalse(snapshot.RootElement.TryGetProperty("error", out _));
        using var error = JsonDocument.Parse(result.Stderr);
        Assert.AreEqual("partial_output", error.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    [DataRow("invalid_target")]
    [DataRow("target_mismatch")]
    [DataRow("uia_unavailable")]
    public async Task InvalidInitialTargetEmitsAnErrorInsteadOfAnEmptySnapshot(string code)
    {
        _snapshot.Failure = new UiAuditSnapshotException(code, "The exact selected target could not be verified.");
        var result = await InvokeAsync(["--window", "42", "--json"]);

        AssertFailure(result, code);
    }

    [TestMethod]
    public async Task MissingWindowErrorsDoNotEchoPotentiallyPrivateTitles()
    {
        _resolver.Failure = new AppNotFoundException("private-window-title");
        var result = await InvokeAsync(["--window", "42", "--json"]);

        AssertFailure(result, "no_target");
        Assert.IsFalse(result.Stderr.Contains("private-window-title", StringComparison.Ordinal));
        Assert.AreEqual(0, _snapshot.Calls);
    }

    [TestMethod]
    public async Task UnexpectedProviderErrorDoesNotEchoItsMessage()
    {
        _snapshot.Failure = new InvalidOperationException("private-provider-value");
        var result = await InvokeAsync(["--window", "42", "--json"]);

        AssertFailure(result, "internal_error");
        Assert.IsFalse(result.Stderr.Contains("private-provider-value", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task CancellationBeforeCaptureProducesAJsonErrorWithoutCallingUia()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var parsed = _services.GetRequiredService<UiAuditSnapshotCommand>().Parse(["--window", "42", "--json"]);
        parsed.InvocationConfiguration.Error = _stderr;
        var exitCode = await _services.GetRequiredService<UiAuditSnapshotCommand.Handler>().InvokeAsync(parsed, cancelled.Token);

        AssertFailure(new CommandResult(exitCode, _console.Output, _stderr.ToString()), "cancelled");
        Assert.AreEqual(0, _resolver.Calls);
        Assert.AreEqual(0, _snapshot.Calls);
    }

    [TestMethod]
    public async Task TextSummaryWarnsAboutNamesWithoutDumpingThem()
    {
        var result = await InvokeAsync(["--window", "42"]);

        Assert.AreEqual(0, result.ExitCode);
        StringAssert.Contains(result.Stdout, "captured: 1 elements");
        StringAssert.Contains(result.Stderr, "Accessible names can contain user data.");
        Assert.IsFalse(result.Stdout.Contains("Accessible evidence", StringComparison.Ordinal));
    }

    private async Task<CommandResult> InvokeAsync(string[] arguments)
    {
        var parsed = _services.GetRequiredService<UiAuditSnapshotCommand>().Parse(arguments);
        parsed.InvocationConfiguration.Output = _console.Profile.Out.Writer;
        parsed.InvocationConfiguration.Error = _stderr;
        var exitCode = await parsed.InvokeAsync(parsed.InvocationConfiguration, cancellationToken: TestContext.CancellationToken);
        return new CommandResult(exitCode, _console.Output, _stderr.ToString());
    }

    private static void AssertFailure(CommandResult result, string code)
    {
        Assert.AreEqual(1, result.ExitCode);
        Assert.AreEqual("", result.Stdout);
        using var error = JsonDocument.Parse(result.Stderr);
        Assert.AreEqual(code, error.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    private sealed record CommandResult(int ExitCode, string Stdout, string Stderr);

    private sealed class ResolverFake : IUiTargetResolver
    {
        internal int Calls { get; private set; }
        internal string? App { get; private set; }
        internal long? Window { get; private set; }
        internal Exception? Failure { get; set; }
        internal UiTarget Target { get; } = new() { ProcessId = 123, WindowHandle = 42 };

        public Task<UiTarget> ResolveAsync(string? app, long? hwnd, CancellationToken ct)
        {
            Calls++;
            App = app;
            Window = hwnd;
            if (Failure is not null) { throw Failure; }
            return Task.FromResult(Target);
        }
    }

    private sealed class SnapshotFake : IUiAuditSnapshotService
    {
        internal int Calls { get; private set; }
        internal UiTarget? Target { get; private set; }
        internal UiAuditSnapshotOptions? Options { get; private set; }
        internal Exception? Failure { get; set; }
        internal UiAuditSnapshotResult Result { get; set; } = new()
        {
            CapturedAtUtc = new DateTimeOffset(2026, 9, 16, 14, 56, 27, TimeSpan.Zero),
            ProcessId = 123,
            WindowHandle = 42,
            MaxDepth = 20,
            MaxElements = 2000,
            Nodes =
            [
                new UiAuditNode
                {
                    Id = "n1",
                    Name = "Accessible evidence",
                    IsPassword = false,
                    IsEnabled = false,
                    TextRuns =
                    [
                        new UiAuditNodeTextRun
                        {
                            Id = "n1.t1",
                            ForegroundColor = new UiAuditField { Status = "value", Number = 0x00112233, NativeType = "VT_I4" },
                            BackgroundColor = new UiAuditField { Status = "unsupported", NativeType = "VT_UNKNOWN" },
                            FontSize = new UiAuditField { Status = "value", Number = 12.5, NativeType = "VT_R8" },
                            IsHidden = new UiAuditField { Status = "value", Boolean = false, NativeType = "VT_BOOL" },
                        },
                    ],
                },
            ],
            Issues = [new UiAuditSnapshotIssue("privacy_notice", "Accessible names can contain user data.")],
        };

        public Task<UiAuditSnapshotResult> CaptureAsync(UiTarget target, UiAuditSnapshotOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            Target = target;
            Options = options;
            if (Failure is not null) { throw Failure; }
            return Task.FromResult(Result);
        }
    }
}
