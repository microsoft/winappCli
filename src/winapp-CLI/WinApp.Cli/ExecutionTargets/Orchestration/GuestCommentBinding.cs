// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

// Host-created authority: guest requests never select the host project or store path.
internal sealed class GuestCommentBinding
{
    private readonly HashSet<string> _sourceFiles;
    private readonly string _projectPath;
    internal string Id { get; } = Guid.NewGuid().ToString("N");
    internal string TargetId { get; }
    internal string Epoch { get; }
    internal GuestProcessStart Process { get; }
    internal string ProjectRoot { get; }
    internal string StorePath { get; }
    internal string? GuestSourceRoot { get; }

    internal GuestCommentBinding(
        ExecutionTargetRef target, ExecutionTargetEpoch epoch, GuestProcessStart process,
        FileInfo project, IEnumerable<string> snapshotRelativeXamlFiles, string? guestSourceRoot = null)
    {
        if (!project.Exists || project.DirectoryName is null || process.ProcessId <= 0 || process.StartTicksUtc <= 0 ||
            epoch.IsNone)
        {
            throw new InvalidOperationException("Host comments require a proven project and a live guest process identity.");
        }
        ProjectRoot = project.DirectoryName;
        _projectPath = project.FullName;
        TargetId = target.StateKey;
        Epoch = epoch.Value;
        Process = process;
        StorePath = CommentStoreLocator.Resolve(ProjectRoot).StorePath;
        GuestSourceRoot = guestSourceRoot;
        _sourceFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in snapshotRelativeXamlFiles)
        {
            var relative = ValidateRelativeSource(file);
            var full = Path.GetFullPath(Path.Combine(ProjectRoot, relative));
            if (!File.Exists(full) || PathSafety.HasReparsePointOnPath(full, ProjectRoot))
            {
                throw new InvalidOperationException("A comment source snapshot entry is missing or redirected.");
            }
            _sourceFiles.Add(relative);
        }
        if (_sourceFiles.Count == 0)
        {
            throw new InvalidOperationException("Host-backed comments require a nonempty, verified XAML source snapshot.");
        }
    }

    internal void Validate(string bindingId, string targetId, string epoch, GuestProcessStart process)
    {
        if (bindingId != Id || targetId != TargetId || epoch != Epoch || process != Process)
        {
            throw new InvalidOperationException("The comment request belongs to a different or expired guest launch. Rediscover the app.");
        }
    }

    internal void VerifyHostPaths()
    {
        if (PathSafety.HasReparsePointOnPath(_projectPath, ProjectRoot) || !File.Exists(_projectPath) ||
            PathSafety.HasReparsePointOnPath(StorePath, Path.GetDirectoryName(Path.GetDirectoryName(StorePath))!))
        {
            throw new InvalidOperationException("The bound host project or comment store is no longer eligible.");
        }
        foreach (var file in _sourceFiles)
        {
            var full = Path.Combine(ProjectRoot, file);
            if (PathSafety.HasReparsePointOnPath(full, ProjectRoot) || !File.Exists(full))
            {
                throw new InvalidOperationException("The host source snapshot is no longer eligible. Relaunch to refresh it.");
            }
        }
    }

    internal Comment Map(Comment input)
    {
        if (string.IsNullOrWhiteSpace(input.Id) || input.Id.Length > 256 ||
            string.IsNullOrWhiteSpace(input.Text) || input.Text.Length > 64 * 1024 ||
            !CommentStatus.IsValid(input.Status) ||
            (input.Kind is not null && !CommentKind.IsValid(input.Kind)) || input.Anchor?.Identity is null)
        {
            throw new InvalidOperationException("The guest comment is incomplete or exceeds the comment limits.");
        }
        var relative = ValidateRelativeSource(input.Anchor.SourceFile ?? string.Empty);
        if (!_sourceFiles.TryGetValue(relative, out var canonical))
        {
            throw new InvalidOperationException("The comment source is not part of this launch's verified project snapshot.");
        }
        if (input.Anchor.Authored is { } identity)
        {
            CommentAuthoredIdentity.Validate(identity);
            if (!CommentStoreLocator.SameProject(identity.ProjectRoot, GuestSourceRoot) ||
                !ValidateRelativeSource(identity.SourceFile).Equals(canonical, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("The authored identity belongs to a different guest project or source.");
            }
        }
        // Clone before assigning host ownership; never mutate the deserialized request or trust its project path.
        var mapped = JsonSerializer.Deserialize(
            JsonSerializer.Serialize(input, CommentsJsonContext.Default.Comment), CommentsJsonContext.Default.Comment)!;
        mapped.ProjectRoot = ProjectRoot;
        mapped.Author = Environment.UserName;
        mapped.Anchor.SourceFile = canonical;
        mapped.Anchor.SourceUri = "ms-appx:///" + canonical.Replace('\\', '/');
        if (mapped.Anchor.Authored is { } authored)
        {
            authored.ProjectRoot = ProjectRoot;
            authored.SourceFile = canonical;
        }
        return mapped;
    }

    internal static string ValidateRelativeSource(string value)
    {
        var relative = value.Replace('/', '\\');
        var parts = relative.Split('\\');
        if (Path.IsPathRooted(relative) || !relative.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase) ||
            parts.Any(p => p.Length == 0 || p is "." or ".." || p.StartsWith('.') ||
                p.Contains(':') || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
                p.Equals("bin", StringComparison.OrdinalIgnoreCase) || p.Equals("obj", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("Comment sources must be project-relative XAML snapshot entries.");
        }
        return relative;
    }
}

internal sealed record GuestCommentRequest(
    string BindingId, string TargetId, string Epoch, GuestProcessStart Process,
    string OperationId, string Id, string ExpectedRevision, Comment? Replacement);

/// <summary>One fixed host project/store; acknowledgement follows the atomic store write.</summary>
internal sealed class GuestCommentOwner(GuestCommentBinding binding, CommentStore store)
{
    internal GuestCommentCommit Apply(GuestCommentRequest request)
    {
        binding.Validate(request.BindingId, request.TargetId, request.Epoch, request.Process);
        binding.VerifyHostPaths();
        if (!Guid.TryParseExact(request.OperationId, "N", out _) ||
            string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 256 || request.Id.Any(char.IsControl) ||
            (request.ExpectedRevision != string.Empty && !CommentStore.ValidRevision(request.ExpectedRevision)))
        {
            throw new InvalidOperationException("A valid stable operation and comment identity are required.");
        }
        var replacement = request.Replacement is null ? null : binding.Map(request.Replacement);
        var resolvedAt = replacement?.Resolution?.ResolvedAt;
        if (replacement is not null)
        {
            // Client clock values must not turn a replay into different content; the host owns timestamps.
            replacement.CreatedAt = string.Empty;
            replacement.UpdatedAt = string.Empty;
            if (replacement.Resolution is { } resolution)
            {
                resolution.ResolvedAt = string.Empty;
            }
        }
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(binding.ProjectRoot + "\n" + request.Id + "\n" +
                request.ExpectedRevision + "\n" + CommentStore.Revision(replacement))));
        if (replacement is not null)
        {
            replacement.CreatedAt = CommentTimestamps.Now();
            replacement.UpdatedAt = replacement.CreatedAt;
            if (replacement.Resolution is { } resolution)
            {
                resolution.ResolvedAt = resolvedAt!;
            }
        }
        return store.CompareExchange(binding.StorePath, binding.ProjectRoot,
            request.Id, request.ExpectedRevision, replacement, binding.Id + ":" + request.OperationId, hash);
    }

    internal IReadOnlyList<Comment> Read() => ReadSnapshot().Comments;

    internal CommentStoreDocument ReadSnapshot()
    {
        binding.VerifyHostPaths();
        var document = store.Load(binding.StorePath);
        return new()
        {
            Generation = document.Generation,
            Comments = document.Comments.FindAll(comment => CommentStoreLocator.SameProject(comment.ProjectRoot, binding.ProjectRoot)),
        };
    }
}
