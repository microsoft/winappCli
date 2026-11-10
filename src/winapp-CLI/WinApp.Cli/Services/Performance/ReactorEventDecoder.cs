// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Performance;

/// <summary>
/// Maps Microsoft-UI-Reactor-Perf TraceLogging events (decoded by TDH) to analyzer events.
/// The contract is microsoft-ui-reactor docs/reference/perf-trace-events.md: spans have no
/// activity ID and pair by thread and nesting, which the analyzer's per-thread stack already does.
/// </summary>
internal static class ReactorEventDecoder
{
    public const string Family = "reactor";
    public const string RenderPass = "ReactorRenderPass";
    /// <summary>A component render begins as <c>ReactorRender:&lt;name&gt;</c>; its Stop carries only the ID.</summary>
    public const string Render = "ReactorRender";
    public const string Reconcile = "ReactorReconcile";

    private sealed record Schema(string EventName, string Name, byte Opcode, string[] Fields);

    private static readonly Dictionary<ushort, Schema> Schemas = new()
    {
        [1] = new("RenderPass", RenderPass, 1, ["hostId"]),
        [2] = new("RenderPass", RenderPass, 2, ["hostId"]),
        [3] = new("ComponentRender", Render, 1, ["componentId", "name", "reason"]),
        [4] = new("ComponentRender", Render, 2, ["componentId"]),
        [5] = new("Reconcile", Reconcile, 1, ["entry", "element"]),
        [6] = new("Reconcile", Reconcile, 2, []),
        [7] = new("Effects", "ReactorEffects", 1, ["componentId"]),
        [8] = new("Effects", "ReactorEffects", 2, ["componentId"]),
        [9] = new("ChildReconcile", "ReactorChildReconcile", 1, ["oldCount", "newCount"]),
        [10] = new("ChildReconcile", "ReactorChildReconcile", 2, ["inserted", "removed", "moved", "replaced"]),
        [11] = new("StateChange", "ReactorStateChange", 0, ["componentId", "hook", "changed"]),
        [12] = new("KeyedListDiagnostic", "ReactorKeyedListDiagnostic", 0, ["kind", "bailedOut"]),
    };

    public static PerfEvent? Decode(PerfRawEvent raw, string id, long origin, long frequency)
    {
        if (frequency <= 0)
        {
            throw new InvalidDataException("The capture QPC frequency is invalid.");
        }
        // Event IDs added by later Reactor versions are ignored rather than guessed.
        if (!Schemas.TryGetValue(raw.EventId, out var schema))
        {
            return null;
        }
        var fields = raw.Fields is null ? new Dictionary<string, string>(StringComparer.Ordinal) : new(raw.Fields, StringComparer.Ordinal);
        var error = raw.DecodeError ??
            (!raw.TraceLogging ? "Reactor events must be self-describing TraceLogging events." :
            raw.Version != 0 ? "Unsupported Reactor event version." :
            raw.EventName != schema.EventName ? "The Reactor event name does not match its event ID." :
            raw.Opcode != schema.Opcode ? "Unexpected Reactor event opcode." :
            schema.Fields.FirstOrDefault(field => !fields.ContainsKey(field)) is { } missing ? $"The Reactor field '{missing}' is missing." :
            null);
        var name = schema.Name;
        if (error is null && raw.EventId == 3)
        {
            var component = fields["name"];
            name += ":" + (string.IsNullOrWhiteSpace(component) ? "(unnamed)" : component);
            if (name.Length > 512)
            {
                name = name[..512];
            }
        }
        var phase = error is not null ? "unknown" : schema.Opcode switch { 1 => "begin", 2 => "end", _ => "info" };
        return new(id, raw.Qpc, (raw.Qpc - origin) * 1000.0 / frequency, raw.ThreadId, raw.Provider, raw.EventId,
            raw.Version, raw.Opcode, raw.ActivityId, name, Family, phase, null, error is null ? fields : [], error);
    }

    /// <summary>The component type name of a <see cref="Render"/> scope or call, or null for other names.</summary>
    public static string? ComponentName(string name) =>
        name.StartsWith(Render + ":", StringComparison.Ordinal) ? name[(Render.Length + 1)..] : null;
}
