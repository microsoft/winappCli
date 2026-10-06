// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Text.Json;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Services.DevTools;

internal sealed class VisualTreeTap(uint targetPid, uint? expectedServerPid = null, long? window = null, string? root = null)
{
    public const int DefaultTimeoutMs = 12000;
    public const int CensusReadTimeoutMs = 8000;
    internal const int MaxRequestBytes = 64 * 1024;
    internal const int MaxResponseBytes = 64 * 1024 * 1024;
    private static readonly UTF8Encoding Utf8 = new(false, true);
    private readonly uint _expectedServerPid = expectedServerPid ?? targetPid;
    public DevToolsProtocolError? LastError { get; private set; }
    public string? LastConnectionError => LastError?.Message;

    public DevToolsProtocolResponse Request(string method, Action<Utf8JsonWriter>? writeParams = null,
        int timeoutMs = DefaultTimeoutMs, CancellationToken cancellationToken = default)
    {
        DevToolsProtocolResponse response;
        try
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
            using var request = new MemoryStream();
            using (var writer = new Utf8JsonWriter(request))
            {
                writer.WriteStartObject();
                writer.WriteString("jsonrpc", "2.0");
                writer.WriteNumber("id", 1);
                writer.WriteString("method", method);
                if (writeParams is not null || window is not null || root is not null)
                {
                    writer.WriteStartObject("params");
                    writeParams?.Invoke(writer);
                    if (window is not null)
                    {
                        writer.WriteString("window", window.Value.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    }
                    if (root is not null)
                    {
                        writer.WriteString("root", root);
                    }
                    writer.WriteEndObject();
                }
                writer.WriteEndObject();
            }
            if (request.Length > MaxRequestBytes)
            {
                response = DevToolsProtocolResponse.Failure(new(-32602, "bad-args", "The DevTools request exceeds 64 KiB."));
            }
            else
            {
                request.WriteByte((byte)'\n');
                response = ExchangeAsync(request.ToArray(), timeoutMs, cancellationToken).GetAwaiter().GetResult();
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            response = DevToolsProtocolResponse.Failure(new(-32004, "unauthorized", ex.Message));
        }
        catch (JsonException ex)
        {
            response = DevToolsProtocolResponse.Failure(new(-32700, "parse-error", ex.Message));
        }
        catch (InvalidDataException ex)
        {
            response = DevToolsProtocolResponse.Failure(new(-32603, "internal", ex.Message));
        }
        catch (DecoderFallbackException ex)
        {
            response = DevToolsProtocolResponse.Failure(new(-32700, "parse-error", ex.Message));
        }
        catch (IOException ex)
        {
            response = DevToolsProtocolResponse.Failure(DevToolsProtocolError.NoResponse(ex.Message));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            response = DevToolsProtocolResponse.Failure(DevToolsProtocolError.NoResponse("The DevTools request timed out."));
        }
        LastError = response.Error;
        return response;
    }

    private async Task<DevToolsProtocolResponse> ExchangeAsync(byte[] request, int timeoutMs, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeoutMs);
        deadline.Token.ThrowIfCancellationRequested();
        using var client = DevToolsPipeClient.Connect(targetPid, _expectedServerPid, timeoutMs, asynchronous: true, deadline.Token);
        await client.WriteAsync(request, deadline.Token);
        using var frame = new MemoryStream();
        var buffer = new byte[4096];
        while (true)
        {
            var count = await client.ReadAsync(buffer, deadline.Token);
            if (count == 0)
            {
                throw new IOException("The DevTools pipe closed before a complete response arrived.");
            }
            var offset = 0;
            while (offset < count)
            {
                deadline.Token.ThrowIfCancellationRequested();
                var end = Array.IndexOf(buffer, (byte)'\n', offset, count - offset);
                var length = end < 0 ? count - offset : end - offset;
                if (frame.Length + length > MaxResponseBytes)
                {
                    throw new InvalidDataException("The DevTools response exceeds 64 MiB.");
                }
                frame.Write(buffer, offset, length);
                offset += length;
                if (end < 0)
                {
                    break;
                }
                offset++;
                using var doc = JsonDocument.Parse(Utf8.GetString(frame.GetBuffer(), 0, (int)frame.Length),
                    TapWireJson.DocumentOptions);
                frame.SetLength(0);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String ||
                    version.GetString() != "2.0")
                {
                    throw new InvalidDataException("The agent sent an invalid JSON-RPC response.");
                }
                if (!root.TryGetProperty("id", out var id))
                {
                    if (root.TryGetProperty("method", out var notification) && notification.ValueKind == JsonValueKind.String)
                    {
                        continue;
                    }
                    throw new InvalidDataException("The DevTools response has no request ID.");
                }
                if (id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var value) || value != 1)
                {
                    throw new InvalidDataException("The DevTools response has a mismatched request ID.");
                }
                var hasResult = root.TryGetProperty("result", out var result);
                var hasError = root.TryGetProperty("error", out var error);
                if (hasResult == hasError)
                {
                    throw new InvalidDataException("The DevTools response must contain either a result or an error.");
                }
                if (hasResult)
                {
                    return DevToolsProtocolResponse.Success(result.GetRawText());
                }
                if (error.ValueKind != JsonValueKind.Object ||
                    !error.TryGetProperty("code", out var code) || code.ValueKind != JsonValueKind.Number ||
                    !code.TryGetInt32(out var codeValue) ||
                    !error.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.String)
                {
                    throw new InvalidDataException("The DevTools response contains a malformed error.");
                }
                var token = error.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object &&
                    data.TryGetProperty("token", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                return DevToolsProtocolResponse.Failure(new(codeValue, token ?? "unknown", message.GetString()!));
            }
        }
    }

    public DevToolsProtocolResponse Hello(int timeoutMs = 4000, CancellationToken cancellationToken = default) =>
        Request("DevTools.negotiate", timeoutMs: timeoutMs, cancellationToken: cancellationToken);

    public IReadOnlyList<string>? GetPublicMethods(CancellationToken cancellationToken = default)
    {
        using var doc = Hello(cancellationToken: cancellationToken).TryParseResult();
        if (doc?.RootElement is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("methods", out var methods) || methods.ValueKind != JsonValueKind.Array ||
            methods.EnumerateArray().Any(m => m.ValueKind != JsonValueKind.String))
        {
            LastError ??= new(-32603, "internal", "The agent returned an invalid public-method list.");
            return null;
        }
        return methods.EnumerateArray().Select(m => m.GetString()!).ToArray();
    }

    public TapHello? WaitHello(int timeoutMs, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = (int)Math.Max(1, until - Environment.TickCount64);
            if (TapHello.TryParse(Hello(Math.Min(4000, remaining), cancellationToken).ResultJson) is { } hello)
            {
                return hello;
            }
            Task.Delay((int)Math.Clamp(until - Environment.TickCount64, 0, 200), cancellationToken).GetAwaiter().GetResult();
        }
        return null;
    }

    public bool WaitReady(int timeoutMs, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(timeoutMs);
        var until = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < until)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = (int)Math.Max(1, until - Environment.TickCount64);
            if (Request("DevTools.ping", timeoutMs: Math.Min(1000, remaining), cancellationToken: cancellationToken).Ok)
            {
                return true;
            }
            Task.Delay((int)Math.Clamp(until - Environment.TickCount64, 0, 300), cancellationToken).GetAwaiter().GetResult();
        }
        return false;
    }

    public int? TryGetNodeCount(CancellationToken cancellationToken = default)
    {
        using var doc = Request("DevTools.ping", timeoutMs: CensusReadTimeoutMs, cancellationToken: cancellationToken).TryParseResult();
        return doc?.RootElement is { ValueKind: JsonValueKind.Object } root &&
            root.TryGetProperty("count", out var count) && count.ValueKind == JsonValueKind.Number &&
            count.TryGetInt32(out var value) ? value : null;
    }

    public bool ShowOverlay(CancellationToken cancellationToken = default) => Request("Overlay.show", cancellationToken: cancellationToken).Ok;
    public bool OpenWindow(CancellationToken cancellationToken = default) => ReadWindowState(Request("Window.open", cancellationToken: cancellationToken)) == true;
    public bool CloseWindow(CancellationToken cancellationToken = default) => ReadWindowState(Request("Window.close", cancellationToken: cancellationToken)) == false;
    public bool? GetWindowState(CancellationToken cancellationToken = default) => ReadWindowState(Request("Window.getState", cancellationToken: cancellationToken));

    private static bool? ReadWindowState(DevToolsProtocolResponse response)
    {
        using var doc = response.TryParseResult();
        return doc?.RootElement is { ValueKind: JsonValueKind.Object } root &&
            root.TryGetProperty("open", out var open) && open.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? open.GetBoolean() : null;
    }

    public DevToolsProtocolResponse PollSelection(CancellationToken cancellationToken = default) => Request("Selection.poll", timeoutMs: 2000, cancellationToken: cancellationToken);
    public DevToolsProtocolResponse GetSourceRoot(CancellationToken cancellationToken = default) => Request("Internal.sourceRoot", timeoutMs: 4000, cancellationToken: cancellationToken);
    public DevToolsProtocolResponse GetElementAnchor(string handle, CancellationToken cancellationToken = default) =>
        Request("Internal.elementAnchor", w => w.WriteString("handle", handle), 4000, cancellationToken);

    public DevToolsProtocolResponse SetComments(IReadOnlyList<TapComment> comments, long generation,
        int timeoutMs = DefaultTimeoutMs, CancellationToken cancellationToken = default) => Request("Internal.setComments", w =>
    {
        w.WriteNumber("generation", generation);
        w.WriteStartArray("comments");
        foreach (var comment in comments)
        {
            w.WriteStartObject();
            w.WriteString("id", comment.Id);
            w.WriteString("text", comment.Text);
            w.WriteString("anchor", comment.Anchor ?? "");
            w.WriteString("status", comment.Status);
            w.WriteString("element", comment.Element ?? "");
            w.WriteString("file", comment.File ?? "");
            w.WriteString("uri", comment.Uri ?? "");
            w.WriteString("revision", comment.Revision ?? "");
            w.WriteNumber("line", comment.Line);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }, timeoutMs, cancellationToken);

    public DevToolsProtocolResponse RequestEnumerate(string? rootHandle, int? depth, bool authored = false, CancellationToken cancellationToken = default) =>
        Request("VisualTree.enumerate", w =>
        {
            if (!string.IsNullOrEmpty(rootHandle))
            {
                w.WriteString("rootHandle", rootHandle);
            }
            if (depth is int d)
            {
                w.WriteNumber("depth", d);
            }
            if (authored)
            {
                w.WriteBoolean("authored", true);
            }
        }, authored ? DefaultTimeoutMs : CensusReadTimeoutMs, cancellationToken);

    public DevToolsProtocolResponse RequestFind(string query, bool appAuthoredOnly, CancellationToken cancellationToken = default) => Request("VisualTree.find", w =>
    {
        w.WriteString("query", query);
        w.WriteBoolean("appAuthoredOnly", appAuthoredOnly);
    }, appAuthoredOnly ? DefaultTimeoutMs : CensusReadTimeoutMs, cancellationToken);

    public DevToolsProtocolResponse RequestAppAuthored(CancellationToken cancellationToken = default) => Request("VisualTree.getAppAuthored", cancellationToken: cancellationToken);
    public DevToolsProtocolResponse RequestProperties(string handle, CancellationToken cancellationToken = default) => Request("Property.get", w => w.WriteString("handle", handle), cancellationToken: cancellationToken);
    public DevToolsProtocolResponse RequestLayout(string handle, CancellationToken cancellationToken = default) => Request("Layout.get", w => w.WriteString("handle", handle), cancellationToken: cancellationToken);
    public DevToolsProtocolResponse RequestSource(string handle, CancellationToken cancellationToken = default) => Request("Source.get", w => w.WriteString("handle", handle), cancellationToken: cancellationToken);
    public DevToolsProtocolResponse RequestBindingDiagnosis(string handle, string property, CancellationToken cancellationToken = default) => Request("Binding.diagnose", w =>
    {
        w.WriteString("handle", handle);
        w.WriteString("prop", property);
    }, cancellationToken: cancellationToken);
    public DevToolsProtocolResponse RequestSetProperty(string handle, string property, string type, string value, CancellationToken cancellationToken = default) =>
        Request("HotReload.setProperty", w =>
        {
            w.WriteString("handle", handle);
            w.WriteString("prop", property);
            w.WriteString("type", type);
            w.WriteString("value", value);
        }, cancellationToken: cancellationToken);
    public DevToolsProtocolResponse RequestResourceList(string? pattern, string? theme, CancellationToken cancellationToken = default) =>
        Request("Resource.list", w =>
        {
            if (pattern is not null)
            {
                w.WriteString("pattern", pattern);
            }
            if (theme is not null)
            {
                w.WriteString("theme", theme);
            }
        }, cancellationToken: cancellationToken);
    public DevToolsProtocolResponse RequestResourceSet(string key, string value, string? theme, string? type, CancellationToken cancellationToken = default) =>
        Request("Resource.set", w =>
        {
            w.WriteString("key", key);
            w.WriteString("value", value);
            if (theme is not null)
            {
                w.WriteString("theme", theme);
            }
            if (type is not null)
            {
                w.WriteString("type", type);
            }
        }, cancellationToken: cancellationToken);
    public DevToolsProtocolResponse RequestResourceReset(string? key, CancellationToken cancellationToken = default) =>
        Request("Resource.reset", w =>
        {
            if (key is not null)
            {
                w.WriteString("key", key);
            }
        }, cancellationToken: cancellationToken);
}
