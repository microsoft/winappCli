// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using WinApp.HotReload.Protocol;

[assembly: MetadataUpdateHandler(typeof(WinApp.DevTools.Managed.HotReload.UpdateLanded))]

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// The app's end of 'winapp run --watch' hot reload: serves the helper's pipe (named by the launch environment) and
/// hands C# updates to <see cref="DeltaApplier"/>. The XAML layer adds its requests here.
/// </summary>
internal static class Agent
{
    public static void Start()
    {
        var pipeName = Environment.GetEnvironmentVariable(HotReloadFrames.PipeVariable);
        var token = Environment.GetEnvironmentVariable(HotReloadFrames.TokenVariable);
        if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(token))
        {
            return;
        }
        // A package or project added while the app runs is copied next to it, but isn't in its startup dependency
        // list, so .NET wouldn't find it when updated code first uses it.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
            Path.Combine(AppContext.BaseDirectory, name.Name + ".dll") is var path && File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        _ = Task.Run(() => ServeAsync(pipeName, token));
    }

    private static async Task ServeAsync(string pipeName, string token)
    {
        while (true)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync().ConfigureAwait(false);
                while (await HotReloadFrames.ReadAsync<HotReloadRequest>(pipe, CancellationToken.None).ConfigureAwait(false) is { } request)
                {
                    var response = request.Token == token ? await HandleAsync(request).ConfigureAwait(false) : new HotReloadResponse { Error = "invalid hot reload token" };
                    await HotReloadFrames.WriteAsync(pipe, response, CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.TraceError("Hot reload connection failed: {0}", ex);
                await Task.Delay(200).ConfigureAwait(false);
            }
        }
    }

    private static async Task<HotReloadResponse> HandleAsync(HotReloadRequest request)
    {
        try
        {
            if (request.Op == "hello")
            {
                // XAML hot reload starts following the app now, so what the user changes before the first save counts.
                _ = XamlService.AcquireAsync();
            }
            return request.Op switch
            {
                "hello" => new HotReloadResponse
                {
                    Ok = true,
                    Capabilities = DeltaApplier.Capabilities,
                    BaseDirectory = AppContext.BaseDirectory,
                    ProcessId = Environment.ProcessId,
                },
                "apply" => new HotReloadResponse { Ok = true, UpdatedTypes = [.. DeltaApplier.Apply(request.Deltas).Select(t => t.FullName ?? t.Name)] },
                "reload" => await ReloadAsync(request.Markup).ConfigureAwait(false),
                _ => new HotReloadResponse { Error = $"unknown operation '{request.Op}'" },
            };
        }
        catch (Exception ex)
        {
            return new HotReloadResponse { Error = ex.GetBaseException().Message };
        }
    }

    /// <summary>Hands WinUI the compiled markup, then rebuilds what the save changed, on the app's UI thread.</summary>
    private static async Task<HotReloadResponse> ReloadAsync(List<MarkupPayload> markup)
    {
        var service = await XamlService.AcquireAsync().ConfigureAwait(false);
        if (service is null)
        {
            return new HotReloadResponse { Ok = true, Restart = "this app's WinUI can't load XAML while it runs" };
        }
        var dispatcher = Rebuild.Dispatcher ?? throw new InvalidOperationException("the app has no WinUI window yet");
        var done = new TaskCompletionSource<HotReloadResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!dispatcher.TryEnqueue(async () =>
        {
            try
            {
                done.SetResult(await Rebuild.RunAsync(service, markup));
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        }))
        {
            throw new InvalidOperationException("the app's UI thread is shutting down");
        }
        return await done.Task.ConfigureAwait(false);
    }
}

/// <summary>
/// The "update landed" signal: .NET calls this handler after any engine applies a C# update, <c>dotnet watch</c>'s
/// agent as well as <see cref="DeltaApplier"/>. The next reload rebuilds what the changed types own.
/// </summary>
internal static class UpdateLanded
{
    public static void UpdateApplication(Type[]? types) => Rebuild.Landed(types ?? []);
}

/// <summary>
/// WinUI's hot reload runtime hook (IXamlHotReloadService in xamlom.winui.h), offered only on the XAML diagnostics
/// site of a WinUI that has it. The DevTools tap receives that site and hands the service out
/// (DevToolsTap_GetXamlHotReloadService).
/// </summary>
internal sealed class XamlService
{
    /// <summary>E_XAMLHOTRELOAD_NOTENABLED: the app isn't stamped for hot reload (not a Debug build).</summary>
    private const int NotEnabled = unchecked((int)0x802B0029);
    private const int NotReady = unchecked((int)0x80070015);
    private static readonly Guid CallbackIid = new("7AF94B54-7C75-42C3-B717-44F25C491A27");
    private static Task<XamlService?>? s_acquire;

    private readonly IntPtr _service;

    private XamlService(IntPtr service) => _service = service;

    /// <summary>The service once DevTools has attached (up to 30 s), or null on a WinUI without it or an app not enabled for it.</summary>
    public static Task<XamlService?> AcquireAsync() => s_acquire ??= Task.Run(async () =>
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var hr = TryCreate(out var service);
            if (service is not null)
            {
                await Rebuild.StartAsync(service).ConfigureAwait(false);
                return service;
            }
            if (hr != NotReady || DateTime.UtcNow > deadline)
            {
                return null;
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
    });

    private static int TryCreate(out XamlService? service)
    {
        service = null;
        var hr = TryGet(out var pointer);
        if (hr < 0)
        {
            return hr;
        }
        var created = new XamlService(pointer);
        // Every method refuses an app without the hot reload stamp.
        hr = created.InvalidateTypeCaches();
        service = hr == NotEnabled ? null : created;
        return hr;
    }

    private static unsafe int TryGet(out IntPtr service)
    {
        service = IntPtr.Zero;
        var module = GetModuleHandleW("WinApp.DevTools.Native.dll");
        if (module == IntPtr.Zero || !NativeLibrary.TryGetExport(module, "DevToolsTap_GetXamlHotReloadService", out var export))
        {
            // The tap attaches right after launch.
            return NotReady;
        }
        IntPtr result;
        var hr = ((delegate* unmanaged[Stdcall]<IntPtr*, int>)export)(&result);
        service = result;
        return hr;
    }

    private unsafe void** Vtbl => *(void***)_service;

    /// <summary>The app loads <paramref name="xbf"/> for <paramref name="uri"/> from now on (the runtime copies it).</summary>
    public unsafe int SetComponentXbf(string uri, byte[] xbf)
    {
        fixed (char* u = uri)
        fixed (byte* content = xbf)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, char*, uint, byte*, int>)Vtbl[3])(_service, u, (uint)xbf.Length, content);
        }
    }

    /// <summary>The app's types changed: the runtime reads them again the next time markup uses them, so new members are found.</summary>
    public unsafe int InvalidateTypeCaches() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl[6])(_service);

    private static Action<object>? s_loaded;

    /// <summary>Calls <paramref name="loaded"/> with every component (page, UserControl, window) as its markup loads.</summary>
    public unsafe void AdviseComponentLoaded(Action<object> loaded)
    {
        s_loaded = loaded;
        uint cookie;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Vtbl[4])(_service, Callback, &cookie));
    }

    /// <summary>An IXamlComponentLoadedCallback that lives as long as the process (its reference count is ignored).</summary>
    private static readonly IntPtr Callback = CreateCallback();

    private static unsafe IntPtr CreateCallback()
    {
        var vtbl = (IntPtr*)NativeMemory.Alloc((nuint)(4 * sizeof(IntPtr)));
        vtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
        vtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
        vtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
        vtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, char*, int>)&OnComponentLoaded;
        var instance = (IntPtr*)NativeMemory.Alloc((nuint)sizeof(IntPtr));
        instance[0] = (IntPtr)vtbl;
        return (IntPtr)instance;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int QueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        if (*iid == CallbackIid || *iid == new Guid("00000000-0000-0000-C000-000000000046"))
        {
            *result = self;
            return 0;
        }
        *result = IntPtr.Zero;
        return unchecked((int)0x80004002);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(IntPtr self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int OnComponentLoaded(IntPtr self, IntPtr component, char* uri)
    {
        try
        {
            if (s_loaded is { } loaded && WinRT.MarshalInspectable<object>.FromAbi(component) is { } instance)
            {
                loaded(instance);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Trace.TraceWarning("Hot reload component callback failed: {0}", ex.Message);
        }
        return 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);
}
