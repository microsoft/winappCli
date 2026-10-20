using System.Security.Cryptography;

namespace AccessLoop;

public sealed class JourneyInput
{
    private readonly byte[] bytes;
    public string Path { get; }
    public JourneyDefinition Definition { get; }
    public string Sha256 { get; }
    public string ProjectRoot => System.IO.Path.GetDirectoryName(Definition.Project)!;

    private JourneyInput(string path, byte[] content, JourneyDefinition definition)
    {
        Path = path;
        bytes = content;
        Definition = definition;
        Sha256 = Convert.ToHexString(SHA256.HashData(bytes));
    }

    public static JourneyInput Load(string path)
    {
        var full = System.IO.Path.GetFullPath(path);
        var content = AuditFiles.ReadBytes(full, 1024 * 1024);
        using var json = AuditFiles.ParseStrict(content);
        AuditFiles.RequireMembers(json.RootElement, "journey", "schemaVersion", "name", "project",
            "navigationRootId", "initialPageId", "pages");
        var definition = AuditFiles.Deserialize<JourneyDefinition>(json.RootElement);
        Validate(definition);
        var directory = System.IO.Path.GetDirectoryName(full)!;
        var project = AuditFiles.ResolveArtifact(directory, definition.Project);
        if (!project.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("The journey project must be a local, source-owned .csproj beneath its input directory.");
        return new(full, content, definition with { Project = project });
    }

    public void Save(string output)
    {
        AuditFiles.WriteNewBytes(System.IO.Path.Combine(output, "journey-input.json"), bytes);
        AuditFiles.WriteNew(System.IO.Path.Combine(output, "acceptance.json"), Definition);
    }

    public void EnsureUnchanged()
    {
        if (AuditFiles.Hash(Path) != Sha256)
            throw new InvalidDataException("The frozen journey input changed. Navigation and acceptance cannot be redefined during a repair run.");
    }

    public static void Validate(JourneyDefinition definition)
    {
        if (definition.SchemaVersion != 1 || definition.Pages is null || definition.Pages.Count is < 1 or > 20)
            throw new InvalidDataException("Journey schemaVersion 1 requires 1..20 explicitly declared pages.");
        Literal(definition.Name, "journey name");
        Literal(definition.NavigationRootId, "navigationRootId");
        Literal(definition.InitialPageId, "initialPageId");
        _ = AuditFiles.NormalizeArtifactPath(definition.Project);
        if (definition.TimeoutSeconds is < 1 or > 120 || definition.MaxTransitions is < 1 or > 200
            || definition.MaxRepairs is < 1 or > 20)
            throw new InvalidDataException("Journey limits: timeoutSeconds 1..120, maxTransitions 1..200, maxRepairs 1..20.");
        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var navIds = new HashSet<string>(StringComparer.Ordinal);
        var readyIds = new HashSet<string>(StringComparer.Ordinal);
        var repairIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in definition.Pages)
        {
            if (page is null)
                throw new InvalidDataException("Journey pages cannot be null.");
            Literal(page.Id, "page id");
            Literal(page.Title, "page title");
            Literal(page.NavigationId, "navigationId");
            Literal(page.ReadyId, "readyId");
            if (!pageIds.Add(page.Id) || !navIds.Add(page.NavigationId) || !readyIds.Add(page.ReadyId))
                throw new InvalidDataException("Each page, navigation entry and ready marker must have a distinct identity.");
            if (page.RequiredControlIds is null || page.RequiredControlIds.Count is < 1 or > 100
                || !page.RequiredControlIds.Contains(page.ReadyId, StringComparer.Ordinal)
                || page.RequiredControlIds.Distinct(StringComparer.Ordinal).Count() != page.RequiredControlIds.Count)
                throw new InvalidDataException($"Page '{page.Id}' must declare unique required controls, including its ready marker.");
            foreach (var id in page.RequiredControlIds)
                Literal(id, "required control ID");
            if (page.Exercise is { } exercise)
            {
                if (!repairIds.Add(exercise.TargetId))
                    throw new InvalidDataException("Each automatic repair target must have one unambiguous owning page exercise.");
                Literal(exercise.InitialResult, "initial result");
                if (exercise.InitialResult == exercise.ExpectedResult)
                    throw new InvalidDataException("Initial and successful exercise results must differ.");
                var scenario = ScenarioFor(definition, page);
                scenario.Validate();
                if (new[] { exercise.TargetId, exercise.AnchorId, exercise.ResultId }
                    .Any(id => !page.RequiredControlIds.Contains(id, StringComparer.Ordinal)))
                    throw new InvalidDataException("Every exercise target, input and result must be declared as a required page control.");
            }
        }
        if (!pageIds.Contains(definition.InitialPageId))
            throw new InvalidDataException("initialPageId must identify a declared page.");
        if (navIds.Overlaps(readyIds) || navIds.Contains(definition.NavigationRootId)
            || readyIds.Contains(definition.NavigationRootId))
            throw new InvalidDataException("Navigation root, entries and page markers must have distinct IDs.");
        if (repairIds.Overlaps(navIds) || repairIds.Overlaps(readyIds))
            throw new InvalidDataException("Repair targets cannot be navigation entries or page-ready markers.");
        if (definition.MaxTransitions < definition.Pages.Count * 2 - 1)
            throw new InvalidDataException("maxTransitions must allow returning home and visiting every declared page.");
    }

    public static Scenario ScenarioFor(JourneyDefinition definition, JourneyPageDefinition page)
    {
        var exercise = page.Exercise ?? throw new InvalidDataException("This page has no declared behavioral exercise.");
        return new Scenario
        {
            Name = page.Id, Project = definition.Project, TargetId = exercise.TargetId,
            ExpectedName = exercise.ExpectedName, AnchorId = exercise.AnchorId, ResultId = exercise.ResultId,
            InputText = exercise.InputText, ExpectedResult = exercise.ExpectedResult,
            MaxTabs = exercise.MaxTabs, TimeoutSeconds = definition.TimeoutSeconds
        };
    }

    public static IReadOnlyList<JourneySourceStamp> CaptureSources(string project)
    {
        var root = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(project))!;
        AuditFiles.EnsureDirectory(root);
        var stamps = new List<JourneySourceStamp>();
        long totalBytes = 0;
        void Visit(string directory)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(directory).Order(StringComparer.Ordinal))
            {
                var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Journey source workspaces cannot contain symbolic links or reparse points.");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (System.IO.Path.GetFileName(path) is not ("bin" or "obj" or ".git" or ".accessloop" or "TestResults"))
                        Visit(path);
                    continue;
                }
                var info = new FileInfo(path);
                if (info.Length > 4 * 1024 * 1024 || (totalBytes += info.Length) > 32 * 1024 * 1024 || stamps.Count >= 1000)
                    throw new InvalidDataException("Journey source fingerprint exceeds its bounded workspace budget.");
                stamps.Add(new(System.IO.Path.GetRelativePath(root, path), AuditFiles.Hash(path)));
            }
        }
        Visit(root);
        return stamps;
    }

    public void EnsureSources(IReadOnlyList<JourneySourceStamp> expected)
    {
        EnsureUnchanged();
        var actual = CaptureSources(Definition.Project);
        if (actual.Count != expected.Count || actual.Any(item =>
            expected.Count(original => original.RelativePath == item.RelativePath && original.Sha256 == item.Sha256) != 1))
            throw new InvalidDataException("Source files changed outside the accepted Name-only patch; the frozen acceptance run cannot continue.");
    }

    public IReadOnlyList<JourneySourceStamp> WithPatch(IReadOnlyList<JourneySourceStamp> previous, AppliedRepair repair)
    {
        var relative = System.IO.Path.GetRelativePath(ProjectRoot, repair.FullPath);
        if (previous.Count(item => item.RelativePath == relative) != 1)
            throw new InvalidDataException("The repaired source was not part of the frozen source inventory.");
        return previous.Select(item => item.RelativePath == relative ? item with { Sha256 = repair.UpdatedSha256 } : item).ToArray();
    }

    private static void Literal(string? value, string field)
    {
        AuditFiles.Text(value, field, 256);
        var literal = value ?? throw new InvalidDataException($"{field} is required.");
        if (literal.Any(char.IsControl))
            throw new InvalidDataException($"{field} must not contain control characters.");
        if (field is "page id" or "navigationId" or "required control ID" or "navigationRootId" or "readyId" or "initialPageId")
            if (literal.IndexOfAny(['/', '\\', ':']) >= 0 || literal is "." or "..")
                throw new InvalidDataException($"{field} must be a literal local UI identifier, not a path.");
    }
}
