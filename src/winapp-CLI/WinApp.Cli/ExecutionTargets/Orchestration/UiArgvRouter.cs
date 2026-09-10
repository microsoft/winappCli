// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.Commands;

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>An output file a forwarded command will produce in the guest.</summary>
/// <param name="GuestRelativePath">Name inside the operation's guest staging folder.</param>
/// <param name="GuestFullPath">Absolute guest path handed to the guest command.</param>
/// <param name="HostDestination">Absolute host path the caller asked for.</param>
internal sealed record RoutedArtifact(
    string GuestRelativePath, string GuestFullPath, string HostDestination,
    bool IsRecording = false, bool Overwrite = true, bool Frames = false)
{
    public string GuestFramesDirectory => RecordingArtifactPublisher.GetFramesDirectory(GuestFullPath);
    public string HostFramesDirectory => RecordingArtifactPublisher.GetFramesDirectory(HostDestination);
}

/// <summary>A UI command rewritten for the guest.</summary>
/// <param name="Arguments">Argument vector to hand to guest winapp.</param>
/// <param name="Artifact">The output file to fetch back, when the command declares one.</param>
internal sealed record RoutedUiCommand(List<string> Arguments, RoutedArtifact? Artifact);

/// <summary>Removes host routing options and redirects capture output into guest staging.</summary>
internal static class UiArgvRouter
{
    private static readonly string[] TargetOptionNames = ["--on"];
    private static readonly string[] AppOptionNames = ["--app", "-a"];
    private static readonly string[] OutputOptionNames = ["--output", "-o"];

    /// <summary>
    /// Rewrites <paramref name="arguments"/> for the target.
    /// </summary>
    /// <param name="arguments">The command line as typed, including the <c>ui</c> verb.</param>
    /// <param name="guestArtifactDirectory">Absolute target folder for this operation's outputs.</param>
    /// <param name="resolveHostPath">Resolves a caller-supplied output path against the host.</param>
    public static RoutedUiCommand Rewrite(
        IReadOnlyList<string> arguments,
        string guestArtifactDirectory,
        Func<string, string> resolveHostPath,
        string? commandName = null,
        string? defaultOutput = null)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        ArgumentException.ThrowIfNullOrWhiteSpace(guestArtifactDirectory);
        ArgumentNullException.ThrowIfNull(resolveHostPath);

        var rewritten = new List<string>(arguments.Count);
        RoutedArtifact? artifact = null;
        var forwardVerbatim = false;
        commandName ??= arguments.Count > 1 && arguments[0] == "ui" ? arguments[1] : null;
        var recording = commandName == "record";
        var options = arguments.TakeWhile(token => token != "--").ToArray();
        var overwrite = !recording || HasFlag(options, "--overwrite");
        var frames = recording && HasFlag(options, "--frames");

        for (var index = 0; index < arguments.Count; index++)
        {
            var token = arguments[index];

            if (forwardVerbatim)
            {
                rewritten.Add(token);
                continue;
            }

            if (token == "--")
            {
                // Everything past a separator belongs to something else and is never rewritten.
                forwardVerbatim = true;
                rewritten.Add(token);
                continue;
            }

            var (name, inlineValue) = SplitOption(token);

            if (Matches(name, TargetOptionNames))
            {
                // '--on sandbox' spends its value on a separate token; dropping only the option
                // would leave 'sandbox' stranded as a positional argument on the target, where it
                // would silently become an element selector.
                if (inlineValue is null && index + 1 < arguments.Count)
                {
                    index++;
                }

                continue;
            }

            if (Matches(name, OutputOptionNames))
            {
                index += Rewrite(token, name, inlineValue, arguments, index, rewritten, value =>
                {
                    artifact = CreateArtifact(value, guestArtifactDirectory, resolveHostPath)
                        with
                    { IsRecording = recording, Overwrite = overwrite, Frames = frames };
                    return artifact.GuestFullPath;
                });
                continue;
            }

            rewritten.Add(token);
        }

        if (artifact is null && commandName is "record" or "screenshot")
        {
            artifact = CreateArtifact(
                defaultOutput ?? (recording ? UiRecordOptionValidator.DefaultOutputPath() : "screenshot.png"),
                guestArtifactDirectory, resolveHostPath)
                with
            { IsRecording = recording, Overwrite = overwrite, Frames = frames };
            var separator = rewritten.IndexOf("--");
            rewritten.InsertRange(separator < 0 ? rewritten.Count : separator, ["--output", artifact.GuestFullPath]);
        }
        return new RoutedUiCommand(rewritten, artifact);
    }

    private static bool HasFlag(string[] arguments, string name)
    {
        var enabled = false;
        for (var index = 0; index < arguments.Length; index++)
        {
            var token = arguments[index];
            if (token == name)
            {
                enabled = index + 1 >= arguments.Length || !bool.TryParse(arguments[index + 1], out var value) || value;
            }
            else if (token.StartsWith(name + "=", StringComparison.Ordinal) ||
                     token.StartsWith(name + ":", StringComparison.Ordinal))
            {
                enabled = bool.TryParse(token[(name.Length + 1)..], out var value) && value;
            }
        }
        return enabled;
    }

    /// <summary>
    /// Emits an option with a transformed value, in whichever spelling the caller used.
    /// </summary>
    /// <returns>How many extra tokens were consumed.</returns>
    private static int Rewrite(
        string token,
        string name,
        string? inlineValue,
        IReadOnlyList<string> arguments,
        int index,
        List<string> rewritten,
        Func<string, string> transform)
    {
        if (inlineValue is not null)
        {
            // Preserve the delimiter the caller used rather than normalising it, so the forwarded
            // command stays as close to what was typed as the rewrite allows.
            var delimiter = token[name.Length];
            rewritten.Add($"{name}{delimiter}{transform(inlineValue)}");
            return 0;
        }

        if (index + 1 >= arguments.Count)
        {
            // A trailing option with no value: forwarded as-is so guest winapp produces its own
            // "missing value" error rather than this rewrite inventing one.
            rewritten.Add(token);
            return 0;
        }

        rewritten.Add(name);
        rewritten.Add(transform(arguments[index + 1]));
        return 1;
    }

    private static RoutedArtifact CreateArtifact(
        string requestedPath,
        string guestArtifactDirectory,
        Func<string, string> resolveHostPath)
    {
        var hostDestination = resolveHostPath(requestedPath);

        // Named from the requested file so guest-side diagnostics and any name the command derives
        // from its output path stay recognisable. The name is validated as a single safe segment,
        // because it is about to become part of a managed guest path.
        var name = Path.GetFileName(hostDestination);

        if (string.IsNullOrWhiteSpace(name))
        {
            throw ExecutionTargetException.Create(
                ExecutionTargetErrorCodes.ArtifactFailed,
                $"'{requestedPath}' does not name an output file.",
                userAction: "Give --output a file path, not a directory.");
        }

        return new RoutedArtifact(
            name,
            TargetPathSafety.CombineInsideRoot(guestArtifactDirectory, name),
            hostDestination);
    }

    /// <summary>Splits an option token into its name and, when present, its inline value.</summary>
    /// <remarks>
    /// The delimiter is honoured only when what precedes it is exactly a known option name.
    /// Otherwise <c>-o C:\out.png</c>'s value would be split at the drive colon.
    /// </remarks>
    private static (string Name, string? InlineValue) SplitOption(string token)
    {
        var delimiter = token.IndexOfAny(['=', ':']);

        if (delimiter <= 0)
        {
            return (token, null);
        }

        var name = token[..delimiter];

        return IsKnownOption(name)
            ? (name, token[(delimiter + 1)..])
            : (token, null);
    }

    private static bool IsKnownOption(string name) =>
        Matches(name, TargetOptionNames) ||
        Matches(name, AppOptionNames) ||
        Matches(name, OutputOptionNames);

    private static bool Matches(string name, string[] candidates) =>
        candidates.Contains(name, StringComparer.Ordinal);
}
