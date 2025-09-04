using System.Text.RegularExpressions;

namespace Winsdk.Cli;

internal sealed class SystemDefaultsService
{
    public string GetDefaultPackageName(string dir)
    {
        var folder = new DirectoryInfo(dir).Name;
        var normalized = Regex.Replace(folder.Trim(), @"\s+", "-").ToLowerInvariant();
        return string.IsNullOrWhiteSpace(normalized) ? "app" : normalized;
    }

    public string GetDefaultPublisherCN()
    {
        var user = Environment.UserName;
        if (string.IsNullOrWhiteSpace(user)) user = "Developer";
        return $"CN={user}";
    }
}
