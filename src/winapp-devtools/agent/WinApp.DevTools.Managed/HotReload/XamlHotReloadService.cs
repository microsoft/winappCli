// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// WinUI's IXamlHotReloadService (xamlom.winui.h), which the runtime offers only on the XAML diagnostics site. The
/// DevTools tap receives that site and hands the service out (DevToolsTap_GetXamlHotReloadService).
/// </summary>
internal sealed class XamlHotReloadService
{
    /// <summary>E_XAMLHOTRELOAD_NOTENABLED: the app isn't stamped for hot reload (not a Debug build).</summary>
    public const int NotEnabled = unchecked((int)0x802B0029);

    public const string NotEnabledText =
        "XAML hot reload is not enabled for this app. Build it in the Debug configuration, or set the EnableXamlHotReload project property to true (for example -p EnableXamlHotReload=true).";

    private const int NotReady = unchecked((int)0x80070015);
    private const int NoInterface = unchecked((int)0x80004002);
    private static readonly Guid IUnknownIid = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid CallbackIid = new("BF866A3C-A025-4CA8-AB23-AFC59DA45B11");

    private readonly IntPtr _service;

    private XamlHotReloadService(IntPtr service) => _service = service;

    /// <summary>
    /// The service, or why there is none: a reason when the app isn't enabled for it, null (no service, no reason)
    /// when this WinUI doesn't offer it.
    /// </summary>
    public static async Task<(XamlHotReloadService? Service, string? Unavailable)> AcquireAsync()
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (true)
        {
            var hr = TryGet(out var pointer);
            if (hr >= 0)
            {
                var service = new XamlHotReloadService(pointer);
                // Every method refuses an app without the hot reload stamp; this one has no other effect worth noting.
                if (service.InvalidateTypeCaches() == NotEnabled)
                {
                    Marshal.Release(pointer);
                    return (null, NotEnabledText);
                }
                return (service, null);
            }
            if (hr != NotReady || DateTime.UtcNow > deadline)
            {
                return (null, null);
            }
            await Task.Delay(100).ConfigureAwait(false);
        }
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

    /// <summary>Markup for a URI, copied by the runtime: XAML text, or a compiled XBF (binary).</summary>
    public unsafe int SetComponentMarkup(string uri, bool binary, byte[] content)
    {
        fixed (char* u = uri)
        fixed (byte* c = content)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, char*, int, uint, byte*, int>)Vtbl[3])(_service, u, binary ? 1 : 0, (uint)content.Length, c);
        }
    }

    /// <summary>Drops the markup set for a URI, or for every URI when null.</summary>
    public unsafe int RemoveComponentMarkup(string? uri)
    {
        fixed (char* u = uri)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, char*, int>)Vtbl[4])(_service, u);
        }
    }

    public unsafe int RegisterMetadataProvider(IntPtr provider) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Vtbl[7])(_service, provider);

    public unsafe int InvalidateTypeCaches() => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Vtbl[8])(_service);

    private static readonly Guid Service3Iid = new("85B7546C-46F3-49C0-8AAD-D866BC9E86FC");
    private IntPtr _service3 = IntPtr.MinValue;

    /// <summary>IXamlHotReloadService3, from a WinUI that can forget what it read about a type (zero when it can't).</summary>
    private IntPtr Service3
    {
        get
        {
            if (_service3 == IntPtr.MinValue)
            {
                var iid = Service3Iid;
                _service3 = Marshal.QueryInterface(_service, ref iid, out var v3) >= 0 ? v3 : IntPtr.Zero;
            }
            return _service3;
        }
    }

    /// <summary>Whether <see cref="InvalidateTypes"/> is available.</summary>
    public bool CanInvalidateTypes => Service3 != IntPtr.Zero;

    /// <summary>
    /// Has the runtime read these types again (by full name) the next time markup uses them: members an update added
    /// (a new dependency property) are then found. Returns how many it knew.
    /// </summary>
    public unsafe int InvalidateTypes(IReadOnlyList<string> fullNames, out uint marked)
    {
        marked = 0;
        if (Service3 == IntPtr.Zero || fullNames.Count == 0)
        {
            return NoInterface;
        }
        var handles = fullNames.Select(n => GCHandle.Alloc(n, GCHandleType.Pinned)).ToArray();
        try
        {
            var names = stackalloc IntPtr[handles.Length];
            for (var i = 0; i < handles.Length; i++)
            {
                names[i] = handles[i].AddrOfPinnedObject();
            }
            uint count;
            var hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, uint*, int>)(*(void***)Service3)[11])(Service3, (uint)handles.Length, names, &count);
            marked = count;
            return hr;
        }
        finally
        {
            foreach (var handle in handles)
            {
                handle.Free();
            }
        }
    }

    private static Action<IntPtr, string>? s_loaded;

    /// <summary>Calls <paramref name="loaded"/> after every component load (component ABI pointer, markup URI), on the loading thread.</summary>
    public unsafe void AdviseComponentLoaded(Action<IntPtr, string> loaded)
    {
        s_loaded = loaded;
        uint advised;
        Marshal.ThrowExceptionForHR(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint*, int>)Vtbl[5])(_service, Callback, &advised));
        var cookie = advised;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Unadvise(cookie);
    }

    private unsafe void Unadvise(uint cookie) => _ = ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Vtbl[6])(_service, cookie);

    /// <summary>An IXamlComponentLoadedCallback that lives as long as the process (its reference count is ignored).</summary>
    private static readonly IntPtr Callback = CreateCallback();

    private static unsafe IntPtr CreateCallback()
    {
        var vtbl = (IntPtr*)NativeMemory.Alloc((nuint)(4 * sizeof(IntPtr)));
        vtbl[0] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&QueryInterface;
        vtbl[1] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&AddRef;
        vtbl[2] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint>)&Release;
        vtbl[3] = (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, char*, int>)&OnComponentLoaded;
        var instance = (IntPtr*)NativeMemory.Alloc((nuint)sizeof(IntPtr));
        instance[0] = (IntPtr)vtbl;
        return (IntPtr)instance;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int QueryInterface(IntPtr self, Guid* iid, IntPtr* result)
    {
        if (*iid == IUnknownIid || *iid == CallbackIid)
        {
            *result = self;
            return 0;
        }
        *result = IntPtr.Zero;
        return NoInterface;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(IntPtr self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(IntPtr self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static unsafe int OnComponentLoaded(IntPtr self, IntPtr component, char* uri)
    {
        s_loaded?.Invoke(component, new string(uri));
        return 0;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);
}
