// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Reads the NuGet packages a C++ project (<c>.vcxproj</c>) pins in the <c>packages.config</c> beside it,
/// in the same shape <c>dotnet package list</c> returns, so the Windows App SDK version used for runtime
/// provisioning comes from the project like it does for a <c>.csproj</c>.
/// </summary>
internal static class PackagesConfigReader
{
    /// <summary>
    /// Returns the project's packages. A project without <c>packages.config</c> references no NuGet packages
    /// (Visual Studio does not support <c>PackageReference</c> for native C++), so it yields an empty list.
    /// Returns <see langword="null"/> when the file exists but cannot be read, so callers treat the graph as unknown.
    /// </summary>
    public static DotNetPackageListJson? Read(FileInfo project)
    {
        var config = new FileInfo(Path.Join(project.DirectoryName, "packages.config"));
        List<DotNetPackage> packages = [];
        if (config.Exists)
        {
            try
            {
                packages = XDocument.Load(config.FullName).Root?
                    .Elements("package")
                    .Select(p => (Id: (string?)p.Attribute("id"), Version: (string?)p.Attribute("version")))
                    .Where(p => !string.IsNullOrWhiteSpace(p.Id) && !string.IsNullOrWhiteSpace(p.Version))
                    .Select(p => new DotNetPackage(p.Id!, p.Version!, p.Version!))
                    .ToList() ?? [];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                return null;
            }
        }

        return new DotNetPackageListJson([new DotNetProject([new DotNetFramework("native", packages, [])])]);
    }
}
