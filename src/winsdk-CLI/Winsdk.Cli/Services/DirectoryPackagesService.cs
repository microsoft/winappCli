// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.Xml;
using Winsdk.Cli.Helpers;

namespace Winsdk.Cli.Services;

/// <summary>
/// Service for managing Directory.Packages.props files
/// </summary>
internal class DirectoryPackagesService(ILogger<DirectoryPackagesService> logger) : IDirectoryPackagesService
{
    private const string DirectoryPackagesFileName = "Directory.Packages.props";

    /// <summary>
    /// Updates Directory.Packages.props in the specified directory to match versions from winsdk.yaml
    /// </summary>
    /// <param name="configDir">Directory containing winsdk.yaml and potentially Directory.Packages.props</param>
    /// <param name="packageVersions">Dictionary of package names to versions from winsdk.yaml</param>
    /// <returns>True if file was found and updated, false otherwise</returns>
    public bool UpdatePackageVersions(string configDir, Dictionary<string, string> packageVersions)
    {
        var propsFilePath = Path.Combine(configDir, DirectoryPackagesFileName);
        
        if (!File.Exists(propsFilePath))
        {
            logger.LogDebug("{UISymbol} No {FileName} found in {ConfigDir}", UiSymbols.Note, DirectoryPackagesFileName, configDir);
            return false;
        }

        try
        {
            logger.LogInformation("{UISymbol} Updating {FileName} to match winsdk.yaml versions...", UiSymbols.Wrench, DirectoryPackagesFileName);

            // Load the XML document with whitespace preservation
            var doc = new XmlDocument();
            doc.PreserveWhitespace = true;
            doc.Load(propsFilePath);
            
            if (doc.DocumentElement == null)
            {
                logger.LogWarning("{UISymbol} {FileName} has no root element", UiSymbols.Note, DirectoryPackagesFileName);
                return false;
            }

            // Find all PackageVersion elements using XPath
            var packageVersionNodes = doc.SelectNodes("//PackageVersion");
            
            if (packageVersionNodes == null || packageVersionNodes.Count == 0)
            {
                logger.LogDebug("{UISymbol} No PackageVersion elements found in {FileName}", UiSymbols.Note, DirectoryPackagesFileName);
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
                
                // Check if this package is in our winsdk.yaml config
                if (packageVersions.TryGetValue(packageName, out var newVersion))
                {
                    var oldVersion = versionAttr.Value;
                    
                    if (oldVersion != newVersion)
                    {
                        versionAttr.Value = newVersion;
                        updated++;
                        logger.LogInformation("{UISymbol} Updated {PackageName}: {OldVersion} → {NewVersion}", 
                            UiSymbols.Check, packageName, oldVersion, newVersion);
                    }
                    else
                    {
                        logger.LogDebug("{UISymbol} {PackageName} already at version {Version}", 
                            UiSymbols.Check, packageName, newVersion);
                    }
                }
            }

            if (updated > 0)
            {
                // Save the document - PreserveWhitespace will maintain original formatting
                doc.Save(propsFilePath);

                logger.LogInformation("{UISymbol} Updated {Count} package version(s) in {FileName}", 
                    UiSymbols.Save, updated, DirectoryPackagesFileName);
                return true;
            }
            else
            {
                logger.LogInformation("{UISymbol} No package versions needed updating in {FileName}", 
                    UiSymbols.Check, DirectoryPackagesFileName);
                return true;
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning("{UISymbol} Failed to update {FileName}: {Message}", 
                UiSymbols.Note, DirectoryPackagesFileName, ex.Message);
            logger.LogDebug("{StackTrace}", ex.StackTrace);
            return false;
        }
    }
}
