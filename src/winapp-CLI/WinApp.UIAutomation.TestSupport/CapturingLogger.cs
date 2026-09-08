// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

/// <summary>
/// Minimal <see cref="ILogger{T}"/> that records every entry (level + rendered
/// message) so service tests can assert the user-visible logging contract instead
/// of just "did not throw". <see cref="MinLevel"/> gates <see cref="IsEnabled"/>,
/// which several services branch on (e.g. Error/Debug/Information gating).
/// </summary>
public sealed class CapturingLogger<T> : ILogger<T>
{
    public List<(LogLevel Level, string Message)> Entries { get; } = [];
    public LogLevel MinLevel { get; set; } = LogLevel.Debug;

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

    public bool Has(LogLevel level, string substring)
        => Entries.Any(e => e.Level == level && e.Message.Contains(substring, StringComparison.OrdinalIgnoreCase));
}
