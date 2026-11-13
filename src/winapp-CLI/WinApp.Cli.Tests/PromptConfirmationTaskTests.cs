// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using Spectre.Console;
using Spectre.Console.Rendering;
using Spectre.Console.Testing;
using WinApp.Cli.ConsoleTasks;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class PromptConfirmationTaskTests
{
    [TestMethod]
    public async Task WaitForInputAsync_YThenEnter_ConfirmsAndShowsYesPrompt()
    {
        var (task, console, updates) = CreateTask("Install package?");
        console.Input.PushKey(ConsoleKey.Y);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsTrue(result);
        Assert.AreEqual(PromptState.Confirmed, task.State);
        Assert.AreEqual(true, task.CompletedMessage);
        Assert.AreEqual(true, task.SuccessfullyCompleted);
        Assert.Contains("Install package?", task.InProgressMessage);
        Assert.Contains("Yes", task.InProgressMessage);
        Assert.IsTrue(updates.Any(message => message.Contains("[green](y)[/]: Y", StringComparison.Ordinal)), string.Join("|", updates));
    }

    [TestMethod]
    public async Task WaitForInputAsync_NThenEnter_DeclinesAndShowsNoPrompt()
    {
        var (task, console, updates) = CreateTask("Continue?");
        console.Input.PushKey(ConsoleKey.N);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.Declined, task.State);
        Assert.AreEqual(false, task.CompletedMessage);
        Assert.AreEqual(true, task.SuccessfullyCompleted);
        Assert.Contains("Continue?", task.InProgressMessage);
        Assert.Contains("No", task.InProgressMessage);
        Assert.IsTrue(updates.Any(message => message.Contains("[green](y)[/]: N", StringComparison.Ordinal)), string.Join("|", updates));
    }

    [TestMethod]
    public async Task WaitForInputAsync_EnterWithNoTypedInput_UsesDefaultYes()
    {
        var (task, console, updates) = CreateTask("Use defaults?");
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsTrue(result);
        Assert.AreEqual(PromptState.Confirmed, task.State);
        Assert.IsTrue(updates.First().Contains("Use defaults? [blue][[Y/n]][/] [green](y)[/]: ", StringComparison.Ordinal));
        Assert.Contains("Yes", task.InProgressMessage);
    }

    [TestMethod]
    public async Task WaitForInputAsync_InvalidThenValid_IgnoresInvalidKeyAndAcceptsValidAnswer()
    {
        var (task, console, updates) = CreateTask("Overwrite?");
        console.Input.PushKey(ConsoleKey.X);
        console.Input.PushKey(ConsoleKey.N);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.Declined, task.State);
        Assert.IsFalse(updates.Any(message => message.Contains(": X", StringComparison.Ordinal)), string.Join("|", updates));
        Assert.IsTrue(updates.Any(message => message.Contains(": N", StringComparison.Ordinal)), string.Join("|", updates));
    }

    [TestMethod]
    public async Task WaitForInputAsync_BackspaceRemovesTypedAnswerBeforeDefaultEnter()
    {
        var (task, console, updates) = CreateTask("Proceed?");
        console.Input.PushKey(ConsoleKey.N);
        console.Input.PushKey(ConsoleKey.Backspace);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsTrue(result);
        Assert.AreEqual(PromptState.Confirmed, task.State);
        Assert.IsTrue(updates.Any(message => message.EndsWith(": N", StringComparison.Ordinal)), string.Join("|", updates));
        Assert.IsTrue(updates.Any(message => message.EndsWith(": ", StringComparison.Ordinal)), string.Join("|", updates));
        Assert.Contains("Yes", task.InProgressMessage);
    }

    [TestMethod]
    public async Task WaitForInputAsync_EscapeDeclinesImmediately()
    {
        var (task, console, _) = CreateTask("Cancel?");
        console.Input.PushKey(ConsoleKey.Escape);
        console.Input.PushKey(ConsoleKey.Y);
        console.Input.PushKey(ConsoleKey.Enter);

        var result = await task.WaitForInputAsync(CancellationToken.None);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.Declined, task.State);
        Assert.Contains("No", task.InProgressMessage);
    }

    [TestMethod]
    public async Task WaitForInputAsync_CancellationBeforeInput_ReturnsFalseAndShowsCancelledPrompt()
    {
        var (task, _, updates) = CreateTask("Wait forever?");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var result = await task.WaitForInputAsync(cts.Token);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.Cancelled, task.State);
        Assert.AreEqual(false, task.CompletedMessage);
        Assert.Contains("Wait forever?", task.InProgressMessage);
        Assert.Contains("(cancelled)", task.InProgressMessage);
        Assert.IsTrue(updates.Last().Contains("(cancelled)", StringComparison.Ordinal), string.Join("|", updates));
    }

    [TestMethod]
    public async Task WaitForInputAsync_NoInputAvailable_TreatedAsCancelled()
    {
        // An empty console reader reports EOF as InvalidOperationException; the prompt treats it as cancelled.
        var (task, _, updates) = CreateTask("Still waiting?");
        using var cts = new CancellationTokenSource();

        var result = await task.WaitForInputAsync(cts.Token);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.Cancelled, task.State);
        Assert.IsTrue(updates.Last().Contains("(cancelled)", StringComparison.Ordinal), string.Join("|", updates));
    }

    [TestMethod]
    public async Task WaitForInputAsync_CancelledWhileWaitingForKey_ReturnsFalseAndShowsCancelledPrompt()
    {
        // The console blocks in ReadKeyAsync until the token fires, so this genuinely exercises the
        // OperationCanceledException path rather than the empty-console EOF fast-path.
        var (task, updates) = CreateBlockingTask("Still waiting?");
        using var cts = new CancellationTokenSource();

        var pending = task.WaitForInputAsync(cts.Token);
        Assert.IsFalse(pending.IsCompleted, "The prompt should block while the reader is waiting for a key.");

        cts.Cancel();
        var result = await pending;

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.Cancelled, task.State);
        Assert.IsTrue(updates.Last().Contains("(cancelled)", StringComparison.Ordinal), string.Join("|", updates));
    }

    [TestMethod]
    public void FormatPromptMessage_UnknownStateFallsBackToPromptText()
    {
        var method = typeof(PromptConfirmationTask).GetMethod("FormatPromptMessage", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

        var result = method!.Invoke(null, ["Raw prompt", (PromptState)999, "ignored"]);

        Assert.AreEqual("Raw prompt", result);
    }

    [TestMethod]
    public async Task ExecuteAsync_IsNotInputDriverAndReturnsFalseWithoutChangingState()
    {
        var (task, _, _) = CreateTask("Execute?");

        var result = await task.ExecuteAsync(null, CancellationToken.None);

        Assert.IsFalse(result);
        Assert.AreEqual(PromptState.WaitingForInput, task.State);
        Assert.IsNull(task.SuccessfullyCompleted);
    }

    private static (PromptConfirmationTask Task, TestConsole Console, List<string> Updates) CreateTask(string promptText)
    {
        var console = new TestConsole();
        var updates = new List<string>();
        PromptConfirmationTask? task = null;
        task = new PromptConfirmationTask(
            promptText,
            parent: null,
            ansiConsole: console,
            logger: NullLogger.Instance,
            renderLock: new Lock(),
            onUpdate: () => updates.Add(task!.InProgressMessage));
        updates.Add(task.InProgressMessage);
        return (task, console, updates);
    }

    private static (PromptConfirmationTask Task, List<string> Updates) CreateBlockingTask(string promptText)
    {
        var console = new BlockingInputConsole(new TestConsole());
        var updates = new List<string>();
        PromptConfirmationTask? task = null;
        task = new PromptConfirmationTask(
            promptText,
            parent: null,
            ansiConsole: console,
            logger: NullLogger.Instance,
            renderLock: new Lock(),
            onUpdate: () => updates.Add(task!.InProgressMessage));
        updates.Add(task.InProgressMessage);
        return (task, updates);
    }

    // A console whose input blocks until the cancellation token fires, so tests can exercise the real
    // "cancelled while waiting for a key" path (ReadKeyAsync throws OperationCanceledException) rather
    // than the empty-console EOF fast-path.
    private sealed class BlockingInputConsole(IAnsiConsole inner) : IAnsiConsole
    {
        public Profile Profile => inner.Profile;
        public IAnsiConsoleCursor Cursor => inner.Cursor;
        public IAnsiConsoleInput Input { get; } = new BlockingConsoleInput();
        public IExclusivityMode ExclusivityMode => inner.ExclusivityMode;
        public RenderPipeline Pipeline => inner.Pipeline;
        public void Clear(bool home) => inner.Clear(home);
        public void Write(IRenderable renderable) => inner.Write(renderable);
    }

    private sealed class BlockingConsoleInput : IAnsiConsoleInput
    {
        public bool IsKeyAvailable() => false;

        public ConsoleKeyInfo? ReadKey(bool intercept) => throw new NotSupportedException();

        public async Task<ConsoleKeyInfo?> ReadKeyAsync(bool intercept, CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(false);
            return null;
        }
    }
}
