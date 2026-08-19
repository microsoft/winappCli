// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace WinApp.Cli.Helpers;

/// <summary>
/// A <see cref="TextWriter"/> wrapper that honors <c>--quiet</c> for the migrate commands, which
/// write their progress transcript directly to <see cref="Console.Out"/> (winapp ships as
/// NativeAOT and these commands predate the injected logger). When quiet is requested, only
/// problem lines — those starting with <c>[ERROR]</c> or <c>[FAIL]</c> — are forwarded to the
/// underlying writer; all progress / <c>[PASS]</c> / <c>[WARN]</c> / blank lines are suppressed.
/// The command's exit code still conveys overall success.
/// </summary>
internal sealed class QuietFilteringTextWriter : TextWriter
{
    // This wrapper borrows the underlying writer (the real Console.Out); the caller restores it,
    // so we must NOT close/dispose it here.
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "The underlying writer is owned by the caller (Console.Out) and is restored, not owned by this wrapper.")]
    private readonly TextWriter _inner;
    private readonly StringBuilder _line = new();

    public QuietFilteringTextWriter(TextWriter inner) => _inner = inner;

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value)
    {
        if (value == '\n')
        {
            FlushLine();
        }
        else if (value != '\r')
        {
            _line.Append(value);
        }
    }

    public override void Write(string? value)
    {
        if (value is null)
        {
            return;
        }
        foreach (var ch in value)
        {
            Write(ch);
        }
    }

    private void FlushLine()
    {
        var text = _line.ToString();
        _line.Clear();
        var trimmed = text.TrimStart();
        if (trimmed.StartsWith("[ERROR]", StringComparison.Ordinal)
            || trimmed.StartsWith("[FAIL]", StringComparison.Ordinal))
        {
            _inner.WriteLine(text);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && _line.Length > 0)
        {
            FlushLine();
        }
        base.Dispose(disposing);
    }
}
