// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Buffers.Binary;
using System.Globalization;

namespace WinApp.Cli.Services.Performance;

/// <summary>GC schemas from dotnet/runtime v10.0.0, src/coreclr/vm/ClrEtwAll.man.</summary>
internal static class ClrGcEventDecoder
{
    public static PerfEvent? Decode(PerfRawEvent raw, string id, long origin, long frequency)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(frequency);
        if (raw.Provider != PerfProviders.Clr)
        {
            return null;
        }
        var (name, opcode) = raw.EventId switch
        {
            1 => ("GCStart", 1),
            2 => ("GCEnd", 2),
            3 => ("GCRestartEEEnd", 132),
            7 => ("GCRestartEEBegin", 136),
            8 => ("GCSuspendEEEnd", 137),
            9 => ("GCSuspendEEBegin", 10),
            _ => ("", 0),
        };
        if (name.Length == 0)
        {
            return null;
        }
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        string? error = null;
        var length = (raw.EventId, raw.Version) switch
        {
            (1, 1) => 18, (1, 2) => 26, (2 or 9, 1) => 10, (3 or 7 or 8, 1) => 2,
            _ => -1,
        };
        if (raw.TraceLogging || raw.Opcode != opcode || raw.Payload.Length != length)
        {
            error = "Unsupported CLR GC descriptor/version/opcode/payload; no interval was inferred.";
        }
        else
        {
            var data = raw.Payload.AsSpan();
            var instanceOffset = 0;
            if (raw.EventId is 1 or 2)
            {
                Add(fields, "Count", BinaryPrimitives.ReadUInt32LittleEndian(data));
                Add(fields, "Depth", BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
                instanceOffset = 8;
            }
            if (raw.EventId == 1)
            {
                Add(fields, "Reason", BinaryPrimitives.ReadUInt32LittleEndian(data[8..]));
                Add(fields, "Type", BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
                instanceOffset = 16;
                if (raw.Version == 2)
                {
                    fields.Add("ClientSequenceNumber", BinaryPrimitives.ReadUInt64LittleEndian(data[18..])
                        .ToString(CultureInfo.InvariantCulture));
                }
            }
            if (raw.EventId == 9)
            {
                Add(fields, "Reason", BinaryPrimitives.ReadUInt32LittleEndian(data));
                Add(fields, "Count", BinaryPrimitives.ReadUInt32LittleEndian(data[4..]));
                instanceOffset = 8;
            }
            Add(fields, "ClrInstanceID", BinaryPrimitives.ReadUInt16LittleEndian(data[instanceOffset..]));
        }
        return new(id, raw.Qpc, (raw.Qpc - origin) * 1000.0 / frequency, raw.ThreadId,
            raw.Provider, raw.EventId, raw.Version, raw.Opcode, raw.ActivityId, name, "gc", "info",
            null, fields, error);
    }

    private static void Add(Dictionary<string, string> fields, string name, uint value) =>
        fields.Add(name, value.ToString(CultureInfo.InvariantCulture));
}
