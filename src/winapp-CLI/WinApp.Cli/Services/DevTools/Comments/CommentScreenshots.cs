// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace WinApp.Cli.Services.DevTools.Comments;

/// <summary>
/// Comment screenshots live in <c>.winapp/ui-comment-shots/&lt;id&gt;.png</c>, beside the store and under its
/// ignore file. The store owns their lifetime: a file is removed when its comment stops referencing it.
/// </summary>
internal static class CommentScreenshots
{
    public const string DirectoryName = "ui-comment-shots";

    // The element-snapshot pipeline renders, crops and encodes in about 150 ms; a stalled UI thread must not
    // hold up the save, which succeeds without an image.
    private const int SnapshotTimeoutMs = 6000;

    public static string RelativePath(string id) => $"{DirectoryName}/{id}.png";

    /// <summary>
    /// The file a stored reference names, or null when it is not this comment's canonical screenshot. Only that
    /// exact name is ever read or deleted, whatever the store says.
    /// </summary>
    public static string? FullPath(string storePath, Comment comment)
    {
        if (comment.Screenshot?.Path is not { } relative || !IsSafeId(comment.Id) ||
            !string.Equals(relative, RelativePath(comment.Id), StringComparison.Ordinal))
        {
            return null;
        }
        var winapp = Path.GetDirectoryName(storePath);
        return winapp is null ? null : Path.Combine(winapp, DirectoryName, comment.Id + ".png");
    }

    internal static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 128 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    /// <summary>The caveats an agent must know before trusting what the image shows, as a short suffix.</summary>
    public static string Describe(CommentScreenshot shot)
    {
        var notes = new List<string>();
        if (shot.Visibility == "partial")
        {
            notes.Add("element partly scrolled or clipped out of view");
        }
        if (shot.NotCaptured is { Count: > 0 } blank)
        {
            notes.Add($"{string.Join(", ", blank)} content renders blank");
        }
        return notes.Count == 0 ? "" : $" ({string.Join("; ", notes)})";
    }

    /// <summary>The view's copy of the reference, with an absolute path, when the file exists.</summary>
    public static CommentScreenshot? ForOutput(string? storePath, Comment comment)
    {
        if (storePath is null || FullPath(storePath, comment) is not { } path || !File.Exists(path))
        {
            return null;
        }
        var shot = comment.Screenshot!;
        return new CommentScreenshot
        {
            Path = path, Width = shot.Width, Height = shot.Height, CapturedAt = shot.CapturedAt,
            Visibility = shot.Visibility, Theme = shot.Theme, NotCaptured = shot.NotCaptured,
        };
    }

    /// <summary>Deletes the screenshots that <paramref name="before"/> referenced and <paramref name="after"/> does not.</summary>
    public static void DeleteReleased(string storePath, IReadOnlyCollection<string> before, IEnumerable<Comment> after)
    {
        if (before.Count == 0)
        {
            return;
        }
        var kept = Referenced(storePath, after).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var path in before.Where(path => !kept.Contains(path)))
        {
            TryDelete(path);
        }
    }

    public static List<string> Referenced(string storePath, IEnumerable<Comment> comments) =>
        comments.Select(c => FullPath(storePath, c)).OfType<string>().ToList();

    internal static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover image is harmless; the comment it belonged to is already gone.
        }
    }

    /// <summary>A captured image, written beside its final name until the comment that references it is saved.</summary>
    internal sealed class Pending(string temporary, string final, CommentScreenshot reference) : IDisposable
    {
        public CommentScreenshot Reference { get; } = reference;

        public void Commit() => File.Move(temporary, final, overwrite: true);

        public void Dispose() => TryDelete(temporary);
    }

    /// <summary>
    /// Asks the app to render the element and stages the PNG. Best effort: any failure returns null and the
    /// comment is saved without an image.
    /// </summary>
    public static Pending? Capture(uint pid, string handle, string storePath, string id, ILogger logger,
        CancellationToken cancellationToken)
    {
        if (!IsSafeId(id))
        {
            return null;
        }
        try
        {
            var started = Environment.TickCount64;
            var response = new VisualTreeTap(pid).GetElementSnapshot(handle, SnapshotTimeoutMs, cancellationToken);
            if (!response.Ok)
            {
                logger.LogDebug("No screenshot for comment {Id}: {Error}", id, response.Error?.Token);
                return null;
            }
            using var doc = JsonDocument.Parse(response.ResultJson!, TapWireJson.DocumentOptions);
            var root = doc.RootElement;
            var png = Convert.FromBase64String(root.GetProperty("png").GetString()!);
            if (png.Length is 0 or > 512 * 1024 || !png.AsSpan().StartsWith((ReadOnlySpan<byte>)[0x89, (byte)'P', (byte)'N', (byte)'G']))
            {
                return null;
            }
            var reference = new CommentScreenshot
            {
                Path = RelativePath(id),
                Width = root.GetProperty("width").GetInt32(),
                Height = root.GetProperty("height").GetInt32(),
                CapturedAt = CommentTimestamps.Now(),
                Visibility = root.GetProperty("visibility").GetString() == "partial" ? "partial" : null,
                Theme = root.GetProperty("theme").GetString(),
                NotCaptured = root.GetProperty("notCaptured").EnumerateArray().Select(e => e.GetString()!).ToList() is { Count: > 0 } kinds ? kinds : null,
            };
            var directory = Path.Combine(Path.GetDirectoryName(storePath)!, DirectoryName);
            if (Helpers.PathSafety.HasReparsePointOnPath(directory, Path.GetDirectoryName(Path.GetDirectoryName(storePath)!)!))
            {
                return null;
            }
            Directory.CreateDirectory(directory);
            var final = Path.Combine(directory, id + ".png");
            var temporary = final + ".tmp-" + Guid.NewGuid().ToString("N");
            File.WriteAllBytes(temporary, png);
            logger.LogDebug("Screenshot for comment {Id}: {Width}x{Height}, {Bytes} bytes, {Elapsed} ms (app timing {Timing})",
                id, reference.Width, reference.Height, png.Length, Environment.TickCount64 - started,
                root.TryGetProperty("timing", out var timing) ? timing.GetRawText() : "");
            return new Pending(temporary, final, reference);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException or
            IOException or UnauthorizedAccessException or DevToolsProtocolException)
        {
            logger.LogDebug("No screenshot for comment {Id}: {Message}", id, ex.Message);
            return null;
        }
    }
}
