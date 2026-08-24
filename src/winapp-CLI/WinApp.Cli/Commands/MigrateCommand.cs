// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

/// <summary>
/// Performs the deterministic portion of a UWP to WinUI 3 migration and records known residual work.
/// </summary>
internal partial class MigrateCommand : Command, IShortDescription
{
    public string ShortDescription => "Mechanically migrate a UWP project to a new WinUI 3 project";

    public static Argument<DirectoryInfo> SourceArgument { get; }
    public static Option<DirectoryInfo> OutputOption { get; }
    public static Option<string?> NameOption { get; }

    // Files that must NOT be copied from the UWP source into the WinUI target: build artifacts,
    // project/solution system files, IDE state, packaging outputs, and private signing material.
    // Everything else — including fonts, JSON/XML data, audio/video, shaders and other project
    // content — is copied so the migrated code never references a missing runtime asset.
    // (Directories such as bin/obj are already pruned by ExcludeDirSegments; Package.appxmanifest
    // is handled by PreserveUwpReferences.)
    private static readonly string[] DoNotCopyExtensions =
    [
        ".csproj", ".vcxproj", ".vbproj", ".shproj", ".projitems", ".sln", ".slnf",
        ".user", ".suo", ".cache", ".vsidx", ".pdb", ".ilk", ".exp", ".idb", ".tlog",
        ".exe", ".dll", ".lib", ".obj", ".appxmanifest",
        ".appx", ".msix", ".appxbundle", ".appxupload", ".nupkg", ".snupkg",
        // Private signing material — must never be copied into (and risk being committed with)
        // the migrated project.
        ".pfx", ".snk", ".p12", ".pvk", ".cer", ".key"
    ];

    private static readonly string[] ExcludeDirSegments =
    [
        "bin", "obj", ".uwp-source", ".vs", ".git", ".github", ".copilot"
    ];

    private static readonly HashSet<string> AllowedExistingOutputEntries =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".git",
            ".github"
        };

    private const string RidFixMarker = "<!-- arm64-f5-fix:winapp-migrate-scaffold -->";
    private const string PresentationXamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private const string WinUiControlsNamespace = "using:Microsoft.UI.Xaml.Controls";
    private const string XamlLanguageNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static readonly HashSet<string> ItemsStackPanelCompatibleAttributes =
        new(StringComparer.Ordinal)
        {
            "AllowDrop",
            "AreStickyGroupHeadersEnabled",
            "Background",
            "CacheLength",
            "CacheMode",
            "ChildrenTransitions",
            "Clip",
            "CompositeMode",
            "DataContext",
            "FlowDirection",
            "GroupHeaderPlacement",
            "Height",
            "HorizontalAlignment",
            "IsHitTestVisible",
            "Language",
            "ManipulationMode",
            "Margin",
            "MaxHeight",
            "MaxWidth",
            "MinHeight",
            "MinWidth",
            "Name",
            "Opacity",
            "Orientation",
            "RenderTransform",
            "RenderTransformOrigin",
            "RequestedTheme",
            "Resources",
            "Tag",
            "Transitions",
            "UseLayoutRounding",
            "VerticalAlignment",
            "Visibility",
            "Width"
        };

    private static readonly HashSet<string> CompatibleXamlDirectives =
        new(StringComparer.Ordinal)
        {
            "DeferLoadStrategy",
            "Key",
            "Load",
            "Name",
            "Phase",
            "Uid"
        };

    static MigrateCommand()
    {
        SourceArgument = new Argument<DirectoryInfo>("source")
        {
            Description = "UWP project source folder (contains the .csproj and Package.appxmanifest)."
        };
        SourceArgument.AcceptExistingOnly();

        OutputOption = new Option<DirectoryInfo>("--output", "-o")
        {
            Description = "New directory where the mechanically migrated WinUI 3 project will be created.",
            Required = true
        };
        NameOption = new Option<string?>("--name", "-n")
        {
            Description = "Target project name. Defaults to the UWP project name with an 'App' suffix."
        };
    }

    public MigrateCommand(MigrateVerifyCommand verifyCommand)
        : base("migrate", "Create a new WinUI 3 project from UWP source and apply deterministic mechanical transforms. Writes migration-report.json with known residual work. Success means the mechanical pass completed; it does not guarantee that the result builds or runs.")
    {
        Arguments.Add(SourceArgument);
        Options.Add(OutputOption);
        Options.Add(NameOption);
        Subcommands.Add(verifyCommand);
    }

    public partial class Handler(IDotNetService dotNetService, ILogger<MigrateCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var quiet = parseResult.GetValue(WinAppRootCommand.QuietOption);
            var originalOut = Console.Out;
            if (quiet) { Console.SetOut(new QuietFilteringTextWriter(originalOut)); }
            try
            {
                return await InvokeCore(parseResult, cancellationToken);
            }
            finally
            {
                if (quiet) { Console.SetOut(originalOut); }
            }
        }

        private async Task<int> InvokeCore(ParseResult parseResult, CancellationToken cancellationToken)
        {
            var source = parseResult.GetValue(SourceArgument)!;
            var target = parseResult.GetValue(OutputOption)!;

            var sourceRoot = source.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var targetRoot = target.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            Console.Out.WriteLine("==> winapp migrate");
            Console.Out.WriteLine($"    Source : {sourceRoot}");
            Console.Out.WriteLine($"    Output : {targetRoot}");

            // Reject overlapping paths: copying a directory onto itself, or into/from an
            // ancestor, corrupts the source or loops the WinUI scaffold back through the copy.
            if (PathsOverlap(sourceRoot, targetRoot))
            {
                Console.Out.WriteLine("[ERROR] Source and target must be two distinct, non-nested directories.");
                return 1;
            }

            // Verify the advertised prerequisites before mutating any files.
            var sourceHasProject = EnumerateFiles(sourceRoot).Any(f =>
                f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileName(f), "Package.appxmanifest", StringComparison.OrdinalIgnoreCase));
            if (!sourceHasProject)
            {
                Console.Out.WriteLine("[ERROR] Source is not a UWP project — no .csproj or Package.appxmanifest found. Nothing to migrate.");
                return 1;
            }

            var unsupportedOutputEntries = GetUnsupportedOutputEntries(targetRoot);
            if (unsupportedOutputEntries.Count > 0)
            {
                Console.Out.WriteLine(
                    "[ERROR] Output directory may contain only supported control-plane metadata " +
                    $"(.git and .github). Remove or relocate: {string.Join(", ", unsupportedOutputEntries)}");
                return 1;
            }

            var sourceProject = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
            var sourceFiles = EnumerateFiles(sourceRoot).ToList();
            var requestedName = parseResult.GetValue(NameOption);
            var projectName = string.IsNullOrWhiteSpace(requestedName)
                ? GetProjectName(sourceRoot, sourceProject)
                : SanitizeProjectName(requestedName);
            var targetParent = Directory.GetParent(targetRoot);
            if (targetParent is null)
            {
                Console.Out.WriteLine("[ERROR] Output directory must have a parent directory.");
                return 1;
            }
            targetParent.Create();

            var stagingRoot = Path.Combine(
                targetParent.FullName,
                $".{Path.GetFileName(targetRoot)}.winapp-migrate-{Guid.NewGuid():N}");
            var templateArguments = WindowsCommandLine.JoinArguments(
                ["new", NewCommand.DefaultTemplateShortName, "--name", projectName, "--output", stagingRoot, "--no-update-check"])!;

            string? stagedProject;
            try
            {
                var templateResult = await dotNetService.RunDotnetCommandAsync(targetParent, templateArguments, cancellationToken);
                if (templateResult.ExitCode != 0)
                {
                    Console.Out.WriteLine("[ERROR] Could not create the WinUI 3 project with 'dotnet new winui'.");
                    Console.Out.WriteLine("        Run 'winapp new --list' once to install or repair the official WinUI template pack.");
                    if (!string.IsNullOrWhiteSpace(templateResult.Error))
                    {
                        Console.Out.WriteLine(templateResult.Error.Trim());
                    }
                    return 1;
                }

                stagedProject = Directory.EnumerateFiles(stagingRoot, "*.csproj", SearchOption.TopDirectoryOnly).FirstOrDefault();
                if (stagedProject is null || FindFile(stagingRoot, "MainWindow.xaml") is null)
                {
                    Console.Out.WriteLine("[ERROR] The 'dotnet new winui' template did not produce the expected project files.");
                    return 1;
                }

                if (!TryMergeScaffold(stagingRoot, targetRoot, out var mergeError))
                {
                    Console.Out.WriteLine($"[ERROR] Could not merge the WinUI scaffold into the output directory: {mergeError}");
                    return 1;
                }
            }
            finally
            {
                if (Directory.Exists(stagingRoot))
                {
                    Directory.Delete(stagingRoot, recursive: true);
                }
            }

            var targetProject = Path.Combine(targetRoot, Path.GetRelativePath(stagingRoot, stagedProject!));
            var report = new MigrationReport
            {
                Source = new MigrationProject
                {
                    Root = NormalizePath(sourceRoot),
                    ProjectFile = sourceProject is null ? null : NormalizePath(Path.GetRelativePath(sourceRoot, sourceProject))
                },
                Target = new MigrationProject
                {
                    Root = NormalizePath(targetRoot),
                    ProjectFile = NormalizePath(Path.GetRelativePath(targetRoot, targetProject))
                }
            };
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-SCAFFOLD",
                Summary = "Created the target with the official WinUI project template",
                ChangedFiles = 1
            });
            report.DependencyAnalysis = AnalyzeSourceDependencies(sourceRoot, sourceProject);
            Console.Out.WriteLine(
                $"    Inventoried {report.DependencyAnalysis.PackageReferences.Count} package reference(s) and " +
                $"{report.DependencyAnalysis.ProjectReferences.Count} project reference(s) across " +
                $"{report.DependencyAnalysis.Projects.Count} source project(s); " +
                $"{report.DependencyAnalysis.Issues.Count} inspection issue(s)");

            var copied = new List<string>();
            var preservedStartup = new List<string>();

            // ── 1. Copy source files (everything but the project) ────────────────
            foreach (var file in EnumerateFiles(sourceRoot))
            {
                if (!ShouldCopy(file))
                {
                    continue;
                }

                var rel = Path.GetRelativePath(sourceRoot, file);

                // Never overwrite the WinUI scaffold's own startup files with their UWP
                // counterparts: a UWP App.xaml.cs launches via Window.Current, so replacing
                // the scaffold's MainWindow bootstrap leaves the migrated app unable to start.
                if (IsScaffoldStartupFile(rel))
                {
                    preservedStartup.Add(rel);
                    continue;
                }

                CopyInto(file, Path.Combine(targetRoot, rel));
                copied.Add(rel);
            }
            Console.Out.WriteLine($"    Copied {copied.Count} source files");
            if (preservedStartup.Count > 0)
            {
                Console.Out.WriteLine($"    Preserved WinUI scaffold startup file(s), did NOT copy UWP: {string.Join(", ", preservedStartup)}");
                Console.Out.WriteLine("      Migrate app-level resources from the UWP App.xaml manually (the WinUI startup bootstrap was kept).");
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG001",
                    Category = "app-resources",
                    Priority = "required",
                    Summary = "Merge application resources and startup behavior from the preserved UWP App files",
                    Reason = "The WinUI startup files were kept because UWP App.xaml and App.xaml.cs cannot safely overwrite the desktop bootstrap.",
                    Locations = preservedStartup.Select(path => new MigrationLocation
                    {
                        Path = NormalizePath(Path.Combine(".uwp-source", path + ".reference"))
                    }).ToList()
                });
            }

            // ── 2. Preserve UWP .csproj / .appxmanifest under .uwp-source/ ───────
            PreserveUwpReferences(sourceRoot, targetRoot, preservedStartup, report);
            var preservedReferences = sourceFiles
                .Where(file =>
                    preservedStartup.Contains(
                        Path.GetRelativePath(sourceRoot, file),
                        StringComparer.OrdinalIgnoreCase)
                    || sourceProject is not null
                        && string.Equals(file, sourceProject, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(
                            Path.GetDirectoryName(file),
                            sourceRoot,
                            StringComparison.OrdinalIgnoreCase)
                        && string.Equals(
                            Path.GetExtension(file),
                            ".appxmanifest",
                            StringComparison.OrdinalIgnoreCase))
                .Select(file => Path.GetRelativePath(sourceRoot, file))
                .ToList();
            var intentionallyExcluded = sourceFiles
                .Where(file => !ShouldCopy(file))
                .Select(file => Path.GetRelativePath(sourceRoot, file))
                .Except(preservedReferences, StringComparer.OrdinalIgnoreCase)
                .ToList();

            // ── 2b. Patch deterministic WinUI 3 project settings ────────────────
            var projectFiles = PatchProjectSettings(targetRoot);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-PROJECT",
                Summary = "Patched deterministic WinUI project settings",
                ChangedFiles = projectFiles
            });

            // ── 3. Namespace rewrite: Windows.UI.Xaml -> Microsoft.UI.Xaml ──────
            var namespaceFiles = RewriteNamespaces(targetRoot);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-XAML-NS",
                Summary = "Rewrote Windows.UI.Xaml namespaces to Microsoft.UI.Xaml",
                ChangedFiles = namespaceFiles
            });

            var reswNamespaceFiles = RewriteReswNamespaces(targetRoot);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-RESW-NS",
                Summary = "Rewrote Windows.UI.Xaml namespaces in WinRT resource keys",
                ChangedFiles = reswNamespaceFiles
            });

            var projectItemResult = MigrateSourceProjectItems(
                sourceRoot,
                sourceProject,
                targetRoot,
                targetProject,
                applyChanges: true);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-PROJECT-ITEMS",
                Summary = "Migrated deterministic Content and PRIResource project items",
                ChangedFiles = projectItemResult.ChangedFiles
            });

            var itemsPanelFiles = RewriteVirtualizingStackPanels(targetRoot, report);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-XAML-ITEMS-PANEL",
                Summary = "Replaced safe ItemsPanelTemplate VirtualizingStackPanel elements with ItemsStackPanel",
                ChangedFiles = itemsPanelFiles
            });

            var dispatcherFiles = RewriteDispatcherAccess(targetRoot);
            report.Transforms.Add(new MigrationTransform
            {
                Id = "UWMIG-DISPATCHER",
                Summary = "Rewrote DependencyObject.Dispatcher.HasThreadAccess to DispatcherQueue.HasThreadAccess",
                ChangedFiles = dispatcherFiles
            });
            AddDispatcherResidualTodo(targetRoot, report);
            AddWindowingResidualTodo(targetRoot, report);
            AddDisplayInformationResidualTodo(targetRoot, report);
            report.MechanicalVerification = VerifyMechanicalMigration(
                sourceRoot,
                sourceFiles,
                copied,
                preservedReferences,
                intentionallyExcluded,
                targetRoot,
                projectItemResult,
                report);
            report.Status = report.MechanicalVerification.Status == "passed"
                ? "mechanical-migration-complete"
                : "mechanical-verification-failed";

            report.Summary.CopiedFiles = copied.Count;
            report.Summary.TransformOperations = report.Transforms.Sum(transform => transform.ChangedFiles);
            report.Summary.TodoCategories = report.Todos.Count;
            var reportPath = Path.Combine(targetRoot, "migration-report.json");
            await File.WriteAllTextAsync(
                reportPath,
                JsonSerializer.Serialize(report, MigrateJsonContext.Default.MigrationReport),
                cancellationToken);

            Console.Out.WriteLine();
            Console.Out.WriteLine(
                report.MechanicalVerification.Status == "passed"
                    ? "=== MECHANICAL MIGRATION COMPLETE ==="
                    : "=== MECHANICAL MIGRATION VERIFICATION FAILED ===");
            Console.Out.WriteLine($"Changed: {report.Summary.CopiedFiles} copied files, {report.Summary.TransformOperations} transform operations");
            Console.Out.WriteLine(
                $"Coverage: {report.MechanicalVerification.Inventory.ClassifiedFiles}/{report.MechanicalVerification.Inventory.SourceFiles} source files classified; " +
                $"{report.MechanicalVerification.ProjectItems.MigratedItems}/{report.MechanicalVerification.ProjectItems.SourceItems} Content/PRIResource items migrated");
            Console.Out.WriteLine($"Remaining work: {report.Summary.TodoCategories} known TODO categor{(report.Summary.TodoCategories == 1 ? "y" : "ies")}");
            foreach (var todo in report.Todos)
            {
                Console.Out.WriteLine($"  [{todo.Priority}] {todo.Category}: {todo.Summary}");
            }
            Console.Out.WriteLine("Details: migration-report.json");
            Console.Out.WriteLine("This result is not guaranteed to build or run.");
            Console.Out.WriteLine("=====================================");

            return report.MechanicalVerification.Status == "passed" ? 0 : 1;
        }

        // ───────────────────────────── 2 ───────────────────────────────────────
        private void PreserveUwpReferences(
            string sourceRoot,
            string targetRoot,
            IReadOnlyList<string> preservedStartup,
            MigrationReport report)
        {
            var refDir = Path.Combine(targetRoot, ".uwp-source");

            var csprojs = Directory.EnumerateFiles(sourceRoot, "*.csproj", SearchOption.TopDirectoryOnly).ToList();
            foreach (var p in csprojs)
            {
                Directory.CreateDirectory(refDir);
                var dst = Path.Combine(refDir, Path.GetFileName(p) + ".reference");
                File.Copy(p, dst, overwrite: true);
                Console.Out.WriteLine($"    Preserved {Path.GetFileName(p)} as .uwp-source/{Path.GetFileName(p)}.reference (reference only)");
            }
            if (csprojs.Count == 0)
            {
                logger.LogWarning("No .csproj found under source — no reference for the original PackageReference list.");
            }
            else
            {
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG003",
                    Category = "project-dependencies",
                    Priority = "required",
                    Summary = "Review UWP project references, linked content, and dependencies",
                    Reason = "Use dependencyAnalysis for the source project-reference graph. Project/package references and files linked from outside the source root require an explicit target-compatibility decision before they can be migrated safely.",
                    Locations = csprojs.Select(path => new MigrationLocation
                    {
                        Path = NormalizePath(Path.Combine(".uwp-source", Path.GetFileName(path) + ".reference"))
                    }).ToList()
                });
            }

            foreach (var relativePath in preservedStartup)
            {
                var sourcePath = Path.Combine(sourceRoot, relativePath);
                if (!File.Exists(sourcePath))
                {
                    continue;
                }

                Directory.CreateDirectory(refDir);
                var destination = Path.Combine(refDir, relativePath + ".reference");
                var destinationDirectory = Path.GetDirectoryName(destination);
                if (!string.IsNullOrEmpty(destinationDirectory))
                {
                    Directory.CreateDirectory(destinationDirectory);
                }
                File.Copy(sourcePath, destination, overwrite: true);
            }

            var extensions = new List<string>();
            var manifests = new List<string>();
            foreach (var mf in Directory.EnumerateFiles(sourceRoot, "*.appxmanifest", SearchOption.TopDirectoryOnly))
            {
                Directory.CreateDirectory(refDir);
                var dst = Path.Combine(refDir, Path.GetFileName(mf) + ".reference");
                File.Copy(mf, dst, overwrite: true);
                manifests.Add(dst);
                Console.Out.WriteLine($"    Preserved {Path.GetFileName(mf)} as .uwp-source/{Path.GetFileName(mf)}.reference (do not overwrite the scaffold manifest)");

                var content = File.ReadAllText(mf);
                foreach (Match m in ManifestExtension().Matches(content))
                {
                    extensions.Add(m.Groups[1].Value);
                }
            }
            if (extensions.Count > 0)
            {
                Console.Out.WriteLine($"    WARNING: UWP manifest declares {extensions.Count} Extension(s): {string.Join(", ", extensions)}");
                Console.Out.WriteLine("      Do NOT copy them verbatim — most UWP manifest extensions have no WinUI 3 desktop equivalent and must be re-implemented or dropped.");
            }
            if (manifests.Count > 0)
            {
                var extensionText = extensions.Count == 0
                    ? ""
                    : $" Detected extension categories: {string.Join(", ", extensions.Distinct(StringComparer.OrdinalIgnoreCase))}.";
                report.Todos.Add(new MigrationTodo
                {
                    Id = "UWMIG002",
                    Category = "manifest",
                    Priority = "required",
                    Summary = "Review UWP manifest capabilities and extensions for WinUI 3 desktop",
                    Reason = "The original manifest was preserved for reference rather than copied over the WinUI package manifest." + extensionText,
                    Locations = manifests.Select(path => new MigrationLocation
                    {
                        Path = NormalizePath(Path.GetRelativePath(targetRoot, path))
                    }).ToList()
                });
            }
        }

        // ───────────────────────────── 2b ──────────────────────────────────────
        private int PatchProjectSettings(string targetRoot)
        {
            var csprojs = EnumerateFiles(targetRoot)
                .Where(f => f.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int patched = 0, ridAlreadyPatched = 0;
            foreach (var cp in csprojs)
            {
                var body = File.ReadAllText(cp);
                var changed = false;

                if (body.Contains(RidFixMarker))
                {
                    ridAlreadyPatched++;
                }
                else
                {
                    var runtimeIdentifier = HostArchRuntimeIdentifier().Match(body);
                    if (runtimeIdentifier.Success)
                    {
                        var indent = runtimeIdentifier.Groups["indent"].Value;
                        var injection =
                            $"{indent}{RidFixMarker}\r\n" +
                            $"{indent}<RuntimeIdentifier Condition=\"'$(RuntimeIdentifier)' == '' AND '$(Platform)' == 'x86'\">win-x86</RuntimeIdentifier>\r\n" +
                            $"{indent}<RuntimeIdentifier Condition=\"'$(RuntimeIdentifier)' == '' AND '$(Platform)' == 'x64'\">win-x64</RuntimeIdentifier>\r\n" +
                            $"{indent}<RuntimeIdentifier Condition=\"'$(RuntimeIdentifier)' == '' AND '$(Platform)' == 'ARM64'\">win-arm64</RuntimeIdentifier>\r\n" +
                            runtimeIdentifier.Value;
                        body = body[..runtimeIdentifier.Index] + injection + body[(runtimeIdentifier.Index + runtimeIdentifier.Length)..];
                        changed = true;
                        Console.Out.WriteLine($"    Patched RuntimeIdentifier in {Path.GetFileName(cp)} — F5 now works on x86/x64/ARM64 hosts");
                    }
                }

                var projectDirectory = Path.GetDirectoryName(cp);
                var hasLegacyAssemblyInfo =
                    projectDirectory is not null &&
                    File.Exists(Path.Combine(projectDirectory, "Properties", "AssemblyInfo.cs"));
                if (hasLegacyAssemblyInfo &&
                    !body.Contains("<GenerateAssemblyInfo>", StringComparison.OrdinalIgnoreCase))
                {
                    var propertyGroup = FirstPropertyGroup().Match(body);
                    if (propertyGroup.Success)
                    {
                        var newline = body.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                        var propertyIndent = propertyGroup.Groups["indent"].Value + "  ";
                        var injection =
                            propertyGroup.Value + newline +
                            $"{propertyIndent}<GenerateAssemblyInfo>false</GenerateAssemblyInfo>";
                        body = body[..propertyGroup.Index] + injection + body[(propertyGroup.Index + propertyGroup.Length)..];
                        changed = true;
                        Console.Out.WriteLine($"    Disabled generated assembly attributes in {Path.GetFileName(cp)} — preserved Properties\\AssemblyInfo.cs remains authoritative");
                    }
                }

                if (changed)
                {
                    File.WriteAllText(cp, body);
                    patched++;
                }
            }
            if (csprojs.Count == 0)
            {
                logger.LogWarning("No WinUI 3 .csproj found at target — did you run 'dotnet new winui' first?");
            }
            else if (patched == 0 && ridAlreadyPatched > 0)
            {
                Console.Out.WriteLine($"    RuntimeIdentifier already patched in {ridAlreadyPatched} .csproj file(s) — no change");
            }
            return patched;
        }

        // ───────────────────────────── 3 ───────────────────────────────────────
        private static int RewriteNamespaces(string targetRoot)
        {
            var files = EnumerateFiles(targetRoot)
                .Where(f => f.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                .ToList();
            int changed = 0;
            foreach (var f in files)
            {
                EncodedTextFile sourceFile;
                try
                {
                    sourceFile = ReadTextFile(f);
                }
                catch (DecoderFallbackException)
                {
                    continue;
                }
                var orig = sourceFile.Content;
                var updated = orig.Replace("Windows.UI.Xaml", "Microsoft.UI.Xaml");
                if (updated != orig)
                {
                    WriteTextFile(f, updated, sourceFile.Encoding);
                    changed++;
                }
            }
            Console.Out.WriteLine($"    Rewrote Windows.UI.Xaml -> Microsoft.UI.Xaml in {changed} of {files.Count} .cs/.xaml files");
            return changed;
        }


    }

    // ───────────────────────── source-gen regexes ──────────────────────────────
    [GeneratedRegex("(?i)<(?:uap\\d?:)?Extension\\s+Category=\"([^\"]+)\"")]
    private static partial Regex ManifestExtension();

    [GeneratedRegex(@"(?m)^(?<indent>\s*)<RuntimeIdentifier\s+Condition=""'\$\(RuntimeIdentifier\)'\s*==\s*''"">win-\$\(\[System\.Runtime\.InteropServices\.RuntimeInformation\][^<]+</RuntimeIdentifier>\s*$")]
    private static partial Regex HostArchRuntimeIdentifier();

    [GeneratedRegex(@"(?m)^(?<indent>[ \t]*)<PropertyGroup(?:\s[^>]*)?>[ \t]*(?=\r?$)")]
    private static partial Regex FirstPropertyGroup();

    [GeneratedRegex(@"\bDispatcher\s*\.\s*HasThreadAccess\b")]
    private static partial Regex DispatcherHasThreadAccess();

    [GeneratedRegex(@"\bclass\s+\w+[^{:]*:\s*[^{]*\bPage\b")]
    private static partial Regex PageClassDeclaration();

    [GeneratedRegex(@"\bDispatcher\s*\.\s*RunAsync\b")]
    private static partial Regex DispatcherRunAsync();

    [GeneratedRegex(@"\bWindow\s*\.\s*Current\b")]
    private static partial Regex WindowCurrent();

    [GeneratedRegex(@"\bWindow\s*\.\s*Current\s*\.\s*Bounds\b")]
    private static partial Regex WindowCurrentBounds();

    [GeneratedRegex(@"\bDisplayInformation\s*\.\s*GetForCurrentView\s*\(")]
    private static partial Regex DisplayInformationGetForCurrentView();

    [GeneratedRegex(@"[^A-Za-z0-9_.-]+")]
    private static partial Regex InvalidProjectNameCharacter();

    [GeneratedRegex(
        """<\?xml[^>]*\bencoding\s*=\s*["'](?<name>[^"']+)["']""",
        RegexOptions.IgnoreCase)]
    private static partial Regex XmlEncodingDeclaration();
}
