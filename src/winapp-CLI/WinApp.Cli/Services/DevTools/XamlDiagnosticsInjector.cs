// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Loads the DevTools visual-tree agent into a running WinUI 3 process by calling the Windows App
/// SDK framework export <c>InitializeXamlDiagnosticsEx</c> (from <c>Microsoft.Internal.FrameworkUdk.dll</c>).
///
/// <para>A short-lived CLI worker authenticates the UDK and restricts DLL searches, including
/// delayed loads, without changing the inspecting CLI's process-wide loader policy. The export
/// runs on an MTA thread and needs no Windows App SDK bootstrap.</para>
/// </summary>
internal static unsafe partial class XamlDiagnosticsInjector
{
    internal const string InternalVerb = "__devtools-inject";
    internal const uint System32Search = 0x00000800;
    internal const int MaximumInitializationLength = 259;
    // Must match the values compiled into the native agent (native/WinApp.DevTools.Native/DevToolsTap.cpp): the framework
    // opens this diagnostics endpoint and loads the TAP dll by path, then activates it via this CLSID.
    private const string DiagnosticsEndpoint = "WinUIVisualDiagConnection1";
    private static readonly Guid TapClsid = new("7C3D6A11-0000-4F00-9A00-5A4F4B450001");

    private const uint COINIT_MULTITHREADED = 0x0;

    [LibraryImport("ole32.dll")]
    private static partial int CoInitializeEx(nint reserved, uint coInit);

    [LibraryImport("ole32.dll")]
    private static partial void CoUninitialize();

    /// <summary>
    /// Injects <paramref name="tapDllPath"/> into process <paramref name="targetPid"/> using the
    /// framework UDK at <paramref name="frameworkUdkPath"/>. Returns the <c>HRESULT</c> from
    /// <c>InitializeXamlDiagnosticsEx</c> (<c>0</c> == <c>S_OK</c>). Throws if the UDK cannot be
    /// loaded or the export is missing.
    /// </summary>
    public static int Inject(
        uint targetPid,
        string tapDllPath,
        string frameworkUdkPath,
        string initializationData)
    {
        using var process = Process.Start(BuildStartInfo(
            Environment.ProcessPath ?? throw new IOException("Could not locate the winapp executable."),
            targetPid, tapDllPath, frameworkUdkPath, initializationData))
            ?? throw new IOException("Could not start the DevTools injection worker.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
            throw new IOException("The DevTools injection worker timed out.");
        }
        var stdout = output.GetAwaiter().GetResult();
        var stderr = error.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !int.TryParse(stdout.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hr))
        {
            throw new IOException($"The DevTools injection worker failed (exit {process.ExitCode}): {stderr.Trim()}");
        }
        return hr;
    }

    internal static ProcessStartInfo BuildStartInfo(string executable, uint targetPid, string tapDllPath,
        string frameworkUdkPath, string initializationData)
    {
        ValidateTransport(tapDllPath, frameworkUdkPath, initializationData);
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.SystemDirectory,
        };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, typeof(XamlDiagnosticsInjector).Assembly.GetName().Name + ".dll"));
        }
        foreach (var argument in new[] { InternalVerb, targetPid.ToString(CultureInfo.InvariantCulture),
            tapDllPath, frameworkUdkPath, initializationData })
        {
            info.ArgumentList.Add(argument);
        }
        return info;
    }

    internal static int RunWorker(string[] args) =>
        RunWorker(args, () => SetDefaultDllDirectories(System32Search), InjectInWorker);

    internal static int RunWorker(string[] args, Func<bool> restrictSearch,
        Func<uint, string, string, string, int> inject)
    {
        try
        {
            if (args.Length != 5 || args[0] != InternalVerb ||
                !uint.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid == 0)
            {
                throw new IOException("Invalid internal DevTools injection request.");
            }
            ValidateTransport(args[2], args[3], args[4]);
            // Per-load flags do not govern delay imports or later LoadLibrary calls. This policy
            // intentionally lasts until worker exit, never affecting the parent CLI.
            if (!restrictSearch())
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not restrict DevTools runtime dependency loading to System32.");
            }
            var hr = inject(pid, args[2], args[3], args[4]);
            Console.Out.WriteLine(hr.ToString(CultureInfo.InvariantCulture));
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
    }

    internal static void ValidateTransport(string tapDllPath, string frameworkUdkPath, string initializationData)
    {
        Validate(initializationData, "initialization data");
        Validate(tapDllPath, "native agent path");
        Validate(frameworkUdkPath, "framework path");

        static void Validate(string value, string field)
        {
            // The UDK packet has 260 WCHAR slots per field, including its terminator.
            if (value.Length > MaximumInitializationLength || value.Contains('\0'))
            {
                throw new InvalidOperationException(
                    $"DevTools {field} exceeds the 259 UTF-16 code-unit transport limit or contains a null character.");
            }
        }
    }

    private static int InjectInWorker(uint targetPid, string tapDllPath, string frameworkUdkPath, string initializationData)
    {
        var hr = unchecked((int)0x8000FFFF); // E_UNEXPECTED until the worker overwrites it.
        Exception? failure = null;

        var worker = new Thread(() =>
        {
            try
            {
                hr = InjectCore(targetPid, tapDllPath, frameworkUdkPath, initializationData);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        })
        {
            IsBackground = true,
            Name = "winapp-devtools-inject",
        };

        worker.SetApartmentState(ApartmentState.MTA);
        worker.Start();
        worker.Join();

        return failure is null ? hr : throw failure;
    }

    private static int InjectCore(
        uint targetPid,
        string tapDllPath,
        string frameworkUdkPath,
        string initializationData)
    {
        // Put this dedicated thread into the MTA. S_OK (first init) or S_FALSE (already initialized
        // in the same mode) are both fine; a hard failure (e.g. RPC_E_CHANGED_MODE) means we could
        // not get the apartment the diagnostics bootstrap requires, so surface it rather than inject
        // from the wrong apartment.
        var comInit = CoInitializeEx(0, COINIT_MULTITHREADED);
        if (comInit < 0)
        {
            return comInit;
        }

        // Balance CoInitializeEx (S_OK/S_FALSE) with CoUninitialize, and free the UDK once the one-shot
        // bootstrap has returned. Both are cleanup hygiene on this short-lived worker thread — the
        // diagnostics connection it establishes in the target is self-sustaining (the tap's pipe outlives
        // this process).
        try
        {
            return WithAuthenticatedUdk(frameworkUdkPath, (path, flags) =>
            {
                var udk = LoadLibraryExW(path, 0, flags);
                if (udk == 0)
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not load the authenticated framework UDK with System32-only dependencies.");
                }
                try
                {
                    var export = NativeLibrary.GetExport(udk, "InitializeXamlDiagnosticsEx");
                    var init = (delegate* unmanaged[Stdcall]<char*, uint, char*, char*, Guid, char*, int>)export;

                    fixed (char* endpoint = DiagnosticsEndpoint)
                    fixed (char* udkPath = frameworkUdkPath)
                    fixed (char* tapPath = tapDllPath)
                    fixed (char* initData = initializationData)
                    {
                        return init(endpoint, targetPid, udkPath, tapPath, TapClsid, initData);
                    }
                }
                finally
                {
                    NativeLibrary.Free(udk);
                }
            });
        }
        finally
        {
            CoUninitialize();
        }
    }

    internal static int WithAuthenticatedUdk(string path, Func<string, uint, int> loadAndInvoke)
    {
        using var snapshot = FrameworkUdkSnapshot.Create(path);
        return loadAndInvoke(snapshot.Path, System32Search);
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetDefaultDllDirectories(uint directoryFlags);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibraryExW(string fileName, nint file, uint flags);
}
