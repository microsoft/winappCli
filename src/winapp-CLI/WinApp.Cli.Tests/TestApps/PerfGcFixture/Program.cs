// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;

if (args.Length != 1)
{
    throw new ArgumentException("Pass the probe capture directory.");
}
var directory = Path.GetFullPath(args[0]);
Console.WriteLine(Environment.ProcessId);
var timeout = Stopwatch.StartNew();
while (!File.Exists(Path.Join(directory, "ready")))
{
    if (timeout.Elapsed > TimeSpan.FromSeconds(60))
    {
        throw new TimeoutException("The private capture did not become ready.");
    }
    Thread.Sleep(50);
}
for (var iteration = 0; iteration < 20; iteration++)
{
    var retained = Enumerable.Range(0, 32).Select(_ => new byte[100_000]).ToArray();
    GC.Collect(2, GCCollectionMode.Forced, blocking: iteration % 2 == 0, compacting: false);
    GC.KeepAlive(retained);
    Thread.Sleep(100);
}
while (!File.Exists(Path.Join(directory, "done")) && timeout.Elapsed < TimeSpan.FromSeconds(90))
{
    Thread.Sleep(50);
}
