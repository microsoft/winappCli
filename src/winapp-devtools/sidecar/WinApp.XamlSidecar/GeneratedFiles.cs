// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.XamlSidecar;

/// <summary>
/// The XAML compiler's generated C# goes into the build's obj folder, where dotnet watch picks it up as an ordinary C#
/// change. Only files whose code changed are written: a file that differs only in its '#pragma checksum' (a text or style
/// edit) would give dotnet watch an update with no code change, which sets up .NET's hot reload crash (tracker #3).
/// </summary>
internal static class GeneratedFiles
{
    public static bool IsGenerated(string path) =>
        path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Pages\Foo" for Pages\Foo.g.cs, Pages\Foo.i.g.cs and Pages\Foo.g.i.cs.</summary>
    public static string Stem(string relative) =>
        relative.EndsWith(".i.g.cs", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".g.i.cs", StringComparison.OrdinalIgnoreCase)
            ? relative[..^".i.g.cs".Length]
            : relative[..^".g.cs".Length];

    /// <summary>
    /// The generated files for these pages (and XamlTypeInfo when regenerated), by their path in obj, with whether their code
    /// differs from what is there now.
    /// </summary>
    public static List<(string ObjPath, string Text, bool CodeChanged)> Changes(IReadOnlyDictionary<string, string> generated, string serviceOutput,
        string intermediate, IReadOnlyCollection<string> stems, bool typeInfo)
    {
        var changes = new List<(string, string, bool)>();
        foreach (var (outputPath, text) in generated)
        {
            if (!IsGenerated(outputPath))
            {
                continue;
            }
            var relative = Path.GetRelativePath(serviceOutput, outputPath);
            var isTypeInfo = Path.GetFileName(relative).Equals("XamlTypeInfo.g.cs", StringComparison.OrdinalIgnoreCase);
            if (isTypeInfo ? !typeInfo : !stems.Contains(Stem(relative), StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }
            var objPath = Path.GetFullPath(Path.Combine(intermediate, relative));
            var before = File.Exists(objPath) ? ReadShared(objPath) : null;
            if (before is not null && Normalize(before) == Normalize(text))
            {
                continue;
            }
            changes.Add((objPath, text, before is null ? HasCode(text) : Code(before) != Code(text)));
        }
        return changes;
    }

    public static void Write(IEnumerable<(string ObjPath, string Text, bool CodeChanged)> changes)
    {
        // Written next to their targets first, then moved in back to back, so dotnet watch is likelier to see one change.
        var staged = new List<(string Temp, string Path)>();
        foreach (var (path, text, _) in changes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".winapp-tmp";
            File.WriteAllText(temp, text, new System.Text.UTF8Encoding(true));
            staged.Add((temp, path));
        }
        foreach (var (temp, path) in staged)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temp, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when ((ex is IOException or UnauthorizedAccessException) && attempt < 20)
                {
                    Console.Error.WriteLine($"[sidecar] retry write {Path.GetFileName(path)}: {ex.Message}");
                    Thread.Sleep(25);
                }
            }
        }
    }

    public static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string Normalize(string text) => text.TrimStart('\uFEFF').Replace("\r\n", "\n").TrimEnd();

    private static string Code(string text) => string.Join("\n", Normalize(text).Split('\n')
        .Where(l => !l.TrimStart().StartsWith("#pragma checksum", StringComparison.Ordinal)).Select(l => l.TrimEnd()));

    /// <summary>A file that declares something (not only comments and pragmas): an empty new file is also a no-change update (tracker #5).</summary>
    private static bool HasCode(string text) => Normalize(text).Split('\n').Select(l => l.Trim())
        .Any(l => l.Length > 0 && !l.StartsWith("//", StringComparison.Ordinal) && !l.StartsWith('#'));
}
