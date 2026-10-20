using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

namespace AccessLoop;

public static partial class AuditFiles
{
    internal const int MaxJsonBytes = 32 * 1024 * 1024;
    internal const int MaxArtifactBytes = 128 * 1024 * 1024;
    internal const long MaxEvidenceBytes = 512L * 1024 * 1024;
    internal const int MaxArtifacts = 128;

    public static void WriteNew<T>(string path, T value) =>
        WriteNewBytes(path, JsonSerializer.SerializeToUtf8Bytes(value, Json.Options));

    public static AuditArtifact Describe(string root, string relativePath, string mediaType, string source)
    {
        Text(mediaType, "artifact mediaType", 256);
        Text(source, "artifact source", 256);
        var path = ResolveArtifact(root, relativePath);
        using var stream = OpenRead(path, MaxArtifactBytes);
        return new(relativePath, Convert.ToHexString(SHA256.HashData(stream)),
            stream.Length, mediaType, source);
    }

    public static string Hash(string path)
    {
        using var stream = OpenRead(path, MaxArtifactBytes);
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    public static string ResolveArtifact(string root, string relativePath)
    {
        var path = ArtifactDestination(root, relativePath);
        EnsureRegularFile(path);
        return path;
    }

    public static AuditReport ReadVerified(string scanPath) => ReadVerifiedDocument(scanPath).Report;

    internal static (AuditReport Report, byte[] Bytes, string Sha256, string Root) ReadVerifiedDocument(string scanPath)
    {
        var path = Path.GetFullPath(scanPath);
        var bytes = ReadBytes(path, MaxJsonBytes);
        using var json = ParseStrict(bytes);
        RequireMembers(json.RootElement, "scan", "schemaVersion", "runId", "startedAtUtc",
            "finishedAtUtc", "status", "mode", "target", "winappVersion", "catalogVersion",
            "catalog", "results", "artifacts");
        var report = Deserialize<AuditReport>(json.RootElement);
        ValidateReport(report, json.RootElement, Path.GetFileName(path));
        var root = Path.GetDirectoryName(path)!;
        foreach (var artifact in report.Artifacts)
            VerifyArtifact(root, artifact);
        var hash = Convert.ToHexString(SHA256.HashData(bytes));
        if (!string.Equals(Hash(path), hash, StringComparison.Ordinal))
            throw new InvalidDataException("scan.json changed while its evidence was being verified.");
        return (report, bytes, hash, root);
    }

    internal static void VerifyArtifact(string root, AuditArtifact artifact)
    {
        using var stream = OpenRead(ResolveArtifact(root, artifact.Path), MaxArtifactBytes);
        VerifyContent(stream, artifact);
    }

    internal static byte[] ReadArtifactBytes(string root, AuditArtifact artifact, int maximumBytes)
    {
        using var stream = OpenRead(ResolveArtifact(root, artifact.Path), Math.Min(maximumBytes, MaxArtifactBytes));
        VerifyContent(stream, artifact);
        stream.Position = 0;
        return ReadBytes(stream);
    }

    internal static void CopyVerifiedArtifact(string root, AuditArtifact artifact, string outputRoot)
    {
        using var source = OpenRead(ResolveArtifact(root, artifact.Path), MaxArtifactBytes);
        if (source.Length != artifact.Length)
            throw new InvalidDataException($"Evidence length changed: {artifact.Path}.");
        var destination = ArtifactDestination(outputRoot, artifact.Path);
        CreateArtifactParents(outputRoot, artifact.Path);
        using (var output = OpenNew(destination))
            source.CopyTo(output);
        VerifyArtifact(outputRoot, artifact);
    }

    internal static void WriteNewBytes(string path, byte[] bytes)
    {
        if (bytes.LongLength > MaxJsonBytes)
            throw new InvalidDataException($"JSON exceeds the {MaxJsonBytes}-byte limit.");
        using var stream = OpenNew(path);
        stream.Write(bytes);
    }

    internal static FileStream OpenNew(string path)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureDirectory(Path.GetDirectoryName(fullPath)!);
        return new FileStream(fullPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
    }

    internal static byte[] ReadBytes(string path, int maximumBytes)
    {
        using var stream = OpenRead(path, maximumBytes);
        return ReadBytes(stream);
    }

    private static byte[] ReadBytes(FileStream stream)
    {
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException("The file changed length while it was being read.");
        return bytes;
    }

    private static FileStream OpenRead(string path, long maximumBytes)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureRegularFile(fullPath);
        var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            EnsureRegularFile(fullPath);
            if (stream.Length > maximumBytes)
                throw new InvalidDataException($"File exceeds the {maximumBytes}-byte limit: {Path.GetFileName(fullPath)}.");
            return stream;
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private static void VerifyContent(FileStream stream, AuditArtifact artifact)
    {
        if (artifact.Length < 0 || artifact.Length > MaxArtifactBytes || stream.Length != artifact.Length)
            throw new InvalidDataException($"Evidence length does not match scan.json: {artifact.Path}.");
        ValidateHash(artifact.Sha256, "artifact sha256");
        var actual = Convert.ToHexString(SHA256.HashData(stream));
        if (!string.Equals(actual, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Evidence SHA256 does not match scan.json: {artifact.Path}.");
    }

    internal static string NormalizeArtifactPath(string relativePath)
    {
        Text(relativePath, "artifact path", 1024);
        if (Path.IsPathRooted(relativePath) || relativePath.Contains(':'))
            throw new InvalidDataException("Artifact paths must be relative to the scan directory.");
        var segments = relativePath.Replace('/', '\\').Split('\\');
        foreach (var segment in segments)
        {
            if (segment.Length is 0 or > 255 || segment is "." or ".."
                || segment.EndsWith('.') || segment.EndsWith(' ')
                || segment.Any(character => character < ' ' || "<>:\"|?*".Contains(character)))
                throw new InvalidDataException($"Unsafe artifact path: {relativePath}.");
            var stem = segment.Split('.')[0].TrimEnd(' ').ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CLOCK$" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal)
                    || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789¹²³".Contains(stem[3])))
                throw new InvalidDataException($"Reserved device name in artifact path: {relativePath}.");
        }
        return string.Join(Path.DirectorySeparatorChar, segments);
    }

    internal static string ArtifactDestination(string root, string relativePath)
    {
        var normalized = NormalizeArtifactPath(relativePath);
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        EnsureDirectory(fullRoot);
        var path = Path.GetFullPath(Path.Combine(fullRoot, normalized));
        var prefix = Path.EndsInDirectorySeparator(fullRoot) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The artifact path escapes the scan directory.");
        return path;
    }

    internal static void EnsureDirectory(string path)
    {
        for (DirectoryInfo? directory = new(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            var attributes = File.GetAttributes(directory.FullName);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException($"Reparse points are not allowed in audit paths: {directory.FullName}.");
            if ((attributes & FileAttributes.Directory) == 0)
                throw new InvalidDataException("An audit directory path refers to a file.");
        }
    }

    private static void EnsureRegularFile(string path)
    {
        EnsureDirectory(Path.GetDirectoryName(path)!);
        var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException($"Audit evidence must be an ordinary file, not a directory or link: {path}.");
    }

    internal static string CreateNewDirectory(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        EnsureDirectory(Path.GetDirectoryName(fullPath)
            ?? throw new InvalidDataException("An output directory must have an existing parent."));
        if (OperatingSystem.IsWindows())
        {
            // Directory.CreateDirectory silently reuses an existing directory.
            if (!CreateDirectoryW(fullPath, nint.Zero))
                throw new IOException($"Cannot create a new audit output directory: {fullPath}.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
        }
        else
        {
            if (Path.Exists(fullPath))
                throw new IOException($"The audit output directory already exists: {fullPath}.");
            Directory.CreateDirectory(fullPath);
        }
        EnsureDirectory(fullPath);
        return fullPath;
    }

    private static void CreateArtifactParents(string root, string relativePath)
    {
        var parts = NormalizeArtifactPath(relativePath).Split(Path.DirectorySeparatorChar);
        var current = Path.GetFullPath(root);
        foreach (var part in parts[..^1])
        {
            current = Path.Combine(current, part);
            if (!Directory.Exists(current))
                CreateNewDirectory(current);
            EnsureDirectory(current);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, nint securityAttributes);
}
