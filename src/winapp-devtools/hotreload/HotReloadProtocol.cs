// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

// Shared by the hot reload helper and the in-app agent (linked into both). A frame is a 4-byte little-endian length
// followed by UTF-8 JSON. Each request gets one response.

using System.Text.Json;

namespace WinApp.HotReload.Protocol;

internal sealed class HotReloadRequest
{
    /// <summary>
    /// "hello" (once per connection), "apply" (C# updates) or "reload" (after an applied save: hand WinUI the compiled
    /// markup, then rebuild what the save changed).
    /// </summary>
    public string Op { get; set; } = "";
    public string? Token { get; set; }
    public List<DeltaPayload> Deltas { get; set; } = [];
    public List<MarkupPayload> Markup { get; set; } = [];
}

/// <summary>One XAML file as the XAML compiler compiled it.</summary>
internal sealed class MarkupPayload
{
    /// <summary>The URI the app loads it by: ms-appx:///Views/ContactsPage.xaml.</summary>
    public string Uri { get; set; } = "";
    /// <summary>"component" (a page, UserControl or window: <see cref="ClassName"/>), "application", "dictionary" or "generic".</summary>
    public string Kind { get; set; } = "";
    public string? ClassName { get; set; }
    public byte[] Xbf { get; set; } = [];
    /// <summary>application and dictionary: the x:Key values this save removed from the markup.</summary>
    public List<string> DroppedKeys { get; set; } = [];
}

/// <summary>One .NET update for one assembly, as <c>MetadataUpdater.ApplyUpdate</c> takes it.</summary>
internal sealed class DeltaPayload
{
    public Guid ModuleId { get; set; }
    public byte[] Metadata { get; set; } = [];
    public byte[] IL { get; set; } = [];
    public byte[] Pdb { get; set; } = [];
    public int[] UpdatedTypes { get; set; } = [];
}

internal sealed class HotReloadResponse
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    /// <summary>hello: what the app's runtime can apply (space-separated edit-and-continue capabilities).</summary>
    public string? Capabilities { get; set; }
    /// <summary>hello: the app's folder, where assemblies a new package brings are copied.</summary>
    public string? BaseDirectory { get; set; }
    /// <summary>hello: the app's process, so the helper stops when it does.</summary>
    public int ProcessId { get; set; }
    /// <summary>apply: the types the update changed (full names).</summary>
    public List<string> UpdatedTypes { get; set; } = [];
    /// <summary>reload: what started over, one line each.</summary>
    public List<string> Notes { get; set; } = [];
    /// <summary>reload: why the save can't go into the running app; the app is left as it was.</summary>
    public string? Restart { get; set; }
    /// <summary>reload: the markup that failed to load (with <see cref="Error"/>); the app shows what it showed before.</summary>
    public string? ErrorUri { get; set; }
}

internal static class HotReloadFrames
{
    public const string PipeVariable = "WINAPP_HOT_RELOAD_PIPE";
    public const string TokenVariable = "WINAPP_HOT_RELOAD_TOKEN";
    private const int MaxFrame = 64 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        await stream.WriteAsync(BitConverter.GetBytes(bytes.Length), cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The next frame, or null when the other side closed the connection between frames.</summary>
    public static async Task<T?> ReadAsync<T>(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        if (!await FillAsync(stream, header, cancellationToken).ConfigureAwait(false))
        {
            return default;
        }
        var length = BitConverter.ToInt32(header, 0);
        if (length is <= 0 or > MaxFrame)
        {
            throw new InvalidDataException("Invalid hot reload frame length.");
        }
        var body = new byte[length];
        if (!await FillAsync(stream, body, cancellationToken).ConfigureAwait(false))
        {
            throw new EndOfStreamException("Truncated hot reload frame.");
        }
        return JsonSerializer.Deserialize<T>(body);
    }

    private static async Task<bool> FillAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var count = await stream.ReadAsync(buffer.AsMemory(read), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                return read == 0 ? false : throw new EndOfStreamException("Truncated hot reload frame.");
            }
            read += count;
        }
        return true;
    }
}
