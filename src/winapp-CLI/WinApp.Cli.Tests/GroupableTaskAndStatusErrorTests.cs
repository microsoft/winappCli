// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class GroupableTaskErrorReportingTests
{
    private static GroupableTask<(int ReturnCode, string CompletedMessage)> CreateTupleTask(
        Func<TaskContext, CancellationToken, Task<(int, string)>> body,
        ILogger logger)
    {
        return new GroupableTask<(int ReturnCode, string CompletedMessage)>(
            "test",
            null,
            body,
            new TestConsole(),
            logger,
            new Lock());
    }

    [TestMethod]
    public async Task ExecuteAsync_TupleReturn_ExceptionMessageSurfacedInItem2()
    {
        var task = CreateTupleTask(
            (_, _) => throw new InvalidOperationException("boom"),
            NullLogger.Instance);

        var result = await task.ExecuteAsync(null, CancellationToken.None);

        Assert.IsFalse(task.SuccessfullyCompleted!.Value);
        Assert.AreEqual(1, result.ReturnCode);
        Assert.AreEqual("boom", result.CompletedMessage);
    }

    [TestMethod]
    public async Task ExecuteAsync_TupleReturn_FallsBackToTypeNameWhenMessageBlank()
    {
        var task = CreateTupleTask(
            (_, _) => throw new InvalidOperationException(""),
            NullLogger.Instance);

        var result = await task.ExecuteAsync(null, CancellationToken.None);

        Assert.AreEqual(1, result.ReturnCode);
        StringAssert.Contains(result.CompletedMessage, nameof(InvalidOperationException));
    }

    [TestMethod]
    public async Task ExecuteAsync_TupleReturn_StackAppendedOnlyAtDebugLevel()
    {
        // Information-level logger: stack should NOT be appended
        var infoTask = CreateTupleTask(
            (_, _) => throw new InvalidOperationException("boom"),
            new MinLevelLogger(LogLevel.Information));
        var infoResult = await infoTask.ExecuteAsync(null, CancellationToken.None);
        Assert.AreEqual("boom", infoResult.CompletedMessage,
            "Stack trace must not be appended when Debug isn't enabled");

        // Debug-level logger: stack SHOULD be appended
        var debugTask = CreateTupleTask(
            (_, _) => throw new InvalidOperationException("boom"),
            new MinLevelLogger(LogLevel.Debug));
        var debugResult = await debugTask.ExecuteAsync(null, CancellationToken.None);
        StringAssert.StartsWith(debugResult.CompletedMessage, "boom");
        Assert.IsTrue(debugResult.CompletedMessage.Length > "boom".Length,
            "Debug-level should include stack trace text");
    }

    [TestMethod]
    public async Task ExecuteAsync_TupleReturn_SuccessPathLeavesItem2Untouched()
    {
        var task = CreateTupleTask(
            (_, _) => Task.FromResult((0, "ok")),
            NullLogger.Instance);

        var result = await task.ExecuteAsync(null, CancellationToken.None);

        Assert.IsTrue(task.SuccessfullyCompleted!.Value);
        Assert.AreEqual(0, result.ReturnCode);
        Assert.AreEqual("ok", result.CompletedMessage);
    }

    /// <summary>Minimal ILogger that only filters by level; ignores writes.</summary>
    private sealed class MinLevelLogger(LogLevel min) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= min;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }
}

[TestClass]
public class StatusServiceErrorMessageFallbackTests
{
    /// <summary>
    /// Captures log entries so we can verify the user-visible error rendering.
    /// </summary>
    private sealed class CapturingLogger : ILogger<StatusService>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public LogLevel MinLevel { get; set; } = LogLevel.Information;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= MinLevel;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_NullCompletedMessage_LogsFallbackText()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger();
        var svc = new StatusService(console, logger);

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running",
            (_, _) => Task.FromResult((1, (string)null!)),
            CancellationToken.None);

        Assert.AreEqual(1, rc);
        Assert.IsTrue(
            logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message == "Operation failed without an error message."),
            "Expected fallback message when CompletedMessage is null");
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_WhitespaceCompletedMessage_LogsFallbackText()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger();
        var svc = new StatusService(console, logger);

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running",
            (_, _) => Task.FromResult((1, "   ")),
            CancellationToken.None);

        Assert.AreEqual(1, rc);
        Assert.IsTrue(
            logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message == "Operation failed without an error message."));
    }

    // `devtools attach --show-window` printed a bare check mark: a step that succeeds with nothing to say prints nothing.
    [TestMethod]
    public async Task ExecuteWithStatusAsync_SuccessWithoutMessage_PrintsNoBareCheckMark()
    {
        var console = new TestConsole();
        var svc = new StatusService(console, new CapturingLogger());

        var rc = await svc.ExecuteWithStatusAsync<string>("Attaching...", (_, _) => Task.FromResult((0, string.Empty)), CancellationToken.None);

        Assert.AreEqual(0, rc);
        var lines = console.Output.Replace("\r\n", "\n").Split('\n');
        Assert.IsFalse(lines.Any(line => line.Trim() == Spectre.Console.Emoji.Known.CheckMarkButton),
            "no line is only a check mark: " + console.Output);
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_RealCompletedMessage_PassesItThrough()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger();
        var svc = new StatusService(console, logger);

        var rc = await svc.ExecuteWithStatusAsync<string>(
            "running",
            (_, _) => Task.FromResult((2, "explicit failure")),
            CancellationToken.None);

        Assert.AreEqual(2, rc);
        Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Error && e.Message == "explicit failure"));
        Assert.IsFalse(logger.Entries.Any(e => e.Message == "Operation failed without an error message."),
            "Fallback must not appear when a real message is present");
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_NonDebugLevel_EmitsVerboseHint()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger { MinLevel = LogLevel.Information };
        var svc = new StatusService(console, logger);

        await svc.ExecuteWithStatusAsync<string>(
            "running",
            (_, _) => Task.FromResult((1, "msg")),
            CancellationToken.None);

        Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Information && e.Message.Contains("--verbose")),
            "Verbose hint should be emitted when not at Debug level");
    }

    [TestMethod]
    public async Task ExecuteWithStatusAsync_DebugLevel_DoesNotEmitVerboseHint()
    {
        var console = new TestConsole();
        var logger = new CapturingLogger { MinLevel = LogLevel.Debug };
        var svc = new StatusService(console, logger);

        await svc.ExecuteWithStatusAsync<string>(
            "running",
            (_, _) => Task.FromResult((1, "msg")),
            CancellationToken.None);

        Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("--verbose")),
            "Verbose hint must be suppressed at Debug level");
    }
}

/// <summary>
/// Coverage for <see cref="GroupableTask{T}"/>'s rendering pipeline and result-shape helpers — the
/// live/plain console renderers that turn a task tree into a Spectre panel. Assertions inspect the
/// actual rendered text (checkmark vs cross, message passthrough, failed-tuple suppression, spinner +
/// sub-status, depth collapsing) and the typed <c>CompletedDisplayMessage</c>/<c>IsFailedTupleResult</c>
/// contracts the plain renderer relies on — not merely that rendering runs.
/// </summary>
[TestClass]
public class GroupableTaskRenderingTests
{
    private static GroupableTask<T> MakeTask<T>(
        string message,
        Func<TaskContext, CancellationToken, Task<T>>? body,
        ILogger? logger = null)
        => new(message, null, body, new TestConsole(), logger ?? NullLogger.Instance, new Lock());

    private static string RenderToText<T>(GroupableTask<T> root, bool lastRender = false)
    {
        var console = new TestConsole();
        console.Profile.Width = 200;
        console.Write(root.Render(lastRender));
        return console.Output;
    }

    [TestMethod]
    public async Task Render_CompletedStringTask_ShowsGreenCheckAndMessage()
    {
        var root = MakeTask<string>("building", (_, _) => Task.FromResult("Build succeeded"));
        await root.ExecuteAsync(null, CancellationToken.None);

        var output = RenderToText(root);

        StringAssert.Contains(output, "Build succeeded");
        StringAssert.Contains(output, Emoji.Known.CheckMarkButton);
    }

    [TestMethod]
    public async Task Render_CompletedIntStringTuple_UsesTrailingStringElement()
    {
        var root = MakeTask<(int, string)>("packaging", (_, _) => Task.FromResult((0, "Packaged app.msix")));
        await root.ExecuteAsync(null, CancellationToken.None);

        var output = RenderToText(root);

        StringAssert.Contains(output, "Packaged app.msix");
        StringAssert.Contains(output, Emoji.Known.CheckMarkButton);
    }

    [TestMethod]
    public async Task Render_CompletedStringIntTuple_UsesLeadingStringElement()
    {
        var root = MakeTask<(string, int)>("signing", (_, _) => Task.FromResult(("Signed package", 0)));
        await root.ExecuteAsync(null, CancellationToken.None);

        var output = RenderToText(root);

        StringAssert.Contains(output, "Signed package");
    }

    [TestMethod]
    public async Task Render_FailedTupleResult_OmitsLineFromTree()
    {
        var root = MakeTask<(int, string)>("deploying", (_, _) => Task.FromResult((3, "unique-deploy-failure-marker")));
        await root.ExecuteAsync(null, CancellationToken.None);

        var output = RenderToText(root, lastRender: true);

        Assert.IsFalse(output.Contains("unique-deploy-failure-marker"),
            "A failed (non-zero return code) tuple is logged to stderr separately and must be omitted from the task tree.");
    }

    [TestMethod]
    public void Render_InProgressTaskWithSubStatus_ShowsMessageAndSubStatus()
    {
        var root = MakeTask<string>("Downloading SDK", null);
        root.SubStatus = "42%";

        var output = RenderToText(root);

        StringAssert.Contains(output, "Downloading SDK (42%)");
    }

    [TestMethod]
    public void Render_InProgressTaskWithMarkupChars_EscapesWithoutThrowing()
    {
        var root = MakeTask<string>("Building [Debug|AnyCPU]", null);

        var output = RenderToText(root);

        StringAssert.Contains(output, "Building [Debug|AnyCPU]",
            "EscapeInProgressMessage must neutralize Spectre markup so literal brackets render verbatim.");
    }

    [TestMethod]
    public void Render_InProgressTaskWithEscapingDisabled_InterpretsCallerMarkup()
    {
        var root = MakeTask<string>("[green]ready[/]", null);
        root.EscapeInProgressMessage = false;

        var output = RenderToText(root);

        StringAssert.Contains(output, "ready",
            "With escaping disabled the caller-supplied markup is interpreted, so the styled text still surfaces.");
        Assert.IsFalse(output.Contains("[green]"),
            "Interpreted markup must not leak its raw tags into the output.");
    }

    [TestMethod]
    public async Task Render_BaseGroupableTaskChild_RendersInProgressMessageAsCompletedLine()
    {
        var root = MakeTask<string>("root", (_, _) => Task.FromResult("root done"));
        await root.ExecuteAsync(null, CancellationToken.None);
        root.SubTasks.Add(new GroupableTask("Cleanup complete", root) { SuccessfullyCompleted = true });

        var output = RenderToText(root);

        StringAssert.Contains(output, "Cleanup complete",
            "A plain (non-generic) completed sub-task renders its in-progress message as the completed line.");
    }

    [TestMethod]
    public async Task Render_StatusMessageChild_RendersItsCompletedMessage()
    {
        var root = MakeTask<string>("root", (_, _) => Task.FromResult("root done"));
        await root.ExecuteAsync(null, CancellationToken.None);
        root.SubTasks.Add(new StatusMessageTask("\u2139  Restored 12 packages", root, new TestConsole(), NullLogger.Instance, new Lock()));

        var output = RenderToText(root);

        StringAssert.Contains(output, "Restored 12 packages");
    }

    [TestMethod]
    public async Task Render_CompletedMessageStartingWithEmoji_OmitsRedundantCheckmark()
    {
        var root = MakeTask<string>("launch", (_, _) => Task.FromResult("\uD83D\uDE80 Launched"));
        await root.ExecuteAsync(null, CancellationToken.None);

        var output = RenderToText(root, lastRender: true);

        StringAssert.Contains(output, "Launched");
        Assert.IsFalse(output.Contains(Emoji.Known.CheckMarkButton),
            "A completion message that already begins with an emoji must not receive a duplicate checkmark prefix.");
    }

    [TestMethod]
    public async Task ExecuteAsync_NonTupleBodyThrows_ReturnsDefaultAndRendersRedCross()
    {
        var root = MakeTask<string>("risky", (_, _) => throw new InvalidOperationException("kaboom"));

        var result = await root.ExecuteAsync(null, CancellationToken.None);

        Assert.IsNull(result, "A non-tuple task whose body throws must yield default(T) (null for string).");
        Assert.IsFalse(root.SuccessfullyCompleted!.Value, "A thrown body must mark the task as not successfully completed.");

        var output = RenderToText(root, lastRender: true);
        StringAssert.Contains(output, Emoji.Known.CrossMark,
            "A failed non-tuple task must render the red cross marker.");
    }

    [TestMethod]
    public async Task Render_VerboseLogger_ExpandsDeeplyNestedCompletedSubTasks()
    {
        var verboseLogger = new CapturingLogger<string> { MinLevel = LogLevel.Debug };
        var root = new GroupableTask<string>("root", null, (_, _) => Task.FromResult("root done"), new TestConsole(), verboseLogger, new Lock());
        await root.ExecuteAsync(null, CancellationToken.None);
        var child = new GroupableTask("child level", root) { SuccessfullyCompleted = true };
        child.SubTasks.Add(new GroupableTask("grandchild-marker", child) { SuccessfullyCompleted = true });
        root.SubTasks.Add(child);

        var output = RenderToText(root, lastRender: true);

        StringAssert.Contains(output, "grandchild-marker",
            "A Debug-enabled (verbose) logger expands the full task tree, including deeply nested completed tasks.");
    }

    [TestMethod]
    public async Task Render_NonVerboseLogger_CollapsesCompletedGrandchildren()
    {
        var root = new GroupableTask<string>("root", null, (_, _) => Task.FromResult("root done"), new TestConsole(), NullLogger.Instance, new Lock());
        await root.ExecuteAsync(null, CancellationToken.None);
        var child = new GroupableTask("child level", root) { SuccessfullyCompleted = true };
        child.SubTasks.Add(new GroupableTask("grandchild-marker", child) { SuccessfullyCompleted = true });
        root.SubTasks.Add(child);

        var output = RenderToText(root, lastRender: true);

        StringAssert.Contains(output, "child level", "Depth-1 completed children are always shown.");
        Assert.IsFalse(output.Contains("grandchild-marker"),
            "Without verbose logging, completed tasks deeper than depth 1 are collapsed to keep the tree concise.");
    }

    [TestMethod]
    public void CompletedDisplayMessage_BaseTask_IsNull()
        => Assert.IsNull(new GroupableTask("plain", null).CompletedDisplayMessage);

    [TestMethod]
    public void CompletedDisplayMessage_NotYetCompleted_IsNull()
    {
        var task = MakeTask<string>("pending", (_, _) => Task.FromResult("later"));
        Assert.IsNull(task.CompletedDisplayMessage, "Before execution there is no completion message to display.");
    }

    [TestMethod]
    public async Task CompletedDisplayMessage_StringResult_ReturnsString()
    {
        var task = MakeTask<string>("s", (_, _) => Task.FromResult("done text"));
        await task.ExecuteAsync(null, CancellationToken.None);
        Assert.AreEqual("done text", task.CompletedDisplayMessage);
    }

    [TestMethod]
    public async Task CompletedDisplayMessage_TupleResult_ReturnsFirstStringElement()
    {
        var task = MakeTask<(int, string)>("t", (_, _) => Task.FromResult((0, "tuple message")));
        await task.ExecuteAsync(null, CancellationToken.None);
        Assert.AreEqual("tuple message", task.CompletedDisplayMessage,
            "The plain renderer prefers the first string element of a tuple result.");
    }

    [TestMethod]
    public async Task CompletedDisplayMessage_TupleWithoutStringElement_FallsBackToToString()
    {
        // A tuple result whose elements are all non-string exercises the loop-fall-through: no element
        // matches, so the getter drops to CompletedMessage.ToString().
        var task = MakeTask<(int, int)>("t", (_, _) => Task.FromResult((7, 9)));
        await task.ExecuteAsync(null, CancellationToken.None);
        Assert.AreEqual("(7, 9)", task.CompletedDisplayMessage,
            "A tuple result with no string element must fall back to ToString().");
    }

    [TestMethod]
    public async Task CompletedDisplayMessage_NonStringNonTupleResult_ReturnsToString()
    {
        var task = MakeTask<int>("n", (_, _) => Task.FromResult(4242));
        await task.ExecuteAsync(null, CancellationToken.None);
        Assert.AreEqual("4242", task.CompletedDisplayMessage);
    }

    [TestMethod]
    public void IsFailedTupleResult_BaseTask_IsFalse()
        => Assert.IsFalse(new GroupableTask("plain", null).IsFailedTupleResult);

    [TestMethod]
    public async Task IsFailedTupleResult_NonZeroReturnCodeTuple_IsTrue()
    {
        var task = MakeTask<(int, string)>("t", (_, _) => Task.FromResult((5, "boom")));
        await task.ExecuteAsync(null, CancellationToken.None);
        Assert.IsTrue(task.IsFailedTupleResult, "A tuple whose first element is a non-zero return code is a failed result.");
    }

    [TestMethod]
    public async Task IsFailedTupleResult_ZeroReturnCodeTuple_IsFalse()
    {
        var task = MakeTask<(int, string)>("t", (_, _) => Task.FromResult((0, "ok")));
        await task.ExecuteAsync(null, CancellationToken.None);
        Assert.IsFalse(task.IsFailedTupleResult, "A zero return code is a success, not a failed tuple.");
    }

    [TestMethod]
    public async Task IsFailedTupleResult_StringResult_IsFalse()
    {
        var task = MakeTask<string>("s", (_, _) => Task.FromResult("done"));
        await task.ExecuteAsync(null, CancellationToken.None);
        Assert.IsFalse(task.IsFailedTupleResult, "A non-tuple result can never be a failed tuple.");
    }

    [TestMethod]
    public void Dispose_RecursivelyWalksSubTaskTreeAndLeavesItIntact()
    {
        var root = MakeTask<string>("root", (_, _) => Task.FromResult("done"));
        var child = new GroupableTask("child", root);
        var grandChild = new GroupableTask("grandchild", child);
        child.SubTasks.Add(grandChild);
        root.SubTasks.Add(child);

        root.Dispose();

        // Dispose is a non-destructive recursive walk: it disposes each descendant without mutating the
        // tree, so the structure remains fully traversable afterwards.
        Assert.AreEqual(1, root.SubTasks.Count);
        Assert.AreSame(child, root.SubTasks.Single());
        Assert.AreEqual(1, child.SubTasks.Count);
        Assert.AreSame(grandChild, child.SubTasks.Single());
    }
}
