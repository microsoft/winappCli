// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Describes an observed boundary of one UI automation action.</summary>
/// <param name="Boundary">The action-start or action-end boundary.</param>
/// <param name="ActionKind">The UI automation action being performed.</param>
/// <param name="Status">The completion status for an action-end boundary.</param>
public readonly record struct UiActionBoundary(
    string Boundary,
    string ActionKind,
    string? Status = null);

/// <summary>Tracks completion of one reported UI automation action.</summary>
public interface IUiActionScope : IDisposable
{
    /// <summary>Marks the action as successfully completed.</summary>
    void Complete();
}

/// <summary>Reports start and end boundaries for UI automation actions.</summary>
public interface IUiActionBoundaryReporter
{
    /// <summary>Uses the observer for actions performed within the returned scope.</summary>
    /// <param name="observer">The boundary observer.</param>
    /// <returns>A scope that restores the previous observer when disposed.</returns>
    IDisposable Push(Action<UiActionBoundary> observer);

    /// <summary>Begins reporting one action.</summary>
    /// <param name="actionKind">The UI automation action being performed.</param>
    /// <returns>A scope that reports the matching end boundary when disposed.</returns>
    IUiActionScope Begin(string actionKind);
}

internal sealed class UiActionBoundaryReportingException(Exception innerException)
    : Exception("The UI action evidence observer failed.", innerException);

internal sealed class UiActionBoundaryReporter : IUiActionBoundaryReporter
{
    private readonly AsyncLocal<Action<UiActionBoundary>?> _observer = new();

    public IDisposable Push(Action<UiActionBoundary> observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var previous = _observer.Value;
        _observer.Value = observer;
        return new ObserverScope(this, previous);
    }

    public IUiActionScope Begin(string actionKind)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actionKind);
        var observer = _observer.Value;
        Report(observer, new("action-start", actionKind));
        return new ActionScope(observer, actionKind);
    }

    private static void Report(
        Action<UiActionBoundary>? observer,
        UiActionBoundary boundary)
    {
        try
        {
            observer?.Invoke(boundary);
        }
        catch (Exception ex) when (ex is not UiActionBoundaryReportingException)
        {
            throw new UiActionBoundaryReportingException(ex);
        }
    }

    private sealed class ObserverScope(
        UiActionBoundaryReporter owner,
        Action<UiActionBoundary>? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            owner._observer.Value = previous;
            _disposed = true;
        }
    }

    private sealed class ActionScope(
        Action<UiActionBoundary>? observer,
        string actionKind) : IUiActionScope
    {
        private bool _completed;
        private bool _disposed;

        public void Complete() => _completed = true;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            Report(observer, new(
                "action-end",
                actionKind,
                _completed ? "completed" : "failed"));
            _disposed = true;
        }
    }
}
