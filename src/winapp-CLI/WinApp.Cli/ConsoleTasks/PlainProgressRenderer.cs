// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.ConsoleTasks;

/// <summary>
/// Renders <see cref="GroupableTask"/> progress as plain, line-buffered output suitable for
/// CI logs and AI-agent terminal captures.
///
/// Unlike <see cref="GroupableTask{T}.Render"/>, which produces a re-drawn tree intended for
/// Spectre's <c>Live</c> display, this renderer prints each task's completion and each status
/// message exactly once as they occur, in the order they happen. No animation, no cursor
/// movement — just an append-only timeline.
///
/// In-progress lines are intentionally suppressed for sub-tasks: in plain mode we cannot
/// overwrite them with a ✅ later, so printing both creates duplicate output. Sub-task
/// progress is conveyed by streaming status messages and completion lines as they arrive.
/// Only the root task's start line is emitted, as an initial heartbeat.
/// </summary>
internal sealed class PlainProgressRenderer
{
    private readonly IAnsiConsole _console;
    private readonly Lock _renderLock;
    private readonly GroupableTask _root;
    private readonly HashSet<GroupableTask> _printedFinish = [];
    private bool _printedRootStart;

    public PlainProgressRenderer(IAnsiConsole console, Lock renderLock, GroupableTask root)
    {
        _console = console;
        _renderLock = renderLock;
        _root = root;
    }

    /// <summary>
    /// Walks the task tree and emits any new lines (newly-completed tasks, status messages
    /// added since the last call). Safe to call repeatedly; each line is emitted at most
    /// once.
    /// </summary>
    public void OnUpdate()
    {
        lock (_renderLock)
        {
            EmitRootStartIfNeeded();
            Walk(_root, depth: 0);
        }
    }

    private void EmitRootStartIfNeeded()
    {
        if (_printedRootStart)
        {
            return;
        }

        _printedRootStart = true;
        _console.WriteLine(_root.InProgressMessage);
    }

    private void Walk(GroupableTask task, int depth)
    {
        foreach (var sub in task.SubTasks)
        {
            Walk(sub, depth + 1);
        }

        EmitFinishIfNeeded(task, depth);
    }

    private void EmitFinishIfNeeded(GroupableTask task, int depth)
    {
        if (task.SuccessfullyCompleted is not bool success)
        {
            return;
        }

        // Prompts manage their own user-facing display.
        if (task is PromptConfirmationTask)
        {
            return;
        }

        if (!_printedFinish.Add(task))
        {
            return;
        }

        var indent = Indent(depth);

        if (task is StatusMessageTask sm)
        {
            // The message already carries its own symbol prefix (info/warning/verbose),
            // applied by TaskContext.AddStatusMessageInternal.
            _console.WriteLine($"{indent}{sm.CompletedMessage}");
            return;
        }

        // Match the live renderer: tasks whose tuple result carries a non-zero return
        // code are reported via StatusService.LogError instead, so suppress the line
        // here to avoid duplicate error output.
        if (task.IsFailedTupleResult)
        {
            return;
        }

        var symbol = success ? UiSymbols.Check : UiSymbols.Error;
        var message = task.CompletedDisplayMessage ?? task.InProgressMessage;
        // A step that succeeded with nothing to say prints nothing rather than a bare check mark.
        if (success && string.IsNullOrWhiteSpace(message))
        {
            return;
        }
        _console.WriteLine($"{indent}{symbol} {message}");
    }

    private static string Indent(int depth) => depth <= 0 ? string.Empty : new string(' ', depth * 2);
}

