namespace Winsdk.Cli;

internal sealed class PackageLayoutService
{
    public void CopyIncludesFromPackages(string pkgsDir, string includeOut)
    {
        EnsureDir(includeOut);
        foreach (var includeDir in SafeEnumDirs(pkgsDir, "include", SearchOption.AllDirectories))
        {
            foreach (var file in SafeEnumFiles(includeDir, "*.*", SearchOption.TopDirectoryOnly))
            {
                var target = Path.Combine(includeOut, Path.GetFileName(file));
                TryCopy(file, target);
            }
        }
    }

    public void CopyLibs(string pkgsDir, string libOut, string arch)
    {
        EnsureDir(libOut);
        foreach (var libDir in SafeEnumDirs(pkgsDir, "lib", SearchOption.AllDirectories))
        {
            var cand1 = Path.Combine(libDir, $"win-{arch}");
            var cand2 = Path.Combine(libDir, $"win10-{arch}");
            var cand3 = Path.Combine(libDir, "native", $"win10-{arch}");

            CopyTopFiles(cand1, "*.lib", libOut);
            CopyTopFiles(cand2, "*.lib", libOut);
            CopyTopFiles(cand3, "*.lib", libOut);
        }
    }

    public void CopyRuntimes(string pkgsDir, string binOut, string arch)
    {
        EnsureDir(binOut);
        foreach (var rtDir in SafeEnumDirs(pkgsDir, "runtimes", SearchOption.AllDirectories))
        {
            var native = Path.Combine(rtDir, $"win-{arch}", "native");
            CopyTopFiles(native, "*.*", binOut);
        }
    }

    public IEnumerable<string> FindWinmds(string pkgsDir)
    {
        var results = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var metadataDir in SafeEnumDirs(pkgsDir, "metadata", SearchOption.AllDirectories))
        {
            foreach (var f in SafeEnumFiles(metadataDir, "*.winmd", SearchOption.TopDirectoryOnly))
                results.Add(Path.GetFullPath(f));

            var v18362 = Path.Combine(metadataDir, "10.0.18362.0");
            foreach (var f in SafeEnumFiles(v18362, "*.winmd", SearchOption.TopDirectoryOnly))
                results.Add(Path.GetFullPath(f));
        }

        foreach (var libDir in SafeEnumDirs(pkgsDir, "lib", SearchOption.AllDirectories))
        {
            foreach (var f in SafeEnumFiles(libDir, "*.winmd", SearchOption.TopDirectoryOnly))
                results.Add(Path.GetFullPath(f));

            var uap10 = Path.Combine(libDir, "uap10.0");
            foreach (var f in SafeEnumFiles(uap10, "*.winmd", SearchOption.TopDirectoryOnly))
                results.Add(Path.GetFullPath(f));

            var uap18362 = Path.Combine(libDir, "uap10.0.18362");
            foreach (var f in SafeEnumFiles(uap18362, "*.winmd", SearchOption.TopDirectoryOnly))
                results.Add(Path.GetFullPath(f));
        }

        return results;
    }

    private static IEnumerable<string> SafeEnumDirs(string root, string searchPattern, SearchOption option)
    {
        try { return Directory.EnumerateDirectories(root, searchPattern, option); }
        catch { return Array.Empty<string>(); }
    }

    private static IEnumerable<string> SafeEnumFiles(string root, string searchPattern, SearchOption option)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateFiles(root, searchPattern, option) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    private static void CopyTopFiles(string fromDir, string pattern, string toDir)
    {
        if (!Directory.Exists(fromDir)) return;
        EnsureDir(toDir);
        foreach (var f in Directory.EnumerateFiles(fromDir, pattern, SearchOption.TopDirectoryOnly))
        {
            var target = Path.Combine(toDir, Path.GetFileName(f));
            TryCopy(f, target);
        }
    }

    private static void EnsureDir(string dir) => Directory.CreateDirectory(dir);

    private static void TryCopy(string src, string dst)
    {
        try
        {
            File.Copy(src, dst, overwrite: true);
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

    private static IEnumerable<string> SafeEnumSubdirs(string root)
    {
        try { return Directory.Exists(root) ? Directory.EnumerateDirectories(root) : Array.Empty<string>(); }
        catch { return Array.Empty<string>(); }
    }

    public void CopyLibsAllArch(string pkgsDir, string libRoot)
    {
        EnsureDir(libRoot);
        foreach (var libDir in SafeEnumDirs(pkgsDir, "lib", SearchOption.AllDirectories))
        {
            foreach (var sub in SafeEnumSubdirs(libDir))
            {
                var name = Path.GetFileName(sub);
                if (name.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
                {
                    var arch = name.Substring("win-".Length);
                    var outDir = Path.Combine(libRoot, arch);
                    CopyTopFiles(sub, "*.lib", outDir);
                }
                else if (name.StartsWith("win10-", StringComparison.OrdinalIgnoreCase))
                {
                    var arch = name.Substring("win10-".Length);
                    var outDir = Path.Combine(libRoot, arch);
                    CopyTopFiles(sub, "*.lib", outDir);
                }
                else if (string.Equals(name, "native", StringComparison.OrdinalIgnoreCase))
                {
                    foreach (var d in SafeEnumSubdirs(sub))
                    {
                        var dn = Path.GetFileName(d);
                        if (dn.StartsWith("win10-", StringComparison.OrdinalIgnoreCase))
                        {
                            var arch = dn.Substring("win10-".Length);
                            var outDir = Path.Combine(libRoot, arch);
                            CopyTopFiles(d, "*.lib", outDir);
                        }
                    }
                }
            }
        }
    }

    public void CopyRuntimesAllArch(string pkgsDir, string binRoot)
    {
        EnsureDir(binRoot);
        foreach (var rtDir in SafeEnumDirs(pkgsDir, "runtimes", SearchOption.AllDirectories))
        {
            foreach (var plat in SafeEnumSubdirs(rtDir))
            {
                var name = Path.GetFileName(plat);
                if (name.StartsWith("win-", StringComparison.OrdinalIgnoreCase))
                {
                    var arch = name.Substring("win-".Length);
                    var native = Path.Combine(plat, "native");
                    var outDir = Path.Combine(binRoot, arch);
                    CopyTopFiles(native, "*.*", outDir);
                }
            }
        }
    }
}
