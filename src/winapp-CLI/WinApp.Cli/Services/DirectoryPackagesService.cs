// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Xml;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

/// <summary>
/// Service for managing Directory.Packages.props files
/// </summary>
internal class DirectoryPackagesService : IDirectoryPackagesService
{
    private const string DirectoryPackagesFileName = "Directory.Packages.props";

    /// <summary>
    /// Updates Directory.Packages.props in the specified directory to match versions from winapp.yaml
    /// </summary>
    /// <param name="configDir">Directory containing winapp.yaml and potentially Directory.Packages.props</param>
    /// <param name="packageVersions">Dictionary of package names to versions from winapp.yaml</param>
    /// <returns>True if file was found and updated, false otherwise</returns>
    public bool UpdatePackageVersions(DirectoryInfo configDir, Dictionary<string, string> packageVersions, TaskContext taskContext)
    {
        var propsFilePath = Path.Combine(configDir.FullName, DirectoryPackagesFileName);

        if (!File.Exists(propsFilePath))
        {
            taskContext.AddDebugMessage($"{UiSymbols.Note} No {DirectoryPackagesFileName} found in {configDir.FullName}");
            return false;
        }

        try
        {
            taskContext.AddStatusMessage($"{UiSymbols.Wrench} Updating {DirectoryPackagesFileName} to match winapp.yaml versions...");

            // Load the XML document with whitespace preservation
            var doc = new XmlDocument();
            doc.PreserveWhitespace = true;
            doc.Load(propsFilePath);

            if (doc.DocumentElement == null)
            {
                taskContext.AddStatusMessage($"{UiSymbols.Note} {DirectoryPackagesFileName} has no root element");
                return false;
            }

            // Find all PackageVersion elements using XPath
            var packageVersionNodes = doc.SelectNodes("//PackageVersion");

            if (packageVersionNodes == null || packageVersionNodes.Count == 0)
            {
                taskContext.AddDebugMessage($"{UiSymbols.Note} No PackageVersion elements found in {DirectoryPackagesFileName}");
                return false;
            }

            var updated = 0;

            foreach (XmlNode packageVersion in packageVersionNodes)
            {
                if (packageVersion.Attributes == null)
                {
                    continue;
                }

                var includeAttr = packageVersion.Attributes["Include"];
                var versionAttr = packageVersion.Attributes["Version"];

                if (includeAttr == null || versionAttr == null)
                {
                    continue;
                }

                var packageName = includeAttr.Value;

                // Check if this package is in our winapp.yaml config
                if (packageVersions.TryGetValue(packageName, out var newVersion))
                {
                    var oldVersion = versionAttr.Value;

                    if (oldVersion != newVersion)
                    {
                        versionAttr.Value = newVersion;
                        updated++;
                        taskContext.AddStatusMessage($"{UiSymbols.Check} Updated {packageName}: {oldVersion} → {newVersion}");
                    }
                    else
                    {
                        taskContext.AddDebugMessage($"{UiSymbols.Check} {packageName} already at version {newVersion}");
                    }
                }
            }

            if (updated > 0)
            {
                // Save the document - PreserveWhitespace will maintain original formatting
                doc.Save(propsFilePath);

                taskContext.AddStatusMessage($"{UiSymbols.Save} Updated {updated} package version(s) in {DirectoryPackagesFileName}");
                return true;
            }
            else
            {
                taskContext.AddStatusMessage($"{UiSymbols.Check} No package versions needed updating in {DirectoryPackagesFileName}");
                return true;
            }
        }
        catch (Exception ex)
        {
            taskContext.AddStatusMessage($"{UiSymbols.Note} Failed to update {DirectoryPackagesFileName}: {ex.Message}");
            if (ex.StackTrace != null)
            {
                taskContext.AddDebugMessage(ex.StackTrace);
            }

            return false;
        }
    }
}
