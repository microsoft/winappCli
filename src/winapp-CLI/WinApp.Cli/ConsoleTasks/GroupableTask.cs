// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Rendering;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace WinApp.Cli.ConsoleTasks;

internal class GroupableTask(string inProgressMessage, GroupableTask? parent) : IDisposable
{
    public BlockingCollection<GroupableTask> SubTasks { get; set; } = [];
    public bool? SuccessfullyCompleted { get; set; }
    public GroupableTask? Parent { get; } = parent;
    public string InProgressMessage { get; set; } = inProgressMessage;
    public bool EscapeInProgressMessage { get; set; } = true;
    public string? SubStatus { get; set; }

    /// <summary>
    /// Returns the completed task's display message — typically the human-readable string
    /// inside a <c>(int ReturnCode, string Message)</c> tuple result. Used by the plain
    /// progress renderer, which doesn't have access to the task's generic <c>T</c> and so
    /// cannot pattern-match <see cref="System.Runtime.CompilerServices.ITuple"/> directly.
    /// Returns <c>null</c> when no completion message is available; callers should fall back
    /// to <see cref="InProgressMessage"/>.
    /// </summary>
    public virtual string? CompletedDisplayMessage => null;

    /// <summary>
    /// True when the task completed with a non-zero <c>ReturnCode</c> as the first element
    /// of an <see cref="System.Runtime.CompilerServices.ITuple"/> result. The live renderer
    /// suppresses the line for these because <c>StatusService</c> logs the error to stderr
    /// separately; the plain renderer does the same to avoid duplicate error output.
    /// </summary>
    public virtual bool IsFailedTupleResult => false;

    public void Dispose()
    {
        foreach (var subTask in SubTasks)
        {
            subTask.Dispose();
        }
    }
}

internal class GroupableTask<T> : GroupableTask
{
    public T? CompletedMessage { get; protected set; }
    private readonly Func<TaskContext, CancellationToken, Task<T>>? _taskFunc;
    protected readonly IAnsiConsole AnsiConsole;
    protected readonly Lock RenderLock;
    private readonly ILogger _logger;

    public GroupableTask(string inProgressMessage, GroupableTask? parent, Func<TaskContext, CancellationToken, Task<T>>? taskFunc, IAnsiConsole ansiConsole, ILogger logger, Lock renderLock)
        : base(inProgressMessage, parent)
    {
        _taskFunc = taskFunc;
        AnsiConsole = ansiConsole;
        _logger = logger;
        RenderLock = renderLock;
    }

    public override string? CompletedDisplayMessage
    {
        get
        {
            if (CompletedMessage is null)
            {
                return null;
            }

            if (CompletedMessage is string s)
            {
                return s;
            }

            // (int ReturnCode, string Message) and similar shapes — match the live
            // renderer's behaviour in RenderTask: prefer the first string element found.
            if (CompletedMessage is ITuple tuple)
            {
                for (var i = 0; i < tuple.Length; i++)
                {
                    if (tuple[i] is string ts)
                    {
                        return ts;
                    }
                }
            }

            return CompletedMessage.ToString();
        }
    }

    public override bool IsFailedTupleResult
        => CompletedMessage is ITuple t && t.Length > 0 && t[0] is int rc && rc != 0;

    public virtual async Task<T?> ExecuteAsync(Action? onUpdate, CancellationToken cancellationToken, bool startSpinner = true)
    {
        onUpdate?.Invoke();

        try
        {
            if (_taskFunc != null)
            {
                var context = new TaskContext(this, onUpdate, AnsiConsole, _logger, RenderLock);
                CompletedMessage = await _taskFunc(context, cancellationToken);
                SuccessfullyCompleted = true;
            }
        }
        catch (Exception ex)
        {
            SuccessfullyCompleted = false;
            Debug.WriteLine(ex);

            // Handle if T is a ValueTuple with an int first element (e.g., (int ReturnCode, string Message))
            // to return a non-zero ReturnCode on error, which prevents the spinner from continuing
            // indefinitely. Surface the exception message (and stack when verbose) so callers like
            // StatusService can present a real error instead of a blank "(null)".
            T? result = default;
            if (result is ValueTuple<int, string> v)
            {
                var errorMessage = ex.Message;
                if (string.IsNullOrWhiteSpace(errorMessage))
                {
                    errorMessage = ex.GetType().FullName ?? "Unknown error";
                }
                if (_logger.IsEnabled(LogLevel.Debug) && !string.IsNullOrEmpty(ex.StackTrace))
                {
                    errorMessage += Environment.NewLine + ex.StackTrace;
                }

                v.Item1 = 1;
                v.Item2 = errorMessage;
                CompletedMessage = (T?)(object)v;
            }
            else
            {
                return result;
            }
        }
        finally
        {
            onUpdate?.Invoke();
        }

        return CompletedMessage;
    }

    public IRenderable Render(bool lastRender = false)
    {
        var sb = new StringBuilder();

        int maxDepth = _logger.IsEnabled(LogLevel.Debug) ? int.MaxValue : 1;
        RenderTask(this, sb, 0, string.Empty, maxDepth);
        var allTasksString = sb.ToString().TrimEnd([.. Environment.NewLine]);
        if (!lastRender && !allTasksString.Contains(Environment.NewLine))
        {
            allTasksString = $"{allTasksString}{Environment.NewLine}";
        }

        var panel = new Panel(allTasksString)
        {
            Border = BoxBorder.None,
            Padding = new Padding(0, 0),
            Expand = true
        };

        return panel;
    }

    private static void RenderSubTasks(GroupableTask task, StringBuilder sb, int indentLevel, int maxForcedDepth)
    {
        if (task.SubTasks.Count == 0)
        {
            return;
        }

        var indentStr = new string(' ', indentLevel * 2);

        foreach (var subTask in task.SubTasks)
        {
            RenderTask(subTask, sb, indentLevel, indentStr, maxForcedDepth);
        }
    }

    private static void RenderTask(GroupableTask task, StringBuilder sb, int indentLevel, string indentStr, int maxForcedDepth)
    {
        string? msg;

        if (task.SuccessfullyCompleted != null)
        {
            string? FormatCheckMarkMessage(string indentStr, string message)
            {
                // A step that succeeded with nothing to say prints nothing rather than a bare check mark.
                if (task.SuccessfullyCompleted == true && string.IsNullOrWhiteSpace(message))
                {
                    return null;
                }
                bool firstCharIsEmojiOrOpenBracket = false;
                if (message.Length > 0)
                {
                    var firstChar = message[0];
                    firstCharIsEmojiOrOpenBracket = char.IsSurrogate(firstChar)
                                                 || char.GetUnicodeCategory(firstChar) == System.Globalization.UnicodeCategory.OtherSymbol
                                                 || firstChar == '[';
                }
                var emoji = task.SuccessfullyCompleted == true ? $"[green]{Emoji.Known.CheckMarkButton}[/]" : $"[red]{Emoji.Known.CrossMark}[/]";
                return firstCharIsEmojiOrOpenBracket ? $"{indentStr}{message}" : $"{indentStr}{emoji} {message}";
            }

            msg = task switch
            {
                StatusMessageTask statusMessageTask => $"{indentStr}{Markup.Escape(statusMessageTask.CompletedMessage ?? string.Empty)}",
                // Error details are logged to stderr by StatusService, so skip rendering them in the task tree.
                GroupableTask<T> failedTask when failedTask.CompletedMessage is ITuple resultTuple
                    && resultTuple[0] is int and not 0 => null,
                GroupableTask<T> genericTask => FormatCheckMarkMessage(indentStr, (genericTask.CompletedMessage as ITuple) switch
                {
                    ITuple tuple when tuple.Length > 0 && tuple[0] is string str => str,
                    ITuple tuple when tuple.Length > 0 && tuple[1] is string str2 => str2,
                    _ => genericTask.CompletedMessage?.ToString() ?? string.Empty
                }),
                GroupableTask _ => FormatCheckMarkMessage(indentStr, Markup.Escape(task.InProgressMessage)),
            };
        }
        else
        {
            var spinnerChars = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
            var spinnerIndex = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 100) % spinnerChars.Length;
            var spinner = spinnerChars[spinnerIndex];

            msg = task.InProgressMessage;
            if (!string.IsNullOrEmpty(task.SubStatus))
            {
                msg = $"{msg} ({task.SubStatus})";
            }
            if (task.EscapeInProgressMessage)
            {
                msg = Markup.Escape(msg);
            }
            if (task is not PromptConfirmationTask promptConfirmationTask || promptConfirmationTask.State != PromptState.WaitingForInput)
            {
                msg = $"{indentStr}[yellow]{spinner}[/]  {msg}";
            }
        }

        // make line endings consistent
        if (msg != null)
        {
            msg = msg.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine).TrimEnd([.. Environment.NewLine]);
            sb.AppendLine(msg);
        }

        bool shouldRenderChildren = indentLevel + 1 <= maxForcedDepth || task.SuccessfullyCompleted == null;
        if (shouldRenderChildren)
        {
            RenderSubTasks(task, sb, indentLevel + 1, maxForcedDepth);
        }
    }
}
