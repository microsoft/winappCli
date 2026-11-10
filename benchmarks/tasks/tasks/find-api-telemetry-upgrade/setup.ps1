# Publishes Contoso.Telemetry 2.0.0 to a local folder feed. The 2.0 client type and its members get new
# names drawn at random on every run, so the fix can't be remembered from an earlier run or from
# training data; it can only be read from the package's metadata. The checker and solve.ps1 read the
# names from the state file.
param([string]$Workspace, [string]$FeedPath = 'C:\feeds\contoso', [string]$StatePath = 'C:\bench-state\find-api-telemetry-upgrade.json')
$ErrorActionPreference = 'Stop'

$client = Get-RandomPick -From 'TelemetrySession', 'TelemetryChannel', 'TelemetryPipeline', 'InsightsSession', 'InsightsChannel', 'EventChannel', 'SignalClient', 'TelemetryAgent', 'UsageReporter', 'TelemetryHub'
# Every name is plausible for what it does, but no single name can be predicted.
$verbs = 'Emit', 'Record', 'Publish', 'Submit', 'Post', 'Capture', 'Log', 'Note', 'Push', 'Raise', 'Report', 'Enqueue', 'Write', 'Stage', 'Mark', 'Register'
$event, $metric, $failure = Get-RandomPick -From $verbs -Count 3
$diag = Get-RandomPick -From 'Trace', 'TraceLine', 'WriteTrace', 'Debug', 'Echo', 'Diagnose'
$flush = Get-RandomPick -From 'DrainAsync', 'CommitAsync', 'SendPendingAsync', 'ShipAsync', 'UploadAsync', 'DeliverAsync', 'PushPendingAsync'
$pause = Get-RandomPick -From 'SuspendAsync', 'HoldAsync', 'ParkAsync', 'PauseAsync'
$metric = "${metric}Value"
$failure = "${failure}Failure"
$nonce = [guid]::NewGuid().ToString('N')

$source = @"
using System.Text.Json;

namespace Contoso.Telemetry;

/// <summary>Collects usage telemetry for one application and sends it to the Contoso telemetry service.</summary>
public sealed class $client : IDisposable
{
    private readonly string _app;
    private readonly List<Dictionary<string, object?>> _queue = new();
    private bool _paused;

    /// <summary>Starts collecting telemetry for the named application.</summary>
    /// <param name="applicationName">Name the telemetry service files the items under.</param>
    public $client(string applicationName) => _app = applicationName;

    /// <summary>Queues a named custom event, with optional properties, to be sent to the telemetry service.</summary>
    /// <param name="name">Event name, for example "orders_synced".</param>
    /// <param name="properties">Optional string properties sent with the event.</param>
    public void $event(string name, IReadOnlyDictionary<string, string>? properties = null) =>
        _queue.Add(new() { ["kind"] = "event", ["name"] = name, ["properties"] = properties });

    /// <summary>Writes a local diagnostic trace line to standard error. Diagnostic traces are never sent to the telemetry service.</summary>
    /// <param name="message">Trace text.</param>
    /// <param name="properties">Optional values appended to the trace line.</param>
    public void $diag(string message, IReadOnlyDictionary<string, string>? properties = null) =>
        Console.Error.WriteLine("[trace] " + message + (properties is null ? "" : " " + string.Join(", ", properties.Select(p => p.Key + "=" + p.Value))));

    /// <summary>Queues a numeric measurement, such as a duration or a size, to be sent to the telemetry service.</summary>
    /// <param name="name">Measurement name.</param>
    /// <param name="value">Measured value.</param>
    public void $metric(string name, double value) =>
        _queue.Add(new() { ["kind"] = "metric", ["name"] = name, ["value"] = value });

    /// <summary>Queues a report of an exception the application caught, to be sent to the telemetry service.</summary>
    /// <param name="exception">The caught exception.</param>
    public void $failure(Exception exception) =>
        _queue.Add(new() { ["kind"] = "exception", ["name"] = exception.GetType().FullName });

    /// <summary>Sends every queued item to the telemetry service and completes when the service has accepted them.</summary>
    /// <param name="cancellationToken">Cancels waiting for the service.</param>
    public async Task $flush(CancellationToken cancellationToken = default)
    {
        if (_paused || _queue.Count == 0) return;
        var outbox = Environment.GetEnvironmentVariable("CONTOSO_TELEMETRY_OUTBOX")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Contoso", "Telemetry", "outbox.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(outbox)!);
        var lines = _queue.Select(item => { item["app"] = _app; item["sig"] = "$nonce"; return JsonSerializer.Serialize(item); });
        await File.AppendAllLinesAsync(outbox, lines, cancellationToken);
        _queue.Clear();
    }

    /// <summary>Stops sending until the application restarts. Queued items stay queued and are not sent.</summary>
    /// <param name="cancellationToken">Not used.</param>
    public Task $pause(CancellationToken cancellationToken = default)
    {
        _paused = true;
        return Task.CompletedTask;
    }

    /// <summary>Releases the client. Items that were queued but not sent are discarded.</summary>
    public void Dispose() => _queue.Clear();
}
"@

[void](New-LocalNuGetPackage -Id 'Contoso.Telemetry' -Version '2.0.0' -Source $source -FeedPath $FeedPath -Description 'Usage telemetry for Contoso applications.')

New-Item -ItemType Directory -Force -Path (Split-Path $StatePath) | Out-Null
[ordered]@{ client = $client; event = $event; diagnostic = $diag; metric = $metric; failure = $failure; flush = $flush; pause = $pause; nonce = $nonce } |
    ConvertTo-Json | Set-Content -LiteralPath $StatePath -Encoding utf8NoBOM
# The names stay out of setup.log: the run's output folder is readable from inside the Sandbox.
'Published Contoso.Telemetry 2.0.0'
