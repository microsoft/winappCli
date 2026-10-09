// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

#pragma warning disable CA1416

using Microsoft.Diagnostics.Runtime.Utilities.DbgEng;
using System.Security.AccessControl;
using System.Security.Principal;
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
    /// Entry point for the isolated child process. Parses <c>--dump</c>, <c>--bin</c>,
    /// <c>--jsprovider</c>, <c>--ext</c> and optional <c>--symbols</c>, runs the extension, and writes
    /// the captured output to stdout. The parent holds the DLLs named by <c>--bin</c> and
    /// <c>--jsprovider</c> open until this process exits. This process still re-verifies them and the
    /// script before loading anything, because anyone can start it with arguments of their choosing.
    /// </summary>
    public static int Run(string[] args)
    {
        string? dump = null, bin = null, ext = null, jsProvider = null;
        var useSymbols = false;
        for (var i = 1; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--dump" when i + 1 < args.Length: dump = args[++i]; break;
                case "--bin" when i + 1 < args.Length: bin = args[++i]; break;
                case "--ext" when i + 1 < args.Length: ext = args[++i]; break;
                case "--jsprovider" when i + 1 < args.Length: jsProvider = args[++i]; break;
                case "--symbols": useSymbols = true; break;
            }
        }

        if (dump == null || bin == null || ext == null || jsProvider == null)
        {
            Console.Error.WriteLine("xaml-triage: --dump, --bin, --jsprovider and --ext are required.");
            return 2;
        }

        try
        {
            // DbgEng reads _NT_* variables for extension and symbol-server search paths, and either can make
            // it load a DLL. The caller chose this environment, so none of it is used.
            foreach (var name in Environment.GetEnvironmentVariables().Keys.Cast<string>()
                .Where(n => n.StartsWith("_NT_", StringComparison.OrdinalIgnoreCase)).ToList())
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            // Everything this process loads or runs is re-verified here, not just by the parent, and held
            // until the engine is done, so the files checked are the files loaded.
            using var inputs = VerifyInputs(bin, jsProvider, ext);
            Console.Out.Write(RunDbgEngExtension(dump, inputs.BinDir, inputs.JsProviderPath, inputs.ExtPath, useSymbols));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"xaml-triage failed: {ex.Message}");
            return 1;
        }
    }

    /// <summary>The verified, held inputs the engine loads. Disposing releases the holds.</summary>
    internal sealed record VerifiedTriageInputs(string BinDir, string JsProviderPath, string ExtPath, IReadOnlyList<IDisposable> Holds) : IDisposable
    {
        public void Dispose()
        {
            foreach (var hold in Holds)
            {
                hold.Dispose();
            }
        }
    }

    /// <summary>Verification seam; tests that drive a real engine with stand-in files replace it.</summary>
    internal static Func<string, string, string, VerifiedTriageInputs> VerifyInputs { get; set; } =
        (bin, jsProvider, ext) => VerifyInputsForLoad(bin, jsProvider, ext, XamlTriageBinaries.HoldForLoad);

    /// <summary>
    /// Refuses unless <c>dbgeng.dll</c>, its supporting DLLs and <c>JsProvider.dll</c> are Microsoft-signed
    /// matching builds, and the extension script is the pinned <c>winui-dbgext.js</c> (a script can
    /// <c>.load</c> any DLL). Returns private copies of the engine files and the script, held with no write
    /// or delete sharing until disposed.
    /// </summary>
    internal static VerifiedTriageInputs VerifyInputsForLoad(
        string binDir, string jsProviderPath, string extPath, Func<string, string, ResolvedTriageBinaries?> holdBinaries)
    {
        using var binaries = holdBinaries(binDir, jsProviderPath)
            ?? throw new InvalidOperationException(
                $"refusing to load the debugger from '{binDir}' and '{jsProviderPath}': they must be Microsoft-signed debugger files from the same build.");
        VerifiedTool script;
        try
        {
            script = VerifiedTool.Open(
                new FileInfo(extPath),
                (path, _) => XamlTriageService.MatchesPinnedExtensionHash(File.ReadAllBytes(path)),
                Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
        }
        catch (BuildToolSignatureException)
        {
            throw new InvalidOperationException($"refusing to run '{extPath}': it is not the pinned WinUI debugger extension.");
        }

        try
        {
            // DbgEng also loads default extension DLLs (exts, uext, ...) found beside the engine, and the
            // pinned script triggers that. A folder chosen by whoever started this process could hold any of
            // them, so the engine runs from a folder this process creates with only the verified files in it.
            var staged = StageVerifiedBinaries(binaries, holdBinaries);
            return new VerifiedTriageInputs(staged.BinDir, staged.JsProviderPath, script.Path, [staged, script]);
        }
        catch
        {
            // The script hold is handed to the caller on success, so it is released here only on failure.
            script.Dispose();
            throw;
        }
    }

    private const string StagingPrefix = "winapp-xaml-triage-";

    /// <summary>
    /// Copies the held, verified engine files into a new private folder, locks it against additions, then
    /// holds and verifies the copies with the same check. Staging folders older than ten minutes are removed
    /// best effort; one still in use has its DLLs loaded and is skipped.
    /// </summary>
    internal static ResolvedTriageBinaries StageVerifiedBinaries(
        ResolvedTriageBinaries binaries, Func<string, string, ResolvedTriageBinaries?> holdBinaries)
    {
        foreach (var previous in new DirectoryInfo(Path.GetTempPath()).EnumerateDirectories(StagingPrefix + "*")
            .Where(d => d.CreationTimeUtc < DateTime.UtcNow.AddMinutes(-10)))
        {
            try
            {
                previous.Delete(recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Still in use by another triage.
            }
        }

        var stage = Directory.CreateTempSubdirectory(StagingPrefix);
        var copied = new List<string>();
        foreach (var held in binaries.Holds.OfType<VerifiedTool>())
        {
            var name = Path.GetFileName(held.Path);
            File.Copy(held.Path, Path.Join(stage.FullName, name));
            copied.Add(name);
        }

        // DbgEng loads default extension DLLs it finds in the engine folder and its winext subfolder, so
        // nothing may be added to this folder while the engine runs. After the lock, not even this user can
        // add files or change the folder's permissions; anything added before the lock is refused below.
        LockAgainstAdditions(stage);
        var present = stage.EnumerateFileSystemInfos().Select(e => e.Name).ToList();
        if (present.Count != copied.Count || present.Except(copied, StringComparer.OrdinalIgnoreCase).Any())
        {
            throw new InvalidOperationException(
                $"something else added files to winapp's private debugger folder '{stage.FullName}'; refusing to run the debugger from it.");
        }

        return holdBinaries(stage.FullName, Path.Join(stage.FullName, Path.GetFileName(binaries.JsProviderPath)))
            ?? throw new InvalidOperationException("the verified debugger files changed while they were being copied; try again.");
    }

    /// <summary>
    /// Replaces the folder's permissions so that, besides SYSTEM and Administrators, everyone can only read,
    /// run and delete what is in it. The OWNER RIGHTS entry also takes away the owner's implicit right to
    /// change the permissions back. Delete lets a later run clean the folder up; the files are held without
    /// delete sharing while in use.
    /// </summary>
    private static void LockAgainstAdditions(DirectoryInfo directory)
    {
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        const FileSystemRights readRunDelete = FileSystemRights.ReadAndExecute | FileSystemRights.Delete;

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var (sid, rights) in new[]
        {
            (new SecurityIdentifier("S-1-3-4"), readRunDelete), // OWNER RIGHTS
            (WindowsIdentity.GetCurrent().User!, readRunDelete),
            (new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl),
            (new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), FileSystemRights.FullControl),
        })
        {
            security.AddAccessRule(new FileSystemAccessRule(sid, rights, inherit, PropagationFlags.None, AccessControlType.Allow));
        }

        directory.SetAccessControl(security);
    }

    /// <summary>
    /// Executes <c>.scriptload</c> + <c>!xamlstowed</c> + <c>!xamltriage</c> against the dump and
    /// returns the captured DbgEng output.
    /// </summary>
    public static string RunDbgEngExtension(string dumpPath, string binDir, string jsProviderPath, string extPath, bool useSymbols)
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
                () => output.ToString());
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
        Func<string> getOutput)
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
        execute("!xamltriage");

        return getOutput();
    }
}
