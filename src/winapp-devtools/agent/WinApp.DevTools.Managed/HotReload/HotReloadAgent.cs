// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.Loader;
using WinApp.HotReload.Protocol;

namespace WinApp.DevTools.Managed.HotReload;

/// <summary>
/// In-app half of 'winapp run --watch': receives deltas and markup from the hot reload helper and applies them
/// on the UI thread. Only started when the CLI launched the app with a hot reload pipe name.
/// </summary>
internal static class HotReloadAgent
{
    private static readonly string? LogPath = Environment.GetEnvironmentVariable("WINAPP_HOT_RELOAD_LOG");

    internal static void Log(string message)
    {
        if (LogPath is not null)
        {
            lock (LogPath)
            {
                File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
    }

    public static void Start()
    {
        var pipeName = Environment.GetEnvironmentVariable(HotReloadFrames.PipeEnvironmentVariable);
        var token = Environment.GetEnvironmentVariable(HotReloadFrames.TokenEnvironmentVariable);
        if (string.IsNullOrEmpty(pipeName) || string.IsNullOrEmpty(token))
        {
            return;
        }
        // Assemblies added by a project edit (for example a new package) are deployed next to the app but are not
        // in its startup dependency list.
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            var path = Path.Combine(AppContext.BaseDirectory, name.Name + ".dll");
            return File.Exists(path) ? context.LoadFromAssemblyPath(path) : null;
        };
        if (LogPath is not null)
        {
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => Log($"first-chance {e.Exception.GetType().Name}: {e.Exception.Message}{Environment.NewLine}{e.Exception.StackTrace}");
        }
        // winapp shows why the app went down: the exit code alone says little.
        // Next to the helper's folder rather than in it: the helper removes its folder as soon as the app is gone.
        CrashFile = Path.Combine(Path.GetTempPath(), "winapp-hotreload", pipeName + ".crash.txt");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => RecordCrash(e.ExceptionObject as Exception);
        // An exception escaping a DispatcherQueue callback (an async void handler resuming, TryEnqueue) ends the app
        // with a fail-fast that no managed event reports, so the last one thrown on the UI thread is kept for winapp.
        var lastException = Path.ChangeExtension(CrashFile, ".last.txt");
        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (Environment.CurrentManagedThreadId == UiThreadId && !s_recording)
            {
                // Thrown just now, so its own trace holds at most the frame that threw (none when that is in WinRT's
                // marshaling, as for a native E_POINTER): the frames that called it are still on this thread's stack.
                RecordCrash(e.Exception, lastException, new System.Diagnostics.StackTrace(1, fNeedFileInfo: true).ToString());
            }
        };
        _ = Task.Run(() => ServeAsync(pipeName, token));
    }

    private static string? CrashFile { get; set; }

    /// <summary>The app's UI thread, once the XAML side of the agent started on it.</summary>
    internal static int UiThreadId { get; set; } = -1;

    [ThreadStatic]
    private static bool s_recording;

    internal static void RecordCrash(Exception? exception, string? file = null, string? callers = null)
    {
        file ??= CrashFile;
        if (file is null || exception is null)
        {
            return;
        }
        s_recording = true;
        try
        {
            var own = (exception.StackTrace ?? "").Split('\n').Select(l => l.Trim()).ToList();
            var frames = own.Concat((callers ?? "").Split('\n').Select(l => l.Trim()).Where(l => !own.Contains(l)))
                .Where(l => l.Length > 0 && !l.StartsWith("at WinRT.", StringComparison.Ordinal) && !l.StartsWith("at ABI.", StringComparison.Ordinal) &&
                    !l.StartsWith("at System.Runtime.", StringComparison.Ordinal))
                // Methods updated while the app runs carry the update's generation (b__4#3_0#3): name them as written.
                .Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"#\d+", "")).Take(8).ToList();
            File.WriteAllText(file, $"{exception.GetType().FullName}: {exception.Message}{Environment.NewLine}{(frames.Count > 0 ? string.Join(Environment.NewLine, frames) : "(no managed frames: it was thrown in native code)")}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            s_recording = false;
        }
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
                    var response = request.Token == token
                        ? await HandleAsync(request).ConfigureAwait(false)
                        : new HotReloadResponse { Error = "invalid hot reload token" };
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
            XamlReload.Compiler = request.Pipeline == "compiler";
            if (PageUpdateStrategies.Get(request.PageUpdate) is null)
            {
                return new HotReloadResponse { Error = $"unknown page update strategy '{request.PageUpdate}' (WINAPP_HOT_RELOAD_PAGE_UPDATE): use {string.Join(", ", PageUpdateStrategies.Names.Order())}" };
            }
            return request.Op switch
            {
                "hello" => await XamlReload.InitializeAsync().ConfigureAwait(false) is { } error
                    ? new HotReloadResponse { Error = error }
                    : new HotReloadResponse { Ok = true, Capabilities = Capabilities(), BaseDirectory = AppContext.BaseDirectory, XamlReload = XamlReload.Supported, XamlReloadUnavailable = XamlReload.Unavailable },
                "apply" => await ApplyAsync(request).ConfigureAwait(false),
                _ => new HotReloadResponse { Error = $"unknown operation '{request.Op}'" },
            };
        }
        catch (Exception ex)
        {
            return new HotReloadResponse { Error = ex.GetBaseException().Message };
        }
    }

    /// <summary>The update on the UI thread, then (a separate callback, as on their own) x:Bind of what it reloaded in place.</summary>
    private static async Task<HotReloadResponse> ApplyAsync(HotReloadRequest request)
    {
        HotReloadResponse response = new();
        try
        {
            response = await XamlReload.OnUiThreadAsync(() => Apply(request)).ConfigureAwait(false);
        }
        finally
        {
            // Always drained, so evaluations a later load defers aren't left waiting for an update that never runs them.
            await XamlReload.OnUiThreadAsync(() =>
            {
                XamlReload.RestoreDetachedLists();
                XamlReload.ApplyDeferredBindings(response.Messages, response.Errors, response.Notes);
                return true;
            }).ConfigureAwait(false);
            await XamlReload.RunSettledAsync(response.Errors, response.Notes).ConfigureAwait(false);
        }
        return response;
    }

    private static string Capabilities() =>
        typeof(MetadataUpdater).GetMethod("GetCapabilities", BindingFlags.NonPublic | BindingFlags.Static)?.Invoke(null, null) as string ?? "Baseline";

    private static HotReloadResponse Apply(HotReloadRequest request)
    {
        var response = new HotReloadResponse { Ok = true };
        var updatedTypes = new List<Type>();
        XamlReload.StopTracking([.. request.RefreshTypes, .. request.RecreateTypes]);
        foreach (var delta in request.Deltas)
        {
            var assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => !a.IsDynamic && a.ManifestModule.ModuleVersionId == delta.ModuleId);
            if (assembly is null)
            {
                return new HotReloadResponse { Error = $"module {delta.ModuleId} is not loaded" };
            }
            MetadataUpdater.ApplyUpdate(assembly, delta.Metadata, delta.IL, delta.Pdb);
            foreach (var token in delta.UpdatedTypes)
            {
                try
                {
                    if (assembly.ManifestModule.ResolveType(token) is { } type)
                    {
                        updatedTypes.Add(type);
                    }
                }
                catch (ArgumentException)
                {
                }
            }
        }
        if (request.Deltas.Count > 0)
        {
            XamlReload.ResetTypeInfo();
            InvokeUpdateHandlers([.. updatedTypes]);
        }
        foreach (var initializer in request.Initializers)
        {
            // Static fields added to a type that already ran its static constructor (e.g. a new DependencyProperty).
            var (typeName, method) = (initializer[..initializer.IndexOf('|')], initializer[(initializer.IndexOf('|') + 1)..]);
            try
            {
                XamlReload.FindType(typeName)?.GetMethod(method, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.Invoke(null, null);
            }
            catch (TargetInvocationException ex)
            {
                response.Messages.Add($"{typeName}: initializing a new static field failed: {ex.GetBaseException().Message}");
            }
        }
        XamlReload.Apply(request, response.Messages, response.Errors, response.Notes, response.Restarts);
        return response;
    }

    /// <summary>Runs [MetadataUpdateHandler] callbacks, which among other things clear the runtime's reflection caches.</summary>
    private static void InvokeUpdateHandlers(Type[] types)
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
                    handler.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static, [typeof(Type[])])?.Invoke(null, [types]);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.TraceWarning("Hot reload handler {0}.{1} failed: {2}", handler, name, ex);
                }
            }
        }
    }
}
