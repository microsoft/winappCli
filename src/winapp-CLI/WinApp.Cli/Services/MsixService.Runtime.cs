// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

internal partial class MsixService
{
    // Matches the framework MSIX filename across all naming conventions WinAppSDK has shipped:
    //   * 1.x stable / per-minor SxS:        Microsoft.WindowsAppRuntime.1.8.msix
    //   * 2.x stable / major-only (2.0.1+):  Microsoft.WindowsAppRuntime.2.msix
    //   * 2.x experimental:                  Microsoft.WindowsAppRuntime.2-experimentalN.msix
    // The minor-version segment is optional so a future 2.0.2 / 2.1.0 / 3.0 still match.
    // DDLM/Singleton/Main/Framework variants are excluded by their own .Contains() filters
    // at the call site, not by this regex.
    [GeneratedRegex(@"^Microsoft\.WindowsAppRuntime\.\d+(\.\d+)?.*\.msix$", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex WindowsAppRuntimeMsixRegex();

    // DLL dedup regex — extract registered file names from SxS manifest StringBuilder
    [GeneratedRegex(@"<asmv3:file\s+name='([^']+)'", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex SxsFileNameRegex();

    /// <summary>
    /// Sets up Windows App SDK for self-contained deployment by extracting MSIX content
    /// and preparing the necessary files for embedding in applications.
    /// </summary>
    public async Task SetupSelfContainedAsync(DirectoryInfo winappDir, string architecture, TaskContext taskContext, DotNetPackageListJson? dotNetPackageList = null, CancellationToken cancellationToken = default)
    {
        await taskContext.AddSubTaskAsync("Setting up Self Contained", async (taskContext, cancellationToken) =>
        {
            // Look for the Runtime package which contains the MSIX files
            var selfContainedDir = winappDir.CreateSubdirectory("self-contained");
            var archSelfContainedDir = selfContainedDir.CreateSubdirectory(architecture);

            var msixDir = await GetRuntimeMsixDirAsync(dotNetPackageList, taskContext, cancellationToken) ?? throw new DirectoryNotFoundException("Windows App SDK Runtime MSIX directory not found. Ensure Windows App SDK is installed.");

            // Look for the MSIX file in the tools/MSIX folder
            var msixToolsDir = new DirectoryInfo(Path.Combine(msixDir.FullName, $"win10-{architecture}"));
            if (!msixToolsDir.Exists)
            {
                throw new DirectoryNotFoundException($"MSIX tools directory not found: {msixToolsDir}");
            }

            // Try to use inventory first for accurate file selection
            FileInfo? msixPath = null;
            try
            {
                var packageEntries = await WindowsAppRuntimeService.ParseMsixInventoryAsync(taskContext, msixDir, cancellationToken);
                if (packageEntries != null)
                {
                    // Look for the base Windows App Runtime package (not Framework, DDLM, or Singleton packages)
                    var mainRuntimeEntry = packageEntries.FirstOrDefault(entry =>
                        entry.PackageIdentity.StartsWith("Microsoft.WindowsAppRuntime.") &&
                        !entry.PackageIdentity.Contains("Framework") &&
                        !entry.FileName.Contains("DDLM", StringComparison.OrdinalIgnoreCase) &&
                        !entry.FileName.Contains("Singleton", StringComparison.OrdinalIgnoreCase));

                    if (mainRuntimeEntry != null)
                    {
                        msixPath = new FileInfo(Path.Combine(msixToolsDir.FullName, mainRuntimeEntry.FileName));
                        taskContext.AddDebugMessage($"{UiSymbols.Package} Found main runtime package from inventory: {mainRuntimeEntry.FileName}");
                    }
                }
            }
            catch (Exception ex)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} Could not parse inventory, falling back to file search: {ex.Message}");
            }

            // Fallback: search for files directly with pattern matching
            if (msixPath == null || !msixPath.Exists)
            {
                var msixFiles = msixToolsDir.GetFiles("Microsoft.WindowsAppRuntime.*.msix");
                if (msixFiles.Length == 0)
                {
                    throw new FileNotFoundException($"No MSIX files found in {msixToolsDir}");
                }

                // Look for the base runtime package (format: Microsoft.WindowsAppRuntime.{version}.msix)
                // Exclude files with additional suffixes like DDLM, Singleton, Framework, etc.
                msixPath = msixFiles.FirstOrDefault(f =>
                {
                    var fileName = f.Name;
                    return !fileName.Contains("DDLM", StringComparison.OrdinalIgnoreCase) &&
                           !fileName.Contains("Singleton", StringComparison.OrdinalIgnoreCase) &&
                           !fileName.Contains("Framework", StringComparison.OrdinalIgnoreCase) &&
                           WindowsAppRuntimeMsixRegex().IsMatch(fileName);
                }) ?? msixFiles[0];
            }

            taskContext.AddDebugMessage($"{UiSymbols.Package} Extracting MSIX: {msixPath.FullName}");

            // Extract MSIX content
            var extractedDir = new DirectoryInfo(Path.Combine(archSelfContainedDir.FullName, "extracted"));
            if (extractedDir.Exists)
            {
                extractedDir.Delete(recursive: true);
            }
            extractedDir.Refresh();
            extractedDir.Create();

            using (var archive = await ZipFile.OpenReadAsync(msixPath.FullName, cancellationToken))
            {
                await archive.ExtractToDirectoryAsync(extractedDir.FullName, cancellationToken);
            }

            // Copy relevant files to deployment directory
            var deploymentDir = archSelfContainedDir.CreateSubdirectory("deployment");

            // Copy DLLs, WinMD files, and other runtime assets
            await CopyRuntimeFilesAsync(extractedDir, deploymentDir, taskContext, cancellationToken);

            taskContext.AddDebugMessage($"{UiSymbols.Check} Self-contained files prepared in: {archSelfContainedDir.FullName}");

            return 0;
        }, cancellationToken);
    }

    private async Task EmbedActivationManifestToExeAsync(FileInfo exePath, DirectoryInfo winAppSDKDeploymentDir, FileInfo windowsAppSDKAppXManifestPath, DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        // Use applicationLocation for DLL content (where runtime files were copied by PrepareRuntimeForPackagingAsync)
        var exeDir = exePath.Directory!;

        taskContext.AddDebugMessage($"{UiSymbols.Note} Generating activation manifest from: {windowsAppSDKAppXManifestPath}");
        taskContext.AddDebugMessage($"{UiSymbols.Package} Using DLL content from: {winAppSDKDeploymentDir}");

        // Create a temporary manifest file
        var tempManifestPath = new FileInfo(Path.Combine(exeDir.FullName, "WindowsAppSDK_temp.manifest"));

        try
        {
            // Build the entire manifest in memory, then write to disk once
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version='1.0' encoding='utf-8' standalone='yes'?>");
            sb.AppendLine("<assembly manifestVersion='1.0'");
            sb.AppendLine("    xmlns:asmv3='urn:schemas-microsoft-com:asm.v3'");
            sb.AppendLine("    xmlns:winrtv1='urn:schemas-microsoft-com:winrt.v1'");
            sb.AppendLine("    xmlns='urn:schemas-microsoft-com:asm.v1'>");

            // Collect all AppX manifests (main package + component fragments) and their DLLs
            (var packageDependencies, _) = await GetWinAppSDKPackageDependenciesAsync(dotNetPackageList, taskContext, cancellationToken);
            if (packageDependencies == null || packageDependencies.Count == 0)
            {
                throw new InvalidOperationException("No Windows SDK packages found. Please install the Windows SDK or Windows App SDK.");
            }

            var architecture = WorkspaceSetupService.GetSystemArchitecture();
            IEnumerable<FileInfo> appxFragments = GetComponents(packageDependencies);

            // Combine all manifests: main AppxManifest.xml (Package root) + fragments (Fragment root)
            var allManifests = new List<FileInfo> { windowsAppSDKAppXManifestPath };
            allManifests.AddRange(appxFragments);

            // Combine all DLL file names from deployment dir and fragment native dirs
            var allDllFiles = new List<string>(winAppSDKDeploymentDir.EnumerateFiles("*.dll").Select(di => di.Name));
            allDllFiles.AddRange(appxFragments
                .Select(fragment => Path.Combine(fragment.DirectoryName!, $"win-{architecture}\\native"))
                .Where(Directory.Exists)
                .SelectMany(dir => Directory.EnumerateFiles(dir, "*.dll"))
                .Select(Path.GetFileName)!);

            // Single pass: process all AppX manifests (auto-detects Package vs Fragment root)
            AppendAppManifestFromAppx(
                sb,
                redirectDlls: false,
                inDllFiles: allDllFiles,
                inAppxManifests: allManifests);

            // Phase 3: Discover and register third-party WinRT components (e.g., Win2D, WebView2)
            // These packages ship .winmd files + native DLLs but no package.appxfragment
            await AppendThirdPartyWinRTManifestEntriesAsync(
                sb, architecture, dotNetPackageList, taskContext, cancellationToken);

            sb.AppendLine("</assembly>");

            // Single write to disk
            await File.WriteAllTextAsync(
                tempManifestPath.FullName,
                sb.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken);

            // Use mt.exe to merge manifests
            await EmbedManifestFileToExeAsync(exePath, tempManifestPath, taskContext, cancellationToken);
        }
        finally
        {
            TryDeleteFile(tempManifestPath);
        }
    }

    private IEnumerable<FileInfo> GetComponents(Dictionary<string, string> packageDependencies)
    {
        var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();

        // Find appx fragments in the NuGet global cache (lowercase-id/version/ layout)
        var appxFragments = packageDependencies
            .Select(package => new FileInfo(Path.Combine(nugetCacheDir.FullName, package.Key.ToLowerInvariant(), package.Value, "runtimes-framework", "package.appxfragment")))
            .Where(f => f.Exists);
        return appxFragments;
    }

    /// <summary>
    /// Collects all user NuGet packages from .csproj or winapp.yaml.
    /// Returns the full package dictionary (name → version) for WinRT component scanning.
    /// </summary>
    internal async Task<Dictionary<string, string>> GetAllUserPackagesAsync(DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Path 1: Try .csproj via `dotnet list package --format json` (cached)
        if (dotNetPackageList != null)
        {
            try
            {
                var allPackages = dotNetPackageList.Projects?
                    .SelectMany(p => p.Frameworks ?? [])
                    .SelectMany(f => (f.TopLevelPackages ?? []).Concat(f.TransitivePackages ?? []));

                if (allPackages != null)
                {
                    foreach (var pkg in allPackages)
                    {
                        if (!string.IsNullOrEmpty(pkg.Id) && !string.IsNullOrEmpty(pkg.ResolvedVersion))
                        {
                            packages.TryAdd(pkg.Id, pkg.ResolvedVersion);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not retrieve package list from .csproj: {NugetErrorMessage.Redact(ex.Message)}");
            }
        }

        // Path 2: Fall back to winapp.yaml (C++ / native projects)
        if (packages.Count == 0 && configService.Exists())
        {
            var config = configService.Load();
            foreach (var pkg in config.Packages)
            {
                packages.TryAdd(pkg.Name, pkg.Version);
            }
        }

        return packages;
    }

    /// <summary>
    /// Discovers third-party WinRT components and appends their activatable class
    /// entries to the in-memory SxS manifest (for self-contained deployment).
    /// </summary>
    private async Task AppendThirdPartyWinRTManifestEntriesAsync(
        StringBuilder sb,
        string architecture,
        DotNetPackageListJson? dotNetPackageList,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var allPackages = await GetAllUserPackagesAsync(dotNetPackageList, taskContext, cancellationToken);
        if (allPackages.Count == 0)
        {
            return;
        }

        var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();

        // DiscoverWinRTComponents filters out packages that have a package.appxfragment
        // (WinAppSDK sub-packages), and only returns packages with both a .winmd and a matching DLL.
        // We do NOT exclude the full WinAppSDK dependency tree because packages like WebView2
        // are transitive WinAppSDK deps but need their own InProcessServer entries.
        var components = winmdService.DiscoverWinRTComponents(nugetCacheDir, allPackages, architecture);
        if (components.Count == 0)
        {
            return;
        }

        taskContext.AddDebugMessage($"{UiSymbols.Package} Found {components.Count} third-party WinRT component(s) to register");

        // Build a set of DLL names already registered in the manifest (from WinAppSDK fragments)
        // so we can do exact-name dedup instead of substring matching.
        var registeredDlls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in SxsFileNameRegex().Matches(sb.ToString()))
        {
            registeredDlls.Add(match.Groups[1].Value);
        }

        foreach (var component in components)
        {
            var classes = winmdService.GetActivatableClasses(component.WinmdPath);
            if (classes.Count == 0)
            {
                continue;
            }

            // Skip components whose DLL is already in the manifest (from WinAppSDK fragments
            // or a previous iteration) to avoid duplicate activatableClass entries.
            if (!registeredDlls.Add(component.ImplementationDll))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} Skipping {component.ImplementationDll} — already in manifest");
                continue;
            }

            taskContext.AddDebugMessage($"{UiSymbols.Note} Registering {classes.Count} activatable class(es) from {component.ImplementationDll}");

            sb.AppendLine($"    <asmv3:file name='{component.ImplementationDll}'>");
            foreach (var className in classes)
            {
                sb.AppendLine($"        <winrtv1:activatableClass name='{className}' threadingModel='both'/>");
            }
            sb.AppendLine("    </asmv3:file>");
        }
    }

    /// <summary>
    /// Discovers third-party WinRT components and generates InProcessServer
    /// extension entries for AppxManifest.xml (for packaged apps).
    /// </summary>
    private async Task<string> AddThirdPartyWinRTExtensionsToAppxManifestAsync(
        string manifestContent,
        DotNetPackageListJson? dotNetPackageList,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        var allPackages = await GetAllUserPackagesAsync(dotNetPackageList, taskContext, cancellationToken);
        if (allPackages.Count == 0)
        {
            return manifestContent;
        }

        var nugetCacheDir = nugetService.GetNuGetGlobalPackagesDir();
        var architecture = WorkspaceSetupService.GetSystemArchitecture();

        // DiscoverWinRTComponents filters out packages that have a package.appxfragment
        // (WinAppSDK sub-packages), and only returns packages with both a .winmd and a matching DLL.
        // We do NOT exclude the full WinAppSDK dependency tree because packages like WebView2
        // are transitive WinAppSDK deps but need their own InProcessServer entries.
        var components = winmdService.DiscoverWinRTComponents(nugetCacheDir, allPackages, architecture);
        if (components.Count == 0)
        {
            return manifestContent;
        }

        taskContext.AddDebugMessage($"{UiSymbols.Package} Adding InProcessServer entries for {components.Count} third-party WinRT component(s)");

        var doc = AppxManifestDocument.Parse(manifestContent);

        // Build a set of DLL names already registered in the manifest
        // so we can do exact-name dedup instead of substring matching.
        var registeredDlls = doc.GetRegisteredExtensionDllPaths();

        var addedAny = false;
        foreach (var component in components)
        {
            var classes = winmdService.GetActivatableClasses(component.WinmdPath);
            if (classes.Count == 0)
            {
                continue;
            }

            // Skip components whose DLL is already in the manifest or in entries we've already generated
            if (!registeredDlls.Add(component.ImplementationDll))
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} Skipping {component.ImplementationDll} — already in manifest");
                continue;
            }

            taskContext.AddDebugMessage($"{UiSymbols.Note} Adding {classes.Count} activatable class(es) for {component.ImplementationDll}");

            doc.AddInProcessServerExtension(component.ImplementationDll, classes);
            addedAny = true;
        }

        return addedAny ? doc.ToXml() : manifestContent;
    }

    /// <summary>
    /// Inserts Package-level extension entries (e.g. InProcessServer) into a manifest string.
    /// Correctly distinguishes Package-level &lt;Extensions&gt; from Application-level ones.
    /// </summary>
    internal static string InsertPackageLevelExtensions(string manifestContent, string extensionEntries)
    {
        var doc = AppxManifestDocument.Parse(manifestContent);
        var extensions = doc.GetOrCreatePackageLevelExtensionsElement();

        // Parse the raw extension entries as XML fragments and add them
        var wrapper = XElement.Parse($"<_wrap xmlns=\"{AppxManifestDocument.DefaultNs}\">{extensionEntries}</_wrap>");
        foreach (var entry in wrapper.Elements())
        {
            extensions.Add(entry);
        }

        return doc.ToXml();
    }

    /// <summary>
    /// Generates Win32 SxS manifest entries from AppX manifests (Package or Fragment format).
    /// Auto-detects the root element name (Package vs Fragment) per document.
    /// </summary>
    /// <param name="sb">StringBuilder to append manifest entries to</param>
    /// <param name="redirectDlls">Whether to redirect DLLs to %MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%</param>
    /// <param name="inDllFiles">List of DLL file names to track</param>
    /// <param name="inAppxManifests">List of paths to the input AppX manifest files or fragments</param>
    internal static void AppendAppManifestFromAppx(
        StringBuilder sb,
        bool redirectDlls,
        IEnumerable<string> inDllFiles,
        IEnumerable<FileInfo> inAppxManifests)
    {
        var dllFileFormat = redirectDlls ?
            @"    <asmv3:file name='{0}' loadFrom='%MICROSOFT_WINDOWSAPPRUNTIME_BASE_DIRECTORY%{0}'>" :
            @"    <asmv3:file name='{0}'>";

        var dllFiles = inDllFiles.ToList();
        var hasPackageManifest = false;

        foreach (var inAppxManifest in inAppxManifests)
        {
            XmlDocument doc = new();
            doc.Load(inAppxManifest.FullName);

            // Auto-detect root element name (Package or Fragment)
            var prefix = doc.DocumentElement?.LocalName ?? "Package";
            var isPackage = prefix == "Package";
            if (isPackage)
            {
                hasPackageManifest = true;
            }

            var nsmgr = new XmlNamespaceManager(doc.NameTable);
            nsmgr.AddNamespace("m", "http://schemas.microsoft.com/appx/manifest/foundation/windows10");
            // Add InProcessServer elements to the generated appxmanifest
            var xQuery = $"./m:{prefix}/m:Extensions/m:Extension/m:InProcessServer";
            XmlNodeList? inProcessServers = doc.SelectNodes(xQuery, nsmgr);
            if (inProcessServers != null)
            {
                foreach (XmlNode winRTFactory in inProcessServers)
                {
                    var dllFileNode = winRTFactory.SelectSingleNode("./m:Path", nsmgr);
                    if (dllFileNode == null)
                    {
                        continue;
                    }

                    var dllFile = dllFileNode.InnerText;
                    var typesNames = winRTFactory.SelectNodes("./m:ActivatableClass", nsmgr)?.OfType<XmlNode>();
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine();
                    if (typesNames != null)
                    {
                        foreach (var typeNode in typesNames)
                        {
                            var attribs = typeNode.Attributes?.OfType<XmlAttribute>().ToArray();
                            var typeName = attribs
                                ?.OfType<XmlAttribute>()
                                ?.SingleOrDefault(x => x.Name == "ActivatableClassId")
                                ?.InnerText;
                            var xmlEntryFormat =
        @"        <winrtv1:activatableClass name='{0}' threadingModel='both'/>";
                            sb.AppendFormat(xmlEntryFormat, typeName);
                            sb.AppendLine();
                            dllFiles.RemoveAll(e => e.Equals(dllFile, StringComparison.OrdinalIgnoreCase));
                        }
                    }
                    sb.AppendLine(@"    </asmv3:file>");
                }
            }

            // Only for Package manifests with redirect
            if (isPackage && redirectDlls)
            {
                foreach (var dllFile in dllFiles)
                {
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine(@"</asmv3:file>");
                }
            }
            // Add ProxyStub elements to the generated appxmanifest
            dllFiles = [.. inDllFiles];

            xQuery = $"./m:{prefix}/m:Extensions/m:Extension/m:ProxyStub";
            var inProcessProxystubs = doc.SelectNodes(xQuery, nsmgr);
            if (inProcessProxystubs != null)
            {
                foreach (XmlNode proxystub in inProcessProxystubs)
                {
                    var classIDAdded = false;

                    var dllFileNode = proxystub.SelectSingleNode("./m:Path", nsmgr);
                    var dllFile = dllFileNode?.InnerText;
                    // exclude PushNotificationsLongRunningTask, which requires the Singleton (which is unavailable for self-contained apps)
                    // exclude Widgets entries unless/until they have been tested and verified by the Widgets team
                    if (dllFile == null || dllFile == "PushNotificationsLongRunningTask.ProxyStub.dll" || dllFile == "Microsoft.Windows.Widgets.dll")
                    {
                        continue;
                    }
                    var typesNamesForProxy = proxystub.SelectNodes("./m:Interface", nsmgr)?.OfType<XmlNode>();
                    sb.AppendFormat(dllFileFormat, dllFile);
                    sb.AppendLine();
                    if (typesNamesForProxy != null)
                    {
                        foreach (var typeNode in typesNamesForProxy)
                        {
                            if (!classIDAdded)
                            {
                                var classIdAttribute = proxystub.Attributes?.OfType<XmlAttribute>().ToArray();
                                var classID = classIdAttribute
                                    ?.OfType<XmlAttribute>()
                                    ?.SingleOrDefault(x => x.Name == "ClassId")
                                    ?.InnerText;

                                if (classID != null)
                                {
                                    var xmlEntryFormat = @"        <asmv3:comClass clsid='{{{0}}}'/>"; 
                                    sb.AppendFormat(xmlEntryFormat, classID);
                                    classIDAdded = true;
                                }
                            }
                            var attribs = typeNode.Attributes?.OfType<XmlAttribute>().ToArray();
                            var typeID = attribs
                                ?.OfType<XmlAttribute>()
                                ?.SingleOrDefault(x => x.Name == "InterfaceId")
                                ?.InnerText;
                            var typeNames = attribs
                                ?.OfType<XmlAttribute>()
                                ?.SingleOrDefault(x => x.Name == "Name")
                                ?.InnerText;
                            var xmlEntryFormatForStubs = @"        <asmv3:comInterfaceProxyStub name='{0}' iid='{{{1}}}'/>"; 
                            if (typeNames != null && typeID != null)
                            {
                                sb.AppendFormat(xmlEntryFormatForStubs, typeNames, typeID);
                                sb.AppendLine();
                                dllFiles.RemoveAll(e => e.Equals(dllFile, StringComparison.OrdinalIgnoreCase));
                            }
                        }
                    }
                    sb.AppendLine(@"    </asmv3:file>");
                }
            }
        }

        if (hasPackageManifest && redirectDlls)
        {
            foreach (var dllFile in dllFiles)
            {
                sb.AppendFormat(dllFileFormat, dllFile);
                sb.AppendLine(@"</asmv3:file>");
            }
        }
    }

    /// <summary>
    /// Updates or inserts the Windows App SDK dependency in the manifest
    /// </summary>
    /// <param name="manifestContent">The manifest content to modify</param>
    /// <returns>The modified manifest content</returns>
    private async Task<string> UpdateWindowsAppSdkDependencyAsync(string manifestContent, DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        var winAppSdkInfo = await GetWindowsAppSdkDependencyInfoAsync(dotNetPackageList, taskContext, cancellationToken);

        if (winAppSdkInfo == null)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not determine Windows App SDK version, skipping dependency update");
            return manifestContent;
        }

        var doc = AppxManifestDocument.Parse(manifestContent);
        const string publisher = "CN=Microsoft Corporation, O=Microsoft Corporation, L=Redmond, S=Washington, C=US";

        var dependencies = doc.GetDependenciesElement();
        if (dependencies == null)
        {
            // Create Dependencies element and insert before Applications (per AppxManifest schema order)
            dependencies = new XElement(AppxManifestDocument.DefaultNs + "Dependencies");
            var applications = doc.Document.Root?.Element(AppxManifestDocument.DefaultNs + "Applications");
            if (applications != null)
            {
                applications.AddBeforeSelf(dependencies);
            }
            else
            {
                doc.Document.Root?.Add(dependencies);
            }

            dependencies.Add(new XElement(AppxManifestDocument.DefaultNs + "PackageDependency",
                new XAttribute("Name", winAppSdkInfo.RuntimeName),
                new XAttribute("MinVersion", winAppSdkInfo.MinVersion),
                new XAttribute("Publisher", publisher)));

            taskContext.AddDebugMessage($"{UiSymbols.Package} Added Windows App SDK dependency {winAppSdkInfo.RuntimeName} (v{winAppSdkInfo.MinVersion})");
        }
        else
        {
            // Check for existing WindowsAppRuntime dependency (prefix match for version-specific names)
            var existing = dependencies.Elements(AppxManifestDocument.DefaultNs + "PackageDependency")
                .FirstOrDefault(e => e.Attribute("Name")?.Value?.StartsWith("Microsoft.WindowsAppRuntime.", StringComparison.OrdinalIgnoreCase) == true);

            if (existing != null)
            {
                existing.SetAttributeValue("Name", winAppSdkInfo.RuntimeName);
                existing.SetAttributeValue("MinVersion", winAppSdkInfo.MinVersion);
                existing.SetAttributeValue("Publisher", publisher);

                taskContext.AddDebugMessage($"{UiSymbols.Sync} Updated Windows App SDK dependency to {winAppSdkInfo.RuntimeName} v{winAppSdkInfo.MinVersion}");
            }
            else
            {
                dependencies.Add(new XElement(AppxManifestDocument.DefaultNs + "PackageDependency",
                    new XAttribute("Name", winAppSdkInfo.RuntimeName),
                    new XAttribute("MinVersion", winAppSdkInfo.MinVersion),
                    new XAttribute("Publisher", publisher)));

                taskContext.AddDebugMessage($"{UiSymbols.Add} Added Windows App SDK dependency {winAppSdkInfo.RuntimeName} to existing Dependencies section (v{winAppSdkInfo.MinVersion})");
            }
        }

        return doc.ToXml();
    }

    /// <summary>
    /// Gets the Windows App SDK dependency information from the locked winapp.yaml config and package source
    /// </summary>
    /// <returns>The dependency information, or null if not found</returns>
    private async Task<WindowsAppRuntimePackageInfo?> GetWindowsAppSdkDependencyInfoAsync(DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        try
        {
            var msixDir = await GetRuntimeMsixDirAsync(dotNetPackageList, taskContext, cancellationToken);
            if (msixDir == null)
            {
                return null;
            }

            // Get the runtime package information from the MSIX inventory
            var runtimeInfo = GetWindowsAppRuntimePackageInfo(taskContext, msixDir, cancellationToken);
            if (runtimeInfo == null)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Warning} Could not parse Windows App Runtime package information from MSIX inventory");
                return null;
            }

            return runtimeInfo;
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Error getting Windows App SDK dependency info: {NugetErrorMessage.Redact(ex.Message)}");
            return null;
        }
    }

    private async Task<DirectoryInfo?> GetRuntimeMsixDirAsync(DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken, bool requireExactVersion = false)
    {
        (var packageDependencies, var mainVersion) = await GetWinAppSDKPackageDependenciesAsync(dotNetPackageList, taskContext, cancellationToken);
        if (packageDependencies == null || mainVersion == null)
        {
            return null;
        }

        // Look for the runtime package in the package dependencies
        var runtimePackage = packageDependencies.FirstOrDefault(kvp =>
            kvp.Key.StartsWith(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, StringComparison.OrdinalIgnoreCase));

        // Create a dictionary with versions for FindWindowsAppSdkMsixDirectory
        var usedVersions = new Dictionary<string, string>
        {
            [BuildToolsService.WINAPP_SDK_PACKAGE] = mainVersion
        };

        if (runtimePackage.Key != null)
        {
            // For Windows App SDK 1.8+, there's a separate runtime package
            var runtimeVersion = runtimePackage.Value;
            usedVersions[runtimePackage.Key] = runtimeVersion;

            taskContext.AddDebugMessage($"{UiSymbols.Package} Found runtime package: {runtimePackage.Key} v{runtimeVersion}");
        }
        else
        {
            // For Windows App SDK 1.7 and earlier, runtime is included in the main package
            taskContext.AddDebugMessage($"{UiSymbols.Note} No separate runtime package found - using main package (Windows App SDK 1.7 or earlier)");
            taskContext.AddDebugMessage($"{UiSymbols.Note} Available package dependencies: {string.Join(", ", packageDependencies.Keys)}");
        }

        // Find the MSIX directory with the runtime package
        var msixDir = windowsAppRuntimeService.FindWindowsAppSdkMsixDirectory(usedVersions, requireExactVersion);
        if (msixDir == null)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Windows App SDK MSIX directory not found for dependent runtime package");
            return null;
        }

        return msixDir;
    }

    internal async Task<(Dictionary<string, string>? CachedPackages, string? MainVersion)> GetWinAppSDKPackageDependenciesAsync(DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken)
    {
        string? mainVersion = null;
        string? localRuntimeId = null;
        string? localRuntimeVersion = null;

        // Path 1: Try .csproj via `dotnet list package --format json`
        if (dotNetPackageList != null)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Package} Querying NuGet package list...");

            var allPackages = dotNetPackageList.Projects?
                .SelectMany(p => p.Frameworks ?? [])
                .SelectMany(f => (f.TopLevelPackages ?? []).Concat(f.TransitivePackages ?? []))
                .ToList();

            var winAppSdkPkg = allPackages?
                .FirstOrDefault(p => string.Equals(p.Id, BuildToolsService.WINAPP_SDK_PACKAGE, StringComparison.OrdinalIgnoreCase));

            if (winAppSdkPkg != null && !string.IsNullOrEmpty(winAppSdkPkg.ResolvedVersion))
            {
                mainVersion = winAppSdkPkg.ResolvedVersion;
            }

            // Windows App SDK 1.8+ ships the runtime as a separate NuGet package that is a transitive
            // dependency of the main package — so a restored project already lists it (with the exact
            // version on disk). Resolve it here to avoid the network round-trip below: an offline
            // `--no-build` run must not need to reach NuGet when the runtime is already in the cache.
            var runtimePkg = allPackages?
                .FirstOrDefault(p => !string.IsNullOrEmpty(p.Id)
                    && p.Id.StartsWith(BuildToolsService.WINAPP_SDK_RUNTIME_PACKAGE, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrEmpty(p.ResolvedVersion));
            if (runtimePkg != null)
            {
                localRuntimeId = runtimePkg.Id;
                localRuntimeVersion = runtimePkg.ResolvedVersion;
            }
        }

        // Path 2: Fall back to winapp.yaml (C++ / native projects)
        if (string.IsNullOrEmpty(mainVersion) && configService.Exists())
        {
            var config = configService.Load();
            mainVersion = config.GetVersion(BuildToolsService.WINAPP_SDK_PACKAGE);
        }

        // Prefer the runtime package resolved from the restored package graph — no network needed.
        // Also covers the framework-dependent app that pulls in the Windows App SDK via the runtime
        // (and other sub-packages) without the meta Microsoft.WindowsAppSDK package: mainVersion is
        // then unavailable, but the runtime version alone locates the exact MSIX.
        if (localRuntimeId != null && localRuntimeVersion != null)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Package} Resolved runtime package locally from restored package list: {localRuntimeId} v{localRuntimeVersion}");
            var localDeps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [localRuntimeId] = localRuntimeVersion,
            };
            if (!string.IsNullOrEmpty(mainVersion))
            {
                localDeps[BuildToolsService.WINAPP_SDK_PACKAGE] = mainVersion;
            }
            // The runtime version is authoritative for the MSIX lookup; fall back to it as the
            // returned main version when the meta package isn't in the graph.
            return (localDeps, mainVersion ?? localRuntimeVersion);
        }

        if (string.IsNullOrEmpty(mainVersion))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} No {BuildToolsService.WINAPP_SDK_PACKAGE} package found");
            return (null, null);
        }
        taskContext.AddDebugMessage($"{UiSymbols.Package} Found Windows App SDK main package: v{mainVersion}");

        try
        {
            // Query the NuGet API for the dependency tree. Only reached when the runtime package
            // wasn't in the restored list — e.g. the winapp.yaml/C++ path (no dotnet package list),
            // or Windows App SDK 1.7 and earlier where the runtime ships inside the main package.
            var deps = await nugetService.GetPackageDependenciesAsync(BuildToolsService.WINAPP_SDK_PACKAGE, mainVersion, cancellationToken);

            // Include the main package itself in the result
            deps.TryAdd(BuildToolsService.WINAPP_SDK_PACKAGE, mainVersion);

            return (deps, mainVersion);
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} {BuildToolsService.WINAPP_SDK_PACKAGE} v{mainVersion} not found in package source: {NugetErrorMessage.Redact(ex.Message)}");
        }

        return (null, null);
    }

    /// <summary>
    /// Parses the MSIX inventory file to extract Windows App Runtime package information
    /// </summary>
    /// <param name="msixDir">The MSIX directory containing the inventory file</param>
    /// <returns>Package information, or null if not found</returns>
    private static WindowsAppRuntimePackageInfo? GetWindowsAppRuntimePackageInfo(TaskContext taskContext, DirectoryInfo msixDir, CancellationToken cancellationToken)
    {
        try
        {
            // Use the shared inventory parsing logic (synchronous version)
            var packageEntries = WindowsAppRuntimeService.ParseMsixInventoryAsync(taskContext, msixDir, cancellationToken).GetAwaiter().GetResult();

            if (packageEntries == null || packageEntries.Count == 0)
            {
                return null;
            }

            // Look for the Windows App Runtime main package (not Framework packages)
            var mainRuntimeEntry = packageEntries
                .FirstOrDefault(entry => entry.PackageIdentity.StartsWith("Microsoft.WindowsAppRuntime.") &&
                                       !entry.PackageIdentity.Contains("Framework"));

            if (mainRuntimeEntry != null)
            {
                // Parse the PackageIdentity (format: Name_Version_Architecture_PublisherId)
                var identityParts = mainRuntimeEntry.PackageIdentity.Split('_');
                if (identityParts.Length >= 2)
                {
                    var runtimeName = identityParts[0];
                    var version = identityParts[1];

                    taskContext.AddDebugMessage($"{UiSymbols.Package} Found Windows App Runtime: {runtimeName} v{version}");

                    return new WindowsAppRuntimePackageInfo
                    {
                        RuntimeName = runtimeName,
                        MinVersion = version
                    };
                }
            }

            taskContext.AddDebugMessage($"{UiSymbols.Note} No Windows App Runtime main package found in inventory");
            taskContext.AddDebugMessage($"{UiSymbols.Note} Available packages: {string.Join(", ", packageEntries.Select(e => e.PackageIdentity))}");

            return null;
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} Error parsing MSIX inventory: {ex.Message}");
            return null;
        }
    }

    private static readonly string[] patterns = new[] { "*.dll", "workloads*.json", "restartAgent.exe", "map.html", "*.mui", "*.png", "*.winmd", "*.xaml", "*.xbf", "*.pri" };

    private static async Task CopyRuntimeFilesAsync(DirectoryInfo extractedDir, DirectoryInfo deploymentDir, TaskContext taskContext, CancellationToken cancellationToken)
    {
        await taskContext.AddSubTaskAsync("Copying Runtime Files", (taskContext, cancellationToken) =>
        {
            foreach (var pattern in patterns)
            {
                var files = extractedDir.GetFiles(pattern, SearchOption.AllDirectories);
                foreach (var file in files)
                {
                    var relativePath = Path.GetRelativePath(extractedDir.FullName, file.FullName);
                    var destPath = Path.Combine(deploymentDir.FullName, relativePath);

                    // Create destination directory if needed
                    var destDir = Path.GetDirectoryName(destPath);
                    if (!string.IsNullOrEmpty(destDir))
                    {
                        Directory.CreateDirectory(destDir);
                    }

                    file.CopyTo(destPath, overwrite: true);

                    taskContext.AddDebugMessage($"{UiSymbols.Files} {relativePath}");
                }
            }

            return Task.FromResult(0);
        }, cancellationToken);
    }

    /// <summary>
    /// Prepares Windows App SDK runtime files for packaging into an MSIX by extracting them to the input folder
    /// </summary>
    /// <param name="inputFolder">The folder where runtime files should be copied</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The path to the self-contained deployment directory</returns>
    private async Task<DirectoryInfo> PrepareRuntimeForPackagingAsync(DirectoryInfo inputFolder, DotNetPackageListJson? dotNetPackageList, TaskContext taskContext, CancellationToken cancellationToken, string? overrideArch = null)
    {
        var arch = overrideArch ?? WorkspaceSetupService.GetSystemArchitecture();

        var winappDir = winappDirectoryService.GetLocalWinappDirectory();

        // Extract runtime files using the existing method
        await SetupSelfContainedAsync(winappDir, arch, taskContext, dotNetPackageList, cancellationToken);

        // Copy runtime files from .winapp/self-contained to input folder
        var runtimeSourceDir = new DirectoryInfo(Path.Combine(winappDir.FullName, "self-contained", arch, "deployment"));

        MergeRuntimeFilesIntoStaging(runtimeSourceDir, inputFolder, taskContext);

        return runtimeSourceDir;
    }

    /// <summary>
    /// Copies runtime files from <paramref name="runtimeSourceDir"/> into <paramref name="stagingDir"/>,
    /// skipping any file that already exists in the staging directory. This ensures the app's own
    /// assets (resources.pri, Assets\StoreLogo.png, etc.) are never overwritten by their runtime equivalents.
    /// </summary>
    internal static void MergeRuntimeFilesIntoStaging(DirectoryInfo runtimeSourceDir, DirectoryInfo stagingDir, TaskContext? taskContext = null)
    {
        if (!runtimeSourceDir.Exists)
        {
            throw new DirectoryNotFoundException($"Runtime files not found at {runtimeSourceDir}");
        }

        foreach (var file in runtimeSourceDir.GetFiles("*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(runtimeSourceDir.FullName, file.FullName);
            var destFile = Path.Combine(stagingDir.FullName, relativePath);

            // Create destination directory if needed
            var destDir = Path.GetDirectoryName(destFile);
            if (!string.IsNullOrEmpty(destDir))
            {
                Directory.CreateDirectory(destDir);
            }

            // Never overwrite existing app files with runtime equivalents. The staging
            // directory already contains the app's own assets, PRI, and resources — these
            // must take precedence. In particular, overwriting resources.pri would destroy
            // the app's MRT mappings (e.g. SplashScreen.png → SplashScreen.scale-200.png)
            // and cause deployment failure (0x80070002). Similarly, the runtime's placeholder
            // Assets\StoreLogo.png must not replace the app's branded icons.
            if (File.Exists(destFile))
            {
                taskContext?.AddDebugMessage($"{UiSymbols.Note} Skipping (app file exists): {relativePath}");
                continue;
            }

            file.CopyTo(destFile, overwrite: false);

            taskContext?.AddDebugMessage($"{UiSymbols.Folder} Bundled runtime: {relativePath}");
        }

        taskContext?.AddDebugMessage($"{UiSymbols.Check} Windows App SDK runtime bundled into package");
    }
}
