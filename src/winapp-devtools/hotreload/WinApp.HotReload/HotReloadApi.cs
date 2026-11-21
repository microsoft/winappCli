// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;

namespace WinApp.HotReload;

internal sealed record ModuleUpdate(Guid ModuleId, byte[] Metadata, byte[] IL, byte[] Pdb, int[] UpdatedTypes);

/// <param name="RudeEdits">Edits the running app can't take; each has an ENC diagnostic ID.</param>
internal sealed record ApiUpdates(IReadOnlyList<Diagnostic> Errors, IReadOnlyList<Diagnostic> RudeEdits, IReadOnlyList<ModuleUpdate> Updates, bool NeedsRestart);

/// <summary>
/// The only code that touches the .NET SDK's private hot reload API: <c>HotReloadService</c> in
/// <c>Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll</c>, the engine behind <c>dotnet watch</c>. It's loaded from
/// the SDK that builds the app, with that SDK's compiler, so analyzers and generators match the build. Every member is
/// bound up front, so an SDK that changed the API fails at start with a reason instead of in the middle of a save.
/// </summary>
internal sealed class HotReloadApi
{
    private const string ServiceTypeName = "Microsoft.CodeAnalysis.ExternalAccess.HotReload.Api.HotReloadService, Microsoft.CodeAnalysis.ExternalAccess.HotReload";

    /// <summary>The Roslyn assemblies the workspace needs for edit and continue (MEF parts).</summary>
    public static readonly string[] FeatureAssemblies =
        ["Microsoft.CodeAnalysis.Features", "Microsoft.CodeAnalysis.CSharp.Features", "Microsoft.CodeAnalysis.ExternalAccess.HotReload"];

    /// <summary>The SDK the project builds with (global.json applies): <c>dotnet --version</c> in its folder.</summary>
    public static (string Directory, string Version) FindSdk(string projectDirectory)
    {
        var dotnet = Environment.ProcessPath!;
        var start = new ProcessStartInfo(dotnet, "--version")
        {
            WorkingDirectory = projectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        var version = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return (Path.Combine(Path.GetDirectoryName(dotnet)!, "sdk", version), version);
    }

    /// <summary>Makes the SDK's compiler and hot reload service loadable; returns why not when this SDK lacks them.</summary>
    public static string? Register(string sdkDirectory, string sdkVersion)
    {
        var bincore = Path.Combine(sdkDirectory, "Roslyn", "bincore");
        var watchRoot = Path.Combine(sdkDirectory, "DotnetTools", "dotnet-watch");
        var service = Directory.Exists(watchRoot)
            ? Directory.EnumerateFiles(watchRoot, "Microsoft.CodeAnalysis.ExternalAccess.HotReload.dll", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (!File.Exists(Path.Combine(bincore, "Microsoft.CodeAnalysis.dll")) || service is null)
        {
            return Unsupported(sdkVersion, "it has no dotnet-watch hot reload service; .NET SDK 10.0.100 or later has one");
        }
        string[] directories = [bincore, Path.GetDirectoryName(service)!];
        AssemblyLoadContext.Default.Resolving += (context, name) =>
            directories.Select(d => Path.Combine(d, name.Name + ".dll")).FirstOrDefault(File.Exists) is { } path ? context.LoadFromAssemblyPath(path) : null;
        return null;
    }

    public static string Unsupported(string sdkVersion, string why) =>
        $"C# hot reload isn't supported on .NET SDK {sdkVersion} yet ({why}), so C# saves restart the app";

    private readonly object _service;
    private readonly object _runningProjects;
    private readonly MethodInfo _start, _getUpdates, _capabilitiesChanged, _commit, _discard;
    private readonly PropertyInfo _persistent, _transient, _projectUpdates, _toRestart, _toRebuild;
    private readonly FieldInfo _moduleId, _metadata, _il, _pdb, _updatedTypes;

    /// <summary>Binds the API. Throws <see cref="MissingMemberException"/> naming what this SDK changed.</summary>
    public HotReloadApi(Solution solution, IEnumerable<ProjectId> runningProjects, Func<ImmutableArray<string>> capabilities)
    {
        var type = Type.GetType(ServiceTypeName, throwOnError: false) ?? throw new MissingMemberException("HotReloadService");
        _start = Method(type, "StartSessionAsync");
        _getUpdates = Method(type, "GetUpdatesAsync");
        _capabilitiesChanged = Method(type, "CapabilitiesChanged");
        _commit = Method(type, "CommitUpdate");
        _discard = Method(type, "DiscardUpdate");
        var updatesType = (typeof(Task).IsAssignableFrom(_getUpdates.ReturnType) ? _getUpdates.ReturnType.GetGenericArguments().SingleOrDefault() : null)
            ?? throw new MissingMemberException("HotReloadService.GetUpdatesAsync returning Task<Updates>");
        _persistent = Property(updatesType, "PersistentDiagnostics");
        _transient = Property(updatesType, "TransientDiagnostics");
        _projectUpdates = Property(updatesType, "ProjectUpdates");
        _toRestart = Property(updatesType, "ProjectsToRestart");
        _toRebuild = Property(updatesType, "ProjectsToRebuild");
        var updateType = _projectUpdates.PropertyType.GetGenericArguments().SingleOrDefault() ?? throw new MissingMemberException("HotReloadService.Update");
        _moduleId = Field(updateType, "ModuleId");
        _metadata = Field(updateType, "MetadataDelta");
        _il = Field(updateType, "ILDelta");
        _pdb = Field(updateType, "PdbDelta");
        _updatedTypes = Field(updateType, "UpdatedTypes");

        Func<ValueTask<ImmutableArray<string>>> provider = () => ValueTask.FromResult(capabilities());
        _service = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, [solution.Services, provider], null)
            ?? throw new MissingMemberException("HotReloadService(SolutionServices, Func<ValueTask<ImmutableArray<string>>>)");
        var infoType = type.GetNestedType("RunningProjectInfo") ?? throw new MissingMemberException("HotReloadService.RunningProjectInfo");
        var pairType = typeof(KeyValuePair<,>).MakeGenericType(typeof(ProjectId), infoType);
        var pairs = runningProjects.Select(id => Activator.CreateInstance(pairType, id, Activator.CreateInstance(infoType))).ToArray();
        var array = Array.CreateInstance(pairType, pairs.Length);
        pairs.CopyTo(array, 0);
        _runningProjects = typeof(ImmutableDictionary).GetMethods()
            .First(m => m.Name == nameof(ImmutableDictionary.CreateRange) && m.GetParameters().Length == 1)
            .MakeGenericMethod(typeof(ProjectId), infoType).Invoke(null, [array])!;
    }

    public Task StartAsync(Solution solution, CancellationToken cancellationToken) =>
        (Task)_start.Invoke(_service, [solution, cancellationToken])!;

    public async Task<ApiUpdates> GetUpdatesAsync(Solution solution, CancellationToken cancellationToken)
    {
        var task = (Task)_getUpdates.Invoke(_service, [solution, _runningProjects, cancellationToken])!;
        await task.ConfigureAwait(false);
        var updates = task.GetType().GetProperty("Result")!.GetValue(task)!;
        var persistent = (ImmutableArray<Diagnostic>)_persistent.GetValue(updates)!;
        var errors = persistent.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        var rude = new List<Diagnostic>();
        foreach (var entry in (IEnumerable)_transient.GetValue(updates)!)
        {
            var diagnostics = (ImmutableArray<Diagnostic>)entry.GetType().GetField("Item2")!.GetValue(entry)!;
            rude.AddRange(diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error));
            persistent = persistent.AddRange(diagnostics.Where(d => d.Severity != DiagnosticSeverity.Error));
        }
        // Warnings (ENC1005: a document out of sync with the build) explain an update that skips a change; winapp
        // shows the helper's diagnostic output with --verbose.
        foreach (var warning in persistent.Where(d => d.Severity == DiagnosticSeverity.Warning))
        {
            Console.Error.WriteLine($"hot reload warning: {warning}");
        }
        var modules = new List<ModuleUpdate>();
        foreach (var update in (IEnumerable)_projectUpdates.GetValue(updates)!)
        {
            byte[] Bytes(FieldInfo field) => [.. (ImmutableArray<byte>)field.GetValue(update)!];
            modules.Add(new ModuleUpdate((Guid)_moduleId.GetValue(update)!, Bytes(_metadata), Bytes(_il), Bytes(_pdb),
                [.. (ImmutableArray<int>)_updatedTypes.GetValue(update)!]));
        }
        var restart = ((ICollection)_toRestart.GetValue(updates)!).Count > 0 || ((ICollection)_toRebuild.GetValue(updates)!).Count > 0;
        return new ApiUpdates(errors, rude, modules, restart);
    }

    public void CapabilitiesChanged() => _capabilitiesChanged.Invoke(_service, null);

    /// <summary>The app took the last update: the next one is computed against it.</summary>
    public void Commit() => Invoke(_commit);

    /// <summary>The last update didn't reach the app: the next one is computed against the one before.</summary>
    public void Discard() => Invoke(_discard);

    private void Invoke(MethodInfo method)
    {
        try
        {
            method.Invoke(_service, null);
        }
        catch (TargetInvocationException)
        {
            // No update pending: nothing to commit or discard.
        }
    }

    private static MethodInfo Method(Type type, string name) =>
        type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public) ?? throw new MissingMemberException(type.Name, name);

    private static PropertyInfo Property(Type type, string name) =>
        type.GetProperty(name) ?? throw new MissingMemberException(type.Name, name);

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name) ?? throw new MissingMemberException(type.Name, name);
}
