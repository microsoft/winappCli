// Copyright (c) Microsoft Corporation. All rights reserved.
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
    public bool IsCompleted { get; protected set; }
    public GroupableTask? Parent { get; } = parent;
    public string InProgressMessage { get; set; } = inProgressMessage;
    public bool EscapeInProgressMessage { get; set; } = true;
    public string? SubStatus { get; set; }

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

    public virtual async Task<T?> ExecuteAsync(Action? onUpdate, CancellationToken cancellationToken, bool startSpinner = true)
    {
        onUpdate?.Invoke();

        try
        {
            if (_taskFunc != null)
            {
                var context = new TaskContext(this, onUpdate, AnsiConsole, _logger, RenderLock);
                CompletedMessage = await _taskFunc(context, cancellationToken);
                IsCompleted = true;
            }
        }
        catch(Exception ex)
        {
            Debug.WriteLine(ex);

            return default(T?);
        }
        finally
        {
            onUpdate?.Invoke();
        }

        return CompletedMessage;
    }

    public IRenderable Render()
    {
        var sb = new StringBuilder();

        int maxDepth = _logger.IsEnabled(LogLevel.Debug) ? int.MaxValue : 1;
        RenderTask(this, sb, 0, string.Empty, maxDepth);
        var allTasksString = sb.ToString().TrimEnd([.. Environment.NewLine]);
        if (!allTasksString.Contains(Environment.NewLine))
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
        string msg;

        if (task.IsCompleted)
        {
            static string FormatCheckMarkMessage(string indentStr, string message)
            {
                bool firstCharIsEmojiOrOpenBracket = false;
                if (message.Length > 0)
                {
                    var firstChar = message[0];
                    firstCharIsEmojiOrOpenBracket = char.IsSurrogate(firstChar)
                                                 || char.GetUnicodeCategory(firstChar) == System.Globalization.UnicodeCategory.OtherSymbol
                                                 || firstChar == '[';
                }
                return firstCharIsEmojiOrOpenBracket ? $"{indentStr} {message}" : $"{indentStr}[green]{Emoji.Known.CheckMarkButton}[/] {message}";
            }

            msg = task switch
            {
                StatusMessageTask statusMessageTask => $"{indentStr} {Markup.Escape(statusMessageTask.CompletedMessage ?? string.Empty)}",
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
        msg = msg.Replace("\r\n", "\n").Replace("\r", "\n").Replace("\n", Environment.NewLine).TrimEnd([.. Environment.NewLine]);

        sb.AppendLine(msg);

        bool shouldRenderChildren = indentLevel + 1 <= maxForcedDepth || !task.IsCompleted;
        if (shouldRenderChildren)
        {
            RenderSubTasks(task, sb, indentLevel + 1, maxForcedDepth);
        }
    }
}
