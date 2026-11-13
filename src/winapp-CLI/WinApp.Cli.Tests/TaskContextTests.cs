// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for <see cref="TaskContext"/> — the handle task bodies use to stream status/debug lines,
/// spawn sub-tasks, prompt the user, and surface errors. Assertions verify the real emitted task tree
/// (symbol-prefixed status messages, verbose gating, sub-task results, prompt outcome, sub-status)
/// rather than merely that the calls do not throw.
/// </summary>
[TestClass]
public sealed class TaskContextTests
{
    private static (TaskContext Ctx, GroupableTask<string> Parent, TestConsole Console, CapturingLogger<TaskContext> Logger, List<string> Updates) Create(
        LogLevel minLevel = LogLevel.Debug)
    {
        var console = new TestConsole();
        var logger = new CapturingLogger<TaskContext> { MinLevel = minLevel };
        var renderLock = new Lock();
        var parent = new GroupableTask<string>("root", null, null, console, logger, renderLock);
        var updates = new List<string>();
        var ctx = new TaskContext(parent, () => updates.Add("update"), console, logger, renderLock);
        return (ctx, parent, console, logger, updates);
    }

    private static StatusMessageTask SingleStatusMessage(GroupableTask parent)
    {
        Assert.AreEqual(1, parent.SubTasks.Count, "Expected exactly one status sub-task to be registered.");
        var sub = parent.SubTasks.Single();
        return (StatusMessageTask)sub;
    }

    [TestMethod]
    public void AddStatusMessage_PrefixesPlainTextWithInfoSymbolAndNotifiesRenderer()
    {
        var (ctx, parent, _, _, updates) = Create();

        ctx.AddStatusMessage("Restored packages");

        var sm = SingleStatusMessage(parent);
        Assert.IsTrue(sm.CompletedMessage!.StartsWith(UiSymbols.Info, StringComparison.Ordinal),
            $"Plain status text must be prefixed with the info symbol; got '{sm.CompletedMessage}'.");
        Assert.Contains("Restored packages", sm.CompletedMessage);
        Assert.AreEqual(1, updates.Count, "Adding a status message must trigger a single render update.");
    }

    [TestMethod]
    public void AddStatusMessage_MessageAlreadyStartingWithSymbol_IsNotReprefixed()
    {
        var (ctx, parent, _, _, _) = Create();

        ctx.AddStatusMessage("• already bulleted");

        var sm = SingleStatusMessage(parent);
        Assert.AreEqual("• already bulleted", sm.CompletedMessage,
            "A message that already starts with a non-alphanumeric symbol must be left untouched.");
    }

    [TestMethod]
    public void AddStatusMessage_WarningPrefixedMessage_GetsSeparatingSpaceInserted()
    {
        var (ctx, parent, _, _, _) = Create();

        ctx.AddStatusMessage($"{UiSymbols.Warning}Low disk space");

        var sm = SingleStatusMessage(parent);
        Assert.AreEqual($"{UiSymbols.Warning} Low disk space", sm.CompletedMessage,
            "A warning-symbol prefix must be separated from its text by a single space.");
    }

    [TestMethod]
    public void AddStatusMessage_EmptyMessage_StillEmitsInfoSymbol()
    {
        var (ctx, parent, _, _, _) = Create();

        ctx.AddStatusMessage(string.Empty);

        var sm = SingleStatusMessage(parent);
        Assert.IsTrue(sm.CompletedMessage!.StartsWith(UiSymbols.Info, StringComparison.Ordinal),
            "An empty status message must still render the info symbol rather than a blank line.");
    }

    [TestMethod]
    public void AddStatusMessage_MalformedLeadingSurrogate_FallsBackToSymbolCheckAndPrefixes()
    {
        var (ctx, parent, _, _, _) = Create();

        // A lone low surrogate is ill-formed UTF-16, so Rune parsing fails and the char-based
        // fallback runs. It is not punctuation/symbol, so the info prefix is applied.
        ctx.AddStatusMessage("\uDC00broken");

        var sm = SingleStatusMessage(parent);
        Assert.IsTrue(sm.CompletedMessage!.StartsWith(UiSymbols.Info, StringComparison.Ordinal),
            "Malformed leading surrogate text must fall back to the char check and still be prefixed.");
    }

    [TestMethod]
    public void AddDebugMessage_VerboseDisabled_EmitsNothing()
    {
        var (ctx, parent, _, _, updates) = Create(LogLevel.Information);

        Assert.IsFalse(ctx.IsVerboseEnabled, "Verbose must report disabled when Debug logging is off.");
        ctx.AddDebugMessage("internal detail");

        Assert.AreEqual(0, parent.SubTasks.Count, "Debug messages must be suppressed when verbose is off.");
        Assert.AreEqual(0, updates.Count, "Suppressed debug messages must not trigger render updates.");
    }

    [TestMethod]
    public void AddDebugMessage_VerboseEnabled_EmitsVerbosePrefixedMessage()
    {
        var (ctx, parent, _, _, updates) = Create(LogLevel.Debug);

        Assert.IsTrue(ctx.IsVerboseEnabled, "Verbose must report enabled when Debug logging is on.");
        ctx.AddDebugMessage("resolved 5 assets");

        var sm = SingleStatusMessage(parent);
        Assert.IsTrue(sm.CompletedMessage!.StartsWith(UiSymbols.Verbose, StringComparison.Ordinal),
            $"Verbose debug text must be prefixed with the verbose symbol; got '{sm.CompletedMessage}'.");
        Assert.Contains("resolved 5 assets", sm.CompletedMessage);
        Assert.AreEqual(1, updates.Count);
    }

    [TestMethod]
    public async Task AddSubTaskAsync_RunsBodyReturnsResultAndRegistersCompletedSubTask()
    {
        var (ctx, parent, _, _, _) = Create();

        var result = await ctx.AddSubTaskAsync(
            "compute answer",
            (_, _) => Task.FromResult(42),
            CancellationToken.None);

        Assert.AreEqual(42, result, "AddSubTaskAsync must return the sub-task body's result.");
        Assert.AreEqual(1, parent.SubTasks.Count, "The sub-task must be registered under the parent.");
        var sub = (GroupableTask<int>)parent.SubTasks.Single();
        Assert.AreEqual(true, sub.SuccessfullyCompleted);
        Assert.AreEqual(42, sub.CompletedMessage);
    }

    [TestMethod]
    public async Task AddSubTaskAsync_NestedContext_CanStreamStatusIntoTheSubTask()
    {
        var (ctx, parent, _, _, _) = Create();

        await ctx.AddSubTaskAsync(
            "outer",
            async (subCtx, ct) =>
            {
                subCtx.AddStatusMessage("inner progress");
                return await Task.FromResult(0);
            },
            CancellationToken.None);

        var sub = parent.SubTasks.Single();
        var inner = (StatusMessageTask)sub.SubTasks.Single();
        Assert.Contains("inner progress", inner.CompletedMessage!);
    }

    [TestMethod]
    public void StatusError_LogsErrorThroughLogger()
    {
        var (ctx, _, _, logger, _) = Create();

        ctx.StatusError("packaging failed for {0}", "app.msix");

        Assert.IsTrue(logger.Has(LogLevel.Error, "packaging failed for app.msix"),
            "StatusError must surface a formatted error through the logger.");
    }

    [TestMethod]
    public async Task PromptConfirmationAsync_UserConfirms_ReturnsTrueAndRegistersPromptTask()
    {
        var (ctx, parent, console, _, _) = Create();
        console.Input.PushKey(ConsoleKey.Y);
        console.Input.PushKey(ConsoleKey.Enter);

        var confirmed = await ctx.PromptConfirmationAsync("Overwrite existing package?", CancellationToken.None);

        Assert.IsTrue(confirmed, "Pressing Y then Enter must confirm the prompt.");
        Assert.IsInstanceOfType<PromptConfirmationTask>(parent.SubTasks.Single(),
            "The confirmation prompt must be registered as a sub-task so it renders inline.");
    }

    [TestMethod]
    public async Task PromptConfirmationAsync_UserDeclines_ReturnsFalse()
    {
        var (ctx, _, console, _, _) = Create();
        console.Input.PushKey(ConsoleKey.N);
        console.Input.PushKey(ConsoleKey.Enter);

        var confirmed = await ctx.PromptConfirmationAsync("Delete output folder?", CancellationToken.None);

        Assert.IsFalse(confirmed, "Pressing N then Enter must decline the prompt.");
    }

    [TestMethod]
    public void UpdateSubStatus_SetsAndClearsParentSubStatus()
    {
        var (ctx, parent, _, _, _) = Create();

        ctx.UpdateSubStatus("2 of 5");
        Assert.AreEqual("2 of 5", parent.SubStatus, "UpdateSubStatus must publish the running detail on the task.");

        ctx.UpdateSubStatus(null);
        Assert.IsNull(parent.SubStatus, "Clearing the sub-status must remove the running detail.");
    }
}
