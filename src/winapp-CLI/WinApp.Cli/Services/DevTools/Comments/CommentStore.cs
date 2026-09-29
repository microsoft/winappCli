// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.DevTools.Comments;

using WinApp.Cli.Helpers;

/// <summary>
/// Reader/writer for the persisted UI-comments store (<c>&lt;repo root&gt;/.winapp/ui-comments.json</c>). The store
/// is the single source of truth for the comment→agent-resolve loop and must survive the running app (a rebuild
/// kills the tap pipe), so this is a plain file store, not a pipe verb. All mutations are read-modify-write
/// under a cross-process mutex and land via write-temp-then-rename so a crashed writer never corrupts the file.
/// </summary>
internal interface ICommentStore
{
    bool Exists(string storePath) => File.Exists(storePath);
    string? GetRevision(Comment comment) => null;

    CommentStoreLocation Locate(string? startDirectory = null);

    string GetStorePath(DirectoryInfo? baseDirectory = null);

    CommentStoreDocument Load(string storePath);

    Comment Add(string storePath, Comment comment);

    Comment AddOrReplace(string storePath, Comment comment, out bool replaced);

    Comment? Update(string storePath, string id, Action<Comment> mutate);

    Comment? Get(string storePath, string id);

    Comment? Delete(string storePath, string id);
}

internal sealed class CommentStore : ICommentStore
{
    public CommentStoreLocation Locate(string? startDirectory = null)
        => CommentStoreLocator.Resolve(startDirectory);

    public string GetStorePath(DirectoryInfo? baseDirectory = null)
        => Locate(baseDirectory?.FullName).StorePath;

    public CommentStoreDocument Load(string storePath)
    {
        try
        {
            if (!File.Exists(storePath))
            {
                return new CommentStoreDocument();
            }

            var json = File.ReadAllText(storePath);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new CommentStoreDocument();
            }

            var doc = System.Text.Json.JsonSerializer.Deserialize(json, CommentsJsonContext.Default.CommentStoreDocument);
            var ids = new HashSet<string>(StringComparer.Ordinal);
            if (doc is null || doc.Version != 1 || doc.Generation < 0 || doc.Comments is null ||
                doc.Comments.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || !ids.Add(c.Id) ||
                    c.Text is null || !CommentStatus.IsValid(c.Status) || c.Anchor?.Identity is null) ||
                doc.GuestReceipts?.Values.Any(r => r is null || !ValidRevision(r.RequestHash) ||
                    (r.PersistedRevision is not null && !ValidRevision(r.PersistedRevision))) == true)
            {
                throw new System.Text.Json.JsonException("Expected a version-1 comments document.");
            }
            return doc;
        }
        catch (System.Text.Json.JsonException ex)
        {
            // Fail CLOSED on a corrupt store: never silently treat malformed JSON as "empty", or a mutating verb
            // would overwrite (and permanently discard) recoverable content. Read verbs surface the error and
            // exit non-zero; the mutating path (WithExclusiveStore) refuses to write and preserves the file.
            throw new CommentStoreCorruptException(
                $"UI comments store is corrupt (invalid JSON): {storePath}. It was left unchanged — fix or delete the file to continue.",
                ex);
        }
    }

    public Comment Add(string storePath, Comment comment)
    {
        WithExclusiveStore(storePath, doc => doc.Comments.Add(comment));
        return comment;
    }

    public Comment AddOrReplace(string storePath, Comment comment, out bool replaced)
    {
        var didReplace = false;
        WithExclusiveStore(storePath, doc =>
        {
            var idx = doc.Comments.FindIndex(c => c.Id == comment.Id);
            if (idx >= 0)
            {
                var previous = doc.Comments[idx];
                if (!CommentStoreLocator.SameProject(previous.ProjectRoot, comment.ProjectRoot))
                {
                    throw new InvalidOperationException($"Comment '{comment.Id}' belongs to a different project. It was left unchanged.");
                }
                // Edit in place: keep the original authored time, swap in the new text/anchor/context.
                comment.CreatedAt = doc.Comments[idx].CreatedAt;
                doc.Comments[idx] = comment;
                didReplace = true;
            }
            else
            {
                doc.Comments.Add(comment);
            }
        });
        replaced = didReplace;
        return comment;
    }

    public Comment? Update(string storePath, string id, Action<Comment> mutate)
    {
        Comment? result = null;
        WithExclusiveStore(storePath, doc =>
        {
            var target = doc.Comments.Find(c => c.Id == id);
            if (target is null)
            {
                return;
            }

            mutate(target);
            target.UpdatedAt = CommentTimestamps.Now();
            result = target;
        });
        return result;
    }

    public Comment? Get(string storePath, string id)
        => Load(storePath).Comments.Find(c => c.Id == id);

    public Comment? Delete(string storePath, string id)
    {
        Comment? removed = null;
        WithExclusiveStore(storePath, doc =>
        {
            var idx = doc.Comments.FindIndex(c => c.Id == id);
            if (idx < 0)
            {
                return;
            }

            removed = doc.Comments[idx];
            doc.Comments.RemoveAt(idx);
        });
        return removed;
    }

    internal GuestCommentCommit CompareExchange(
        string storePath, string projectRoot, string id, string expectedRevision, Comment? replacement,
        string operationId, string requestHash)
    {
        Comment? result = null;
        string? persistedRevision = null;
        WithExclusiveStore(storePath, doc =>
        {
            if (doc.GuestReceipts?.TryGetValue(operationId, out var receipt) == true)
            {
                if (receipt.RequestHash != requestHash)
                {
                    throw new InvalidOperationException("A comment operation ID was reused with different content.");
                }
                result = doc.Comments.Find(c => c.Id == id &&
                    CommentStoreLocator.SameProject(c.ProjectRoot, projectRoot));
                persistedRevision = receipt.PersistedRevision;
                return;
            }
            if (doc.GuestReceipts?.Count >= 10_000)
            {
                throw new InvalidOperationException("The host comment acknowledgement journal is full. No mutation was applied.");
            }
            var index = doc.Comments.FindIndex(c => c.Id == id);
            var previous = index < 0 ? null : doc.Comments[index];
            if (previous is not null && !CommentStoreLocator.SameProject(previous.ProjectRoot, projectRoot))
            {
                throw new InvalidOperationException($"Comment '{id}' belongs to a different project. It was left unchanged.");
            }
            if (replacement is not null)
            {
                if (replacement.Id != id || !CommentStoreLocator.SameProject(replacement.ProjectRoot, projectRoot))
                {
                    throw new InvalidOperationException("The replacement does not belong to the bound project and comment.");
                }
                replacement.CreatedAt = previous?.CreatedAt ?? replacement.CreatedAt;
                // Retrying an unacknowledged exact write is safe; do not refresh its timestamp.
                replacement.UpdatedAt = previous?.UpdatedAt ?? replacement.UpdatedAt;
                if (previous is not null && Revision(previous) == Revision(replacement))
                {
                    result = previous;
                    Remember();
                    return;
                }
            }
            else if (previous is null)
            {
                throw new KeyNotFoundException($"Comment '{id}' was not found in the bound host project.");
            }
            if (!string.Equals(Revision(previous), expectedRevision, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Comment '{id}' changed on the host. Refresh it before saving; no edit was overwritten.");
            }
            if (replacement is null)
            {
                doc.Comments.RemoveAt(index);
                Remember();
                return;
            }
            replacement.UpdatedAt = CommentTimestamps.Now();
            if (index < 0)
            {
                doc.Comments.Add(replacement);
            }
            else
            {
                doc.Comments[index] = replacement;
            }
            result = replacement;
            Remember();

            void Remember()
            {
                doc.GuestReceipts ??= new Dictionary<string, GuestCommentReceipt>(StringComparer.Ordinal);
                persistedRevision = result is null ? null : Revision(result);
                doc.GuestReceipts.Add(operationId, new(requestHash, persistedRevision));
            }
        });
        return new(persistedRevision, result);
    }

    internal static string Revision(Comment? comment) => comment is null ? string.Empty :
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(comment, CommentsJsonContext.Default.Comment)));

    internal static bool ValidRevision(string? value) =>
        value is { Length: 64 } && value.All(char.IsAsciiHexDigit);

    /// <summary>
    /// Runs <paramref name="mutate"/> under a cross-process mutex (keyed on the store path) as a single
    /// read-modify-write, then persists atomically. The mutex serializes the CLI against a second CLI (or the
    /// future in-proc writer) touching the same file; temp-then-rename keeps the on-disk file always-valid.
    /// </summary>
    private void WithExclusiveStore(string storePath, Action<CommentStoreDocument> mutate)
    {
        using var mutex = new Mutex(false, MutexName(storePath));
        var held = false;
        try
        {
            try
            {
                held = mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                // Previous holder crashed without releasing; we now own it and the file is still valid.
                held = true;
            }

            if (!held)
            {
                throw new TimeoutException("Timed out waiting for the UI comments store lock. The store was left unchanged.");
            }
            var doc = Load(storePath);
            var before = System.Text.Json.JsonSerializer.Serialize(doc, CommentsJsonContext.Default.CommentStoreDocument);
            mutate(doc);
            if (System.Text.Json.JsonSerializer.Serialize(doc, CommentsJsonContext.Default.CommentStoreDocument) == before)
            {
                return;
            }
            doc.Generation = checked(doc.Generation + 1);
            Save(storePath, doc);
        }
        catch (CommentStoreCorruptException ex)
        {
            // A mutating verb hit a corrupt store: do NOT overwrite it. Preserve a recovery copy alongside the
            // original (which is also left untouched) and rethrow so the command reports the error + exits non-zero.
            var backup = TryBackupCorruptStore(storePath);
            throw backup is null
                ? ex
                : new CommentStoreCorruptException(
                    $"UI comments store is corrupt (invalid JSON): {storePath}. It was left unchanged and copied to " +
                    $"{Path.GetFileName(backup)} — fix or delete the store to continue.",
                    ex.InnerException);
        }
        finally
        {
            if (held)
            {
                mutex.ReleaseMutex();
            }
        }
    }

    private static void Save(string storePath, CommentStoreDocument doc)
    {
        var dir = Path.GetDirectoryName(storePath);
        if (!string.IsNullOrEmpty(dir))
        {
            // Creating the store is how .winapp first appears in a repo winapp never scaffolded (DevTools
            // attaching to somebody else's app), so it must also make the folder ignore itself.
            EnsureSelfIgnored(dir);
        }

        var json = System.Text.Json.JsonSerializer.Serialize(doc, CommentsJsonContext.Default.CommentStoreDocument);
        var tmp = storePath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllText(tmp, json);
            File.Move(tmp, storePath, overwrite: true);
        }
        catch
        {
            TryDelete(tmp);
            throw;
        }
    }

    private static void EnsureSelfIgnored(string directory)
    {
        if (PathSafety.HasReparsePointOnPath(directory, Path.GetDirectoryName(directory)!))
        {
            throw new IOException($"The UI comments directory is redirected or inaccessible: {directory}");
        }
        Directory.CreateDirectory(directory);
        if (!string.Equals(Path.GetFileName(directory), ".winapp", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        var ignorePath = Path.Combine(directory, ".gitignore");
        if (File.Exists(ignorePath))
        {
            return;
        }
        FileStream file;
        try
        {
            file = new FileStream(ignorePath, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        }
        catch (IOException) when (File.Exists(ignorePath))
        {
            // Another writer created the local ignore file. Never replace its contents.
            return;
        }
        using (file)
        using (var writer = new StreamWriter(file))
        {
            writer.Write("# Created by winapp. Local UI comments are not committed.\n*\n");
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch
        {
            // best-effort cleanup of the temp file
        }
    }

    private static string? TryBackupCorruptStore(string storePath)
    {
        try
        {
            var backup = storePath + ".bak";
            File.Copy(storePath, backup, overwrite: true);
            return backup;
        }
        catch
        {
            // Best-effort recovery copy; if it fails we still refuse to overwrite the original.
            return null;
        }
    }

    private static string MutexName(string storePath)
    {
        // Mutex names cannot contain '\'; hash the normalized full path to a stable, backslash-free key.
        var full = Path.GetFullPath(storePath).ToLowerInvariant();
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full));
        return "winapp-ui-comments-" + Convert.ToHexString(bytes, 0, 8);
    }
}

internal static class CommentTimestamps
{
    public static string Now() => DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Thrown when the on-disk UI-comments store exists but is not valid JSON. Signals callers to fail closed —
/// surface the error and exit non-zero, and never overwrite the corrupt file — so recoverable content is not
/// silently discarded.
/// </summary>
internal sealed class CommentStoreCorruptException(string message, Exception? innerException = null)
    : Exception(message, innerException);
