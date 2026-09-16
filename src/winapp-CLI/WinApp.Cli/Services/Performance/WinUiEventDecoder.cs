// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.Performance;

internal sealed record PerfEvent(
    string Id,
    [property: JsonNumberHandling(JsonNumberHandling.WriteAsString | JsonNumberHandling.AllowReadingFromString)] long Qpc,
    double TimeMs, uint Thread, Guid Provider, ushort Descriptor, byte Version, byte Opcode, Guid Activity,
    string Name, string Family, string Phase, string? ObjectId, Dictionary<string, string> Fields,
    string? DecodeError = null, int OmittedFields = 0, string? ElementId = null,
    string? SourceFile = null, int TargetRecord = 0);

/// <summary>
/// Explicit legacy schemas from microsoft-ui-xaml commit 2f88d24861f19237350437f6f818ede0600bd375,
/// dxaml/xcp/plat/win/desktop/Microsoft-Windows-XAML-ETW.man. TDH only supplies self-describing schemas.
/// </summary>
internal static class WinUiEventDecoder
{
    private sealed record Schema(string Name, string Family, byte Opcode, string Fields);

    private static readonly Dictionary<ushort, Schema> Main = CreateMain();
    private static readonly Dictionary<ushort, Schema> Diagnostics = CreateDiagnostics();

    private static Dictionary<ushort, Schema> CreateMain()
    {
        var result = new Dictionary<ushort, Schema>
        {
            [5] = new("ApplyTemplate", "layout", 1, "p:ElementId s:ClassName"),
            [6] = new("ApplyTemplate", "layout", 2, ""),
            [15] = new("EventCallback", "input", 1, "s:CallbackName"),
            [16] = new("EventCallback", "input", 2, ""),
            [26] = new("Tick", "frames", 0, "u:IsHighPriority"),
            [47] = new("MeasureElement", "layout", 1, "p:ElementId f:Width f:Height"),
            [48] = new("MeasureElement", "layout", 2, "p:ElementId f:DesiredWidth f:DesiredHeight"),
            [49] = new("ArrangeElement", "layout", 1, "p:ElementId f:Left f:Top f:Width f:Height"),
            [50] = new("ArrangeElement", "layout", 2, "p:ElementId f:VisualOffsetX f:VisualOffsetY f:RenderWidth f:RenderHeight"),
            [61] = new("Created", "metadata", 0, "p:ElementId"),
            [62] = new("Destroyed", "metadata", 0, "p:ElementId"),
            [101] = new("SubmitFrameInfo", "frames", 0, "p:FrameId"),
            [165] = new("ProcessPointerInput", "input", 1, "u:PointerId u:MsgId f:X f:Y"),
            [166] = new("ProcessPointerInput", "input", 2, ""),
            [213] = new("Name", "metadata", 0, "p:ElementId s:Name"),
            [323] = new("VirtualizedCollectionUpdated", "virtualization", 0,
                "p:ScrollViewerId f:Left f:Top f:Width f:Height f:ViewportWidth f:ViewportHeight f:ExtentWidth f:ExtentHeight b:HasPlaceholders"),
            [352] = new("VirtualizedCollectionBounds", "virtualization", 0, "p:ScrollViewerId f:Left f:Top f:Width f:Height"),
            [353] = new("VirtualizedItemAdded", "virtualization", 0, "p:ElementId p:ScrollViewerId i:ItemIndex b:IsPlaceholder"),
            [354] = new("VirtualizedItemUpdated", "virtualization", 0, "p:ElementId u:Phase"),
            [355] = new("VirtualizedItemRemoved", "virtualization", 0, "p:ElementId"),
            [377] = new("VirtualizationEnabledByModernPanel", "virtualization", 0, "b:IsEnabled"),
            [378] = new("VirtualizationEnabledByLayout", "virtualization", 0, "b:IsEnabled"),
        };
        Pair(result, 41, 42, "Layout", "layout");
        Pair(result, 43, 44, "Measure", "layout");
        Pair(result, 45, 46, "Arrange", "layout");
        Pair(result, 63, 65, "Frame", "frames");
        Pair(result, 93, 94, "RenderWalk", "frames");
        Pair(result, 100, 102, "SubmitFrame", "frames");
        Pair(result, 159, 160, "PointerWheel", "input");
        Pair(result, 199, 200, "VirtualizationMeasure", "virtualization");
        Pair(result, 201, 202, "VirtualizationCleanup", "virtualization");
        Pair(result, 203, 204, "VirtualizationAdd", "virtualization");
        Pair(result, 205, 206, "GenerateContainer", "virtualization");
        Pair(result, 207, 208, "MeasureChild", "layout");
        Pair(result, 209, 210, "PrepareContainer", "virtualization");
        Pair(result, 503, 504, "GenerateItems", "virtualization");
        return result;
    }

    private static Dictionary<ushort, Schema> CreateDiagnostics()
    {
        var result = new Dictionary<ushort, Schema>
        {
            [26] = new("PointerWheelChanged", "input", 1, "p:RegisterdOn u:PointerDeviceType p:OriginalSource"),
            [27] = new("PointerWheelChanged", "input", 2, "b:Handled"),
            [59] = new("InvalidateArrange", "layout", 0, "p:ElementId p:ParentId"),
            [60] = new("InvalidateMeasure", "layout", 0, "p:ElementId p:ParentId"),
            [61] = new("Added", "metadata", 0, "p:ElementId p:ParentId"),
            [62] = new("Removed", "metadata", 0, "p:ElementId p:ParentId"),
            [64] = new("MeasureOverride", "layout", 1, "p:ElementId"),
            [65] = new("MeasureOverride", "layout", 2, "p:ElementId"),
            [66] = new("ArrangeOverride", "layout", 1, "p:ElementId"),
            [67] = new("ArrangeOverride", "layout", 2, "p:ElementId"),
            [83] = new("Source", "metadata", 0, "p:ElementId s:FileURI u:LineNumber u:ColumnNumber s:FileHash"),
        };
        Pair(result, 1, 2, "InputEvent", "input");
        return result;
    }

    private static void Pair(Dictionary<ushort, Schema> schemas, ushort begin, ushort end, string name, string family)
    {
        schemas.Add(begin, new(name, family, 1, ""));
        schemas.Add(end, new(name, family, 2, ""));
    }

    public static PerfEvent? Decode(PerfRawEvent raw, string id, long origin, long frequency)
    {
        if (frequency <= 0)
        {
            throw new InvalidDataException("The capture QPC frequency is invalid.");
        }
        if (raw.TraceLogging)
        {
            return DecodeSelfDescribing(raw, id, origin, frequency);
        }
        var schemas = raw.Provider == PerfProviders.Xaml ? Main :
            raw.Provider == PerfProviders.Diagnostics ? Diagnostics : null;
        if (schemas is null || !schemas.TryGetValue(raw.EventId, out var schema))
        {
            return null;
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? error = null;
        try
        {
            var shape = schema.Fields;
            if (raw.Version == 1)
            {
                shape = (raw.Provider == PerfProviders.Xaml, raw.EventId) switch
                {
                    (true, 94) => "i:Visited i:Rendered",
                    (true, 377 or 378) => "b:IsEnabled p:ElementId s:Name s:ClassType s:PropertyType",
                    (false, 83) => "p:ElementId s:ClassName s:FileURI u:LineNumber u:ColumnNumber s:FileHash",
                    _ => throw new InvalidDataException("Unsupported manifest descriptor version."),
                };
            }
            else if (raw.Version != 0)
            {
                throw new InvalidDataException("Unsupported manifest descriptor version.");
            }
            if (raw.Opcode != schema.Opcode)
            {
                throw new InvalidDataException("Unexpected manifest opcode.");
            }
            var payload = new Payload(raw.Payload);
            foreach (var field in shape.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                fields.Add(field[2..], payload.Read(field[0]));
            }
            payload.End();
        }
        catch (Exception ex) when (ex is InvalidDataException or DecoderFallbackException)
        {
            fields.Clear();
            error = ex.Message;
        }
        return Create(raw, id, origin, frequency, schema.Name, schema.Family,
            error is null ? Phase(raw.Opcode) : "unknown", fields.GetValueOrDefault("ElementId"), fields, error);
    }

    private static PerfEvent? DecodeSelfDescribing(PerfRawEvent raw, string id, long origin, long frequency)
    {
        var name = raw.EventName ?? $"TraceLogging:{raw.EventId}";
        var family = raw.Provider == PerfProviders.Controls ? "virtualization" :
            name.StartsWith("FlowLayout", StringComparison.Ordinal) || name.StartsWith("Items", StringComparison.Ordinal) ? "virtualization" :
            name.StartsWith("Scroll", StringComparison.Ordinal) ? "scrolling" :
            name.StartsWith("Pointer", StringComparison.Ordinal) ? "input" :
            name.StartsWith("Scheduling_", StringComparison.Ordinal) || name.StartsWith("Dispatch_", StringComparison.Ordinal) ||
                name is "CoreServices_Frame" or "FirstUiThreadFrameEnd" || name.StartsWith("RenderWalk_", StringComparison.Ordinal) ? "frames" :
            name == "FrameworkElement_ApplyTemplate" ? "layout" :
            name is "PublicApiCall" or "PerfXamlEvent" ? "framework" :
            raw.DecodeError is not null ? "unknown" : null;
        if (family is null)
        {
            return null;
        }
        var fields = raw.Fields is null ? new Dictionary<string, string>() : new(raw.Fields, StringComparer.Ordinal);
        var phase = "info";
        var error = raw.DecodeError;
        string? objectId = fields.GetValueOrDefault("ObjectPointer");
        // These contracts describe synchronous invocation scopes, not completion of returned async operations.
        if (name is "PublicApiCall" or "PerfXamlEvent")
        {
            var flag = fields.GetValueOrDefault("IsStart");
            if (string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase) || flag == "1")
            {
                phase = "begin";
            }
            else if (string.Equals(flag, "false", StringComparison.OrdinalIgnoreCase) || flag == "0")
            {
                phase = "end";
            }
            else
            {
                error ??= "The synchronous scope IsStart field is invalid, missing or ambiguous.";
            }
            if (fields.TryGetValue(name == "PublicApiCall" ? "MethodName" : "EventName", out var operation))
            {
                name += ":" + operation;
                if (operation == "WXM::InitializeForCurrentThread")
                {
                    family = "initialization";
                }
            }
            else
            {
                error ??= "The synchronous scope operation name is missing or ambiguous.";
            }
        }
        if (objectId is not null)
        {
            var hex = objectId.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (ulong.TryParse(hex ? objectId[2..] : objectId, hex ? NumberStyles.HexNumber : NumberStyles.Integer,
                CultureInfo.InvariantCulture, out var pointer))
            {
                objectId = pointer.ToString("x16", CultureInfo.InvariantCulture);
            }
            else
            {
                objectId = null;
            }
        }
        return Create(raw, id, origin, frequency, name, family, error is null ? phase : "unknown", objectId, fields, error);
    }

    private static PerfEvent Create(PerfRawEvent raw, string id, long origin, long frequency,
        string name, string family, string phase, string? objectId, Dictionary<string, string> fields, string? error)
    {
        var omitted = 0;
        if (name.Length > 512)
        {
            name = name[..512];
            error = "The event name exceeded the supported length; no duration was inferred.";
            phase = "unknown";
        }
        var retained = new Dictionary<string, string>(StringComparer.Ordinal);
        var budget = 8192;
        foreach (var field in fields)
        {
            var cost = Encoding.UTF8.GetByteCount(field.Key) + Encoding.UTF8.GetByteCount(field.Value);
            if (cost > budget || retained.Count >= 64)
            {
                omitted++;
                continue;
            }
            retained.Add(field.Key, field.Value);
            budget -= cost;
        }
        return new(id, raw.Qpc, (raw.Qpc - origin) * 1000.0 / frequency, raw.ThreadId, raw.Provider, raw.EventId,
            raw.Version, raw.Opcode, raw.ActivityId, name, family, phase, objectId, retained, error, omitted);
    }

    private static string Phase(byte opcode) => opcode switch { 1 => "begin", 2 => "end", _ => "info" };

    private sealed class Payload(byte[] bytes)
    {
        private int position;
        private ReadOnlySpan<byte> Take(int count)
        {
            if (count > bytes.Length - position)
            {
                throw new InvalidDataException("Short manifest payload.");
            }
            var part = bytes.AsSpan(position, count);
            position += count;
            return part;
        }

        public string Read(char type) => type switch
        {
            'p' => BinaryPrimitives.ReadUInt64LittleEndian(Take(8)).ToString("x16", CultureInfo.InvariantCulture),
            'u' => BinaryPrimitives.ReadUInt32LittleEndian(Take(4)).ToString(CultureInfo.InvariantCulture),
            'i' => BinaryPrimitives.ReadInt32LittleEndian(Take(4)).ToString(CultureInfo.InvariantCulture),
            'b' => ReadBoolean(),
            'f' => ReadFloat(),
            's' => ReadString(),
            _ => throw new InvalidDataException("Unsupported manifest field."),
        };

        private string ReadBoolean() => BinaryPrimitives.ReadUInt32LittleEndian(Take(4)) switch
        {
            0 => "false",
            1 => "true",
            _ => throw new InvalidDataException("Invalid manifest Boolean."),
        };

        private string ReadFloat()
        {
            var value = BinaryPrimitives.ReadSingleLittleEndian(Take(4));
            if (float.IsNaN(value))
            {
                throw new InvalidDataException("Invalid layout size.");
            }
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private string ReadString()
        {
            var start = position;
            while (BinaryPrimitives.ReadUInt16LittleEndian(Take(2)) != 0)
            {
            }
            return new UnicodeEncoding(false, false, true).GetString(bytes, start, position - start - 2);
        }

        public void End()
        {
            if (position != bytes.Length)
            {
                throw new InvalidDataException("Unexpected manifest payload length.");
            }
        }
    }
}
