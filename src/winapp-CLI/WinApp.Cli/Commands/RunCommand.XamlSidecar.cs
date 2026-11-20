// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using WinApp.Cli.Services.DevTools;

namespace WinApp.Cli.Commands;

// SPIKE (hot reload option A): XAML hot reload as a side-car next to dotnet watch. Not for merging.
internal partial class RunCommand
{
    public partial class Handler
    {
        private const string SidecarPipeVariable = "WINAPP_XAML_SIDECAR_PIPE";

        /// <summary>
        /// Records the C# compiler's arguments in obj when dotnet watch's build compiles (the side-car builds a metadata image of
        /// the app from them). Chains the packaged launch targets when there are any.
        /// </summary>
        private const string SidecarTargets =
            """
            <Project>
              <Target Name="WinAppXamlSidecarCapture" AfterTargets="CoreCompile" Condition="'@(CscCommandLineArgs)' != ''">
                <WriteLinesToFile File="$(ProjectDir)$(IntermediateOutputPath)winapp-csc.rsp" Lines="@(CscCommandLineArgs)" Overwrite="true" />
              </Target>
              <Import Project="$(WinAppXamlSidecarChainTargets)" Condition="'$(WinAppXamlSidecarChainTargets)' != ''" />
            </Project>
            """;

        private sealed class XamlSidecar : IDisposable
        {
            public required string PipeName { get; init; }
            public required string TargetsPath { get; init; }
            public Process? Process { get; set; }

            public void Dispose()
            {
                try
                {
                    if (Process is { HasExited: false })
                    {
                        Process.Kill(entireProcessTree: true);
                    }
                }
                catch (InvalidOperationException)
                {
                }
                Process?.Dispose();
            }
        }

        /// <summary>The side-car for a project with XAML files (null for a Reactor or other app without .xaml).</summary>
        private XamlSidecar? StartXamlSidecar(FileInfo csproj)
        {
            if (Environment.GetEnvironmentVariable("WINAPP_XAML_SIDECAR_OFF") == "1")
            {
                return null;
            }
            var hasXaml = Directory.EnumerateFiles(csproj.DirectoryName!, "*.xaml", SearchOption.AllDirectories)
                .Any(f => !f.Contains(@"\bin\", StringComparison.OrdinalIgnoreCase) && !f.Contains(@"\obj\", StringComparison.OrdinalIgnoreCase));
            if (!hasXaml)
            {
                return null;
            }
            var dll = Environment.GetEnvironmentVariable("WINAPP_XAML_SIDECAR") is { Length: > 0 } custom ? custom
                : Path.Combine(AppContext.BaseDirectory, "xaml-sidecar", "WinApp.XamlSidecar.dll");
            if (!File.Exists(dll))
            {
                Console.Out.WriteLine($"[watch] note: XAML hot reload is off: the side-car wasn't found ({dll})");
                return null;
            }
            var targets = Path.Combine(winappDirectoryService.GetGlobalWinappDirectory().FullName, "devtools", "xaml-sidecar.targets");
            if (!File.Exists(targets) || File.ReadAllText(targets) != SidecarTargets)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(targets)!);
                File.WriteAllText(targets, SidecarTargets);
            }
            var sidecar = new XamlSidecar { PipeName = $"winapp-xaml-{Environment.ProcessId}-{Guid.NewGuid():N}", TargetsPath = targets };
            var start = new ProcessStartInfo("dotnet") { UseShellExecute = false, WorkingDirectory = csproj.DirectoryName! };
            foreach (var argument in new[] { "exec", dll, "--project", csproj.FullName, "--pipe", sidecar.PipeName, "--rsp", "auto" })
            {
                start.ArgumentList.Add(argument);
            }
            sidecar.Process = Process.Start(start);
            return sidecar;
        }

        /// <summary>dotnet watch's MSBuild properties for the side-car's compiler-argument capture.</summary>
        private static IEnumerable<string> SidecarProperties(XamlSidecar? sidecar, string? chainTargets) => sidecar is null ? [] :
        [
            "ProvideCommandLineArgs=true",
            $"CustomAfterMicrosoftCommonTargets={EscapeMSBuildProperty(sidecar.TargetsPath)}",
            .. chainTargets is null ? Array.Empty<string>() : [$"WinAppXamlSidecarChainTargets={EscapeMSBuildProperty(chainTargets)}"],
        ];

        private static IReadOnlyDictionary<string, string?> WithSidecar(IReadOnlyDictionary<string, string?> environment, XamlSidecar? sidecar)
        {
            if (sidecar is null)
            {
                return environment;
            }
            return new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase) { [SidecarPipeVariable] = sidecar.PipeName };
        }
    }
}
