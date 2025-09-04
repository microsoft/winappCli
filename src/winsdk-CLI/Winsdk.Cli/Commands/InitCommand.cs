using System.CommandLine;
using System.Runtime.InteropServices;

namespace Winsdk.Cli.Commands;

internal class InitCommand : Command
{
    enum Architecture
    {
        x64,
        arm64
    }

    public InitCommand() : base("init", "Initialize a new project")
    {
        var experimentalOption = new Option<bool>("--experimental")
        {
            Description = "Use experimental package versions"
        };
        var ignoreConfigOption = new Option<bool>("--ignore-config")
        {
            Description = "Ignore existing winsdk.yaml configuration"
        };
        var yesOption = new Option<bool>("--yes")
        {
            Description = "Assume yes to all prompts"
        };
        var archOption = new Option<Architecture>("--arch")
        {
            Description = "Target architecture",
            DefaultValueFactory = (argumentResult) =>
            {
                // Default to host architecture
                return RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64 ? Architecture.arm64 : Architecture.x64;
            }
        };

        Options.Add(experimentalOption);
        Options.Add(ignoreConfigOption);
        Options.Add(yesOption);
        Options.Add(archOption);

        SetAction(async (parseResult, ct) =>
        {
            var experimental = parseResult.GetValue(experimentalOption);
            var ignoreConfig = parseResult.GetValue(ignoreConfigOption);
            var assumeYes = parseResult.GetValue(yesOption);
            var arch = parseResult.GetValue(archOption);

            var cwd = Directory.GetCurrentDirectory();
            var winsdkDir = Path.Combine(cwd, ".winsdk");
            var pkgsDir = Path.Combine(winsdkDir, "packages");
            var includeOut = Path.Combine(winsdkDir, "include");
            var libOut = Path.Combine(winsdkDir, "lib");
            var binOut = Path.Combine(winsdkDir, "bin");
            Directory.CreateDirectory(pkgsDir);
            Directory.CreateDirectory(includeOut);
            Directory.CreateDirectory(libOut);
            Directory.CreateDirectory(binOut);
            Console.WriteLine($"{UiSymbols.Rocket} winsdk init starting in {cwd}");
            Console.WriteLine($"{UiSymbols.Folder} Workspace → {winsdkDir}");

            var config = new ConfigService(cwd);
            var nuget = new NugetService();
            var cppwinrt = new CppWinrtService();
            var usedVersions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            WinsdkConfig pinned = new WinsdkConfig();

            Console.WriteLine($"{UiSymbols.Note} Checking for winsdk.yaml...");
            var hadConfig = config.Exists();
            if (hadConfig)
            {
                pinned = config.Load();
                Console.WriteLine($"{UiSymbols.Note} Found winsdk.yaml; using pinned versions unless overridden.");
                if (!ignoreConfig && pinned.Packages.Count > 0)
                {
                    var overwrite = assumeYes || Program.PromptYesNo("winsdk.yaml exists with pinned versions. Overwrite with latest versions? [y/N]: ");
                    if (overwrite) { ignoreConfig = true; }
                }
            }
            else
            {
                Console.WriteLine($"{UiSymbols.New} No winsdk.yaml found; will generate one after init.");
            }

            Console.WriteLine($"{UiSymbols.Wrench} Ensuring nuget.exe is available...");
            Console.WriteLine($"{UiSymbols.Package} Installing SDK packages → {pkgsDir}");
            await nuget.EnsureNugetExeAsync();

            // 1) Resolve all versions first (pinned vs latest)
            foreach (var pkg in NugetService.SDK_PACKAGES)
            {
                string version;
                if (!ignoreConfig && config.Exists())
                {
                    var v = pinned.GetVersion(pkg);
                    version = string.IsNullOrWhiteSpace(v) ? await nuget.GetLatestVersionAsync(pkg, experimental)
                                                           : v!;
                }
                else
                {
                    version = await nuget.GetLatestVersionAsync(pkg, experimental);
                }

                Console.WriteLine($"  {UiSymbols.Bullet} {pkg} {version}");
                usedVersions[pkg] = version;
            }

            // 2) Single bulk install using a temp packages.config
            await nuget.InstallPackagesFromConfigAsync(usedVersions, pkgsDir);

            // Prepare consolidated layout and run cppwinrt
            var cppWinrtExe = cppwinrt.FindCppWinrtExe(pkgsDir, usedVersions);
            if (cppWinrtExe is null)
            {
                Console.Error.WriteLine("cppwinrt.exe not found in installed packages.");
                return 2;
            }
            Console.WriteLine($"{UiSymbols.Tools} Using cppwinrt tool → {cppWinrtExe}");

            // Copy headers, libs, runtimes
            var layout = new PackageLayoutService();
            Console.WriteLine($"{UiSymbols.Files} Copying headers → {includeOut}");
            layout.CopyIncludesFromPackages(pkgsDir, includeOut);
            Console.WriteLine($"{UiSymbols.Check} Headers ready → {includeOut}");

            var libRoot = Path.Combine(winsdkDir, "lib");
            Console.WriteLine($"{UiSymbols.Books} Copying import libs by arch → {libRoot}");
            layout.CopyLibsAllArch(pkgsDir, libRoot);
            var libArchs = Directory.Exists(libRoot) ? string.Join(", ", Directory.EnumerateDirectories(libRoot).Select(Path.GetFileName)) : "(none)";
            Console.WriteLine($"{UiSymbols.Books} Import libs ready for archs: {libArchs}");

            var binRoot = Path.Combine(winsdkDir, "bin");
            Console.WriteLine($"{UiSymbols.Gear} Copying runtime binaries by arch → {binRoot}");
            layout.CopyRuntimesAllArch(pkgsDir, binRoot);
            var binArchs = Directory.Exists(binRoot) ? string.Join(", ", Directory.EnumerateDirectories(binRoot).Select(Path.GetFileName)) : "(none)";
            Console.WriteLine($"{UiSymbols.Gear} Runtime binaries ready for archs: {binArchs}");

            // Collect winmd inputs
            Console.WriteLine($"{UiSymbols.Search} Searching for .winmd metadata...");
            var winmds = layout.FindWinmds(pkgsDir).ToList();
            Console.WriteLine($"{UiSymbols.Search} Found {winmds.Count} .winmd");
            if (winmds.Count == 0)
            {
                Console.Error.WriteLine("No .winmd files found for C++/WinRT projection.");
                return 2;
            }

            // Run cppwinrt inline with -input sdk+ and explicit winmds
            Console.WriteLine($"{UiSymbols.Gear} Generating C++/WinRT projections...");
            await CppWinrtRunner.RunWithRspAsync(cppWinrtExe, winmds, includeOut, winsdkDir, verbose: true);
            Console.WriteLine($"{UiSymbols.Check} C++/WinRT headers generated → {includeOut}");

            // Save winsdk.yaml with used versions
            var finalConfig = new WinsdkConfig();
            foreach (var kvp in usedVersions)
            {
                finalConfig.SetVersion(kvp.Key, kvp.Value);
            }
            config.Save(finalConfig);
            Console.WriteLine($"{UiSymbols.Save} Wrote config → {Path.Combine(cwd, "winsdk.yaml")}");

            Console.WriteLine($"{UiSymbols.Party} winsdk init completed.");

            return 0;
        });
    }
}
