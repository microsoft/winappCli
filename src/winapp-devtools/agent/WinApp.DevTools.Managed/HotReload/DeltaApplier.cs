// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Reflection.Metadata;
using WinApp.HotReload.Protocol;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// Applies engine B's updates in the app, by module ID, then runs the app's [MetadataUpdateHandler]s (which clear
/// .NET's reflection caches and raise <see cref="UpdateLanded"/>). An update for an assembly the app hasn't loaded
/// yet is held, and applied when it loads.
/// </summary>
internal static class DeltaApplier
{
    private static readonly List<DeltaPayload> s_held = [];
    private static bool s_listening;

    /// <summary>What this runtime can apply (edit-and-continue capabilities); the API that tells is internal.</summary>
    public static string Capabilities =>
        typeof(MetadataUpdater).GetMethod("GetCapabilities", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null) as string ?? "Baseline";

    /// <summary>Applies the updates in order; returns the types they changed.</summary>
    public static IReadOnlyList<Type> Apply(IEnumerable<DeltaPayload> deltas)
    {
        var types = new List<Type>();
        lock (s_held)
        {
            foreach (var delta in deltas)
            {
                var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.ManifestModule.ModuleVersionId == delta.ModuleId);
                // An earlier update to the same assembly still held: this one waits behind it.
                if (assembly is null || s_held.Any(h => h.ModuleId == delta.ModuleId))
                {
                    Hold(delta);
                    continue;
                }
                types.AddRange(ApplyOne(assembly, delta));
            }
        }
        RunHandlers(types);
        return types;
    }

    private static List<Type> ApplyOne(Assembly assembly, DeltaPayload delta)
    {
        MetadataUpdater.ApplyUpdate(assembly, delta.Metadata, delta.IL, delta.Pdb);
        var types = new List<Type>();
        foreach (var token in delta.UpdatedTypes)
        {
            try
            {
                types.Add(assembly.ManifestModule.ResolveType(token));
            }
            catch (ArgumentException)
            {
                // A type the update added and removed again.
            }
        }
        return types;
    }

    private static void Hold(DeltaPayload delta)
    {
        s_held.Add(delta);
        if (!s_listening)
        {
            s_listening = true;
            AppDomain.CurrentDomain.AssemblyLoad += (_, e) => ApplyHeld(e.LoadedAssembly);
        }
    }

    private static void ApplyHeld(Assembly assembly)
    {
        var types = new List<Type>();
        lock (s_held)
        {
            var id = assembly.IsDynamic ? Guid.Empty : assembly.ManifestModule.ModuleVersionId;
            foreach (var delta in s_held.Where(h => h.ModuleId == id).ToList())
            {
                s_held.Remove(delta);
                types.AddRange(ApplyOne(assembly, delta));
            }
        }
        if (types.Count > 0)
        {
            RunHandlers(types);
        }
    }

    /// <summary>Calls ClearCache, then UpdateApplication, on every [MetadataUpdateHandler] loaded, as .NET's own agent does.</summary>
    private static void RunHandlers(List<Type> types)
    {
        var handlers = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => a.GetCustomAttributes<MetadataUpdateHandlerAttribute>())
            .Select(a => a.HandlerType)
            .Distinct()
            .ToList();
        foreach (var name in new[] { "ClearCache", "UpdateApplication" })
        {
            foreach (var handler in handlers)
            {
                try
                {
                    handler.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Type[])])?.Invoke(null, [types.ToArray()]);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning("Hot reload handler {0}.{1} failed: {2}", handler, name, ex);
                }
            }
        }
    }
}
