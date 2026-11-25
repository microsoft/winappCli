// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.HotReload;

/// <summary>
/// What 'winapp run --watch' adds to a WinUI XAML app's build so its hot reload helper (engine B) can compile exactly
/// what the build compiled: MSBuild targets that record the app's compiler arguments, its referenced projects and its
/// sources as compiled, in a folder that lives as long as the watch.
/// </summary>
internal sealed class HotReloadCapture : IDisposable
{
    // CoreCompile is forced (a missing input) so every build reports the compiler arguments of the app project only.
    internal const string Targets = """
        <Project>
          <PropertyGroup Condition="'$(WinAppHotReloadProject)' == '$(MSBuildProjectFullPath)'">
            <ProvideCommandLineArgs>true</ProvideCommandLineArgs>
          </PropertyGroup>
          <ItemGroup Condition="'$(WinAppHotReloadProject)' == '$(MSBuildProjectFullPath)'">
            <CustomAdditionalCompileInputs Include="$(MSBuildThisFileDirectory)force-compile.missing" />
          </ItemGroup>
          <Target Name="WinAppHotReloadSettings">
            <WriteLinesToFile File="$(WinAppHotReloadOut)\settings.txt" Overwrite="true" Lines="DefineConstants=$(DefineConstants.Replace(';', ','));LangVersion=$(LangVersion);Nullable=$(Nullable);AllowUnsafeBlocks=$(AllowUnsafeBlocks);Optimize=$(Optimize);TargetFramework=$(TargetFramework);AssemblyName=$(AssemblyName);RootNamespace=$(RootNamespace);OutputType=$(OutputType);WindowsPackageType=$(WindowsPackageType)" />
          </Target>
          <Target Name="WinAppHotReloadCapture" AfterTargets="CoreCompile" DependsOnTargets="WinAppHotReloadSettings" Condition="'$(WinAppHotReloadProject)' == '$(MSBuildProjectFullPath)'">
            <WriteLinesToFile File="$(WinAppHotReloadOut)\csc.rsp" Lines="@(CscCommandLineArgs)" Overwrite="true" />
            <WriteLinesToFile File="$(WinAppHotReloadOut)\projects.txt" Lines="@(ReferencePath->WithMetadataValue('ReferenceSourceTarget','ProjectReference')->'%(MSBuildSourceProjectFile)')" Overwrite="true" />
            <!-- The sources as compiled, so a save made while the app starts is compared with what runs. -->
            <ItemGroup>
              <_WinAppHotReloadSource Include="@(Compile)" Condition="!$([System.IO.Path]::IsPathRooted('%(Compile.Identity)')) and !$([System.String]::Copy('%(Compile.Identity)').StartsWith('..'))" />
            </ItemGroup>
            <Copy SourceFiles="@(_WinAppHotReloadSource)" DestinationFiles="@(_WinAppHotReloadSource->'$(WinAppHotReloadOut)\src\%(Identity)')" SkipUnchangedFiles="true" />
          </Target>
          <!-- The XAML compiler's inputs, right after the pass that wrote them: an editor's design-time build (VS Code,
               Visual Studio) rewrites obj's input.json for IntelliSense at any time. Only a real pass 2's are kept. -->
          <Target Name="WinAppHotReloadXamlInputs" AfterTargets="MarkupCompilePass2"
                  Condition="'$(WinAppHotReloadProject)' == '$(MSBuildProjectFullPath)' and '$(XamlGeneratedOutputPath)' != '' and Exists('$(XamlGeneratedOutputPath)input.json') and $([System.IO.File]::ReadAllText('$(XamlGeneratedOutputPath)input.json').Contains('RealBuildPass2'))">
            <Copy SourceFiles="$(XamlGeneratedOutputPath)input.json" DestinationFiles="$(WinAppHotReloadOut)\xaml-input.json" />
            <!-- Which build's hot reload state the app runs with: another build that replaces it starts a new session. -->
            <Delete Files="$(WinAppHotReloadOut)\xaml-build.txt" />
            <Copy SourceFiles="$(XamlGeneratedOutputPath)XamlHotReload\build.txt" DestinationFiles="$(WinAppHotReloadOut)\xaml-build.txt" Condition="Exists('$(XamlGeneratedOutputPath)XamlHotReload\build.txt')" />
          </Target>
          <Target Name="WinAppHotReloadReferences" DependsOnTargets="ResolveReferences;FindReferenceAssembliesForReferences;WinAppHotReloadSettings">
            <WriteLinesToFile File="$(WinAppHotReloadOut)\references.txt" Overwrite="true" Lines="@(ReferencePathWithRefAssemblies->'/reference:%(FullPath)');@(Analyzer->'/analyzer:%(FullPath)');@(ReferenceCopyLocalPaths->'/copylocal:%(FullPath)|%(DestinationSubDirectory)')" />
          </Target>
        </Project>
        """;

    private const string HelperFileName = "WinApp.HotReload.dll";

    private HotReloadCapture(FileInfo project, string directory)
    {
        Project = project;
        Directory = directory;
    }

    public FileInfo Project { get; }

    /// <summary>The watch's folder: the targets, what the build records, and the helper's copy of the app's assembly.</summary>
    public string Directory { get; }

    private string TargetsPath => Path.Combine(Directory, "winapp-hotreload.targets");

    /// <summary>Whether the last build recorded the app's compiler arguments.</summary>
    public bool Captured => File.Exists(Path.Combine(Directory, "csc.rsp"));

    /// <summary>
    /// The build properties that import the targets for the app project, and turn on the XAML compiler's hot reload
    /// code (a WinUI without it ignores the property; with it, the code each save compiles fits the running app).
    /// </summary>
    public IReadOnlyList<string> BuildProperties =>
    [
        $"CustomAfterMicrosoftCommonTargets={Commands.RunCommand.Handler.EscapeMSBuildProperty(TargetsPath)}",
        $"WinAppHotReloadProject={Commands.RunCommand.Handler.EscapeMSBuildProperty(Project.FullName)}",
        $"WinAppHotReloadOut={Commands.RunCommand.Handler.EscapeMSBuildProperty(Directory)}",
        "EnableXamlHotReloadCodegen=true",
    ];

    public static HotReloadCapture Create(FileInfo project)
    {
        var directory = Path.Combine(Path.GetTempPath(), "winapp-hotreload", Guid.NewGuid().ToString("N"));
        System.IO.Directory.CreateDirectory(directory);
        var capture = new HotReloadCapture(project, directory);
        File.WriteAllText(capture.TargetsPath, Targets);
        // An old timestamp keeps the import from making the rest of the build (the XAML compiler) out of date.
        File.SetLastWriteTimeUtc(capture.TargetsPath, new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        return capture;
    }

    /// <summary>Whether the project folder has XAML files: only then can the build report XAML items, and engine B run.</summary>
    public static bool MayHaveXaml(FileInfo project) =>
        project.Directory is { } directory && System.IO.Directory.EnumerateFiles(directory.FullName, "*.xaml", SearchOption.AllDirectories)
            .Any(file => !Path.GetRelativePath(directory.FullName, file).Split(Path.DirectorySeparatorChar)
                .Any(part => part.Equals("bin", StringComparison.OrdinalIgnoreCase) || part.Equals("obj", StringComparison.OrdinalIgnoreCase)));

    /// <summary>The arguments of the build that produced the running app, for the helper to restore and read references with.</summary>
    public void WriteBuildArguments(string buildArguments) =>
        File.WriteAllLines(Path.Combine(Directory, "build.args"), WindowsCommandLine.SplitArguments(buildArguments));

    /// <summary>A pipe and token for one app launch, and the environment that lets the app take .NET updates on it.</summary>
    public static (string Pipe, string Token, IReadOnlyDictionary<string, string> Environment) NewSession()
    {
        var pipe = "winapp-hotreload-" + Guid.NewGuid().ToString("N");
        var token = Guid.NewGuid().ToString("N");
        return (pipe, token, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DOTNET_MODIFIABLE_ASSEMBLIES"] = "debug",
            ["WINAPP_HOT_RELOAD_PIPE"] = pipe,
            ["WINAPP_HOT_RELOAD_TOKEN"] = token,
        });
    }

    public static string HelperPath(string cliDirectory) => Path.Combine(cliDirectory, HelperFileName);

    /// <summary>Why the helper can't start from winapp's folder, or null.</summary>
    public static string? HelperProblem(string cliDirectory)
    {
        if (!File.Exists(HelperPath(cliDirectory)))
        {
            return $"--watch could not find its hot reload helper ({HelperPath(cliDirectory)}). Reinstall winapp.";
        }
        // The helper runs on the newest .NET runtime installed; framework files next to it would replace that runtime's.
        return File.Exists(Path.Combine(cliDirectory, "System.Runtime.dll"))
            ? $"--watch can't start its hot reload helper from {cliDirectory}: that folder has its own .NET framework files (System.Runtime.dll). Run winapp from its published single-file layout."
            : null;
    }

    public void Dispose()
    {
        try
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A temp folder left behind is harmless.
        }
    }
}
