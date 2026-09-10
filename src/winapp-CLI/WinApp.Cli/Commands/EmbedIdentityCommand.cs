// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging;
using System.CommandLine;
using System.CommandLine.Invocation;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services;

namespace WinApp.Cli.Commands;

internal class EmbedIdentityCommand : Command, IShortDescription
{
    public string ShortDescription => "Embed sparse package identity into an app's manifest";

    public static Argument<FileInfo> TargetArgument { get; }
    public static Option<FileInfo> ManifestOption { get; }

    static EmbedIdentityCommand()
    {
        TargetArgument = new Argument<FileInfo>("target")
        {
            Description = "Path to the .exe (embeds identity into its side-by-side manifest via mt.exe) or an .xml/.manifest side-by-side manifest file (inserts/replaces the <msix> element; created if it doesn't exist)."
        };
        ManifestOption = new Option<FileInfo>("--manifest")
        {
            Description = "Path to the sparse appxmanifest.xml to read identity from. When omitted, searched in a 'sparse/' folder (where 'winapp init --exe --sparse' writes it by default) beside the target first, then in the current directory, then beside the target and in the current directory."
        };
        ManifestOption.AcceptExistingOnly();
    }

    public EmbedIdentityCommand() : base("embed-identity", "Connect a desktop exe to its sparse identity package by embedding the <msix> element. Reads identity (packageName, publisher, applicationId) from a sparse appxmanifest.xml and writes it into the target's side-by-side (fusion) manifest. EXE targets are updated with mt.exe; .xml/.manifest targets are edited directly. Example: winapp embed-identity ./bin/MyApp.exe. This is step 3 of the sparse packaging workflow (after 'winapp init --exe --sparse' and 'winapp pack').")
    {
        Arguments.Add(TargetArgument);
        Options.Add(ManifestOption);
    }

    public class Handler(IMsixService msixService, ICurrentDirectoryProvider currentDirectoryProvider, IStatusService statusService, ILogger<EmbedIdentityCommand> logger) : AsynchronousCommandLineAction
    {
        public override async Task<int> InvokeAsync(ParseResult parseResult, CancellationToken cancellationToken = default)
        {
            var target = parseResult.GetRequiredValue(TargetArgument);

            // Validate the target type up front, before any manifest discovery, so an unsupported
            // target (e.g. 'notes.txt') reports the actionable unsupported-target error rather than
            // a confusing "manifest not found" from the probing below.
            var extension = target.Extension.ToLowerInvariant();
            var isSupported = extension is ".exe" or ".xml" or ".manifest";
            if (!isSupported)
            {
                logger.LogError("Unsupported target '{Target}'. Provide a .exe (EXE mode) or an .xml/.manifest file (XML mode).", target.Name);
                return 1;
            }

            // Resolve the identity manifest: explicit --manifest wins; otherwise probe the
            // dedicated 'sparse/' folder (where 'winapp init --exe --sparse' now writes by default)
            // beside the target first, then in the current directory, then fall back to the older
            // "beside the target / current directory" locations for back-compat. Preferring the
            // target's own sparse/ folder means an explicitly named exe outside the current project
            // embeds its own identity rather than the current directory's. Prefer a *sparse*
            // manifest so a full Package.appxmanifest sitting nearby doesn't get picked and then
            // rejected by embed-identity's sparse-only check.
            var manifest = parseResult.GetValue(ManifestOption);
            if (manifest == null)
            {
                var targetDir = target.Directory?.FullName;
                var currentDir = currentDirectoryProvider.GetCurrentDirectory();
                var currentSparseDir = Path.Join(currentDir, "sparse");
                var targetSparseDir = targetDir != null ? Path.Join(targetDir, "sparse") : null;
                manifest = FindSparseManifest(targetSparseDir)
                    ?? FindSparseManifest(currentSparseDir)
                    ?? FindSparseManifest(targetDir)
                    ?? FindSparseManifest(currentDir)
                    ?? FallbackManifest(targetDir, currentDir);
            }

            if (!manifest.Exists)
            {
                logger.LogError("AppX manifest not found: {Manifest}. Pass --manifest, or generate one with 'winapp init --exe <exe> --sparse'.", manifest.FullName);
                return 1;
            }


            return await statusService.ExecuteWithStatusAsync("Embedding package identity...", async (taskContext, ct) =>
            {
                try
                {
                    var identity = await msixService.EmbedIdentityAsync(target, manifest, taskContext, ct);

                    taskContext.AddStatusMessage($"{UiSymbols.Package} Package: {identity.PackageName}");
                    taskContext.AddStatusMessage($"{UiSymbols.User} Publisher: {identity.Publisher}");
                    taskContext.AddStatusMessage($"{UiSymbols.Id} App ID: {identity.ApplicationId}");

                    if (extension is ".xml" or ".manifest")
                    {
                        taskContext.AddStatusMessage($"{UiSymbols.Info} Rebuild your app so the updated side-by-side manifest is embedded in the exe.");
                    }
                    else
                    {
                        // mt.exe rewrites the exe in place, which invalidates any existing Authenticode
                        // signature. Remind the user to re-sign before distributing.
                        taskContext.AddStatusMessage($"{UiSymbols.Warning} Embedding rewrote the exe and invalidated any existing signature. Re-sign it before distributing: winapp sign \"{target.FullName}\" <cert.pfx>.");
                    }

                    return (0, "Package identity embedded successfully.");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    var baseEx = ex.GetBaseException();
                    return (1, $"{UiSymbols.Error} Failed to embed package identity: {baseEx.Message}");
                }
            }, cancellationToken);
        }

        // Candidate manifest file names to probe, in priority order. Static to satisfy CA1861 and
        // avoid re-allocating the array on every lookup.
        private static readonly string[] SparseManifestCandidateNames = ["appxmanifest.xml", "Package.appxmanifest"];

        /// <summary>
        /// Returns the first existing manifest candidate in <paramref name="directory"/> that is a
        /// sparse identity manifest (declares <c>uap10:AllowExternalContent</c>), preferring the
        /// sparse <c>appxmanifest.xml</c> name. Returns null if the directory is null/empty or no
        /// sparse manifest is present.
        /// </summary>
        private static FileInfo? FindSparseManifest(string? directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            return SparseManifestCandidateNames
                .Where(name => !Path.IsPathRooted(name))
                .Select(name => Path.Join(directory, name))
                .Where(path => File.Exists(path) && IsSparseManifest(path))
                .Select(path => new FileInfo(path))
                .FirstOrDefault();
        }

        private static bool IsSparseManifest(string path)
        {
            try
            {
                return AppxManifestDocument.Parse(File.ReadAllText(path)).AllowsExternalContent;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
            {
                return false;
            }
        }

        /// <summary>
        /// Fallback used when no sparse manifest is found: preserves the original auto-detect order
        /// (beside the target, then the current directory) so the downstream "not found" or
        /// "not a sparse manifest" error still reports a sensible path.
        /// </summary>
        private static FileInfo FallbackManifest(string? targetDir, string currentDir)
        {
            var besideTarget = targetDir != null ? ManifestHelper.FindManifest(targetDir) : null;
            return besideTarget?.Exists == true
                ? besideTarget
                : ManifestHelper.FindManifest(currentDir);
        }
    }
}
