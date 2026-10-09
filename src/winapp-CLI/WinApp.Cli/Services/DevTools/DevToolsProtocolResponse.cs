// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// A structured DevTools failure: the JSON-RPC <c>error.code</c>, the tap's stable <c>error.data.token</c>
/// (<c>not-found</c>, <c>refused-unsafe</c>, …; see <c>WINAPP_DEVTOOLS_PROTOCOL_ERROR</c> in <c>DevToolsProtocolSchema.inc</c>), and the
/// human message. Kept whole so a command can branch on the token and still print what the app said.
/// </summary>
internal sealed record DevToolsProtocolError(int Code, string Token, string Message)
{
    /// <summary>The transport failure used when the agent never answered (no reply line at all).</summary>
    public static DevToolsProtocolError NoResponse(string? detail = null) =>
        new(-32000, "no-response", detail ?? "The DevTools agent did not answer.");
}

/// <summary>
/// One DevTools request outcome: either the raw inner <c>result</c> JSON or a structured <see cref="DevToolsProtocolError"/>.
/// This is the shared request path the agent-facing <c>winapp devtools</c> commands use, so every one of them
/// reports the same error code/token/message instead of re-deriving meaning from an <c>"ERR &lt;token&gt;"</c>
/// string. <see cref="ResultJson"/> is exactly what the tap put in <c>result</c> (<c>"null"</c> for the
/// resultless acks), never a re-serialization.
/// </summary>
internal sealed record DevToolsProtocolResponse(string? ResultJson, DevToolsProtocolError? Error)
{
    public bool Ok => Error is null;

    public static DevToolsProtocolResponse Success(string resultJson) => new(resultJson, null);

    public static DevToolsProtocolResponse Failure(DevToolsProtocolError error) => new(null, error);

    public string RequireResult() => Ok && ResultJson is not null
        ? ResultJson
        : throw new DevToolsProtocolException(Error ?? new(-32603, "internal", "The DevTools response has no result."));

    /// <summary>
    /// Parses <see cref="ResultJson"/> into a <see cref="JsonDocument"/> the caller owns and must dispose.
    /// Returns <c>null</c> when this response is an error or the payload is not parseable JSON.
    /// </summary>
    public JsonDocument? TryParseResult()
    {
        if (ResultJson is null)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(ResultJson, TapWireJson.DocumentOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

internal sealed class DevToolsProtocolException(DevToolsProtocolError error) : IOException(error.Message)
{
    public DevToolsProtocolError Error { get; } = error;
}
