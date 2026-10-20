// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class UiAccessibilityCommandTests : IDisposable
{
    private static readonly string[] Verbs =
        ["survey", "explore", "journey", "loop", "check", "repair", "assessment-template", "assess"];
    private ServiceProvider _services = null!;
    private TestConsole _console = null!;
    private StringWriter _stdout = null!;
    private StringWriter _stderr = null!;
    private WorkerFake _worker = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _worker = new WorkerFake();
        _console = new TestConsole();
        _stdout = new StringWriter();
        _stderr = new StringWriter();
        _services = new ServiceCollection()
            .ConfigureCommands()
            .AddSingleton<IAnsiConsole>(_console)
            .AddSingleton<IUiAccessibilityWorkerService>(_worker)
            .BuildServiceProvider();
    }

    [TestCleanup]
    public void Cleanup() => Dispose();

    public void Dispose()
    {
        _services?.Dispose();
        _console?.Dispose();
        _stdout?.Dispose();
        _stderr?.Dispose();
    }

    [TestMethod]
    public async Task HelpAndSchemaNeedNoWorkerServicesOrBundle()
    {
        using var services = new ServiceCollection()
            .ConfigureCommands()
            .AddSingleton<IAnsiConsole>(_console)
            .BuildServiceProvider();
        var root = services.GetRequiredService<WinAppRootCommand>();
        foreach (var verb in Verbs.Prepend(""))
        {
            var arguments = new List<string> { "ui", "accessibility" };
            if (verb.Length > 0)
            {
                arguments.Add(verb);
            }
            foreach (var help in new[] { "--help", "--cli-schema" })
            {
                _stdout.GetStringBuilder().Clear();
                var parsed = root.Parse([.. arguments, help], WinAppParserConfiguration.Default);
                var code = await InvokeAsync(parsed);
                Assert.AreEqual(0, code, $"{verb} {help}: {_stderr}");
                Assert.IsNotEmpty(_stdout.ToString(), $"{verb} {help}");
                if (help == "--cli-schema")
                {
                    using var schema = JsonDocument.Parse(_stdout.ToString());
                    Assert.IsTrue(schema.RootElement.TryGetProperty("name", out _));
                }
            }
        }
        foreach (var help in new[] { "--help", "--cli-schema" })
        {
            _stdout.GetStringBuilder().Clear();
            Assert.AreEqual(0, await InvokeAsync(root.Parse(["ui", "audit", help], WinAppParserConfiguration.Default)));
            Assert.IsNotEmpty(_stdout.ToString());
        }
        Assert.AreEqual(0, _worker.Calls);
    }

    [TestMethod]
    [DataRow("audit")]
    [DataRow("survey")]
    [DataRow("explore")]
    [DataRow("journey")]
    [DataRow("loop")]
    public async Task JsonDocumentationWithRequiredOperandsDoesNotRequireActionConsent(string verb)
    {
        var root = _services.GetRequiredService<WinAppRootCommand>();
        string[] operands = verb switch
        {
            "explore" => ["profile.json", "--output", "unused-output"],
            "journey" => ["journey.json"],
            "loop" => ["--output", "unused-output"],
            _ => [],
        };
        foreach (var documentation in new[] { "--help", "--cli-schema" })
        {
            _stdout.GetStringBuilder().Clear();
            string[] path = verb == "audit" ? ["ui", "audit"] : ["ui", "accessibility", verb];
            var parsed = root.Parse([.. path, .. operands, documentation, "--json"], WinAppParserConfiguration.Default);
            Assert.IsEmpty(parsed.Errors, $"{verb} {documentation} --json");
            Assert.AreEqual(0, await InvokeAsync(parsed));
            Assert.IsNotEmpty(_stdout.ToString());
        }
        Assert.AreEqual(0, _worker.Calls);
    }

    [TestMethod]
    public async Task RootSchemaExposesTypedOptionsAndOnlySupportedJsonModes()
    {
        var root = _services.GetRequiredService<WinAppRootCommand>();
        Assert.AreEqual(0, await InvokeAsync(root.Parse(["--cli-schema"])));
        using var schema = JsonDocument.Parse(_stdout.ToString());
        var group = schema.RootElement.GetProperty("subcommands").GetProperty("ui")
            .GetProperty("subcommands").GetProperty("accessibility");
        var commands = group.GetProperty("subcommands");
        CollectionAssert.AreEquivalent(Verbs, commands.EnumerateObject().Select(item => item.Name).ToArray());
        foreach (var verb in Verbs)
        {
            var options = commands.GetProperty(verb).GetProperty("options");
            Assert.IsFalse(options.TryGetProperty("--winapp", out _), "The native executable is an internal argument.");
            Assert.IsTrue(options.TryGetProperty("--quiet", out _));
            Assert.IsTrue(options.TryGetProperty("--verbose", out _));
            Assert.AreEqual(verb is "survey" or "explore" or "journey" or "loop",
                options.TryGetProperty("--json", out _), verb);
        }
        var scan = schema.RootElement.GetProperty("subcommands").GetProperty("ui")
            .GetProperty("subcommands").GetProperty("audit").GetProperty("options");
        Assert.AreEqual(20, scan.GetProperty("--depth").GetProperty("defaultValue").GetInt32());
        Assert.AreEqual(15000, scan.GetProperty("--timeout-ms").GetProperty("defaultValue").GetInt32());
        Assert.IsTrue(scan.TryGetProperty("--app", out _));
        Assert.IsTrue(scan.TryGetProperty("--current-page", out _));
        Assert.IsFalse(scan.TryGetProperty("--pid", out _));
        Assert.IsFalse(scan.TryGetProperty("--navigation", out _));
        Assert.IsFalse(scan.TryGetProperty("--accept-navigation", out _));
        Assert.IsFalse(commands.TryGetProperty("scan", out _));
        Assert.IsTrue(commands.GetProperty("explore").GetProperty("options").GetProperty("--output").GetProperty("required").GetBoolean());
        Assert.IsTrue(commands.GetProperty("check").GetProperty("options").TryGetProperty("--accept-actions", out _));
        Assert.IsTrue(commands.GetProperty("repair").GetProperty("options").TryGetProperty("--accept-actions", out _));
        Assert.IsTrue(commands.GetProperty("survey").GetProperty("options").TryGetProperty("--retry-page", out _));
        Assert.IsTrue(commands.GetProperty("assessment-template").GetProperty("arguments").TryGetProperty("scan", out _));
        Assert.AreEqual(0, _worker.Calls);
    }

    [TestMethod]
    public async Task DefaultAuditSelectsStandardTabsWithoutExtraPublicFlags()
    {
        Assert.AreEqual(0, await InvokeAsync(["audit", "-a", "My app"]));
        string[] expected = ["scan", "--app", "My app", "--navigation", "standard-tabs", "--accept-navigation"];
        CollectionAssert.AreEqual(expected, _worker.Arguments);
        Assert.IsFalse(_worker.Arguments.Contains("--accept-input", StringComparer.Ordinal));
        Assert.IsFalse(_worker.Arguments.Contains("--tab-steps", StringComparer.Ordinal));
        Assert.IsFalse(_worker.Arguments.Contains("--max-pages", StringComparer.Ordinal),
            "The worker owns the effective page-budget default.");
        Assert.AreEqual(20, _services.GetRequiredService<UiAuditCommand>()
            .Options.Single(option => option.Name == "--max-pages").GetDefaultValue());
    }

    [TestMethod]
    public async Task CurrentPageIsReadOnlyAndAnExactWindowTakesPrecedence()
    {
        Assert.AreEqual(0, await InvokeAsync(["audit", "-a", "ignored app", "-w", "123", "--current-page"]));
        string[] expected = ["scan", "--window", "123"];
        CollectionAssert.AreEqual(expected, _worker.Arguments);
        Assert.IsFalse(_worker.Arguments.Contains("--current-page", StringComparer.Ordinal));
        Assert.IsFalse(_worker.Arguments.Contains("--navigation", StringComparer.Ordinal));
        Assert.IsFalse(_worker.Arguments.Contains("--accept-navigation", StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task ApprovedCurrentPageKeyboardProbeDoesNotInventAnObservationBudget()
    {
        Assert.AreEqual(0, await InvokeAsync(["audit", "--window", "123", "--current-page", "--tab-steps", "4", "--accept-input"]));
        string[] expected = ["scan", "--tab-steps", "4", "--accept-input", "--window", "123"];
        CollectionAssert.AreEqual(expected, _worker.Arguments);
        Assert.IsFalse(_worker.Arguments.Contains("--observe-seconds", StringComparer.Ordinal));
        Assert.IsFalse(_worker.Arguments.Contains("--navigation", StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task DefaultNavigationForwardsPageBudgetsAndObservationWithoutKeyboardInput()
    {
        Assert.AreEqual(0, await InvokeAsync(["audit", "--window", "99",
            "--max-pages", "100", "--observe-seconds", "1", "--json"]));
        string[] bounded =
        [
            "scan", "--max-pages", "100", "--observe-seconds", "1", "--json", "--window", "99",
            "--navigation", "standard-tabs", "--accept-navigation",
        ];
        CollectionAssert.AreEqual(bounded, _worker.Arguments);
        Assert.IsFalse(_worker.Arguments.Contains("--accept-input", StringComparer.Ordinal));
        Assert.IsFalse(_worker.Arguments.Contains("--tab-steps", StringComparer.Ordinal));
    }

    [TestMethod]
    public async Task EveryBackendVerbHasExplicitSafeForwarding()
    {
        (string[] Native, string[] Backend)[] cases =
        [
            (["survey", "profile file.json", "--window", "99", "--output", "new evidence", "--accept-navigation", "--json"],
             ["survey", "profile file.json", "--window", "99", "--output", "new evidence", "--accept-navigation", "--json"]),
            (["survey", "--resume", "saved run", "--accept-navigation", "--confirm-unchanged",
                "--retry-page", "General", "--retry-page", "Keyboard", "--stop-after", "2"],
             ["survey", "--resume", "saved run", "--accept-navigation", "--confirm-unchanged",
                "--stop-after", "2", "--retry-page", "General", "--retry-page", "Keyboard"]),
            (["explore", "profile.json", "--launch-owned", "--accept-interactions", "--output", "evidence", "--probe-scope-guard"],
             ["explore", "profile.json", "--launch-owned", "--accept-interactions", "--output", "evidence", "--probe-scope-guard"]),
            (["journey", "journey.json", "--accept-navigation", "--plan", "reviewed plan.json",
                "--apply", "--proposals", "reviewed proposals", "--no-screenshot"],
             ["journey", "journey.json", "--accept-navigation", "--plan", "reviewed plan.json",
                "--apply", "--proposals", "reviewed proposals", "--no-screenshot"]),
            (["loop", "preset.json", "--accept-actions", "--output", "run", "--apply", "--copilot", @"C:\Copilot Folder\copilot.exe"],
             ["loop", "preset.json", "--accept-actions", "--output", "run", "--apply", "--copilot", @"C:\Copilot Folder\copilot.exe"]),
            (["loop", "--replay", "saved run", "--round", "baseline", "--accept-actions", "--output", "new run"],
             ["loop", "--replay", "saved run", "--round", "baseline", "--accept-actions", "--output", "new run"]),
            (["check", "suite.json", "--accept-actions", "--output", "check evidence", "--quiet"],
             ["check", "suite.json", "--output", "check evidence"]),
            (["repair", "scenario.json", "--accept-actions", "--finding", "finding-1", "--apply",
                "--proposal", "proposal with spaces.json", "--verbose"],
             ["repair", "scenario.json", "--finding", "finding-1", "--apply", "--proposal", "proposal with spaces.json"]),
            (["assessment-template", "scan.json", "--output", "assessment.json", "--author", "Demo Author", "--source", "ai", "--model", "Model"],
             ["assessment-template", "scan.json", "--output", "assessment.json", "--author", "Demo Author", "--source", "ai", "--model", "Model"]),
            (["assess", "scan.json", "--assessment", "assessment.json", "--output", "combined report"],
             ["assess", "scan.json", "--assessment", "assessment.json", "--output", "combined report"]),
        ];

        foreach (var (native, backend) in cases)
        {
            Assert.AreEqual(0, await InvokeAsync(native), $"{string.Join(" ", native)}: {_stderr}");
            CollectionAssert.AreEqual(backend, _worker.Arguments, string.Join(" ", native));
        }
    }

    [TestMethod]
    public async Task FalseApprovalsAreNeverForwardedAsTrue()
    {
        Assert.AreEqual(0, await InvokeAsync(["repair", "suite.json", "--accept-actions", "--apply=false"]));
        string[] repair = ["repair", "suite.json"];
        CollectionAssert.AreEqual(repair, _worker.Arguments);
        Assert.AreEqual(0, await InvokeAsync(["audit", "--app", "42", "--current-page", "--accept-input=false", "--json=false", "--no-screenshot=false"]));
        string[] scan = ["scan", "--app", "42"];
        CollectionAssert.AreEqual(scan, _worker.Arguments);
    }

    [TestMethod]
    public async Task FalseCurrentPageUsesTheDocumentedAutomaticNavigationDefault()
    {
        Assert.AreEqual(0, await InvokeAsync(["audit", "-w", "42", "--current-page=false"]));
        string[] expected = ["scan", "--window", "42", "--navigation", "standard-tabs", "--accept-navigation"];
        CollectionAssert.AreEqual(expected, _worker.Arguments);
    }

    [TestMethod]
    public async Task LiteralPathsAndQuotedValuesDoNotBecomeOptionsOrShellSyntax()
    {
        const string output = @"C:\Audit evidence\space & (literal)\";
        const string input = "@reviewed suite.json";
        const string proposal = "quoted \"value\" & exit 87.json";
        Assert.AreEqual(0, await InvokeAsync(
            ["repair", input, "--accept-actions", "--output", output, "--proposal", proposal]));
        CollectionAssert.AreEqual(new[] { "repair", input, "--proposal", proposal, "--output", output }, _worker.Arguments);
    }

    [TestMethod]
    [DataRow("audit", "--app")]
    [DataRow("audit --app \" \"", "--app")]
    [DataRow("audit --pid 1", "--pid")]
    [DataRow("audit --window -1", "--window")]
    [DataRow("audit --window 0x123", "--window")]
    [DataRow("audit --app Demo --depth 0", "--depth")]
    [DataRow("audit --app Demo --depth 51", "--depth")]
    [DataRow("audit --app Demo --depth nope", "--depth")]
    [DataRow("audit --app Demo --max-elements 10001", "--max-elements")]
    [DataRow("audit --app Demo --timeout-ms 999", "--timeout-ms")]
    [DataRow("audit --app Demo --timeout-ms 120001", "--timeout-ms")]
    [DataRow("audit --app Demo --observe-seconds 61", "--observe-seconds")]
    [DataRow("audit --app Demo --current-page --tab-steps 31 --accept-input", "--tab-steps")]
    [DataRow("audit --app Demo --current-page --tab-steps 1", "--accept-input")]
    [DataRow("audit --app Demo --current-page --tab-steps 1 --accept-input=false", "--accept-input")]
    [DataRow("audit --app Demo --current-page --tab-steps 1 --accept-input=invalid", "invalid")]
    [DataRow("audit --app Demo --current-page=invalid", "invalid")]
    [DataRow("audit --app Demo --accept-input", "--tab-steps")]
    [DataRow("audit --app Demo --current-page --tab-steps 1 --accept-input --observe-seconds 0", "--observe-seconds")]
    [DataRow("audit --app Demo --navigation standard-tabs", "--navigation")]
    [DataRow("audit --app Demo --accept-navigation", "--accept-navigation")]
    [DataRow("audit --app Demo --current-page --max-pages 20", "--max-pages")]
    [DataRow("audit --app Demo --max-pages 0", "--max-pages")]
    [DataRow("audit --app Demo --max-pages 101", "--max-pages")]
    [DataRow("audit --app Demo --tab-steps 1 --accept-input", "--current-page")]
    [DataRow("audit --app Demo --output \" \"", "--output")]
    [DataRow("audit --app Demo --winapp other.exe", "--winapp")]
    [DataRow("audit --app Demo --apply", "--apply")]
    [DataRow("audit --app Demo extra.json", "extra.json")]
    [DataRow("scan --pid 1", "scan")]
    [DataRow("survey p.json --pid 1 --output out", "--accept-navigation")]
    [DataRow("survey --accept-navigation --pid 1 --output out", "profile")]
    [DataRow("survey p.json --pid 1 --accept-navigation", "--output")]
    [DataRow("survey --resume run --accept-navigation", "--confirm-unchanged")]
    [DataRow("survey p.json --resume run --accept-navigation --confirm-unchanged", "either")]
    [DataRow("survey --resume run --accept-navigation --confirm-unchanged --pid 1", "frozen")]
    [DataRow("survey --resume run --accept-navigation --confirm-unchanged --no-screenshot", "frozen")]
    [DataRow("survey --resume run --accept-navigation --confirm-unchanged --output other", "frozen")]
    [DataRow("survey p.json --pid 1 --output out --accept-navigation --retry-page Page", "resume-only")]
    [DataRow("survey --resume run --accept-navigation --confirm-unchanged --retry-page A --retry-page A", "unique")]
    [DataRow("survey --resume run --accept-navigation --confirm-unchanged --retry-page ..", "identifier")]
    [DataRow("survey --resume run --accept-navigation --confirm-unchanged --stop-after 101", "--stop-after")]
    [DataRow("explore p.json --launch-owned --output out", "--accept-interactions")]
    [DataRow("explore p.json --launch-owned --pid 1 --output out --accept-interactions", "exactly one")]
    [DataRow("explore p.json --pid 1 --output out --accept-interactions --probe-scope-guard", "--launch-owned")]
    [DataRow("explore p.json --pid 1 --accept-interactions", "--output")]
    [DataRow("journey j.json", "--accept-navigation")]
    [DataRow("journey j.json --accept-navigation --copilot-plan --plan plan.json", "either")]
    [DataRow("journey j.json --accept-navigation --proposals folder", "--apply")]
    [DataRow("loop preset.json --output out", "--accept-actions")]
    [DataRow("loop --accept-actions --output out", "preset")]
    [DataRow("loop preset.json --replay run --accept-actions --output out", "preset")]
    [DataRow("loop preset.json --accept-actions --output out --round baseline", "--round")]
    [DataRow("loop --replay run --accept-actions --output out --round ..", "--round")]
    [DataRow("loop --replay run --accept-actions --output out --apply", "Replay")]
    [DataRow("loop preset.json --accept-actions --output out --proposals folder", "--apply")]
    [DataRow("loop preset.json --accept-actions --output out --apply --proposals folder --copilot other", "not both")]
    [DataRow("check suite.json", "--accept-actions")]
    [DataRow("check suite.json --accept-actions=invalid", "invalid")]
    [DataRow("repair suite.json --apply", "--accept-actions")]
    [DataRow("check suite.json --accept-actions --json", "--json")]
    [DataRow("repair suite.json --accept-actions --json", "--json")]
    [DataRow("assessment-template scan.json --author Person", "--output")]
    [DataRow("assessment-template scan.json --output new.json", "--author")]
    [DataRow("assessment-template scan.json --output new.json --author Person --source ai", "--model")]
    [DataRow("assessment-template scan.json --output new.json --author Person --source robot", "robot")]
    [DataRow("assess scan.json --output out", "--assessment")]
    [DataRow("assess scan.json --assessment review.json --output out --json", "--json")]
    public async Task InvalidArgumentsNeverStartTheWorker(string commandLine, string message)
    {
        var parsed = _services.GetRequiredService<WinAppRootCommand>()
            .Parse((commandLine.StartsWith("audit", StringComparison.Ordinal) ? "ui " : "ui accessibility ") + commandLine,
                WinAppParserConfiguration.Default);
        Assert.IsNotEmpty(parsed.Errors, commandLine);
        StringAssert.Contains(string.Join(" ", parsed.Errors.Select(error => error.Message)), message);
        Assert.AreNotEqual(0, await InvokeAsync(parsed), commandLine);
        Assert.AreEqual(0, _worker.Calls, commandLine);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(17)]
    public async Task JsonStdoutStderrAndAllWorkerExitCodesArePreserved(int exitCode)
    {
        _worker.ExitCode = exitCode;
        _worker.Stdout = "{\"status\":\"partial\",\"evidence\":\"observed\"}\n";
        _worker.Stderr = "{\"type\":\"survey-progress\",\"progress\":1}\nworker diagnostic\n";
        Assert.AreEqual(exitCode, await InvokeAsync(["audit", "--app", "1", "--current-page", "--json", "--quiet"]));
        Assert.AreEqual(_worker.Stdout, _stdout.ToString());
        Assert.AreEqual(_worker.Stderr, _stderr.ToString());
        string[] expected = ["scan", "--json", "--app", "1"];
        CollectionAssert.AreEqual(expected, _worker.Arguments);
    }

    [TestMethod]
    public async Task VerboseIsNativeOnlyAndNeverPollutesJson()
    {
        Assert.AreEqual(0, await InvokeAsync(["audit", "--app", "1", "--verbose"]));
        StringAssert.Contains(_stderr.ToString(), "Starting the bundled");
        StringAssert.Contains(_stderr.ToString(), "exited with code 0");
        Assert.IsEmpty(_stdout.ToString());
        Assert.IsFalse(_worker.Arguments.Contains("--verbose", StringComparer.Ordinal));
        _stderr.GetStringBuilder().Clear();
        _worker.Stdout = "{\"status\":\"completed\"}\n";
        Assert.AreEqual(0, await InvokeAsync(["audit", "--app", "1", "--verbose", "--json"]));
        Assert.AreEqual(_worker.Stdout, _stdout.ToString());
        Assert.IsEmpty(_stderr.ToString());
    }

    [TestMethod]
    public async Task MissingWorkerProducesActionableJsonErrorAndNoSuccessOutput()
    {
        _worker.Error = new FileNotFoundException("Re-extract the complete local accessibility demo bundle; keep accessibility next to winapp.exe.");
        Assert.AreEqual(2, await InvokeAsync(["audit", "--app", "1", "--json"]));
        Assert.IsEmpty(_stdout.ToString());
        using var error = JsonDocument.Parse(_stderr.ToString());
        Assert.AreEqual("accessibility_worker_missing", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        StringAssert.Contains(error.RootElement.GetProperty("error").GetProperty("message").GetString()!, "complete local");
    }

    [TestMethod]
    public async Task WorkerCancellationIsNotReportedAsCompletedOrPaused()
    {
        _worker.Error = new OperationCanceledException();
        Assert.AreEqual(2, await InvokeAsync(["audit", "--app", "1", "--json"]));
        Assert.IsEmpty(_stdout.ToString());
        using var error = JsonDocument.Parse(_stderr.ToString());
        Assert.AreEqual("cancelled", error.RootElement.GetProperty("error").GetProperty("code").GetString());
        StringAssert.Contains(error.RootElement.GetProperty("error").GetProperty("message").GetString()!, "partial evidence");
    }

    private Task<int> InvokeAsync(string[] arguments) =>
        InvokeAsync(_services.GetRequiredService<WinAppRootCommand>()
            .Parse(arguments[0] == "audit" ? ["ui", .. arguments] : ["ui", "accessibility", .. arguments], WinAppParserConfiguration.Default));

    private Task<int> InvokeAsync(ParseResult parsed)
    {
        parsed.InvocationConfiguration.Output = _stdout;
        parsed.InvocationConfiguration.Error = _stderr;
        return parsed.InvokeAsync(parsed.InvocationConfiguration, TestContext.CancellationToken);
    }

    private sealed class WorkerFake : IUiAccessibilityWorkerService
    {
        internal int Calls { get; private set; }
        internal string[] Arguments { get; private set; } = [];
        internal int ExitCode { get; set; }
        internal string Stdout { get; set; } = "";
        internal string Stderr { get; set; } = "";
        internal Exception? Error { get; set; }

        public Task<int> RunAsync(IReadOnlyList<string> arguments, TextWriter output, TextWriter error, CancellationToken cancellationToken)
        {
            Calls++;
            Arguments = [.. arguments];
            cancellationToken.ThrowIfCancellationRequested();
            if (Error is not null)
            {
                throw Error;
            }
            output.Write(Stdout);
            error.Write(Stderr);
            return Task.FromResult(ExitCode);
        }
    }
}
