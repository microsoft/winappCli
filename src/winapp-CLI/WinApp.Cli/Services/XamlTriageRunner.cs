// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

#pragma warning disable CA1416

using Microsoft.Diagnostics.Runtime.Utilities.DbgEng;
using System.Text;

namespace WinApp.Cli.Services;

/// <summary>
/// Runs the DbgEng-hosted WinUI extension (<c>!xamlstowed</c> / <c>!xamltriage</c>) in a dedicated
/// process. Isolation is required: the parent winapp process loads the system32
/// <c>dbghelp.dll</c> while capturing/analyzing the dump, and the modern (DbgX-era) <c>dbgeng.dll</c>
/// from NuGet then fails to load because its <c>dbghelp.dll</c> import binds to that older,
/// already-resident module (ERROR_PROC_NOT_FOUND). A fresh process has a clean loader state, so the
/// engine's own co-located <c>dbghelp.dll</c> is the one that gets bound.
/// </summary>
internal static class XamlTriageRunner
{
    /// <summary>Hidden first-argument verb that routes <c>Program.Main</c> to this runner.</summary>
    public const string InternalVerb = "__xaml-triage";

    private static readonly string SymbolCachePath = Path.Combine(Path.GetTempPath(), "symbols");

    /// <summary>
    /// Entry point for the isolated child process. Parses <c>--dump</c>, <c>--bin</c>, <c>--ext</c>
    /// and optional <c>--symbols</c>, runs the extension, and writes the captured output to stdout.
    /// </summary>
    public static int Run(string[] args)
    {
        string? dump = null, bin = null, ext = null, jsProvider = null;
        var useSymbols = false;
        var stowedOnly = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dump" when i + 1 < args.Length: dump = args[++i]; break;
                case "--bin" when i + 1 < args.Length: bin = args[++i]; break;
                case "--ext" when i + 1 < args.Length: ext = args[++i]; break;
                case "--jsprovider" when i + 1 < args.Length: jsProvider = args[++i]; break;
                case "--symbols": useSymbols = true; break;
                case "--stowed-only": stowedOnly = true; break;
            }
        }

        if (dump == null || bin == null || ext == null)
        {
            Console.Error.WriteLine("xaml-triage: --dump, --bin and --ext are required.");
            return 2;
        }

        // The provider may live in a winext subfolder; the parent passes its resolved path. Fall back
        // to the engine directory for backward compatibility when it is not supplied.
        jsProvider ??= Path.Combine(bin, "JsProvider.dll");

        try
        {
            Console.Out.Write(RunDbgEngExtension(
                dump,
                bin,
                jsProvider,
                ext,
                useSymbols,
                runExtendedTriage: !stowedOnly));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"xaml-triage failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>
    /// Executes <c>.scriptload</c> + <c>!xamlstowed</c> + <c>!xamltriage</c> against the dump and
    /// returns the captured DbgEng output.
    /// </summary>
    public static string RunDbgEngExtension(
        string dumpPath,
        string binDir,
        string jsProviderPath,
        string extPath,
        bool useSymbols,
        bool runExtendedTriage = true)
    {
        using IDisposable dbgeng = IDebugClient.Create(binDir);
        IDebugClient client = (IDebugClient)dbgeng;
        IDebugControl control = (IDebugControl)dbgeng;

        var hr = client.OpenDumpFile(dumpPath);
        if (hr < 0)
        {
            return $"DbgEng failed to open dump for WinUI triage: HRESULT 0x{(uint)hr:X8}";
        }

        hr = control.WaitForEvent(TimeSpan.FromSeconds(60));
        if (hr < 0)
        {
            return $"DbgEng WaitForEvent failed during WinUI triage: HRESULT 0x{(uint)hr:X8}";
        }

        var output = new StringBuilder();
        string result;
        using (var holder = new DbgEngOutputHolder(client, DEBUG_OUTPUT.ALL))
        {
            holder.OutputReceived += (text, _) => output.Append(text);
            result = RunTriageSequence(
                jsProviderPath,
                extPath,
                useSymbols,
                command => control.Execute(DEBUG_OUTCTL.THIS_CLIENT, command, DEBUG_EXECUTE.DEFAULT),
                () => output.ToString(),
                runExtendedTriage);
        }

        return result;
    }

    /// <summary>
    /// Emits the DbgEng command sequence for WinUI triage — optional symbol-server configuration, the
    /// exception-context switch, the JavaScript-provider load, the script load, and the
    /// <c>!xamlstowed</c>/<c>!xamltriage</c> commands — through the supplied <paramref name="execute"/>
    /// delegate (which returns each command's HRESULT), returning the engine output captured by
    /// <paramref name="getOutput"/>. Extracted from <see cref="RunDbgEngExtension"/> so the symbol path,
    /// the provider-load-failure path, and the happy path are unit-testable without a live engine, dump,
    /// or symbol server. Behavior-preserving: the commands, their order, and the failure message match the
    /// original in-situ sequence exactly.
    /// </summary>
    internal static string RunTriageSequence(
        string jsProviderPath,
        string extPath,
        bool useSymbols,
        Func<string, int> execute,
        Func<string> getOutput,
        bool runExtendedTriage = true)
    {
        if (useSymbols)
        {
            // Configure the public symbol server (symsrv.dll is co-located with the engine) and
            // force-download the modules the extension dereferences: combase.dll provides the
            // _STOWED_EXCEPTION_INFORMATION_* types !xamlstowed needs, and the WinUI module
            // provides the XAML error-context types. Forcing avoids lazy-load gaps mid-script.
            execute($".sympath srv*{SymbolCachePath}*https://msdl.microsoft.com/download/symbols");
            execute(".reload /f combase.dll");
            execute(".reload /f Microsoft.UI.Xaml.dll");
        }

        // Switch to the recorded exception context so the extension analyzes the faulting thread
        // (the stowed-exception raise site) rather than whichever thread the dump opened on.
        execute(".ecxr");

        // Register the JavaScript script provider (ships as JsProvider.dll alongside the engine).
        // Without this, '.scriptload <file>.js' fails with "No script provider ... for '.js'".
        var jsProvider = jsProviderPath.Replace('\\', '/');
        var loadHr = execute($".load \"{jsProvider}\"");
        if (loadHr < 0)
        {
            return getOutput() + $"\nWinUI triage could not load the JavaScript provider " +
                $"({jsProviderPath}): HRESULT 0x{(uint)loadHr:X8}";
        }

        // Load the JS extension, then run the stowed-exception + triage commands.
        // Forward slashes avoid escaping issues in the DbgEng command parser.
        var scriptPath = extPath.Replace('\\', '/');
        execute($".scriptload \"{scriptPath}\"");
        execute("!xamlstowed");
        if (runExtendedTriage)
        {
            execute("!xamltriage");
        }

        return getOutput();
    }
}
