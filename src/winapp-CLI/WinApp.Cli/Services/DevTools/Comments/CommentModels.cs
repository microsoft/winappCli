// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.DevTools.Comments;

// Persisted UI-comments store; comments use durable source identity, never ephemeral wire handles.
internal sealed class CommentStoreDocument
{
    [JsonRequired]
    public int Version { get; set; } = 1;

    // Bumped on every write; running views drop snapshots older than the last one they applied.
    public long Generation { get; set; }

    [JsonRequired]
    public List<Comment> Comments { get; set; } = [];

    // In the comment document itself so a guest mutation and its idempotency receipt commit atomically.
    public Dictionary<string, GuestCommentReceipt>? GuestReceipts { get; set; }
}

internal sealed record GuestCommentReceipt(string RequestHash, string? PersistedRevision);

internal sealed record GuestCommentCommit(string? PersistedRevision, Comment? Current);

internal sealed class Comment
{
    [JsonRequired]
    public string Id { get; set; } = string.Empty;

    [JsonRequired]
    public string Text { get; set; } = string.Empty;

    [JsonRequired]
    public string Status { get; set; } = CommentStatus.Open;

    public string? Kind { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public string UpdatedAt { get; set; } = string.Empty;

    public string? Author { get; set; }

    public string? ProjectRoot { get; set; }

    [JsonRequired]
    public CommentAnchor Anchor { get; set; } = new();

    public CommentContext? Context { get; set; }

    public CommentResolution? Resolution { get; set; }
}

internal sealed class CommentAnchor
{
    public string? SourceFile { get; set; }

    public string? SourceUri { get; set; }

    public int? Line { get; set; }

    public int? Column { get; set; }
    public string? SourceProvenance { get; set; }
    public string? SourceEvidence { get; set; }
    public int? RawLine { get; set; }
    public int? RawColumn { get; set; }

    public CommentAuthoredAnchor? Authored { get; set; }

    [JsonRequired]
    public CommentIdentity Identity { get; set; } = new();

    /// <summary>
    /// The running app's opaque source-qualified anchor, including authored parent context.
    /// Captured by the tap and consumed unchanged by the overlay. Absent for offline comments
    /// or when the live source/parent context cannot be established.
    /// </summary>
    public string? ElementPath { get; set; }

    /// <summary>Identity is weak / source uninstrumented — the agent must grep the whole project and not guess.</summary>
    public bool Weak { get; set; }

    public bool Templated { get; set; }
}

internal sealed class CommentAuthoredAnchor
{
    [JsonRequired]
    public string ProjectRoot { get; set; } = string.Empty;

    [JsonRequired]
    public string SourceFile { get; set; } = string.Empty;

    [JsonRequired]
    public string Declaration { get; set; } = string.Empty;

    [JsonRequired]
    public string Signature { get; set; } = string.Empty;

    [JsonRequired]
    public string Type { get; set; } = string.Empty;

    [JsonRequired]
    public string Scope { get; set; } = string.Empty;

    [JsonRequired]
    public string Structure { get; set; } = string.Empty;

    public bool Templated { get; set; }

    public bool UniqueInstance { get; set; }

    public string? UniquenessReason { get; set; }
}

internal sealed class CommentIdentity
{
    public string? Type { get; set; }

    public string? Name { get; set; }

    public string? AutomationId { get; set; }

    public string? Content { get; set; }

    public string? TreePath { get; set; }
}

internal sealed class CommentContext
{
    public string? Property { get; set; }

    public CommentBounds? Bounds { get; set; }

    /// <summary>The element's style when it comes from a resource. Captured from a live element only.</summary>
    public CommentStyleContext? Style { get; set; }

    /// <summary>The element's set brush properties and the resources they come from. Captured from a live element only.</summary>
    public List<CommentBrushContext>? Brushes { get; set; }
}

internal sealed class CommentStyleContext
{
    /// <summary>The style's resource key; absent for an implicit style, which applies by <see cref="TargetType"/>.</summary>
    public string? Key { get; set; }

    /// <summary><c>staticResource</c>, <c>themeResource</c> or <c>implicit</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public string? TargetType { get; set; }

    /// <summary>Where the style is declared, when that is in the project.</summary>
    public string? File { get; set; }

    public int? Line { get; set; }
}

internal sealed class CommentBrushContext
{
    public string Property { get; set; } = string.Empty;

    /// <summary>The resolved value, such as <c>#FF0067C0</c>.</summary>
    public string? Value { get; set; }

    /// <summary>Where the value comes from: <c>Local</c>, <c>Style</c>, <c>Built-in style</c>, <c>Template</c>…</summary>
    public string? Source { get; set; }

    public string? ResourceKey { get; set; }

    /// <summary><c>themeResource</c> or <c>staticResource</c>, with <see cref="ResourceKey"/>.</summary>
    public string? ResourceKind { get; set; }

    /// <summary>The project file that sets it (the element or its style), when known.</summary>
    public string? File { get; set; }

    public int? Line { get; set; }
}

internal sealed class CommentBounds
{
    public double X { get; set; }

    public double Y { get; set; }

    public double W { get; set; }

    public double H { get; set; }
}

internal sealed class CommentResolution
{
    public string? ResolvedAt { get; set; }

    public string? ResolvedBy { get; set; }

    public string? Note { get; set; }
}

internal static class CommentStatus
{
    public const string Open = "open";
    public const string Resolved = "resolved";
    public const string Stale = "stale";
    public const string Dismissed = "dismissed";

    public static readonly string[] All = [Open, Resolved, Stale, Dismissed];

    public static bool IsValid(string? s) => s is not null && Array.Exists(All, v => v == s);
}

internal static class CommentKind
{
    public static readonly string[] All = ["visual", "binding", "behavior", "a11y", "other"];

    public static bool IsValid(string? s) => s is not null && Array.Exists(All, v => v == s);
}

// ---- OUTPUT (--json) shapes: the stable agent contract. Kept separate from the persisted
//      store so re-derived, non-persisted `hits[]` never leak into the file. ----

internal sealed class CommentsListPayload
{
    public int Version { get; set; } = 1;

    public CommentAppInfo App { get; set; } = new();

    public List<CommentView> Comments { get; set; } = [];
}

internal sealed class CommentAppInfo
{
    public string? Title { get; set; }

    public string? SourceRoot { get; set; }

    public string? StorePath { get; set; }
}

internal sealed class CommentView
{
    public string Id { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public string Status { get; set; } = CommentStatus.Open;

    public string? Kind { get; set; }

    public string CreatedAt { get; set; } = string.Empty;

    public string UpdatedAt { get; set; } = string.Empty;

    public CommentAnchor Anchor { get; set; } = new();

    public string? ProjectRoot { get; set; }

    public CommentContext? Context { get; set; }

    public CommentResolution? Resolution { get; set; }

    public List<SourceHit> Hits { get; set; } = [];

    public MappedSourceCandidate? MappedSourceCandidate { get; set; }

    public bool AnchorConfirmed { get; set; }

    public bool RequiresConfirmation { get; set; }

    public bool Ambiguous { get; set; }

    public List<string> Candidates { get; set; } = [];

    public string? CandidatesReason { get; set; }
}

internal sealed record MappedSourceCandidate(string File, int Line, int Column, string Provenance,
    bool Advisory = true);

internal sealed class SourceHit
{
    public string File { get; set; } = string.Empty;

    public int Line { get; set; }

    public int Column { get; set; }

    public string Confidence { get; set; } = "weak";

    internal int Rank { get; set; }

    public string Via { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;
}

internal sealed class CommentResultPayload
{
    public bool Ok { get; set; }

    public string? Error { get; set; }

    public string? Warning { get; set; }

    public CommentView? Comment { get; set; }
}

[JsonSerializable(typeof(CommentStoreDocument))]
[JsonSerializable(typeof(CommentsListPayload))]
[JsonSerializable(typeof(CommentResultPayload))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class CommentsJsonContext : JsonSerializerContext
{
    private static CommentsJsonContext? s_output;

    /// <summary>Command output: relaxed escaping so captured XAML reads as XAML. The persisted store keeps the default.</summary>
    internal static CommentsJsonContext Output => s_output ??= new(new System.Text.Json.JsonSerializerOptions(Default.Options)
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    });
}
