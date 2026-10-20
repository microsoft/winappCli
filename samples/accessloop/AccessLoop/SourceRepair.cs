using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace AccessLoop;

public sealed record SourceCandidate(
    string RelativePath, string FullPath, int Line, string ElementName,
    string AutomationId, string Sha256, string Snippet)
{
    internal string? ProjectPath { get; init; }
}

public sealed record RepairProposal(
    string RelativePath, string SourceSha256, string AutomationId,
    string AccessibleName, string Explanation);

public sealed record AppliedRepair(
    string FullPath, byte[] OriginalBytes, string UpdatedSha256, string Diff);

public static class SourceRepair
{
    private const int MaxSourceBytes = 2 * 1024 * 1024;
    private const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git"
    };

    public static IReadOnlyList<SourceCandidate> Locate(string projectPath, string automationId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ValidateLiteral(automationId, "AutomationId", 256);
        var project = Path.GetFullPath(projectPath);
        if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || !File.Exists(project))
            throw new InvalidDataException("Source mapping requires an existing .csproj file.");
        RejectReparsePoints(project);
        var root = Path.GetDirectoryName(project)!;
        var candidates = new List<SourceCandidate>();
        foreach (var path in EnumerateXaml(root).Order(StringComparer.OrdinalIgnoreCase))
        {
            using var file = File.OpenRead(path);
            var source = ReadSource(ReadBytes(file), path);
            foreach (var element in FindMatches(source.Document, automationId))
            {
                if (element.Name != XName.Get("Button", PresentationNamespace))
                    throw new InvalidDataException(
                        $"AutomationId '{automationId}' also identifies an unsupported non-Button in '{Path.GetRelativePath(root, path)}'. Map it manually.");
                var line = ((IXmlLineInfo)element).LineNumber;
                var snippet = string.Join("\n", Lines(source.Text).Skip(line - 1).Take(4));
                if (snippet.Length > 768)
                    snippet = snippet[..768];
                candidates.Add(new(
                    Path.GetRelativePath(root, path), path, line, element.Name.LocalName,
                    automationId, Hash(source.Bytes), snippet) { ProjectPath = project });
            }
        }
        return candidates;
    }

    public static AppliedRepair Apply(SourceCandidate candidate, RepairProposal proposal, string expectedName)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentNullException.ThrowIfNull(proposal);
        ValidateProposalFields(proposal);
        ValidateLiteral(expectedName, "Expected accessible name", 200);
        if (!string.Equals(proposal.AccessibleName, expectedName, StringComparison.Ordinal))
            throw new InvalidDataException("The proposed accessibleName must exactly match the scenario expectedName.");
        if (candidate.ProjectPath is null)
            throw new InvalidDataException("Apply requires a known source candidate returned by Locate.");
        if (!string.Equals(candidate.RelativePath, proposal.RelativePath, StringComparison.Ordinal) ||
            !string.Equals(candidate.AutomationId, proposal.AutomationId, StringComparison.Ordinal) ||
            !string.Equals(candidate.Sha256, proposal.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Proposal path, AutomationId, and source hash must match the known candidate.");

        var current = Locate(candidate.ProjectPath, candidate.AutomationId);
        if (current.Count != 1)
            throw new InvalidDataException($"Repair requires exactly one static Button source candidate; found {current.Count}. Map it manually.");
        var verified = current[0];
        if (!string.Equals(verified.FullPath, candidate.FullPath, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(verified.RelativePath, candidate.RelativePath, StringComparison.Ordinal) ||
            !string.Equals(verified.Sha256, candidate.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source changed since mapping. Capture fresh evidence and a new proposal.");

        RejectReparsePoints(verified.FullPath);
        using var file = OpenLocked(verified.FullPath);
        var source = ReadSource(ReadBytes(file), verified.FullPath);
        if (!string.Equals(Hash(source.Bytes), proposal.SourceSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The source changed before applying the repair. No changes were made.");
        var matches = FindMatches(source.Document, proposal.AutomationId).ToArray();
        if (matches.Length != 1 || matches[0].Name != XName.Get("Button", PresentationNamespace))
            throw new InvalidDataException("The exact AutomationId no longer maps to one static Button.");
        var button = matches[0];
        EnsureSupported(button);
        if ((string?)button.Attribute("AutomationProperties.Name") == expectedName)
            throw new InvalidDataException("The source already has the expected accessible name; this repair cannot fix the observed failure.");
        var updatedText = UpdateNameSource(source, button, expectedName);
        button.SetAttributeValue("AutomationProperties.Name", expectedName);
        var updated = source.Encoding.GetPreamble().Concat(source.Encoding.GetBytes(updatedText)).ToArray();
        var reparsed = ReadSource(updated, verified.FullPath);
        if (!XNode.DeepEquals(source.Document, reparsed.Document))
            throw new InvalidDataException("The source-span edit did not match the intended Name-only XML change. No changes were made.");
        var diff = MakeDiff(candidate.RelativePath, source.Text, updatedText);
        WriteBytes(file, updated, source.Bytes);
        return new(verified.FullPath, source.Bytes, Hash(updated), diff);
    }

    public static void Rollback(AppliedRepair repair)
    {
        ArgumentNullException.ThrowIfNull(repair);
        RejectReparsePoints(repair.FullPath);
        using var file = OpenLocked(repair.FullPath);
        var current = ReadBytes(file);
        if (!string.Equals(Hash(current), repair.UpdatedSha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Rollback refused: the source changed after repair. Your changes have not been overwritten.");
        WriteBytes(file, repair.OriginalBytes, current);
    }

    internal static void ValidateProposalFields(RepairProposal proposal)
    {
        if (string.IsNullOrWhiteSpace(proposal.RelativePath) || proposal.RelativePath.Length > 240 ||
            Path.IsPathRooted(proposal.RelativePath) || proposal.RelativePath.Contains(':') ||
            proposal.RelativePath.Split(['\\', '/']).Any(part => part is "" or "." or "..") ||
            !proposal.RelativePath.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Proposal relativePath must be a project-relative .xaml path without traversal.");
        if (proposal.SourceSha256 is null || proposal.SourceSha256.Length != 64 ||
            !proposal.SourceSha256.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Proposal sourceSha256 must be a 64-character SHA-256 hash.");
        ValidateLiteral(proposal.AutomationId, "Proposal AutomationId", 256);
        ValidateLiteral(proposal.AccessibleName, "Proposal accessibleName", 200);
        if (string.IsNullOrWhiteSpace(proposal.Explanation) || proposal.Explanation.Length > 500)
            throw new InvalidDataException("Proposal explanation must be nonempty and at most 500 characters.");
    }

    private static void ValidateLiteral(string value, string label, int maximum)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximum ||
            value.Any(char.IsControl) || value.TrimStart().StartsWith('{'))
            throw new InvalidDataException($"{label} must be a nonempty literal of at most {maximum} characters, without control characters or markup extensions.");
    }

    private static IEnumerable<string> EnumerateXaml(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                continue;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if (!ExcludedDirectories.Contains(Path.GetFileName(entry)))
                    foreach (var child in EnumerateXaml(entry))
                        yield return child;
            }
            else if (Path.GetExtension(entry).Equals(".xaml", StringComparison.OrdinalIgnoreCase))
            {
                RejectReparsePoints(entry);
                yield return Path.GetFullPath(entry);
            }
        }
    }

    private static IEnumerable<XElement> FindMatches(XDocument document, string automationId) =>
        document.Descendants().Where(element =>
            (string?)element.Attribute("AutomationProperties.AutomationId") == automationId &&
            !element.AncestorsAndSelf().Any(IsTemplateOrResource));

    private static bool IsTemplateOrResource(XElement element) =>
        element.Name.LocalName is "Style" or "ResourceDictionary" ||
        element.Name.LocalName.EndsWith("Template", StringComparison.Ordinal) ||
        element.Name.LocalName.EndsWith(".Resources", StringComparison.Ordinal);

    private static void EnsureSupported(XElement button)
    {
        if (button.Attribute(Xaml + "Uid") is not null)
            throw new InvalidDataException("A localized x:Uid Button requires a manual resource repair; its accessible name was not changed.");
        if (button.AncestorsAndSelf().Any(element =>
            element.Attribute(Xaml + "Load") is not null || element.Attribute(Xaml + "DeferLoadStrategy") is not null))
            throw new InvalidDataException("Deferred or conditional XAML is unsupported; only static Buttons can be repaired.");
        if (button.Elements().Any(element => element.Name.LocalName == "AutomationProperties.Name"))
            throw new InvalidDataException("AutomationProperties.Name property-element syntax requires manual repair.");
        var name = (string?)button.Attribute("AutomationProperties.Name");
        if (name is not null && name.TrimStart().StartsWith('{') && !name.StartsWith("{}", StringComparison.Ordinal))
            throw new InvalidDataException("A bound or resource-based AutomationProperties.Name requires manual repair; its value was preserved.");
    }

    private static void RejectReparsePoints(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Source mapping and repair do not follow symbolic links or reparse points: '{current}'.");
        }
    }

    private sealed record SourceFile(byte[] Bytes, string Text, Encoding Encoding, XDocument Document);

    private static SourceFile ReadSource(byte[] bytes, string path)
    {
        if (bytes.Length > MaxSourceBytes)
            throw new InvalidDataException($"XAML file '{path}' exceeds the 2 MiB source-mapping limit.");
        try
        {
            var (encoding, preambleLength) = DetectEncoding(bytes);
            var text = encoding.GetString(bytes.AsSpan(preambleLength));
            using var stream = new MemoryStream(bytes);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaxSourceBytes
            });
            var document = XDocument.Load(reader, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
            if (document.Declaration?.Encoding is string declared)
            {
                var declaredEncoding = Encoding.GetEncoding(declared);
                var genericEndian = declared.Equals("utf-16", StringComparison.OrdinalIgnoreCase) && encoding.CodePage is 1200 or 1201 ||
                    declared.Equals("utf-32", StringComparison.OrdinalIgnoreCase) && encoding.CodePage is 12000 or 12001;
                if (declaredEncoding.CodePage != encoding.CodePage && !genericEndian)
                    throw new InvalidDataException("Only UTF-8, UTF-16, or UTF-32 XAML encodings are supported without conversion.");
            }
            return new(bytes, text, encoding, document);
        }
        catch (Exception ex) when (ex is XmlException or DecoderFallbackException or ArgumentException)
        {
            throw new InvalidDataException($"Cannot safely parse XAML '{path}': {ex.Message}", ex);
        }
    }

    private static (Encoding Encoding, int PreambleLength) DetectEncoding(byte[] bytes)
    {
        ReadOnlySpan<byte> data = bytes;
        if (data.StartsWith(new byte[] { 0xff, 0xfe, 0, 0 }))
            return (new UTF32Encoding(false, true, true), 4);
        if (data.StartsWith(new byte[] { 0, 0, 0xfe, 0xff }))
            return (new UTF32Encoding(true, true, true), 4);
        if (data.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }))
            return (new UTF8Encoding(true, true), 3);
        if (data.StartsWith(new byte[] { 0xff, 0xfe }))
            return (new UnicodeEncoding(false, true, true), 2);
        if (data.StartsWith(new byte[] { 0xfe, 0xff }))
            return (new UnicodeEncoding(true, true, true), 2);
        if (data.StartsWith(new byte[] { 0x3c, 0, 0, 0 }))
            return (new UTF32Encoding(false, false, true), 0);
        if (data.StartsWith(new byte[] { 0, 0, 0, 0x3c }))
            return (new UTF32Encoding(true, false, true), 0);
        if (data.StartsWith(new byte[] { 0x3c, 0 }))
            return (new UnicodeEncoding(false, false, true), 0);
        if (data.StartsWith(new byte[] { 0, 0x3c }))
            return (new UnicodeEncoding(true, false, true), 0);
        return (new UTF8Encoding(false, true), 0);
    }

    private static string UpdateNameSource(SourceFile source, XElement button, string expectedName)
    {
        var name = button.Attribute("AutomationProperties.Name");
        var anchor = name ?? button.Attributes().Last();
        var start = SourceOffset(source.Text, (IXmlLineInfo)anchor);
        var end = AttributeEnd(source.Text, start);
        var serialized = new XAttribute("AutomationProperties.Name", expectedName).ToString();
        return name is null
            ? source.Text.Insert(end, " " + serialized)
            : source.Text[..start] + serialized + source.Text[end..];
    }

    private static int SourceOffset(string text, IXmlLineInfo position)
    {
        if (!position.HasLineInfo() || position.LineNumber < 1 || position.LinePosition < 1)
            throw new InvalidDataException("The XML parser did not provide a reliable source position.");
        var offset = 0;
        var line = 1;
        while (line < position.LineNumber && offset < text.Length)
        {
            var character = text[offset++];
            if (character == '\r')
            {
                if (offset < text.Length && text[offset] == '\n')
                    offset++;
                line++;
            }
            else if (character == '\n')
            {
                line++;
            }
        }
        offset += position.LinePosition - 1;
        if (line != position.LineNumber || offset >= text.Length)
            throw new InvalidDataException("The XML source position is outside the original source.");
        return offset;
    }

    private static int AttributeEnd(string text, int start)
    {
        var index = start;
        while (index < text.Length && !char.IsWhiteSpace(text[index]) && text[index] != '=')
        {
            if (text[index] is '<' or '>' or '/' or '\'' or '"')
                throw new InvalidDataException("Cannot determine the mapped XML attribute's source span.");
            index++;
        }
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        if (index >= text.Length || text[index++] != '=')
            throw new InvalidDataException("Cannot find the mapped XML attribute's assignment.");
        while (index < text.Length && char.IsWhiteSpace(text[index]))
            index++;
        if (index >= text.Length || text[index] is not ('\'' or '"'))
            throw new InvalidDataException("Cannot find the mapped XML attribute's quoted value.");
        var quote = text[index++];
        while (index < text.Length && text[index] != quote)
            index++;
        if (index >= text.Length)
            throw new InvalidDataException("The mapped XML attribute's quoted value is incomplete.");
        return index + 1;
    }

    private static FileStream OpenLocked(string path) =>
        new(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);

    private static byte[] ReadBytes(FileStream file)
    {
        if (file.Length > MaxSourceBytes)
            throw new InvalidDataException("The source exceeds the 2 MiB repair limit.");
        file.Position = 0;
        var bytes = new byte[checked((int)file.Length)];
        file.ReadExactly(bytes);
        return bytes;
    }

    private static void WriteBytes(FileStream file, byte[] bytes, byte[] original)
    {
        void Write(byte[] content)
        {
            file.Position = 0;
            file.Write(content);
            file.SetLength(content.Length);
            file.Flush(flushToDisk: true);
        }
        try
        {
            Write(bytes);
        }
        catch (IOException writeError)
        {
            try
            {
                Write(original);
            }
            catch (IOException recoveryError)
            {
                throw new IOException("Writing XAML and restoring its original contents both failed. Inspect the source before continuing.",
                    new AggregateException(writeError, recoveryError));
            }
            throw new IOException("Writing XAML failed; the original contents were restored.", writeError);
        }
    }

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string[] Lines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');

    private static string MakeDiff(string relativePath, string before, string after)
    {
        var oldHasNewline = before.EndsWith('\n') || before.EndsWith('\r');
        var newHasNewline = after.EndsWith('\n') || after.EndsWith('\r');
        var oldLines = oldHasNewline ? Lines(before)[..^1] : Lines(before);
        var newLines = newHasNewline ? Lines(after)[..^1] : Lines(after);
        bool SameLine(int oldIndex, int newIndex) =>
            oldLines[oldIndex] == newLines[newIndex] &&
            (oldIndex != oldLines.Length - 1 || oldHasNewline) == (newIndex != newLines.Length - 1 || newHasNewline);
        var prefix = 0;
        while (prefix < Math.Min(oldLines.Length, newLines.Length) && SameLine(prefix, prefix))
            prefix++;
        var suffix = 0;
        while (suffix < Math.Min(oldLines.Length, newLines.Length) - prefix &&
            SameLine(oldLines.Length - suffix - 1, newLines.Length - suffix - 1))
            suffix++;
        var start = Math.Max(0, prefix - 3);
        var oldEnd = Math.Min(oldLines.Length, oldLines.Length - suffix + 3);
        var newEnd = Math.Min(newLines.Length, newLines.Length - suffix + 3);
        var label = relativePath.Replace('\\', '/');
        var diff = new StringBuilder($"--- a/{label}\n+++ b/{label}\n@@ -{start + 1},{oldEnd - start} +{start + 1},{newEnd - start} @@\n");
        void AppendLine(char marker, string[] lines, int index, bool hasNewline)
        {
            diff.Append(marker).Append(lines[index]).Append('\n');
            if (index == lines.Length - 1 && !hasNewline)
                diff.Append("\\ No newline at end of file\n");
        }
        for (var index = start; index < prefix; index++)
            AppendLine(' ', oldLines, index, oldHasNewline);
        for (var index = prefix; index < oldLines.Length - suffix; index++)
            AppendLine('-', oldLines, index, oldHasNewline);
        for (var index = prefix; index < newLines.Length - suffix; index++)
            AppendLine('+', newLines, index, newHasNewline);
        for (var index = oldLines.Length - suffix; index < oldEnd; index++)
            AppendLine(' ', oldLines, index, oldHasNewline);
        return diff.ToString();
    }
}
