// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Emits structured JSON error responses for `winapp ui` commands when --json is set.
/// In --json mode the logger is silenced (LogLevel.None), so error paths must call
/// <see cref="Emit"/> explicitly to give consumers something to parse.
/// </summary>
internal static class UiJsonError
{
    public const string CodeMissingApp = "missing_app";
    public const string CodeMissingSelector = "missing_selector";
    public const string CodeElementNotFound = "element_not_found";
    public const string CodeStaleElement = "stale_element";
    public const string CodeInvalidArguments = "invalid_arguments";
    public const string CodeInternalError = "internal_error";
    public const string CodeZeroSize = "zero_size_element";
    public const string CodeForegroundNotTarget = "foreground_not_target";
    public const string CodeNoInteractiveDesktop = "no_interactive_desktop";
    public const string CodeTargetMoved = "target_moved";
    public const string CodeNoTarget = "no_target";
    public const string CodeInjectionUnsupported = "injection_unsupported";
    public const string CodeAmbiguousSelector = "ambiguous_selector";
    public const string CodeOutputExists = "output_exists";
    public const string CodeFrameOutputFailed = "frame_output_failed";
    public const string CodePartialOutput = "partial_output";

    /// <summary>Write a JSON error envelope to stderr. No-op when <paramref name="json"/> is false.</summary>
    /// <param name="errorOut">
    /// Optional error writer; defaults to <see cref="Console.Error"/>. Pass
    /// <c>parseResult.InvocationConfiguration.Error</c> from a command handler so test harnesses
    /// that set <c>InvocationConfiguration.Error</c> to a capturing writer can inspect the output.
    /// </param>
    /// <param name="coordination">
    /// Optional desktop-coordination detail (wait duration, queue position) attached to cancellation and
    /// other coordination errors.
    /// </param>
    public static void Emit(bool json, string code, string message,
                            string? selector = null, string? details = null,
                            TextWriter? errorOut = null,
                            string? recoveryHint = null,
                            UiPartialOutputInfo? partialOutput = null,
                            UiCoordinationInfo? coordination = null)
    {
        if (!json) { return; }

        var result = new UiErrorResult
        {
            Error = new UiErrorInfo
            {
                Code = code,
                Message = message,
                Selector = selector,
                Details = details,
                RecoveryHint = recoveryHint,
                PartialOutput = partialOutput,
                Coordination = coordination,
            },
        };
        var payload = JsonSerializer.Serialize(result, UiJsonLineContext.Default.UiErrorResult);
        // Errors go to stderr so consumers can separate them from successful stdout payloads.
        (errorOut ?? Console.Error).WriteLine(payload);
    }
}
