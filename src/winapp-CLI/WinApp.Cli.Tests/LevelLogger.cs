// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Tests;

/// <summary>
/// A minimal <see cref="ILogger{T}"/> whose <see cref="IsEnabled"/> honours a configurable minimum
/// level. Tests use it to drive log-level–dependent behaviour (e.g. the project-mode build verbosity
/// mapping, which reads <c>logger.IsEnabled(LogLevel.Debug/Trace/Information)</c>).
/// <c>NullLogger</c> always reports disabled, and <c>LoggerFactory.Create</c> with no providers also
/// reports disabled, so neither can exercise the verbose paths.
/// </summary>
internal sealed class LevelLogger<T>(LogLevel minLevel) : ILogger<T>, IDisposable
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => this;

    public void Dispose()
    {
    }

    public bool IsEnabled(LogLevel logLevel) => minLevel != LogLevel.None && logLevel >= minLevel;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (IsEnabled(logLevel))
        {
            Entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
