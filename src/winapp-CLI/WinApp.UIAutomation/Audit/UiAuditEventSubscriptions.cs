// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed class UiAuditEventSubscriptions(UiAuditEventBuffer events) : IDisposable
{
    private readonly int _ownerThread = Environment.CurrentManagedThreadId;
    private readonly List<(string Kind, Action Remove)> _registered = [];
    private bool _disposed;

    internal bool Add(string kind, Action subscribe, Action unsubscribe)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            subscribe();
            _registered.Add((kind, unsubscribe));
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UiAuditObservationException)
        {
            throw;
        }
        catch
        {
            events.Issue($"{kind.Replace('-', '_')}_events_unavailable",
                $"The native {kind} event subscription is unavailable. No polling substitute was used.");
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        if (Environment.CurrentManagedThreadId != _ownerThread)
        {
            throw new InvalidOperationException("Native subscriptions must be removed on their creating context.");
        }
        _disposed = true;
        for (var i = _registered.Count - 1; i >= 0; i--)
        {
            try
            {
                _registered[i].Remove();
            }
            catch
            {
                events.Issue("event_cleanup_failed",
                    "Some native event subscriptions could not be confirmed removed; callbacks were disabled.");
            }
        }
        _registered.Clear();
    }
}
