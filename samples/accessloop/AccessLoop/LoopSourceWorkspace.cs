namespace AccessLoop;

public static class LoopSourceWorkspace
{
    public static string Copy(string project, string destination)
    {
        LoopInput.RequireProject(Path.GetFullPath(project));
        var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(project))!;
        var output = Path.GetFullPath(destination);
        var sourcePrefix = Path.TrimEndingDirectorySeparator(sourceRoot) + Path.DirectorySeparatorChar;
        if (output.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)
            || output.StartsWith(sourcePrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Store an owned source copy outside the source project.");
        var sources = JourneyInput.CaptureSources(project);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        AuditFiles.CreateNewDirectory(output);
        foreach (var source in sources)
        {
            var input = AuditFiles.ResolveArtifact(sourceRoot, source.RelativePath);
            var target = AuditFiles.ArtifactDestination(output, source.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(input, target, overwrite: false);
            if (AuditFiles.Hash(target) != source.Sha256)
                throw new InvalidDataException("A source file changed while its owned copy was being created.");
        }
        var copiedProject = AuditFiles.ResolveArtifact(output, Path.GetFileName(project));
        RequireSources(project, sources);
        RequireSources(copiedProject, sources);
        return copiedProject;
    }

    public static void RequireSources(string project, IReadOnlyList<JourneySourceStamp> expected)
    {
        if (!LoopEvidence.SameSources(expected, JourneyInput.CaptureSources(project)))
            throw new InvalidDataException("Sources changed outside the accepted Name-only patch. The frozen loop cannot continue.");
    }

    public static IReadOnlyList<JourneySourceStamp> WithPatch(
        string project, IReadOnlyList<JourneySourceStamp> previous, AppliedRepair patch)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(project))!;
        var file = LoopEvidence.BoundFile(root, patch.FullPath);
        var relative = Path.GetRelativePath(root, file);
        if (previous.Count(item => item.RelativePath == relative) != 1 || AuditFiles.Hash(file) != patch.UpdatedSha256)
            throw new InvalidDataException("The accepted repair does not match the frozen source inventory.");
        var updated = previous.Select(item => item.RelativePath == relative ? item with { Sha256 = patch.UpdatedSha256 } : item).ToArray();
        RequireSources(project, updated);
        return updated;
    }
}
