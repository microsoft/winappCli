// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;
using System.Text.Json;

namespace WinApp.Cli.Services.DevTools;

internal static class DevToolsPipeDiscovery
{
    public const string PipePrefix = "winapp-devtools-";

    // The Win32 named-pipe filesystem. Directory.EnumerateFiles over it lists every open pipe leaf.
    private const string PipeDirectory = @"\\.\pipe\";

    public static string PipeNameFor(int pid) => PipePrefix + pid.ToString(CultureInfo.InvariantCulture);

    public static int? TryParseTapPid(string? pipeName)
    {
        if (string.IsNullOrEmpty(pipeName))
        {
            return null;
        }

        // Accept either a bare leaf or a full pipe path; take the last path segment.
        var leaf = pipeName;
        var slash = leaf.LastIndexOf('\\');
        if (slash >= 0)
        {
            leaf = leaf[(slash + 1)..];
        }

        if (!leaf.StartsWith(PipePrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var pidPart = leaf[PipePrefix.Length..];
        if (pidPart.Length == 0)
        {
            return null;
        }

        // NumberStyles.None rejects signs/whitespace/thousands so only a clean positive decimal matches.
        return int.TryParse(pidPart, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 0
            ? pid
            : null;
    }

    public static IReadOnlyList<int> EnumerateInjectedPids(Func<IEnumerable<string>>? pipeLister = null)
    {
        var lister = pipeLister ?? DefaultPipeLister;
        var pids = new SortedSet<int>();
        foreach (var name in lister())
        {
            if (TryParseTapPid(name) is int pid)
            {
                pids.Add(pid);
            }
        }

        return pids.ToArray();
    }

    private static IEnumerable<string> DefaultPipeLister() => Directory.EnumerateFiles(PipeDirectory);
}

/// <summary>
/// The parsed <c>DevTools.negotiate</c> capability handshake. Parsed with
/// <see cref="JsonDocument"/> (reflection-free, AOT-safe) so an unknown/extra field never breaks a client.
/// </summary>
internal sealed record TapHello(
    string Protocol,
    string ProtocolVersion,
    bool Experimental,
    int Pid,
    bool Mutation,
    string Posture)
{
    public static TapHello? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(line, TapWireJson.DocumentOptions);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var protocol = root.TryGetProperty("protocol", out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() ?? string.Empty
                : string.Empty;
            if (protocol != "winapp-devtools")
            {
                return null;
            }

            var version = root.TryGetProperty("protocolVersion", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString() ?? string.Empty
                : string.Empty;
            var experimental = root.TryGetProperty("experimental", out var e) && e.ValueKind == JsonValueKind.True;
            var pid = root.TryGetProperty("pid", out var pidEl) && pidEl.ValueKind == JsonValueKind.Number &&
                pidEl.TryGetInt32(out var pidVal) ? pidVal : 0;
            if (version.Length == 0 || pid <= 0 ||
                !root.TryGetProperty("mutation", out var m) ||
                m.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !root.TryGetProperty("posture", out var postureElement) ||
                postureElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var mutation = m.GetBoolean();
            var posture = postureElement.GetString();
            if (posture is not ("read" or "ui" or "mutation") || mutation != (posture == "mutation"))
            {
                return null;
            }

            return new TapHello(protocol, version, experimental, pid, mutation, posture);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
