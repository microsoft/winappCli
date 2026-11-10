using Contoso.Telemetry;

namespace OrderSync;

// Usage telemetry for the sync job, sent through the shared Contoso.Telemetry package.
public static class AppTelemetry
{
    public static void OrdersSynced(int count, TimeSpan elapsed)
    {
        using var client = new TelemetryClient("order-sync");
        client.TrackEvent("orders_synced", new Dictionary<string, string>
        {
            ["count"] = count.ToString(),
            ["elapsed_ms"] = ((int)elapsed.TotalMilliseconds).ToString(),
        });
        // The job exits right after this, so send the event before the client goes away.
        client.Flush();
    }
}
