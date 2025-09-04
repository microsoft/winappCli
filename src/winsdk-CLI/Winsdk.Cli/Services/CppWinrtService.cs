namespace Winsdk.Cli;

internal sealed class CppWinrtService
{
    public string? FindCppWinrtExe(string packagesDir, IDictionary<string, string> usedVersions)
    {
        var pkgName = "Microsoft.Windows.CppWinRT";
        if (!usedVersions.TryGetValue(pkgName, out var v)) return null;
        var baseDir = Path.Combine(packagesDir, $"{pkgName}.{v}");
        var exe = Path.Combine(baseDir, "bin", "cppwinrt.exe");
        return File.Exists(exe) ? exe : null;
    }
}
