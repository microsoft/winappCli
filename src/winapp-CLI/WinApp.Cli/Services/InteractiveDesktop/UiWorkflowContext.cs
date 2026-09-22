// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.InteractiveDesktop;

internal interface IUiWorkflowContext
{
    string? CurrentId { get; }

    IDisposable Push(string workflowId);
}

internal sealed class UiWorkflowContext : IUiWorkflowContext
{
    private readonly AsyncLocal<string?> _current = new();

    public string? CurrentId => _current.Value;

    public IDisposable Push(string workflowId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workflowId);
        var previous = _current.Value;
        _current.Value = workflowId;
        return new Scope(this, previous);
    }

    private sealed class Scope(UiWorkflowContext owner, string? previous) : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
            owner._current.Value = previous;
        }
    }
}
