// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>Announces target preparation phases through the caller's output policy.</summary>
internal interface ITargetProgress
{
    void Report(string message);
}

/// <summary>Uses the CLI's logging filter while keeping progress off the child's stdout.</summary>
internal sealed class LoggerTargetProgress : ITargetProgress
{
    private readonly ILogger<LoggerTargetProgress> _logger;
    private readonly Func<TextWriter> _writer;

    public LoggerTargetProgress(ILogger<LoggerTargetProgress> logger) : this(logger, () => Console.Error) { }

    internal LoggerTargetProgress(ILogger<LoggerTargetProgress> logger, Func<TextWriter> writer)
    {
        _logger = logger;
        _writer = writer;
    }

    public void Report(string message)
    {
        if (!string.IsNullOrWhiteSpace(message) && _logger.IsEnabled(LogLevel.Information))
        {
            var writer = _writer();
            writer.WriteLine(message);
            writer.Flush();
        }
    }
}

/// <summary>For callers that render progress themselves.</summary>
internal sealed class NullTargetProgress : ITargetProgress
{
    public static NullTargetProgress Instance { get; } = new();
    public void Report(string message) { }
}
