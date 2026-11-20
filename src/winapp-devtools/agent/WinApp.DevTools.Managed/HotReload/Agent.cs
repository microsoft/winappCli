// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Pipes;
using System.Reflection.Metadata;
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
                    var response = request.Token == token ? Handle(request) : new HotReloadResponse { Error = "invalid hot reload token" };
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

    private static HotReloadResponse Handle(HotReloadRequest request)
    {
        try
        {
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
                _ => new HotReloadResponse { Error = $"unknown operation '{request.Op}'" },
            };
        }
        catch (Exception ex)
        {
            return new HotReloadResponse { Error = ex.GetBaseException().Message };
        }
    }
}

/// <summary>
/// The "update landed" signal: .NET calls this handler after any engine applies a C# update, <c>dotnet watch</c>'s
/// agent as well as <see cref="DeltaApplier"/>. The XAML layer rebuilds what the changed types own from here.
/// </summary>
internal static class UpdateLanded
{
    public static event Action<Type[]>? Landed;

    public static void UpdateApplication(Type[]? types) => Landed?.Invoke(types ?? []);
}
