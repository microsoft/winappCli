// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics.Tracing;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.UI.Xaml;

[assembly: System.Reflection.Metadata.MetadataUpdateHandler(typeof(WinApp.DevTools.Managed.ReactorEvents))]

namespace WinApp.DevTools.Managed;

/// <summary>One Microsoft-UI-Reactor event as DevTools reports it.</summary>
/// <param name="Kind">state, unmount, renderError, swallowedError, hresult, warning, hotReload, windowOpened, windowClosed, rendered or other.</param>
/// <param name="Level">error, warning, info or verbose.</param>
/// <param name="Log">Whether <c>winapp devtools logs</c> keeps it (warnings, errors, hot reload and windows).</param>
internal sealed record ReactorEventRecord(string Kind, string Level, string Event, bool Log, IReadOnlyList<(string Name, object? Value)> Fields);

/// <summary>
/// Observes the <c>Microsoft-UI-Reactor</c> EventSource in-process, the same way <c>ReactorTrace.Subscribe</c> does
/// (an <see cref="EventListener"/>), so it needs no reflection and works on every Reactor version. Keeps the warnings
/// and errors since startup for <c>winapp devtools logs</c> and streams every event to the native inspector, which
/// raises <c>Reactor.changed</c>. Reactor's payloads carry no user data (exception types, never messages; no window
/// titles), and DevTools adds none.
/// </summary>
internal static class ReactorEvents
{
    internal const int Capacity = 500;
    internal const int MaximumFieldLength = 1024;

    private const long Errors = 0x20, Lifecycle = 0x10, Hosting = 0x80, HotReload = 0x2000, Theme = 0x800, Intl = 0x400,
        Persistence = 0x100, State = 0x4, RenderDetail = 0x4000;
    internal const long BaseKeywords = Errors | Lifecycle | Hosting | HotReload | Theme | Intl | Persistence | State;
    internal const int RendersMask = 1;

    private static readonly object Gate = new();
    private static readonly Queue<(long Sequence, string Json)> Recent = new();
    private static readonly List<Sink> Sinks = [];
    private static long _sequence;
    private static long _dropped;
    private static Listener? _listener;
    private static int _renderSubscribers;

    internal static void Start()
    {
        lock (Gate)
        {
            _listener ??= new Listener();
        }
    }

    /// <summary>Called by .NET Hot Reload after it applied an edit: report it like Reactor's own hot-reload event.</summary>
    internal static void UpdateApplication(Type[]? types) =>
        Publish(new ReactorEventRecord("hotReload", "info", "MetadataUpdate", true,
            [("source", "metadataUpdate"), ("types", types?.Length ?? 0)]));

    internal static void ClearCache(Type[]? types) { }

    /// <summary>What DevTools reports for one Reactor event, or null for an event it does not report.</summary>
    internal static ReactorEventRecord? Translate(string name, EventLevel level, IReadOnlyList<string>? names, IReadOnlyList<object?>? payload)
    {
        object? Field(string field)
        {
            if (names is null || payload is null)
            {
                return null;
            }
            for (var i = 0; i < names.Count && i < payload.Count; i++)
            {
                if (names[i] == field)
                {
                    return payload[i];
                }
            }
            return null;
        }
        (string, object?) F(string output, string input) => (output, Field(input));
        return name switch
        {
            "StateChange" => new("state", "verbose", name, false, [F("hook", "hookKind"), F("valueType", "valueType"), F("changed", "changed")]),
            "ComponentUnmount" => new("unmount", "info", name, false, [F("component", "componentName")]),
            "RenderError" => new("renderError", "error", name, true, [F("component", "componentName"), F("exception", "exceptionType")]),
            "SwallowedError" => new("swallowedError", "warning", name, true,
                [F("category", "category"), F("operation", "operation"), F("exception", "exceptionType")]),
            "HResultFailed" => new("hresult", "warning", name, true,
                [F("category", "category"), F("operation", "operation"),
                 ("hresult", Field("hr") is int hr ? "0x" + hr.ToString("X8", CultureInfo.InvariantCulture) : null)]),
            "Warning" => new("warning", "warning", name, true, [F("category", "category"), F("operation", "operation"), F("message", "message")]),
            "HotReloadStateMigrated" => new("hotReload", "info", name, true, [F("valueType", "valueType")]),
            "HotReloadFieldDropped" => new("hotReload", "warning", name, true,
                [F("owner", "ownerType"), F("field", "fieldName"), F("from", "fromType"), F("to", "toType")]),
            "WindowOpened" => new("windowOpened", "info", name, true, [F("windowType", "windowType"), F("hwnd", "hwnd")]),
            "WindowClosed" => new("windowClosed", "info", name, true, [F("windowType", "windowType"), F("hwnd", "hwnd")]),
            "ComponentRendered" => new("rendered", "verbose", name, false,
                [F("component", "componentName"), F("componentId", "componentId"), F("reason", "reason"), F("elapsedUs", "elapsedMicroseconds")]),
            _ when level is EventLevel.Warning or EventLevel.Error or EventLevel.Critical =>
                new("other", level == EventLevel.Warning ? "warning" : "error", name, true,
                    [("detail", names is null || payload is null ? null :
                        string.Join("; ", names.Zip(payload, (n, v) =>
                            n + "=" + (ReactorSecrets.IsSecretName(n) ? ReactorElementProps.Redacted : Convert.ToString(v, CultureInfo.InvariantCulture)))))]),
            _ => null,
        };
    }

    internal static string ToJson(ReactorEventRecord record, long sequence, DateTime time, bool replay = false)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("seq", sequence);
            w.WriteString("kind", record.Kind);
            w.WriteString("level", record.Level);
            w.WriteString("event", record.Event);
            w.WriteString("time", time.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture));
            if (record.Log)
            {
                w.WriteBoolean("log", true);
            }
            if (replay)
            {
                w.WriteBoolean("replay", true);
            }
            foreach (var (name, value) in record.Fields)
            {
                switch (value)
                {
                    case null:
                        break;
                    case bool b:
                        w.WriteBoolean(name, b);
                        break;
                    case int or long or short or byte or uint or ulong or double or float:
                        w.WriteNumber(name, Convert.ToDouble(value, CultureInfo.InvariantCulture));
                        break;
                    default:
                        var text = Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
                        // Event text (a warning's message, an unknown event's payload) can quote app values; Reactor
                        // does not redact them, so a labelled secret is redacted here.
                        if (ReactorSecrets.HasLabelledSecret(text))
                        {
                            text = ReactorElementProps.Redacted;
                        }
                        if (text.Length > 0)
                        {
                            w.WriteString(name, text.Length > MaximumFieldLength ? text[..MaximumFieldLength] : text);
                        }
                        break;
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Keeps a log entry and sends the event to every connected inspector.</summary>
    internal static void Publish(ReactorEventRecord record)
    {
        string json;
        Sink[] sinks;
        lock (Gate)
        {
            if (!record.Log && Sinks.Count == 0)
            {
                return;
            }
            json = ToJson(record, ++_sequence, DateTime.UtcNow);
            if (record.Log)
            {
                Recent.Enqueue((_sequence, json));
                while (Recent.Count > Capacity)
                {
                    Recent.Dequeue();
                    _dropped++;
                }
            }
            sinks = [.. Sinks];
        }
        foreach (var sink in sinks)
        {
            sink.Post(json);
        }
    }

    /// <summary>The kept log entries, oldest first, and how many older ones were dropped.</summary>
    internal static (IReadOnlyList<string> Entries, long Dropped) Snapshot()
    {
        lock (Gate)
        {
            return ([.. Recent.Select(item => item.Json)], _dropped);
        }
    }

    internal static void ResetForTests()
    {
        lock (Gate)
        {
            Recent.Clear();
            _sequence = 0;
            _dropped = 0;
        }
    }

    /// <summary>
    /// Serves one inspector connection (<c>EVENTS1 &lt;mask&gt;</c>): the kept log entries flagged <c>replay</c>, a
    /// <c>ready</c> marker, then every event as it happens, one <c>EVENT {json}</c> line each. The inspector changes
    /// what it wants with <c>MASK &lt;n&gt;</c> lines (1 = component renders).
    /// </summary>
    internal static async Task ServeAsync(Stream stream, int mask, Func<Stream, string?> readLine)
    {
        Start();
        var sink = new Sink();
        lock (Gate)
        {
            foreach (var (sequence, json) in Recent)
            {
                sink.Post(json.Insert(json.Length - 1, ",\"replay\":true"));
            }
            sink.Post("{\"kind\":\"ready\",\"next\":" + (_sequence + 1).ToString(CultureInfo.InvariantCulture) +
                ",\"dropped\":" + _dropped.ToString(CultureInfo.InvariantCulture) +
                (RenderHighlights.Available ? ",\"renders\":true,\"rendersBy\":\"componentRendered\"}"
                    : ReconcileHighlight.Flag() is not null ? ",\"renders\":true,\"rendersBy\":\"reactor\"}" : "}"));
            Sinks.Add(sink);
        }
        SetMask(sink, mask);
        var writer = Task.Run(async () =>
        {
            using var output = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
            await foreach (var line in sink.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                await output.WriteLineAsync("EVENT " + line).ConfigureAwait(false);
            }
        });
        try
        {
            while (await Task.Run(() => readLine(stream)).ConfigureAwait(false) is string command)
            {
                if (command.StartsWith("MASK ", StringComparison.Ordinal) &&
                    int.TryParse(command.AsSpan(5), NumberStyles.None, CultureInfo.InvariantCulture, out var next))
                {
                    SetMask(sink, next);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or ObjectDisposedException)
        {
            // The inspector went away.
        }
        finally
        {
            lock (Gate)
            {
                Sinks.Remove(sink);
            }
            SetMask(sink, 0);
            sink.Complete();
            try { await writer.ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
        }
    }

    private static void SetMask(Sink sink, int mask)
    {
        var renders = (mask & RendersMask) != 0;
        int count;
        lock (Gate)
        {
            if (sink.Renders == renders)
            {
                return;
            }
            sink.Renders = renders;
            _renderSubscribers += renders ? 1 : -1;
            count = _renderSubscribers;
        }
        _listener?.Configure(count > 0);
        // A Reactor without ComponentRendered draws its own reconcile highlights when asked.
        if (!RenderHighlights.Available)
        {
            ReconcileHighlight.Set(count > 0);
        }
    }

    /// <summary>
    /// Reactor's public <c>ReactorFeatureFlags.HighlightReconcileChanges</c>: Reactor outlines the controls each reconcile
    /// pass mounted or changed. The fallback for "Highlight updates" on a Reactor that does not report component renders.
    /// Restores the app's own value when nobody wants highlights any more.
    /// </summary>
    internal static class ReconcileHighlight
    {
        private static bool? saved;

        internal static PropertyInfo? Flag() => ReactorType("Microsoft.UI.Reactor.Core.ReactorFeatureFlags")?
            .GetProperty("HighlightReconcileChanges", BindingFlags.Public | BindingFlags.Static) is { PropertyType: var type } property &&
            type == typeof(bool) ? property : null;

        internal static void Set(bool on)
        {
            if (Flag() is not { } flag)
            {
                return;
            }
            try
            {
                if (on && saved is null)
                {
                    saved = (bool)flag.GetValue(null)!;
                    flag.SetValue(null, true);
                }
                else if (!on && saved is { } previous)
                {
                    flag.SetValue(null, previous);
                    saved = null;
                }
            }
            catch (TargetInvocationException)
            {
                // The flag is the app's; leave it alone if Reactor refuses.
            }
        }
    }

    internal static Type? ReactorType(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!assembly.IsDynamic && assembly.GetType(name) is { } type)
            {
                return type;
            }
        }
        return null;
    }

    /// <summary>
    /// Reactor's keyed-list bailouts (a null or duplicate key makes a list re-create its rows), from the public
    /// <c>ReactorDiagnostics.RecentKeyedListWarnings</c>, reported as log entries when they first appear (<c>first</c>,
    /// which the toolbar badge counts) or fire again. Reactor's in-app dev menu used to be the only place to see them.
    /// The offending keys are app data, so only how many are reported. The component and the C# call site are added
    /// when the Reactor version reports them.
    /// </summary>
    internal static class KeyedLists
    {
        // One entry per list as Reactor identifies it; Reactor bumps the entry's count and time each time it fires again.
        private static readonly Dictionary<(string?, string, string?, string?, int?), (int Count, DateTime Time)> Seen = [];
        private static Timer? timer;
        private static PropertyInfo? warnings;

        /// <summary>Where the recent warnings come from; Reactor's public list unless a test substitutes one.</summary>
        internal static Func<System.Collections.IEnumerable?> Source { get; set; } = ReactorWarnings;

        /// <summary>Where translated entries go; the event log unless a test substitutes one.</summary>
        internal static Action<ReactorEventRecord> Sink { get; set; } = Publish;

        internal static void Start() => timer ??= new Timer(_ => Poll(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        private static System.Collections.IEnumerable? ReactorWarnings()
        {
            warnings ??= ReactorType("Microsoft.UI.Reactor.Core.Diagnostics.ReactorDiagnostics")?
                .GetProperty("RecentKeyedListWarnings", BindingFlags.Public | BindingFlags.Static);
            return warnings?.GetValue(null) as System.Collections.IEnumerable;
        }

        internal static void Poll()
        {
            try
            {
                if (Source() is not { } recent)
                {
                    return;
                }
                foreach (var warning in recent.Cast<object>().ToList())
                {
                    if (warning is not null && Translate(warning) is { } record)
                    {
                        Sink(record);
                    }
                }
            }
            catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or MemberAccessException or InvalidOperationException)
            {
                // A Reactor version with a different shape reports nothing.
            }
        }

        internal static ReactorEventRecord? Translate(object warning)
        {
            var type = warning.GetType();
            object? Read(string name) => type.GetProperty(name)?.GetValue(warning);
            var control = Read("ControlContext") as string;
            var kind = Read("Kind")?.ToString() ?? "";
            var time = Read("TimestampUtc") is DateTime stamp ? stamp : default;
            var count = Read("Count") is int n ? n : 1;
            var keys = Read("SampleKeys") is System.Collections.ICollection sample ? sample.Count : 0;
            var component = (Read("ComponentName") ?? Read("Component")) switch
            {
                string name => name,
                Type componentType => componentType.Name,
                _ => null,
            };
            var site = ReactorApi.Location(Read("CallSite"));
            var list = (control, kind, component, site?.File, site?.Line);
            bool first;
            lock (Seen)
            {
                first = !Seen.TryGetValue(list, out var known);
                if (!first && count <= known.Count && time <= known.Time)
                {
                    return null;
                }
                Seen[list] = (Math.Max(count, known.Count), time > known.Time ? time : known.Time);
            }
            return new("keyedList", "warning", "KeyedListDiagnostic", true,
                [("control", control), ("problem", kind == "NullKey" ? "nullKey" : kind == "DuplicateKey" ? "duplicateKey" : kind),
                 ("count", count), ("keys", keys), ("component", component),
                 ("file", site?.File), ("line", site?.Line), ("column", site?.Column is > 0 and var column ? (object?)column : null),
                 ("first", first ? (object?)true : null)]);
        }
    }

    private sealed class Sink
    {
        private readonly Channel<string> channel = Channel.CreateBounded<string>(
            new BoundedChannelOptions(2048) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

        public bool Renders { get; set; }

        public ChannelReader<string> Reader => channel.Reader;

        public void Post(string json) => channel.Writer.TryWrite(json);

        public void Complete() => channel.Writer.TryComplete();
    }

    private sealed class Listener : EventListener
    {
        private EventSource? source;
        private bool renders;

        internal void Configure(bool withRenders)
        {
            renders = withRenders;
            if (source is { } s)
            {
                EnableEvents(s, EventLevel.Verbose, (EventKeywords)(BaseKeywords | (withRenders ? RenderDetail : 0)));
            }
        }

        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name == "Microsoft-UI-Reactor")
            {
                source = eventSource;
                // Reactor is loaded: watch its keyed-list diagnostics too.
                KeyedLists.Start();
                EnableEvents(eventSource, EventLevel.Verbose, (EventKeywords)(BaseKeywords | (renders ? RenderDetail : 0)));
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventData)
        {
            try
            {
                if (eventData.EventSource?.Name != "Microsoft-UI-Reactor" ||
                    Translate(eventData.EventName ?? "", eventData.Level, eventData.PayloadNames, eventData.Payload) is not { } record)
                {
                    return;
                }
                if (record.Kind == "rendered")
                {
                    RenderHighlights.Note(record);
                    return;
                }
                Publish(record);
            }
            catch (Exception ex)
            {
                // Never let a DevTools fault surface through Reactor's EventSource.
                System.Diagnostics.Trace.TraceWarning("Reactor event relay failed: {0}", ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// "Highlight updates": component renders are coalesced and, once the render pass is over (the next dispatcher tick,
    /// as <c>ReactorTrace.GetComponentControl</c> asks), sent with each component's control identity so the inspector
    /// can flash it. Needs a Reactor that has <c>ComponentRendered</c>; older versions never raise it.
    /// </summary>
    private static class RenderHighlights
    {
        private static readonly object PendingGate = new();
        private static readonly Dictionary<long, (string Component, string Reason)> Pending = [];
        private static bool scheduled;
        private static MethodInfo? resolve;

        /// <summary>Whether the loaded Reactor reports component renders (ReactorTrace.GetComponentControl).</summary>
        internal static bool Available => Resolver() is not null;

        internal static void Note(ReactorEventRecord record)
        {
            long id = 0;
            string component = "", reason = "";
            foreach (var (name, value) in record.Fields)
            {
                if (name == "componentId" && value is long l) { id = l; }
                else if (name == "component") { component = value as string ?? ""; }
                else if (name == "reason") { reason = value as string ?? ""; }
            }
            if (id == 0)
            {
                return;
            }
            bool schedule;
            lock (PendingGate)
            {
                Pending[id] = (component, reason);
                schedule = !scheduled;
                scheduled = true;
            }
            if (schedule && Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread() is { } queue)
            {
                if (!queue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, Flush))
                {
                    lock (PendingGate) { scheduled = false; Pending.Clear(); }
                }
            }
            else if (schedule)
            {
                lock (PendingGate) { scheduled = false; Pending.Clear(); }
            }
        }

        private static void Flush()
        {
            KeyValuePair<long, (string Component, string Reason)>[] items;
            lock (PendingGate)
            {
                items = [.. Pending];
                Pending.Clear();
                scheduled = false;
            }
            var method = Resolver();
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartArray();
                foreach (var (id, (component, reason)) in items.Take(256))
                {
                    var control = method?.Invoke(null, [id]) as UIElement;
                    var identity = control is null ? 0 : ReactorProvider.IdentityOf(control);
                    if (identity == 0)
                    {
                        continue;
                    }
                    w.WriteStartObject();
                    w.WriteString("component", component);
                    w.WriteString("reason", reason);
                    w.WriteString("id", "0x" + ((ulong)identity).ToString("x", CultureInfo.InvariantCulture));
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            var list = Encoding.UTF8.GetString(buffer.ToArray());
            if (list == "[]")
            {
                return;
            }
            lock (Gate)
            {
                var json = "{\"seq\":" + (++_sequence).ToString(CultureInfo.InvariantCulture) +
                    ",\"kind\":\"rendered\",\"level\":\"verbose\",\"event\":\"ComponentRendered\",\"components\":" + list + "}";
                foreach (var sink in Sinks.Where(sink => sink.Renders))
                {
                    sink.Post(json);
                }
            }
        }

        private static MethodInfo? Resolver()
        {
            if (resolve is not null)
            {
                return resolve;
            }
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (assembly.IsDynamic)
                {
                    continue;
                }
                var trace = assembly.GetType("Microsoft.UI.Reactor.Diagnostics.ReactorTrace");
                if (trace?.GetMethod("GetComponentControl", BindingFlags.Public | BindingFlags.Static, [typeof(long)]) is { } method)
                {
                    return resolve = method;
                }
            }
            return null;
        }
    }
}
