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
    protected readonly LiveUpdateSignal Signal;
    private readonly ILogger _logger;

    public GroupableTask(string inProgressMessage, GroupableTask? parent, Func<TaskContext, CancellationToken, Task<T>>? taskFunc, IAnsiConsole ansiConsole, ILogger logger, LiveUpdateSignal signal)
        : base(inProgressMessage, parent)
    {
        _taskFunc = taskFunc;
        AnsiConsole = ansiConsole;
        _logger = logger;
        Signal = signal;
    }

    public virtual async Task<T?> ExecuteAsync(Action? onUpdate, CancellationToken cancellationToken, bool startSpinner = true)
    {
        onUpdate?.Invoke();

        try
        {
            if (_taskFunc != null)
            {
                var context = new TaskContext(this, onUpdate, AnsiConsole, _logger, Signal);
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

    public (IRenderable, int) Render()
    {
        var sb = new StringBuilder();

        int maxDepth = _logger.IsEnabled(LogLevel.Debug) ? int.MaxValue : 1;
        var lineCount = RenderTask(this, sb, 0, string.Empty, maxDepth);

        var panel = new Panel(new Markup(sb.ToString().TrimEnd([.. Environment.NewLine])))
        {
            Border = BoxBorder.None,
            Padding = new Padding(0, 0),
            Expand = true
        };

        return (panel, lineCount);
    }

    private static int RenderSubTasks(GroupableTask task, StringBuilder sb, int indentLevel, int maxForcedDepth)
    {
        if (task.SubTasks.Count == 0)
        {
            return 0;
        }

        var indentStr = new string(' ', indentLevel * 2);
        int lineCount = 0;

        foreach (var subTask in task.SubTasks)
        {
            lineCount += RenderTask(subTask, sb, indentLevel, indentStr, maxForcedDepth);
        }

        return lineCount;
    }

    private static int RenderTask(GroupableTask task, StringBuilder sb, int indentLevel, string indentStr, int maxForcedDepth)
    {
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
            sb.AppendLine(task switch
            {
                StatusMessageTask statusMessageTask => $"{indentStr} {Markup.Escape(statusMessageTask.CompletedMessage ?? string.Empty)}",
                GroupableTask<T> genericTask => FormatCheckMarkMessage(indentStr, (genericTask.CompletedMessage as ITuple) switch
                {
                    ITuple tuple when tuple.Length > 0 && tuple[0] is string str => str,
                    ITuple tuple when tuple.Length > 0 && tuple[1] is string str2 => str2,
                    _ => genericTask.CompletedMessage?.ToString() ?? string.Empty
                }),
                GroupableTask _ => FormatCheckMarkMessage(indentStr, Markup.Escape(task.InProgressMessage)),
            });
        }
        else
        {
            var spinnerChars = new[] { "⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏" };
            var spinnerIndex = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 100) % spinnerChars.Length;
            var spinner = spinnerChars[spinnerIndex];

            var msg = task.InProgressMessage;
            if (!string.IsNullOrEmpty(task.SubStatus))
            {
                msg = $"{msg} ({task.SubStatus})";
            }

            sb.AppendLine($"{indentStr}[yellow]{spinner}[/]  {Markup.Escape(msg)}");
        }

        int lineCount = 1;

        bool shouldRenderChildren = indentLevel + 1 <= maxForcedDepth || !task.IsCompleted;
        if (shouldRenderChildren)
        {
            lineCount += RenderSubTasks(task, sb, indentLevel + 1, maxForcedDepth);
        }

        return lineCount;
    }
}
