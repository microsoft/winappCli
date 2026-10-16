// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using System.Security.Cryptography;
using WinApp.Cli.ConsoleTasks;
using WinApp.Cli.Helpers;
using WinApp.Cli.Tools;

namespace WinApp.Cli.Services;

/// <summary>
/// Handles PRI (Package Resource Index) configuration, generation, and language extraction
/// via MakePri.exe.
/// </summary>
internal partial class PriService(
    IBuildToolsService buildToolsService) : IPriService
{
    public async Task<PriXamlResources> VerifyXamlResourcesAsync(FileInfo priFile, IReadOnlyDictionary<string, string> expectedHashes,
        TaskContext taskContext, CancellationToken cancellationToken)
    {
        var directory = Directory.CreateTempSubdirectory("winapp-xaml-pri-");
        try
        {
            await using var input = new FileStream(priFile.FullName, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (input.Length is <= 0 or > 32 * 1024 * 1024)
            {
                throw new InvalidDataException("The XAML PRI exceeds its 32 MiB inspection limit.");
            }
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(input, cancellationToken).ConfigureAwait(false));
            var output = Path.Combine(directory.FullName, "resources.xml");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await buildToolsService.RunBuildToolAsync(new MakePriTool(),
                $"""dump /if "{priFile.FullName}" /of "{output}" /dt Detailed""", taskContext,
                cancellationToken: timeout.Token).ConfigureAwait(false);
            await using var dump = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (dump.Length > 64 * 1024 * 1024) { throw new InvalidDataException("The XAML PRI dump exceeds its 64 MiB limit."); }
            using var reader = XmlReader.Create(dump, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 64 * 1024 * 1024,
            });
            return new(hash, VerifyXamlResourcesDump(XDocument.Load(reader), expectedHashes, priFile.DirectoryName));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    internal static IReadOnlyDictionary<string, string[]> VerifyXamlResourcesDump(
        XDocument dump, IReadOnlyDictionary<string, string> expectedHashes, string? payloadRoot = null)
    {
        var paths = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        var primary = dump.Descendants("ResourceMap").Where(map => (string?)map.Attribute("primary") == "true").ToArray();
        if (primary.Length != 1) { throw new InvalidDataException("The XAML PRI must have one primary resource map."); }
        foreach (var (resource, hash) in expectedHashes)
        {
            var key = "Files/" + Path.ChangeExtension(resource, ".xbf").Replace('\\', '/');
            var mapName = (string?)primary[0].Attribute("name");
            var expectedUri = $"ms-resource://{mapName}/{key}";
            var matches = primary[0].Descendants("NamedResource")
                .Where(item => string.Equals((string?)item.Attribute("uri"), expectedUri, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (string.IsNullOrEmpty(mapName) || matches.Length != 1)
            {
                throw new InvalidDataException($"The PRI does not uniquely identify '{key}'.");
            }
            var scopedPath = string.Join("/", matches[0].Ancestors("ResourceMapSubtree").Reverse()
                .Select(element => (string?)element.Attribute("name")).Append((string?)matches[0].Attribute("name")));
            if (scopedPath != key || matches[0].Ancestors("ResourceMap").FirstOrDefault() != primary[0])
            {
                throw new InvalidDataException($"The PRI resource path disagrees with '{key}'.");
            }
            var candidates = matches[0].Elements("Candidate").ToArray();
            var decision = matches[0].Elements("Decision").ToArray();
            var indices = candidates.Select(candidate => (string?)candidate.Element("QualifierSet")?.Attribute("index")).ToArray();
            if (candidates.Length is 0 or > 64 || decision.Length != 1 ||
                candidates.Any(candidate => candidate.Elements("QualifierSet").Count() != 1) ||
                indices.Any(index => !uint.TryParse(index, out _)) ||
                indices.Distinct(StringComparer.Ordinal).Count() != indices.Length ||
                !indices.Order(StringComparer.Ordinal).SequenceEqual(decision[0].Elements("QualifierSet")
                    .Select(set => (string?)set.Attribute("index")).Order(StringComparer.Ordinal)) ||
                candidates.Any(candidate => !decision[0].Elements("QualifierSet")
                    .Any(set => XNode.DeepEquals(set, candidate.Element("QualifierSet")))))
            {
                throw new InvalidDataException($"The PRI resource '{key}' has an ambiguous candidate decision.");
            }
            var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var candidate in candidates)
            {
                byte[] bytes;
                switch ((string?)candidate.Attribute("type"))
                {
                    case "EmbeddedData" when candidate.Elements("Base64Value").Count() == 1 && !candidate.Elements("Value").Any():
                        var encoded = candidate.Element("Base64Value")!.Value;
                        if (encoded.Length > 3 * 1024 * 1024) { throw new InvalidDataException("The embedded XBF exceeds its limit."); }
                        try { bytes = Convert.FromBase64String(encoded); }
                        catch (FormatException ex) { throw new InvalidDataException("The embedded XBF is not valid base64.", ex); }
                        break;
                    case "Path" when payloadRoot is not null && candidate.Elements("Value").Count() == 1 && !candidate.Elements("Base64Value").Any():
                        var relative = candidate.Element("Value")!.Value.Replace('\\', '/');
                        if (string.IsNullOrWhiteSpace(relative) || relative.Split('/').Any(part => part is "" or "." or "..") ||
                            relative.Contains(':') || !relative.EndsWith(".xbf", StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException("The PRI XBF path is not a relative compiled resource path.");
                        }
                        var path = Path.GetFullPath(relative, payloadRoot);
                        if (!path.StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(payloadRoot)) + Path.DirectorySeparatorChar,
                                StringComparison.OrdinalIgnoreCase) || PathSafety.HasReparsePointOnPath(path, payloadRoot))
                        {
                            throw new InvalidDataException("The PRI XBF path is outside the payload or redirected.");
                        }
                        using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        {
                            if (input.Length is <= 0 or > 2 * 1024 * 1024) { throw new InvalidDataException("The referenced XBF exceeds its limit."); }
                            bytes = new byte[checked((int)input.Length)];
                            input.ReadExactly(bytes);
                        }
                        referenced.Add(relative);
                        break;
                    default:
                        throw new InvalidDataException($"The PRI resource '{key}' has an unsupported XBF candidate.");
                }
                // All alternatives must have identical bytes: resource context cannot change attribution.
                if (bytes.Length > 2 * 1024 * 1024 || Convert.ToHexString(SHA256.HashData(bytes)) != hash)
                {
                    throw new InvalidDataException($"The PRI resource '{key}' differs from the selected compiled XAML.");
                }
            }
            paths.Add(resource, referenced.ToArray());
        }
        return paths;
    }

    // Extracts language tag from PRI dump qualifier strings like 'Language-en-US'
    [GeneratedRegex(@"qualifiers=""[^""]*Language-([a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*)", RegexOptions.IgnoreCase, "en-US")]
    private static partial Regex PriDumpLanguageQualifierRegex();

    /// <summary>
    /// Creates a PRI configuration file for the given package directory
    /// </summary>
    public async Task<FileInfo> CreatePriConfigAsync(
        DirectoryInfo packageDir,
        TaskContext taskContext,
        IEnumerable<string> precomputedPriResourceCandidates,
        string language = "en-US",
        string platformVersion = "10.0.0",
        CancellationToken cancellationToken = default)
    {
        if (!packageDir.Exists)
        {
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");
        }

        ArgumentNullException.ThrowIfNull(precomputedPriResourceCandidates);

        var resfilesPath = Path.Combine(packageDir.FullName, "pri.resfiles");
        var priResourceCandidates = precomputedPriResourceCandidates.ToList();

        priResourceCandidates = [.. priResourceCandidates
            .Where(path => MrtAssetHelper.PriIncludedExtensions.Contains(Path.GetExtension(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)];

        taskContext.AddDebugMessage($"PRI resource candidates discovered: {priResourceCandidates.Count}");

        using (var writer = new StreamWriter(resfilesPath))
        {
            foreach (var priFile in priResourceCandidates)
            {
                await writer.WriteLineAsync(priFile);
            }
        }

        var configPath = new FileInfo(Path.Combine(packageDir.FullName, "priconfig.xml"));
        var configPathStr = LongPathHelper.EnsureExtendedLengthPrefix(configPath.FullName);
        var arguments = $@"createconfig /cf ""{configPathStr}"" /dq lang-{language}_scale-200 /pv {platformVersion} /o";

        taskContext.AddDebugMessage("Creating PRI configuration file...");

        try
        {
            await buildToolsService.RunBuildToolAsync(new MakePriTool(), arguments, taskContext, cancellationToken: cancellationToken);

            taskContext.AddDebugMessage($"PRI configuration created: {configPath}");

            var xmlDoc = new XmlDocument();
            xmlDoc.Load(configPath.FullName);
            var resourcesNode = xmlDoc.SelectSingleNode("/resources");
            if (resourcesNode != null)
            {
                var indexNode = resourcesNode.SelectSingleNode("index");
                if (indexNode != null)
                {
                    if (indexNode.Attributes?["startIndexAt"]?.Value != null)
                    {
                        // set to relative path
                        indexNode.Attributes["startIndexAt"]!.Value = ".\\pri.resfiles";
                    }

                    var resfilesIndexerNode = xmlDoc.CreateElement("indexer-config");
                    var typeAttr = xmlDoc.CreateAttribute("type");
                    typeAttr.Value = "resfiles";
                    resfilesIndexerNode.Attributes.Append(typeAttr);

                    var delimiterAttr = xmlDoc.CreateAttribute("qualifierDelimiter");
                    delimiterAttr.Value = ".";
                    resfilesIndexerNode.Attributes.Append(delimiterAttr);

                    indexNode.AppendChild(resfilesIndexerNode);

                    // Ensure folder-based indexer is configured to parse qualifiers from
                    // both folder names and file names (e.g. targetsize-48_altform-unplated).
                    var folderIndexerNode = indexNode
                        .SelectNodes("indexer-config")
                        ?.OfType<XmlNode>()
                        .FirstOrDefault(node =>
                            node.Attributes?["type"]?.Value?.Equals("folder", StringComparison.OrdinalIgnoreCase) == true);

                    if (folderIndexerNode?.Attributes != null)
                    {
                        var folderAttributes = folderIndexerNode.Attributes;

                        var folderNameAsQualifierAttr = folderAttributes["foldernameAsQualifier"];
                        if (folderNameAsQualifierAttr == null)
                        {
                            folderNameAsQualifierAttr = xmlDoc.CreateAttribute("foldernameAsQualifier");
                            folderAttributes.Append(folderNameAsQualifierAttr);
                        }
                        folderNameAsQualifierAttr.Value = "true";

                        var fileNameAsQualifierAttr = folderAttributes["filenameAsQualifier"];
                        if (fileNameAsQualifierAttr == null)
                        {
                            fileNameAsQualifierAttr = xmlDoc.CreateAttribute("filenameAsQualifier");
                            folderAttributes.Append(fileNameAsQualifierAttr);
                        }
                        fileNameAsQualifierAttr.Value = "true";
                    }

                    xmlDoc.Save(configPath.FullName);
                }
            }

            return configPath;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to create PRI configuration: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Generates a PRI file from the configuration
    /// </summary>
    public async Task<List<FileInfo>> GeneratePriFileAsync(DirectoryInfo packageDir, TaskContext taskContext, FileInfo? configPath = null, FileInfo? outputPath = null, CancellationToken cancellationToken = default)
    {
        if (!packageDir.Exists)
        {
            throw new DirectoryNotFoundException($"Package directory not found: {packageDir}");
        }

        var priConfigPath = configPath ?? new FileInfo(Path.Combine(packageDir.FullName, "priconfig.xml"));
        var priOutputPath = outputPath ?? new FileInfo(Path.Combine(packageDir.FullName, "resources.pri"));

        if (!priConfigPath.Exists)
        {
            throw new FileNotFoundException($"PRI configuration file not found: {priConfigPath}");
        }

        var prPath = LongPathHelper.EnsureExtendedLengthPrefix(Path.TrimEndingDirectorySeparator(packageDir.FullName));
        var cfPath = LongPathHelper.EnsureExtendedLengthPrefix(priConfigPath.FullName);
        var ofPath = LongPathHelper.EnsureExtendedLengthPrefix(priOutputPath.FullName);
        var arguments = $@"new /pr ""{prPath}"" /cf ""{cfPath}"" /of ""{ofPath}"" /o";

        taskContext.AddDebugMessage("Generating PRI file...");

        try
        {
            var (stdout, stderr) = await buildToolsService.RunBuildToolAsync(new MakePriTool(), arguments, taskContext, cancellationToken: cancellationToken);

            // Parse the output to extract resource files
            var resourceFiles = new List<FileInfo>();
            var lines = stdout.Replace("\0", "").Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                // Look for lines that match the pattern "Resource File: *"
                const string resourceFileStr = "Resource File: ";
                if (line.StartsWith(resourceFileStr, StringComparison.OrdinalIgnoreCase))
                {
                    var fileName = line[resourceFileStr.Length..].Trim();
                    if (!string.IsNullOrEmpty(fileName))
                    {
                        resourceFiles.Add(new FileInfo(Path.Combine(packageDir.FullName, fileName)));
                    }
                }
            }

            taskContext.AddDebugMessage($"PRI file generated: {priOutputPath}");
            if (resourceFiles.Count > 0)
            {
                taskContext.AddDebugMessage($"Processed {resourceFiles.Count} resource files");
            }

            return resourceFiles;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"Failed to generate PRI file: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Extracts language qualifiers from a PRI file using <c>makepri dump</c>.
    /// Returns a distinct, sorted list of BCP-47 language tags found in the PRI resource map.
    /// </summary>
    public async Task<List<string>> ExtractLanguagesFromPriAsync(
        FileInfo priFile,
        TaskContext taskContext,
        CancellationToken cancellationToken)
    {
        try
        {
            var dumpOutputFile = Path.Combine(Path.GetTempPath(), $"winapp-pri-dump-{Guid.NewGuid():N}.xml");
            var ifPath = LongPathHelper.EnsureExtendedLengthPrefix(priFile.FullName);
            var dumpPath = LongPathHelper.EnsureExtendedLengthPrefix(dumpOutputFile);
            var arguments = $@"dump /if ""{ifPath}"" /of ""{dumpPath}"" /o";

            await buildToolsService.RunBuildToolAsync(new MakePriTool(), arguments, taskContext, cancellationToken: cancellationToken);

            if (!File.Exists(dumpPath))
            {
                return [];
            }

            try
            {
                var dumpContent = await File.ReadAllTextAsync(dumpPath, cancellationToken);

                // Extract language qualifiers from Candidate elements:
                // <Candidate qualifiers="Language-en-US" ...> or multi-qualifier like "Language-en-US, Scale-200"
                var languages = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match match in PriDumpLanguageQualifierRegex().Matches(dumpContent))
                {
                    languages.Add(match.Groups[1].Value);
                }

                return languages.OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList();
            }
            finally
            {
                File.Delete(dumpPath);
            }
        }
        catch (Exception ex)
        {
            taskContext.AddDebugMessage($"{UiSymbols.Warning} Failed to extract languages from PRI: {ex.Message}");
            return [];
        }
    }
}
