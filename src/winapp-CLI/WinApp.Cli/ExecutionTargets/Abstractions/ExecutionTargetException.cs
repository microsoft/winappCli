// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Abstractions;

/// <summary>
/// Failure carrying a structured <see cref="ExecutionTargetErrorInfo"/> so command handlers can
/// render the spec's envelope without re-deriving codes or recovery guidance at the call site.
/// </summary>
/// <remarks>
/// Throwing this type is how any layer reports an infrastructure failure. The invoking command
/// decides where the envelope goes, preserving its existing <c>--json</c> stdout/stderr contract.
/// </remarks>
internal sealed class ExecutionTargetException : Exception
{
    /// <summary>Creates an exception carrying <paramref name="error"/>.</summary>
    public ExecutionTargetException(ExecutionTargetErrorInfo error, Exception? innerException = null)
        : base(error.Message, innerException)
    {
        Error = error;
    }

    /// <summary>The structured failure detail.</summary>
    public ExecutionTargetErrorInfo Error { get; }

    /// <summary>Convenience factory for the common code-plus-message case.</summary>
    public static ExecutionTargetException Create(
        string code,
        string message,
        string? userAction = null,
        Dictionary<string, string>? context = null,
        ExecutionTargetNextCommand? nextCommand = null,
        string? example = null,
        List<string>? validValues = null,
        Exception? innerException = null) =>
        new(
            new ExecutionTargetErrorInfo
            {
                Code = code,
                Message = message,
                UserAction = userAction,
                Context = context,
                NextCommand = nextCommand,
                Example = example,
                ValidValues = validValues,
            },
            innerException);
}
