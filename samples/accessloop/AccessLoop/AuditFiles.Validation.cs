using System.Text.Json;

namespace AccessLoop;

public static partial class AuditFiles
{
    internal static JsonDocument ParseStrict(byte[] bytes)
    {
        if (bytes.Length > MaxJsonBytes)
            throw new InvalidDataException("The JSON document is too large.");
        try
        {
            ReadOnlyMemory<byte> content = bytes;
            if (content.Span.StartsWith(new byte[] { 239, 187, 191 }))
                content = content[3..];
            var document = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 64 });
            try
            {
                var remainingValues = 2_000_000;
                ValidateMembers(document.RootElement, ref remainingValues);
                return document;
            }
            catch
            {
                document.Dispose();
                throw;
            }
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Malformed audit JSON.", exception);
        }
    }

    internal static T Deserialize<T>(JsonElement element)
    {
        try
        {
            return element.Deserialize<T>(Json.Options)
                ?? throw new InvalidDataException("The audit document is empty.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Audit JSON contains invalid, missing, or unknown fields.", exception);
        }
    }

    private static void ValidateMembers(JsonElement element, ref int remainingValues)
    {
        if (--remainingValues < 0)
            throw new InvalidDataException("The audit JSON contains too many values.");
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var member in element.EnumerateObject())
            {
                if (!names.Add(member.Name))
                    throw new InvalidDataException($"Duplicate JSON member: {member.Name}.");
                ValidateMembers(member.Value, ref remainingValues);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                ValidateMembers(item, ref remainingValues);
        }
        else if (element.ValueKind == JsonValueKind.String && element.GetString()!.Length > 65_536)
            throw new InvalidDataException("An audit JSON string exceeds 65536 characters.");
    }

    internal static JsonElement Property(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object)
            return default;
        if (value.TryGetProperty(name, out var property))
            return property;
        foreach (var member in value.EnumerateObject())
            if (string.Equals(member.Name, name, StringComparison.OrdinalIgnoreCase))
                return member.Value;
        return default;
    }

    internal static void RequireMembers(JsonElement value, string context, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{context} must be an object.");
        foreach (var name in names)
            if (Property(value, name).ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
                throw new InvalidDataException($"{context}.{name} is required.");
    }

    internal static void Text(string? value, string name, int maximumLength = 65_536, bool allowBlank = false)
    {
        if (value is null || value.Length > maximumLength || (!allowBlank && string.IsNullOrWhiteSpace(value)))
            throw new InvalidDataException($"{name} must be {(allowBlank ? "a string" : "nonempty")} of at most {maximumLength} characters.");
    }

    internal static void ValidateHash(string? value, string name)
    {
        if (value is null || value.Length != 64 || value.Any(character => !char.IsAsciiHexDigit(character)))
            throw new InvalidDataException($"{name} must be a 64-character SHA256 hexadecimal value.");
    }

    private static void ValidateReport(AuditReport report, JsonElement json, string scanFileName)
    {
        if (report.SchemaVersion != 1)
            throw new InvalidDataException("Only audit schemaVersion 1 is supported.");
        Text(report.RunId, "runId", 256);
        Text(report.WinappVersion, "winappVersion", 256);
        Text(report.CatalogVersion, "catalogVersion", 256);
        Text(report.PrivacyNotice, "privacyNotice");
        if (report.Status is not ("completed" or "partial" or "blocked"))
            throw new InvalidDataException("Audit status must be completed, partial, or blocked; it is not a compliance verdict.");
        if (report.Mode is not ("snapshot" or "snapshot+events" or "snapshot+keyboard+events"))
            throw new InvalidDataException("Audit mode must be snapshot, snapshot+events, or snapshot+keyboard+events.");
        if (report.StartedAtUtc == default || report.FinishedAtUtc < report.StartedAtUtc)
            throw new InvalidDataException("Audit timestamps are missing or out of order.");
        if (report.Target is null)
            throw new InvalidDataException("The audit target is required.");
        RequireMembers(Property(json, "target"), "target", "processId", "windowHandle",
            "processName", "processStartedAtUtc");
        Text(report.Target.ProcessName, "target processName", 4096);
        if (report.Target.ProcessId <= 0 || report.Target.WindowHandle <= 0
            || report.Target.ProcessStartedAtUtc == default)
            throw new InvalidDataException("The audit target must identify its process and window.");
        if (report.Catalog is null || report.Catalog.Count is 0 or > 512
            || report.Results is null || report.Results.Count > 50_000
            || report.Artifacts is null || report.Artifacts.Count > MaxArtifacts)
            throw new InvalidDataException("The audit catalog, results, or artifact collection is missing or exceeds its limit.");

        var catalog = new Dictionary<string, AuditCheckDefinition>(StringComparer.Ordinal);
        var catalogJson = Property(json, "catalog");
        foreach (var item in catalogJson.EnumerateArray())
            RequireMembers(item, "catalog entry", "id", "title", "source", "mode", "criteria", "availability");
        foreach (var check in report.Catalog)
        {
            if (check is null)
                throw new InvalidDataException("Null catalog entries are not allowed.");
            Text(check.Id, "catalog id", 256);
            Text(check.Title, "catalog title", 4096);
            Text(check.Source, "catalog source", 256);
            if (!catalog.TryAdd(check.Id, check))
                throw new InvalidDataException($"Duplicate catalog ID: {check.Id}.");
            if (check.Mode is not ("snapshot" or "observe" or "active" or "environment" or "assisted")
                || check.Availability is not ("implemented" or "unsupported" or "assisted"))
                throw new InvalidDataException($"Invalid catalog mode or availability: {check.Id}.");
            if (check.Criteria is null || check.Criteria.Count > 128)
                throw new InvalidDataException("Catalog criteria are missing or unbounded.");
            foreach (var criterion in check.Criteria)
                Text(criterion, "criterion", 1024);
        }

        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "scan.json", "assessment.json", "report.html", scanFileName
        };
        long totalLength = 0;
        foreach (var item in Property(json, "artifacts").EnumerateArray())
            RequireMembers(item, "artifact", "path", "sha256", "length", "mediaType", "source");
        foreach (var artifact in report.Artifacts)
        {
            if (artifact is null)
                throw new InvalidDataException("Null artifacts are not allowed.");
            var normalized = NormalizeArtifactPath(artifact.Path);
            if (!paths.Add(normalized) || !names.Add(Path.GetFileName(normalized)))
                throw new InvalidDataException("Artifact paths and file names must be unique, ignoring case.");
            if (reserved.Contains(normalized.Split(Path.DirectorySeparatorChar)[0]))
                throw new InvalidDataException($"Artifact path conflicts with an audit output file: {artifact.Path}.");
            ValidateHash(artifact.Sha256, "artifact sha256");
            Text(artifact.MediaType, "artifact mediaType", 256);
            Text(artifact.Source, "artifact source", 256);
            if (artifact.Length < 0 || artifact.Length > MaxArtifactBytes
                || (totalLength += artifact.Length) > MaxEvidenceBytes)
                throw new InvalidDataException("The audit evidence exceeds its per-file or total size limit.");
        }

        var nodeIds = SnapshotNodeIds(report.Snapshot);
        ValidateObservation(report);
        foreach (var item in Property(json, "results").EnumerateArray())
            RequireMembers(item, "result", "checkId", "status", "summary", "details", "evidence");
        foreach (var result in report.Results)
        {
            if (result is null || string.IsNullOrWhiteSpace(result.CheckId) || !catalog.ContainsKey(result.CheckId))
                throw new InvalidDataException("Every result must reference an existing catalog ID.");
            if (!AuditCatalog.ResultStatuses.Contains(result.Status))
                throw new InvalidDataException($"Invalid automatic result status: {result.Status}.");
            Text(result.Summary, "result summary");
            if (result.SourceRuleId is not null)
                Text(result.SourceRuleId, "sourceRuleId", 1024);
            if (result.CheckId == "axe.scan" && result.Status == "rule-failure" && string.IsNullOrWhiteSpace(result.SourceRuleId))
                throw new InvalidDataException("Axe rule failures must retain their actual native rule ID.");
            if (result.ElementId is not null && !nodeIds.Contains(result.ElementId))
                throw new InvalidDataException($"Unknown snapshot element ID: {result.ElementId}.");
            if (result.Details is null || result.Details.Count > 256)
                throw new InvalidDataException("Result details are missing or unbounded.");
            foreach (var (key, value) in result.Details)
            {
                Text(key, "detail key", 1024);
                Text(value, "detail value", allowBlank: true);
            }
            ValidateReferences(result.Evidence, paths);
        }
        var count = Property(json, "ruleFailureCount");
        if (count.ValueKind != JsonValueKind.Undefined
            && (count.ValueKind != JsonValueKind.Number || !count.TryGetInt32(out var reportedCount) || reportedCount != report.RuleFailureCount))
            throw new InvalidDataException("ruleFailureCount disagrees with the automatic result records.");
        ValidateAxe(report);
    }

    internal static HashSet<string> SnapshotNodeIds(JsonElement? snapshot)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (snapshot is null || snapshot.Value.ValueKind == JsonValueKind.Null)
            return ids;
        var root = snapshot.Value;
        RequireMembers(root, "snapshot", "schemaVersion", "nodes");
        var schema = Property(root, "schemaVersion");
        if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("Only snapshot schemaVersion 1 is supported.");
        var nodes = Property(root, "nodes");
        if (nodes.ValueKind != JsonValueKind.Array || nodes.GetArrayLength() > 10_000)
            throw new InvalidDataException("Snapshot nodes must be a bounded array.");
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var node in nodes.EnumerateArray())
        {
            RequireMembers(node, "snapshot node", "id");
            var idValue = Property(node, "id");
            var id = idValue.ValueKind == JsonValueKind.String ? idValue.GetString() : null;
            Text(id, "snapshot node id", 256);
            if (!ids.Add(id!))
                throw new InvalidDataException($"Duplicate snapshot node ID: {id}.");
            var parentValue = Property(node, "parentId");
            if (parentValue.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null or JsonValueKind.String))
                throw new InvalidDataException("snapshot parentId must be a string or null.");
            var parent = parentValue.ValueKind == JsonValueKind.String ? parentValue.GetString() : null;
            if (parent is not null)
                Text(parent, "snapshot parentId", 256);
            parents.Add(id!, parent);
        }
        var verified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            var path = new HashSet<string>(StringComparer.Ordinal);
            for (string? current = id; current is not null && !verified.Contains(current); current = parents[current])
            {
                if (!parents.ContainsKey(current) || !path.Add(current))
                    throw new InvalidDataException("Snapshot parent references must exist and cannot form a cycle.");
            }
            verified.UnionWith(path);
        }
        return ids;
    }

    internal static void ValidateReferences(IReadOnlyList<string>? references, IReadOnlySet<string> artifacts)
    {
        if (references is null || references.Count > MaxArtifacts)
            throw new InvalidDataException("Evidence references are missing or unbounded.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var reference in references)
        {
            var normalized = NormalizeArtifactPath(reference);
            if (!artifacts.Contains(normalized) || !seen.Add(normalized))
                throw new InvalidDataException($"Evidence must reference a unique artifact listed in scan.json: {reference}.");
        }
    }

    private static void ValidateObservation(AuditReport report)
    {
        if (report.Observation is not { ValueKind: not JsonValueKind.Null } observation)
            return;
        RequireMembers(observation, "observation", "schemaVersion", "focusTrace", "events");
        var schema = Property(observation, "schemaVersion");
        if (schema.ValueKind != JsonValueKind.Number || !schema.TryGetInt32(out var version) || version != 1)
            throw new InvalidDataException("Only observation schemaVersion 1 is supported.");
        foreach (var name in new[] { "focusTrace", "events" })
        {
            var array = Property(observation, name);
            if (array.ValueKind != JsonValueKind.Array || array.GetArrayLength() > 50_000
                || array.EnumerateArray().Any(item => item.ValueKind != JsonValueKind.Object))
                throw new InvalidDataException($"observation.{name} must be a bounded array of objects.");
        }
    }

    private static void ValidateAxe(AuditReport report)
    {
        if (report.Axe is not { } axe)
            return;
        Text(axe.Engine, "Axe engine", 256);
        Text(axe.EngineVersion, "Axe engineVersion", 256);
        if (axe.ProcessId != report.Target.ProcessId || axe.WindowHandle != report.Target.WindowHandle
            || axe.ResultGroupErrorCounts is null || axe.ResultGroupErrorCounts.Count is 0 or > 10_000
            || axe.ResultGroupErrorCounts.Any(count => count < 0)
            || axe.Findings is null || axe.Findings.Count > 50_000
            || axe.ResultGroupErrorCounts.Sum(count => (long)count) is > int.MaxValue
            || axe.ResultGroupErrorCounts.Sum(count => (long)count) < axe.Findings.Count
            || (axe.Findings.Count == 0 && axe.ResultGroupErrorCounts.Any(count => count > 0)))
            throw new InvalidDataException("Axe scan identity, result groups, or finding counts are invalid.");
        foreach (var finding in axe.Findings)
        {
            if (finding is null)
                throw new InvalidDataException("Null Axe findings are not allowed.");
            Text(finding.RuleId, "Axe rule ID", 1024);
            Text(finding.Description, "Axe description", allowBlank: true);
            Text(finding.HowToFix, "Axe howToFix", allowBlank: true);
            Text(finding.Standard, "Axe standard", allowBlank: true);
            if (finding.ElementProperties is null || finding.ElementProperties.Count > 256
                || finding.Patterns is null || finding.Patterns.Count > 256)
                throw new InvalidDataException("Axe finding properties or patterns are missing or unbounded.");
            foreach (var (key, value) in finding.ElementProperties)
            {
                Text(key, "Axe property key", 1024);
                Text(value, "Axe property value", allowBlank: true);
            }
            foreach (var pattern in finding.Patterns)
                Text(pattern, "Axe pattern", 1024);
        }
    }
}
