// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Runtime.CompilerServices;
using System.Text;
using WinApp.Cli.Models;

namespace WinApp.Cli.ConsoleTasks;

internal class GroupableTask(string inProgressMessage, GroupableTask? parent) : IDisposable
{
    public List<GroupableTask> SubTasks { get; set; } = [];
    public bool IsCompleted { get; protected set; }
    public GroupableTask? Parent { get; } = parent;
    public string InProgressMessage { get; set; } = inProgressMessage;
    public string? SubStatus { get; set; }

    public bool SpinnerPaused
    {
        get
        {
            var spinnerTask = FindTaskWithSpinner();
            return spinnerTask == null || spinnerTask._spinnerPaused;
        }
    }

    private CancellationTokenSource? _spinnerCancellation;
    private Task? _spinnerTask;
    private bool _spinnerPaused;
    private Action? _spinnerOnUpdate;

    public GroupableTask? FindTaskWithSpinner()
    {
        var current = this;
        while (current != null)
        {
            if (current._spinnerCancellation != null)
            {
                return current;
            }
            current = current.Parent;
        }
        return null;
    }

    public void PauseSpinner()
    {
        if (_spinnerCancellation != null && !_spinnerPaused)
        {
            _spinnerPaused = true;
            _spinnerCancellation.Cancel();
            try
            {
                _spinnerTask?.Wait(200);
            }
            catch (AggregateException)
            {
                // Task was cancelled, this is expected
            }
        }
    }

    public void ResumeSpinner()
    {
        if (_spinnerPaused && !IsCompleted && _spinnerOnUpdate != null)
        {
            _spinnerPaused = false;
            _spinnerCancellation = new CancellationTokenSource();
            ScheduleNextSpinnerUpdate(DateTimeOffset.UtcNow, _spinnerCancellation.Token);
        }
    }

    protected void StartSpinner(Action? onUpdate)
    {
        if (onUpdate == null)
        {
            return;
        }

        _spinnerOnUpdate = onUpdate;
        _spinnerCancellation = new CancellationTokenSource();
        ScheduleNextSpinnerUpdate(DateTimeOffset.UtcNow, _spinnerCancellation.Token);
    }

    private void ScheduleNextSpinnerUpdate(DateTimeOffset lastUpdateTime, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        var targetNextUpdateTime = lastUpdateTime.AddMilliseconds(100);
        var now = DateTimeOffset.UtcNow;
        var delayMs = Math.Max(0, (int)(targetNextUpdateTime - now).TotalMilliseconds);

        _spinnerTask = Task.Delay(delayMs, token).ContinueWith(t =>
        {
            if (!t.IsCanceled && !token.IsCancellationRequested)
            {
                var updateTime = DateTimeOffset.UtcNow;
                _spinnerOnUpdate?.Invoke();
                ScheduleNextSpinnerUpdate(updateTime, token);
            }
        }, token, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    protected void StopSpinner()
    {
        _spinnerCancellation?.Cancel();
        try
        {
            _spinnerTask?.Wait(200);
        }
        catch (AggregateException)
        {
            // Task was cancelled, this is expected
        }
        _spinnerCancellation?.Dispose();
        _spinnerCancellation = null;
        _spinnerTask = null;
        _spinnerOnUpdate = null;
    }

    public void Dispose()
    {
        foreach (var subTask in SubTasks)
        {
            subTask.Dispose();
        }

        StopSpinner();
    }
}

internal class GroupableTask<T>(string inProgressMessage, GroupableTask? parent, Func<TaskContext, Task<T>>? taskFunc, AnsiConsoleContext ansiConsoleContext, ILogger logger) : GroupableTask(inProgressMessage, parent)
{
    public T? CompletedMessage { get; protected set; }
    private readonly Func<TaskContext, Task<T>>? _taskFunc = taskFunc;
    protected readonly AnsiConsoleContext AnsiConsoleContext = ansiConsoleContext;

    public virtual async Task<T?> ExecuteAsync(Action? onUpdate = null, bool startSpinner = true)
    {
        onUpdate?.Invoke();

        if (startSpinner)
        {
            StartSpinner(onUpdate);
        }

        try
        {
            if (_taskFunc != null)
            {
                var context = new TaskContext(this, onUpdate, AnsiConsoleContext, logger);
                CompletedMessage = await _taskFunc(context);
                IsCompleted = true;
            }
        }
        finally
        {
            if (startSpinner)
            {
                StopSpinner();
            }
            onUpdate?.Invoke();
        }

        return CompletedMessage;
    }

    public IRenderable Render()
    {
        var sb = new StringBuilder();
        var consoleWidth = AnsiConsoleContext.AnsiConsole.Profile.Width;

        int maxDepth = logger.IsEnabled(LogLevel.Debug) ? int.MaxValue : 1;
        RenderTask(sb, 0, string.Empty, maxDepth, this, consoleWidth);

        return new Markup(sb.ToString());
    }

    private static void RenderSubTasks(GroupableTask task, StringBuilder sb, int indentLevel, int maxForcedDepth, int consoleWidth)
    {
        if (task.SubTasks.Count == 0)
        {
            return;
        }

        var indentStr = new string(' ', indentLevel * 2);

        foreach (var subTask in task.SubTasks)
        {
            RenderTask(sb, indentLevel, indentStr, maxForcedDepth, subTask, consoleWidth);
        }
    }

    private static void RenderTask(StringBuilder sb, int indentLevel, string indentStr, int maxForcedDepth, GroupableTask task, int consoleWidth)
    {
        if (task.IsCompleted)
        {
            static string FormatCheckMarkMessage(string indentStr, string? message, int consoleWidth)
            {
                return $"{indentStr}[green]{Emoji.Known.CheckMarkButton}[/] {message}".PadRight(consoleWidth - 1);
            }
            sb.AppendLine(task switch
            {
                StatusMessageTask statusMessageTask => $"{indentStr} {Markup.Escape(statusMessageTask.CompletedMessage ?? string.Empty)}".PadRight(consoleWidth - 1),
                GroupableTask<T> genericTask => FormatCheckMarkMessage(indentStr, Markup.Escape((genericTask.CompletedMessage as ITuple) switch
                {
                    ITuple tuple when tuple.Length > 0 && tuple[0] is string str => str,
                    ITuple tuple when tuple.Length > 0 && tuple[1] is string str2 => str2,
                    _ => genericTask.CompletedMessage?.ToString() ?? string.Empty
                }), consoleWidth),
                GroupableTask _ => FormatCheckMarkMessage(indentStr, Markup.Escape(task.InProgressMessage), consoleWidth),
            });
        }
        else
        {
            string spinner;
            if (task.SpinnerPaused)
            {
                spinner = "❯";
            }
            else
            {
                var spinnerChars = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
                var spinnerIndex = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 100) % spinnerChars.Length;
                spinner = spinnerChars[spinnerIndex];
            }

            var msg = task.InProgressMessage;
            if (!string.IsNullOrEmpty(task.SubStatus))
            {
                msg = $"{msg} ({task.SubStatus})";
            }

            sb.AppendLine($"{indentStr}[yellow]{spinner}[/]  {Markup.Escape(msg)}".PadRight(consoleWidth - 1));
        }

        bool shouldRenderChildren = indentLevel + 1 <= maxForcedDepth || !task.IsCompleted;
        if (shouldRenderChildren)
        {
            RenderSubTasks(task, sb, indentLevel + 1, maxForcedDepth, consoleWidth);
        }
    }
}
