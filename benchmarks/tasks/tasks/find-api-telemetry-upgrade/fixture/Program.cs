using System.Diagnostics;
using OrderSync;

var watch = Stopwatch.StartNew();
var orders = new[] { "SO-1041", "SO-1042", "SO-1043" };
foreach (var order in orders)
{
    Console.WriteLine($"Synced {order}");
}

AppTelemetry.OrdersSynced(orders.Length, watch.Elapsed);
Console.WriteLine($"Done: {orders.Length} orders");
