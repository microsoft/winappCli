// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services.DevTools;

internal sealed record XamlCompilerArtifacts(string Input, string Output, string SavedState);
internal sealed record XamlSourceExclusion(
    [property: System.Text.Json.Serialization.JsonPropertyName("source")] string Source,
    [property: System.Text.Json.Serialization.JsonPropertyName("resource")] string Resource,
    [property: System.Text.Json.Serialization.JsonPropertyName("reason")] string Reason);
internal sealed record XamlCoordinateCapture(
    IReadOnlyList<XamlSourceCoordinateFile> Files, IReadOnlyList<XamlSourceExclusion> Exclusions);

internal sealed record XamlSourceCoordinateFile(
    string Source, string Resource, string SourceHash, string XbfHash,
    IReadOnlyList<XamlCoordinateMap.Element> Elements, string GeneratedHash,
    string CompilerInputHash, string CompilerOutputHash, string SavedStateHash, string SelectedBuild,
    string? PriHash = null, IReadOnlyList<string>? PayloadPaths = null, string? Attribution = null, string? Evidence = null);

/// <summary>Captures a selected compiler tuple; it never searches intermediate directories.</summary>
internal static class XamlSourceCoordinates
{
    internal const int MaximumElements = 4096;
    internal static readonly string[] Properties =
        ["XamlCompilerExeInputJson", "XamlCompilerExeOutputJson", "XamlSavedStateFilePath"];

    internal static XamlCompilerArtifacts? FromProperties(FileInfo project, IReadOnlyDictionary<string, string> properties)
    {
        var paths = Properties.Select(name => properties.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? Path.GetFullPath(value, project.DirectoryName!) : null).ToArray();
        return paths[2] is null ? null : new(paths[0] ?? "", paths[1] ?? "", paths[2]!);
    }

    internal sealed class XamlCoordinateLaunch : IDisposable
    {
        // Delete-on-close: the OS removes the inventory when the CLI exits, even if it is killed. Readers must share
        // write and delete with this handle; the tap verifies the inventory by hash, not by locking it.
        private readonly FileStream _inventory;
        private PayloadLease? _payload;
        internal string Hash { get; }
        internal string? Error { get; private set; }
        internal IReadOnlyList<XamlSourceExclusion> Exclusions { get; private init; } = [];

        private XamlCoordinateLaunch(FileStream inventory, string hash, string? error)
        {
            _inventory = inventory;
            Hash = hash;
            Error = error;
        }

        internal static async Task<XamlCoordinateLaunch?> CreateAsync(
            FileInfo? project, IReadOnlyList<string>? sources, XamlCompilerArtifacts? compiler, CancellationToken cancellationToken,
            Func<GuestSourceManifest, CancellationToken, Task<GuestSourceManifest>>? bindPayload = null)
        {
            if (project is null || sources is not { Count: > 0 }) { return null; }
            var snapshot = await GuestSourceSnapshot.CreateAsync(project, sources, null, cancellationToken, compiler).ConfigureAwait(false);
            if (bindPayload is not null) { snapshot = await bindPayload(snapshot, cancellationToken).ConfigureAwait(false); }
            var bytes = GuestSourceSnapshot.SerializeInventory(snapshot);
            var path = Path.Combine(Path.GetTempPath(), $"winapp-source-inventory-{Guid.NewGuid():N}.json");
            var inventory = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.DeleteOnClose);
            try
            {
                await inventory.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
                await inventory.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await inventory.DisposeAsync().ConfigureAwait(false);
                throw;
            }
            return new(inventory, Convert.ToHexString(SHA256.HashData(bytes)), snapshot.CoordinateError)
            { Exclusions = snapshot.CoordinateExclusions ?? [] };
        }

        internal IReadOnlyDictionary<string, string?> Apply(IReadOnlyDictionary<string, string?> environment, string payloadRoot)
        {
            _payload = HoldPayload(_inventory.Name, payloadRoot);
            Error ??= _payload.Error;
            return Apply(environment, _inventory.Name, Hash, _payload.Verified);
        }

        internal static IReadOnlyDictionary<string, string?> Apply(
            IReadOnlyDictionary<string, string?> environment, string path, string hash, bool payloadHeld)
        {
            var result = new Dictionary<string, string?>(environment, StringComparer.OrdinalIgnoreCase)
            {
                ["WINAPP_DEVTOOLS_SOURCE_INVENTORY"] = path,
                ["WINAPP_DEVTOOLS_SOURCE_INVENTORY_HASH"] = hash,
                ["WINAPP_DEVTOOLS_SOURCE_PAYLOAD_HELD"] = payloadHeld ? hash : null,
            };
            return result;
        }

        public void Dispose()
        {
            _inventory.Dispose();
            _payload?.Dispose();
        }
    }

    internal sealed class PayloadLease : IDisposable
    {
        internal readonly List<FileStream> Files = [];
        internal bool Verified { get; set; }
        internal string? Error { get; set; }
        public void Dispose() { foreach (var file in Files) { file.Dispose(); } Files.Clear(); }
    }

    internal static PayloadLease HoldPayload(string inventoryPath, string payloadRoot)
    {
        var lease = new PayloadLease();
        try
        {
            using var inventoryFile = new FileStream(inventoryPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (inventoryFile.Length > GuestSourceSnapshot.MaximumInventoryBytes) { throw new InvalidDataException("The source inventory exceeds its bound."); }
            var inventory = JsonSerializer.Deserialize(inventoryFile, GuestCommentsJsonContext.Default.GuestSourceInventory)
                ?? throw new InvalidDataException("The source inventory is empty.");
            string? heldPriHash = null;
            foreach (var entry in inventory.Coordinates ?? [])
            {
                if (entry.Attribution == "likely") { continue; }
                var resource = GuestCommentBinding.ValidateRelativeSource(entry.Resource);
                foreach (var relative in entry.PayloadPaths ?? [])
                {
                    var referencedPath = Path.GetFullPath(relative, payloadRoot);
                    if (!relative.EndsWith(".xbf", StringComparison.OrdinalIgnoreCase) ||
                        relative.Replace('\\', '/').Split('/').Any(part => part is "" or "." or "..") || relative.Contains(':'))
                    {
                        throw new InvalidDataException("The PRI XBF path is invalid.");
                    }
                    var referenced = Open(referencedPath, payloadRoot);
                    lease.Files.Add(referenced);
                    if (Convert.ToHexString(SHA256.HashData(referenced)) != entry.XbfHash)
                    {
                        throw new InvalidDataException("A referenced PRI XBF differs from the selected compiled XAML.");
                    }
                }
                if (entry.PriHash is not null && entry.PriHash == heldPriHash) { continue; }
                var path = Path.Combine(payloadRoot, entry.PriHash is null ? Path.ChangeExtension(resource, ".xbf") : "resources.pri");
                if (entry.PriHash is null && File.Exists(Path.Combine(payloadRoot, "resources.pri")))
                {
                    throw new InvalidDataException("An indexed XAML resource requires verification through its PRI resource key.");
                }
                var file = Open(path, payloadRoot, entry.PriHash is null ? GuestSourceSnapshot.MaximumFileBytes : 32 * 1024 * 1024);
                lease.Files.Add(file);
                if (Convert.ToHexString(SHA256.HashData(file)) != (entry.PriHash ?? entry.XbfHash))
                {
                    throw new InvalidDataException("The deployed XAML resource differs from the verified build payload.");
                }
                heldPriHash = entry.PriHash;
            }
            lease.Verified = true;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or JsonException or ArgumentException)
        {
            lease.Dispose();
            lease.Error = $"Compiled XAML coordinates are unavailable: the deployed XAML payload cannot be verified ({ex.Message}).";
        }
        return lease;
    }

    internal static XamlCoordinateCapture Capture(
        FileInfo project, GuestSourceManifest snapshot, string inputPath, string outputPath, string savedStatePath)
    {
        if (inputPath.Length == 0 && outputPath.Length == 0)
        {
            return CaptureSavedState(project, snapshot, savedStatePath);
        }
        using var inputBytes = Open(inputPath, project.DirectoryName!);
        using var outputBytes = Open(outputPath, project.DirectoryName!);
        using var savedBytes = Open(savedStatePath, project.DirectoryName!);
        using var inputDocument = JsonDocument.Parse(inputBytes);
        using var outputDocument = JsonDocument.Parse(outputBytes);
        var input = inputDocument.RootElement;
        var output = outputDocument.RootElement;
        if (!string.Equals(Text(input, "ProjectPath"), project.FullName, StringComparison.OrdinalIgnoreCase) ||
            !input.TryGetProperty("IsPass1", out var pass) || pass.ValueKind != JsonValueKind.False ||
            !input.TryGetProperty("XAMLFingerprint", out var fingerprint) || fingerprint.ValueKind != JsonValueKind.True)
        {
            throw new InvalidDataException("The selected XAML compiler input does not identify this project's fingerprinted second pass.");
        }
        if (Text(input, "OutputPath").Length == 0) { throw new InvalidDataException("The selected compiler intermediate path is missing."); }
        var intermediate = Path.GetFullPath(Text(input, "OutputPath"), project.DirectoryName!);
        if (!Inside(inputPath, intermediate) || !Inside(outputPath, intermediate) || !Inside(savedStatePath, intermediate))
        {
            throw new InvalidDataException("The selected XAML compiler metadata does not share one intermediate directory.");
        }
        using var xml = XmlReader.Create(savedBytes, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = GuestSourceSnapshot.MaximumFileBytes,
        });
        var saved = XDocument.Load(xml);
        inputBytes.Position = 0;
        outputBytes.Position = 0;
        savedBytes.Position = 0;
        var inputHash = Convert.ToHexString(SHA256.HashData(inputBytes));
        var outputHash = Convert.ToHexString(SHA256.HashData(outputBytes));
        var stateHash = Convert.ToHexString(SHA256.HashData(savedBytes));
        var generated = Paths(output, "GeneratedXamlFiles");
        var compiled = Paths(output, "GeneratedXbfFiles");
        var pages = Items(input, "XamlPages").Concat(Items(input, "XamlApplications")).ToArray();
        if (pages.Length is 0 or > GuestSourceSnapshot.MaximumFiles)
        {
            throw new InvalidDataException("The selected compiler input exceeds the source file limit.");
        }
        var result = new List<XamlSourceCoordinateFile>();
        var exclusions = new List<XamlSourceExclusion>();
        var resources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var capturedSources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pages)
        {
            var fullPath = Text(page, "FullPath");
            var source = GuestCommentBinding.ValidateRelativeSource(Path.GetRelativePath(project.DirectoryName!, fullPath));
            if (!snapshot.Files.Any(file => file.RelativePath.Equals(source, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }
            if (!capturedSources.Add(source)) { throw new InvalidDataException("The compiler input repeats an admitted source."); }
            var link = Text(page, "MSBuild_Link");
            var resource = GuestCommentBinding.ValidateRelativeSource(link.Length == 0 ? source : link);
            if (!resources.Add(resource) || Text(page, "MSBuild_TargetPath") is { Length: > 0 } target && target != resource)
            {
                throw new InvalidDataException("The selected compiler resource identity is ambiguous.");
            }
            try
            {
                CapturePage();
            }
            catch (Exception ex) when (IsSourceFailure(ex))
            {
                exclusions.Add(new(source, resource, ex.Message));
            }

            void CapturePage()
            {
                if (Flag(page, "IsSystem") || Flag(page, "IsNuGet") || Flag(page, "IsStaticLibraryReference") ||
                    Text(page, "XamlResourceMapName").Length != 0 || Text(page, "XamlComponentResourceLocation").Length != 0)
                {
                    throw new InvalidDataException($"XAML source '{source}' uses unsupported resource ownership metadata.");
                }
                var generatedPath = Path.GetFullPath(Path.Combine(intermediate, resource));
                var xbfPath = Path.ChangeExtension(generatedPath, ".xbf");
                if (!generated.Contains(generatedPath))
                {
                    throw new InvalidDataException($"The selected compiler output does not list generated XAML for '{resource}'.");
                }
                using var originalFile = Open(fullPath, project.DirectoryName!);
                using var generatedFile = Open(generatedPath, intermediate);
                var original = Bytes(originalFile);
                var rewritten = Bytes(generatedFile);
                if (original.AsSpan().SequenceEqual(rewritten)) { return; }
                if (!compiled.Contains(xbfPath))
                {
                    throw new InvalidDataException($"The selected compiler output does not list both artifacts for '{resource}'.");
                }
                var savedFile = saved.Descendants().Where(element => element.Name.LocalName == "XamlSourceFileData" &&
                    string.Equals((string?)element.Attribute("GeneratedCodePathPrefix"),
                        Path.ChangeExtension(generatedPath, null), StringComparison.OrdinalIgnoreCase)).ToArray();
                if (savedFile.Length == 0)
                {
                    savedFile = saved.Descendants().Where(element => element.Name.LocalName == "XamlSourceFileData" &&
                        IsClassless(element) &&
                        string.Equals((string?)element.Attribute("XamlFileName"), Text(page, "ItemSpec"), StringComparison.OrdinalIgnoreCase)).ToArray();
                }
                if (savedFile.Length != 1 ||
                    !string.Equals((string?)savedFile[0].Attribute("XamlFileName"), Text(page, "ItemSpec"), StringComparison.OrdinalIgnoreCase) ||
                    !long.TryParse((string?)savedFile[0].Attribute("XamlFileTimeAtLastCompileInTicks"), out var ticks))
                {
                    throw new InvalidDataException($"The selected compiler saved state does not uniquely identify '{resource}'.");
                }
                using var xbfFile = Open(xbfPath, intermediate);
                var xbf = Bytes(xbfFile);
                var proof = new XamlCoordinateMap.BuildProof(project.FullName, intermediate, intermediate,
                    "XBF2-original-source-fingerprint", source, resource, link.Length == 0 ? null : link,
                    generatedPath, xbfPath, Hash(original), Hash(rewritten), Hash(xbf),
                    File.GetLastWriteTime(originalFile.SafeFileHandle).Ticks, ticks);
                var map = XamlCoordinateMap.Create(snapshot, proof, original, rewritten, xbf);
                result.Add(new(source, resource, proof.SourceHash, proof.XbfHash, map.Elements,
                    proof.GeneratedHash, inputHash, outputHash, stateHash, Path.GetRelativePath(project.DirectoryName!, intermediate)));
            }
        }
        if (!capturedSources.SetEquals(snapshot.Files.Select(file => file.RelativePath)))
        {
            throw new InvalidDataException("The selected compiler input does not cover the launch source snapshot.");
        }
        return new(result, exclusions);
    }

    private static XamlCoordinateCapture CaptureSavedState(
        FileInfo project, GuestSourceManifest snapshot, string savedStatePath)
    {
        using var savedBytes = Open(savedStatePath, project.DirectoryName!);
        using var xml = XmlReader.Create(savedBytes, new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = GuestSourceSnapshot.MaximumFileBytes,
        });
        var saved = XDocument.Load(xml);
        savedBytes.Position = 0;
        var stateHash = Convert.ToHexString(SHA256.HashData(savedBytes));
        var intermediate = Path.GetDirectoryName(savedStatePath)!;
        var entries = saved.Descendants().Where(element => element.Name.LocalName == "XamlSourceFileData").ToArray();
        if (entries.Length is 0 or > GuestSourceSnapshot.MaximumFiles)
        {
            throw new InvalidDataException("The selected XAML saved state has no bounded source table.");
        }
        var result = new List<XamlSourceCoordinateFile>();
        var exclusions = new List<XamlSourceExclusion>();
        foreach (var source in snapshot.Files)
        {
            try
            {
                CaptureSource();
            }
            catch (Exception ex) when (IsSourceFailure(ex))
            {
                exclusions.Add(new(source.RelativePath, source.RelativePath, ex.Message));
            }

            void CaptureSource()
            {
                var matches = entries.Where(element => string.Equals(
                    (string?)element.Attribute("XamlFileName"), source.RelativePath, StringComparison.OrdinalIgnoreCase)).ToArray();
                var generatedPath = Path.GetFullPath(source.RelativePath, intermediate);
                // Without compiler item metadata, only an exact unlinked source/resource association is admitted.
                if (matches.Length != 1 || (!IsClassless(matches[0]) && !string.Equals(
                        (string?)matches[0].Attribute("GeneratedCodePathPrefix"), Path.ChangeExtension(generatedPath, null),
                        StringComparison.OrdinalIgnoreCase)) ||
                    !long.TryParse((string?)matches[0].Attribute("XamlFileTimeAtLastCompileInTicks"), out var ticks))
                {
                    throw new InvalidDataException($"The selected XAML saved state does not identify the unlinked source '{source.RelativePath}'.");
                }
                using var originalFile = Open(Path.GetFullPath(source.RelativePath, project.DirectoryName!), project.DirectoryName!);
                using var generatedFile = Open(generatedPath, intermediate);
                var original = Bytes(originalFile);
                var generated = Bytes(generatedFile);
                var xbfPath = Path.ChangeExtension(generatedPath, ".xbf");
                using var xbfFile = Open(xbfPath, intermediate);
                var xbf = Bytes(xbfFile);
                var proof = new XamlCoordinateMap.BuildProof(project.FullName, intermediate, intermediate,
                    "saved-state-and-validated-rewrite", source.RelativePath, source.RelativePath, null,
                    generatedPath, xbfPath, Hash(original), Hash(generated), Hash(xbf),
                    File.GetLastWriteTime(originalFile.SafeFileHandle).Ticks, ticks);
                var map = XamlCoordinateMap.Create(snapshot, proof, original, generated, xbf);
                result.Add(new(source.RelativePath, source.RelativePath, proof.SourceHash, proof.XbfHash,
                    map.Elements, proof.GeneratedHash, "", "", stateHash, Path.GetRelativePath(project.DirectoryName!, intermediate)));
            }
        }
        return new(result, exclusions);
    }

    // A classless ResourceDictionary has no generated code, so its saved state is keyed only by file name.
    private static bool IsClassless(XElement entry) =>
        (string?)entry.Attribute("GeneratedCodePathPrefix") is "" && (string?)entry.Attribute("ClassFullName") is "";

    private static bool IsSourceFailure(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or InvalidOperationException or XmlException or ArgumentException;

    internal static IReadOnlyList<XamlSourceCoordinateFile> CaptureLikely(
        FileInfo project, GuestSourceManifest snapshot, string evidence)
    {
        var result = new List<XamlSourceCoordinateFile>();
        foreach (var source in snapshot.Files)
        {
            using var file = Open(Path.GetFullPath(source.RelativePath, project.DirectoryName!), project.DirectoryName!);
            var bytes = Bytes(file);
            if (Hash(bytes) != source.Sha256) { throw new InvalidDataException("The authored source changed during capture."); }
            result.Add(new(source.RelativePath, source.RelativePath, source.Sha256, "",
                XamlCoordinateMap.SourceElements(bytes), "", "", "", "", "", Attribution: "likely", Evidence: evidence));
        }
        return result;
    }

    private static FileStream Open(string path, string root, int maximum = GuestSourceSnapshot.MaximumFileBytes)
    {
        if (!Path.IsPathFullyQualified(path) || !Inside(path, root) || PathSafety.HasReparsePointOnPath(path, root))
        {
            throw new InvalidDataException("A selected XAML compiler artifact is outside its admitted directory or redirected.");
        }
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length <= 0 || stream.Length > maximum)
        {
            stream.Dispose();
            throw new InvalidDataException("A selected XAML compiler artifact exceeds its size limit.");
        }
        return stream;
    }

    private static byte[] Bytes(FileStream stream)
    {
        var bytes = new byte[checked((int)stream.Length)];
        stream.ReadExactly(bytes);
        return bytes;
    }

    private static bool Inside(string path, string root) =>
        Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static string Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";
    private static bool Flag(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;
    private static JsonElement[] Items(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().ToArray() : [];
    private static HashSet<string> Paths(JsonElement element, string name) =>
        Items(element, name).Select(value => value.GetString() ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
}
