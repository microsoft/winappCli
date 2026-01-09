// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using Spectre.Console;
using System.Text;

namespace WinApp.Cli.Helpers;

public sealed class TextWriterLoggerOptions
{
}

public sealed class TextWriterLoggerProvider : ILoggerProvider, ISupportExternalScope
{
    private readonly TextWriter _stdout;
    private readonly TextWriter _stderr;
    private readonly TextWriterLoggerOptions _options;
    private IExternalScopeProvider? _scopes;

    public TextWriterLoggerProvider(TextWriter stdout, TextWriter stderr, TextWriterLoggerOptions? options = null)
    {
        _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
        _stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
        _options = options ?? new TextWriterLoggerOptions();
    }

    public ILogger CreateLogger(string categoryName) =>
        new TextWriterLogger(_stdout, _stderr, _options, _scopes);

    public void Dispose() { /* writers are owned by caller */ }

    public void SetScopeProvider(IExternalScopeProvider scopeProvider) => _scopes = scopeProvider;

    private sealed class TextWriterLogger : ILogger
    {
        private readonly TextWriter _stdout;
        private readonly TextWriter _stderr;
        private readonly TextWriterLoggerOptions _options;
        private readonly IExternalScopeProvider? _scopes;

        public TextWriterLogger(TextWriter stdout, TextWriter stderr,
                                TextWriterLoggerOptions options, IExternalScopeProvider? scopes)
        {
            _stdout = stdout;
            _stderr = stderr;
            _options = options;
            _scopes = scopes;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _scopes?.Push(state) ?? NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
                                Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var writer = logLevel >= LogLevel.Error ? _stderr : _stdout;

            var sb = new StringBuilder(256);

            // Message
            var message = formatter(state, exception);

            bool firstCharIsEmojiOrOpenBracket = false;
            if (message.Length > 0)
            {
                var firstChar = message[0];
                firstCharIsEmojiOrOpenBracket = char.IsSurrogate(firstChar)
                                             || char.GetUnicodeCategory(firstChar) == System.Globalization.UnicodeCategory.OtherSymbol
                                             || firstChar == '[';
            }

            if (logLevel != LogLevel.Information && !firstCharIsEmojiOrOpenBracket)
            {
                if (logLevel == LogLevel.Debug)
                {
                    sb.Append(UiSymbols.Verbose).Append(' ');
                }
                else
                {
                    var lvl = logLevel.ToString().ToUpperInvariant();

                    sb.Append('[').Append(lvl).Append("] - ");
                }
            }

            sb.Append(message);

            // Scopes (if any)
            _scopes?.ForEachScope((scope, s) =>
            {
                s.Append(" | ")
                 .Append("=> ").Append(scope);
            }, sb);

            // Exception
            if (exception != null)
            {
                sb.Append(" | ")
                  .Append(exception);
            }

            // Write atomically
            if (logLevel >= LogLevel.Error)
            {
                writer.WriteLine(sb.ToString());
                writer.Flush();
            }
            else
            {
                AnsiConsole.WriteLine(sb.ToString());
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }
}

internal sealed class OutputCapture : StringWriter, IDisposable
{
    private readonly TextWriter _stdOutWriter;
    public override Encoding Encoding => Encoding.ASCII;
    
    public OutputCapture(TextWriter textWriter)
    {
        _stdOutWriter = textWriter;
    }

    public override void Write(string? value)
    {
        base.Write(value);
        _stdOutWriter.Write(value);
    }

    public override void WriteLine(string? value)
    {
        base.WriteLine(value);
        _stdOutWriter.WriteLine(value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stdOutWriter.Dispose();
        }
        base.Dispose(disposing);
    }

    public void Clear()
    {
        GetStringBuilder().Clear();
    }
}

public static class TextWriterLoggerExtensions
{
    public static ILoggingBuilder AddTextWriterLogger(
        this ILoggingBuilder builder,
        TextWriter stdout,
        TextWriter stderr)
    {
        var options = new TextWriterLoggerOptions();
        builder.AddProvider(new TextWriterLoggerProvider(stdout, stderr, options));
        return builder;
    }
}
