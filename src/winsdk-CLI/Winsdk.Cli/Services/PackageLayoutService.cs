// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Winsdk.Cli.Services;

internal sealed class PackageLayoutService : IPackageLayoutService
{
    public void CopyIncludesFromPackages(DirectoryInfo pkgsDir, DirectoryInfo includeOut)
    {
        includeOut.Create();
        foreach (var includeDir in SafeEnumDirs(pkgsDir, "include", SearchOption.AllDirectories))
        {
            foreach (var file in SafeEnumFiles(includeDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(includeOut.FullName, file.Name);
                TryCopy(file, target);
            }
        }
    }

    public void CopyLibs(DirectoryInfo pkgsDir, DirectoryInfo libOut, string arch)
    {
        libOut.Create();
        foreach (var libDir in SafeEnumDirs(pkgsDir, "lib", SearchOption.AllDirectories))
        {
            var archDir = new DirectoryInfo(Path.Combine(libDir.FullName, arch));
            var nativeArchDir = new DirectoryInfo(Path.Combine(libDir.FullName, "native", arch));
            var winArchDir = new DirectoryInfo(Path.Combine(libDir.FullName, $"win-{arch}"));
            var win10ArchDir = new DirectoryInfo(Path.Combine(libDir.FullName, $"win10-{arch}"));
            var nativeWin10ArchDir = new DirectoryInfo(Path.Combine(libDir.FullName, "native", $"win10-{arch}"));
            
            CopyTopFiles(archDir, "*.lib", libOut);
            CopyTopFiles(nativeArchDir, "*.lib", libOut);
            CopyTopFiles(winArchDir, "*.lib", libOut);
            CopyTopFiles(win10ArchDir, "*.lib", libOut);
            CopyTopFiles(nativeWin10ArchDir, "*.lib", libOut);
        }
    }

    public void CopyRuntimes(DirectoryInfo pkgsDir, DirectoryInfo binOut, string arch)
    {
        binOut.Create();
        foreach (var rtDir in SafeEnumDirs(pkgsDir, "runtimes", SearchOption.AllDirectories))
        {
            var native = new DirectoryInfo(Path.Combine(rtDir.FullName, $"win-{arch}", "native"));
            CopyTopFiles(native, "*.*", binOut);
        }
    }

    public IEnumerable<FileInfo> FindWinmds(DirectoryInfo pkgsDir, Dictionary<string, string> usedVersions)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Only search in package directories that were actually used
        foreach (var (packageName, version) in usedVersions)
        {
            var packageDir = new DirectoryInfo(Path.Combine(pkgsDir.FullName, $"{packageName}.{version}"));
            if (!packageDir.Exists)
            {
                continue;
            }

            // Search for metadata directories within this specific package
            foreach (var metadataDir in SafeEnumDirs(packageDir, "metadata", SearchOption.AllDirectories))
            {
                foreach (var f in SafeEnumFiles(metadataDir, "*.winmd", SearchOption.TopDirectoryOnly))
                {
                    results.Add(f.FullName);
                }

                var v18362 = new DirectoryInfo(Path.Combine(metadataDir.FullName, "10.0.18362.0"));
                foreach (var f in SafeEnumFiles(v18362, "*.winmd", SearchOption.TopDirectoryOnly))
                {
                    results.Add(f.FullName);
                }
            }

            // Search for lib directories within this specific package
            foreach (var libDir in SafeEnumDirs(packageDir, "lib", SearchOption.AllDirectories))
            {
                foreach (var f in SafeEnumFiles(libDir, "*.winmd", SearchOption.TopDirectoryOnly))
                {
                    results.Add(f.FullName);
                }

                var uap10 = new DirectoryInfo(Path.Combine(libDir.FullName, "uap10.0"));
                foreach (var f in SafeEnumFiles(uap10, "*.winmd", SearchOption.TopDirectoryOnly))
                {
                    results.Add(f.FullName);
                }

                var uap18362 = new DirectoryInfo(Path.Combine(libDir.FullName, "uap10.0.18362"));
                foreach (var f in SafeEnumFiles(uap18362, "*.winmd", SearchOption.TopDirectoryOnly))
                {
                    results.Add(f.FullName);
                }
            }

            // Search for References directories within this specific package
            foreach (var refDir in SafeEnumDirs(packageDir, "References", SearchOption.AllDirectories))
            {
                foreach (var f in SafeEnumFiles(refDir, "*.winmd", SearchOption.AllDirectories))
                {
                    results.Add(f.FullName);
                }
            }
        }

        return results.Select(f => new FileInfo(f));
    }

    private static IEnumerable<DirectoryInfo> SafeEnumDirs(DirectoryInfo root, string searchPattern, SearchOption option)
    {
        try { return root.EnumerateDirectories(searchPattern, option); }
        catch { return Array.Empty<DirectoryInfo>(); }
    }

    private static IEnumerable<FileInfo> SafeEnumFiles(DirectoryInfo root, string searchPattern, SearchOption option)
    {
        try { return root.Exists ? root.EnumerateFiles(searchPattern, option) : Array.Empty<FileInfo>(); }
        catch { return Array.Empty<FileInfo>(); }
    }

    private static void CopyTopFiles(DirectoryInfo fromDir, string pattern, DirectoryInfo toDir)
    {
        if (!fromDir.Exists)
        {
            return;
        }

        toDir.Create();
        foreach (var f in fromDir.EnumerateFiles(pattern, SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(toDir.FullName, f.Name);
            TryCopy(f, target);
        }
    }

    private static void TryCopy(FileInfo src, string dst)
    {
        try
        {
            src.CopyTo(dst, overwrite: true);
        }
        catch (IOException)
        {
            // Ignore to keep resilient.
        }
        catch (UnauthorizedAccessException)
        {
            // Ignore and continue.
        }
    }

    private static IEnumerable<DirectoryInfo> SafeEnumSubdirs(DirectoryInfo root)
    {
        try { return root.Exists ? root.EnumerateDirectories() : Array.Empty<DirectoryInfo>(); }
        catch { return Array.Empty<DirectoryInfo>(); }
    }

    public void CopyLibsAllArch(DirectoryInfo pkgsDir, DirectoryInfo libRoot)
    {
        libRoot.Create();
        foreach (var libDir in SafeEnumDirs(pkgsDir, "lib", SearchOption.AllDirectories))
        {
            foreach (var sub in SafeEnumSubdirs(libDir))
            {
                var name = sub.Name;
                if (name.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
                {
                    var arch = name.Substring("win-".Length);
                    var outDir = new DirectoryInfo(Path.Combine(libRoot.FullName, arch));
                    CopyTopFiles(sub, "*.lib", outDir);
                }
                else if (name.StartsWith("win10-", StringComparison.OrdinalIgnoreCase))
                {
                    var arch = name.Substring("win10-".Length);
                    var outDir = new DirectoryInfo(Path.Combine(libRoot.FullName, arch));
                    CopyTopFiles(sub, "*.lib", outDir);
                }
                else if (string.Equals(name, "native", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var d in SafeEnumSubdirs(sub))
                    {
                        var dn = d.Name;
                        if (dn.StartsWith("win10-", StringComparison.OrdinalIgnoreCase))
                        {
                            var arch = dn.Substring("win10-".Length);
                            var outDir = new DirectoryInfo(Path.Combine(libRoot.FullName, arch));
                            CopyTopFiles(d, "*.lib", outDir);
                        }
                    }
                    
                    // Also check for direct arch folders under native
                    foreach (var d in SafeEnumSubdirs(sub))
                    {
                        var dn = d.Name;
                        // Check for direct architecture names (x86, x64, arm, arm64)
                        if (IsValidArchitecture(dn))
                        {
                            var outDir = new DirectoryInfo(Path.Combine(libRoot.FullName, dn));
                            CopyTopFiles(d, "*.lib", outDir);
                        }
                    }
                }
                
                // Handle direct architecture folders
                if (IsValidArchitecture(name))
                {
                    var outDir = new DirectoryInfo(Path.Combine(libRoot.FullName, name));
                    CopyTopFiles(sub, "*.lib", outDir);
                }
            }
        }
    }

    public void CopyRuntimesAllArch(DirectoryInfo pkgsDir, DirectoryInfo binRoot)
    {
        binRoot.Create();
        foreach (var rtDir in SafeEnumDirs(pkgsDir, "runtimes", SearchOption.AllDirectories))
        {
            foreach (var plat in SafeEnumSubdirs(rtDir))
            {
                var name = plat.Name;
                if (name.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
                {
                    var arch = name.Substring("win-".Length);
                    var native = new DirectoryInfo(Path.Combine(plat.FullName, "native"));
                    var outDir = new DirectoryInfo(Path.Combine(binRoot.FullName, arch));
                    CopyTopFiles(native, "*.*", outDir);
                }
            }
        }
    }

    private static bool IsValidArchitecture(string name)
    {
        return string.Equals(name, "x86", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "x64", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "arm", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(name, "arm64", StringComparison.OrdinalIgnoreCase);
    }
}
