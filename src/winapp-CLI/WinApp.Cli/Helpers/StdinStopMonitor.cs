// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Helpers;

/// <summary>
/// Stops a recording on redirected-stdin newline, Ctrl+C, or EOF after capture is ready.
/// </summary>
/// <remarks>
/// A terminal that feeds the command through a pipe instead of a console sends Ctrl+C as the
/// character U+0003 rather than as a console signal, so it has to be read here or it is ignored.
/// </remarks>
internal static class StdinStopMonitor
{
    private const int CtrlC = 3;

    public static void Start(TextReader stdin, Task readyTask, Action stop)
    {
        var t = new Thread(() => MonitorCore(stdin, readyTask, stop))
        {
            IsBackground = true,
            Name = "StdinStopMonitor",
        };
        t.Start();
    }

    internal static void MonitorCore(TextReader stdin, Task readyTask, Action stop)
    {
        try
        {
            int c;
            do
            {
                c = stdin.Read();
            }
            while (c is not (-1 or '\n' or '\r' or CtrlC));
        }
        catch
        {
            // Treat stdin failures as EOF.
        }

        readyTask.GetAwaiter().GetResult();
        stop();
    }
}
