// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using NuGet.Common;
using NuGet.Packaging;
using NuGet.Packaging.Core;
using NuGet.Packaging.Signing;
using NuGet.Protocol;
using NuGet.Protocol.Core.Types;

namespace WinApp.Cli.Services;

/// <summary>
/// Downloads a NuGet package from the user's configured sources and extracts it into the global packages
/// folder. Owns the package-transfer concern for <see cref="NugetService"/> so download/extraction can be
/// tested independently of source resolution and version selection. Source eligibility
/// (<c>&lt;packageSourceMapping&gt;</c>), credentials and settings come from
/// <see cref="NugetSourceProvider"/>.
/// </summary>
internal sealed class NugetPackageDownloader(NugetSourceProvider sourceProvider)
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private readonly NugetSourceProvider _sourceProvider = sourceProvider;

    /// <summary>
    /// Deletes the temporary download file after a package has been transferred. Defaults to
    /// <see cref="File.Delete(string)"/> and is exposed as a settable seam only so a test can force the
    /// best-effort cleanup to fail and verify the failure is swallowed rather than surfaced to the caller;
    /// production behavior is identical to calling <see cref="File.Delete(string)"/> directly.
    /// </summary>
    internal Action<string> DeleteTempFile { get; set; } = File.Delete;

    /// <summary>
    /// Downloads <paramref name="identity"/> from the first configured source that has it and extracts it
    /// into <paramref name="globalPackagesFolder"/> using the standard NuGet on-disk layout. Honors
    /// <c>&lt;packageSourceMapping&gt;</c> for source selection and throws an
    /// <see cref="InvalidOperationException"/> (preserving the underlying source error) when no configured
    /// source can provide the package.
    /// </summary>
    internal async Task DownloadPackageAsync(PackageIdentity identity, string globalPackagesFolder, SourceCacheContext cacheContext, CancellationToken cancellationToken)
    {
        var package = identity.Id;
        var version = identity.Version.ToNormalizedString();
        var clientPolicyContext = ClientPolicyContext.GetClientPolicy(_sourceProvider.Settings, Logger);

        var repos = _sourceProvider.GetRepositoriesForPackage(package);
        Exception? lastError = null;
        string? lastErrorSource = null;

        foreach (var repo in repos)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Buffer to a temp file rather than memory: SDK packages (e.g. Windows App SDK) are large.
            // Use a random temp path instead of Path.GetTempFileName(): the latter eagerly creates an
            // empty file (which File.Create below immediately overwrites) and throws once ~65,535 temp
            // files already exist in the directory.
            var tempFile = Path.Join(Path.GetTempPath(), Path.GetRandomFileName());
            try
            {
                bool copied;

                // Capture warning/error diagnostics for this source. CopyNupkgToStreamAsync reports content
                // failures (e.g. a 401/403 on the .nupkg endpoint) through the logger and can then return
                // false instead of throwing; with NullLogger that detail would be lost and the failure
                // misreported as "not found".
                var downloadLogger = new CollectingLogger();
                await using (var fileStream = File.Create(tempFile))
                {
                    try
                    {
                        // Acquiring the resource loads the source's service index, which can throw for an
                        // unreachable/unauthorized source; keep it inside the try so we fail over instead.
                        var byIdResource = await repo.GetResourceAsync<FindPackageByIdResource>(cancellationToken);
                        if (byIdResource is null)
                        {
                            // Coverage note: every v3 HTTP, v2, and local folder source exposes a
                            // FindPackageByIdResource, so GetResourceAsync only returns null for a malformed
                            // source that cannot be produced by a real/in-memory feed. This is a defensive
                            // guard; it is intentionally not covered because no feed shape can drive it.
                            continue;
                        }

                        copied = await byIdResource.CopyNupkgToStreamAsync(identity.Id, identity.Version, fileStream, cacheContext, downloadLogger, cancellationToken);
                    }
                    catch (Exception ex) when (ex is FatalProtocolException or IOException or UnauthorizedAccessException)
                    {
                        // A canceled request can be surfaced as a FatalProtocolException once the final
                        // HTTP attempt is exhausted; preserve cancellation instead of recording it as a
                        // source failure and later throwing a misleading "download failed" error.
                        cancellationToken.ThrowIfCancellationRequested();

                        // Source unreachable/unauthorized or does not have this package; remember why
                        // (e.g. 401/403/network) and try the next source. IO/access failures count too: a
                        // local-folder source opens the .nupkg straight off disk, so a locked or unreadable
                        // feed folder surfaces as those rather than a protocol error, and it must fail over
                        // to the next eligible source instead of aborting the whole download.
                        lastError = ex;
                        lastErrorSource = repo.PackageSource.Name;
                        continue;
                    }
                }

                if (!copied)
                {
                    // A canceled download can surface as a logged-and-returned false rather than a thrown
                    // OperationCanceledException; keep Ctrl+C as cancellation instead of misreporting it
                    // as a feed failure below.
                    cancellationToken.ThrowIfCancellationRequested();

                    // A false return covers both "this source doesn't have the package" (normal failover)
                    // and a content-endpoint failure (e.g. a 5xx/401/403 on the .nupkg download) that the
                    // NuGet client retried, logged, and reported by returning false rather than throwing.
                    // Preserve any captured error so an auth/network failure isn't later reported as a plain
                    // "package/version was not found".
                    if (downloadLogger.LastErrorMessage is not null)
                    {
                        lastError = new InvalidOperationException(downloadLogger.LastErrorMessage);
                        lastErrorSource = repo.PackageSource.Name;
                    }
                    continue;
                }

                await using var readStream = File.OpenRead(tempFile);

                // NuGet reports signature/trust policy failures by throwing SignatureException with an EMPTY
                // Message and writing the actual diagnosis (e.g. NU3004 "the package is not signed") through
                // the logger. Passing NullLogger here therefore surfaced a completely blank error to the user
                // whenever signatureValidationMode=require rejected a package. Capture the diagnosis the same
                // way the download step already does.
                var addLogger = new CollectingLogger();
                try
                {
                    using var addResult = await GlobalPackagesFolderUtility.AddPackageAsync(
                        source: repo.PackageSource.Source,
                        packageIdentity: identity,
                        packageStream: readStream,
                        globalPackagesFolder: globalPackagesFolder,
                        parentId: Guid.Empty,
                        clientPolicyContext: clientPolicyContext,
                        logger: addLogger,
                        token: cancellationToken);
                }
                catch (SignatureException ex)
                {
                    // Not a failover condition: the package downloaded fine and was rejected by the client's
                    // own trust policy, so trying the next source would only repeat it. The raw exception is
                    // deliberately not attached — its verification results can carry the source URL, and this
                    // is the same sanitization boundary as the other throws in this file.
                    throw new InvalidOperationException(
                        NugetErrorMessage.Redact(
                            $"Signature validation rejected {package} {version} from source '{repo.PackageSource.Name}': "
                            + $"{DescribeSignatureFailure(ex, addLogger)} "
                            + "This is enforced by 'signatureValidationMode' / 'trustedSigners' in your nuget.config."));
                }

                return;
            }
            finally
            {
                try
                {
                    DeleteTempFile(tempFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best-effort cleanup of the temp download: a failure to delete the temporary file must
                    // not fail the user's package download, so the cleanup error is swallowed here. Scoped to
                    // the failures File.Delete actually reports (a locked/in-use file, a denied ACL) so an
                    // unexpected defect in the seam still surfaces.
                }
            }
        }

        // No configured source could provide the package. Surface the underlying reason when we have it
        // so authentication/network failures are distinguishable from a genuinely missing package.
        var sources = string.Join(", ", repos.Select(r => r.PackageSource.Name));
        var baseMessage = string.IsNullOrEmpty(sources)
            ? $"Failed to download {package} {version}: {_sourceProvider.DescribeNoEligibleSources(package)}."
            : $"Failed to download {package} {version} from the configured NuGet sources ({sources}).";

        if (lastError is not null)
        {
            throw new InvalidOperationException($"{baseMessage} Last error from source '{lastErrorSource}': {NugetErrorMessage.Redact(lastError.Message)}");
        }

        throw new InvalidOperationException($"{baseMessage} The package/version was not found on any configured source.");
    }

    /// <summary>
    /// Builds a non-empty explanation for a <see cref="SignatureException"/>. Its own <c>Message</c> is empty,
    /// so the detail has to be recovered from the diagnostics NuGet logged, or failing that from the
    /// verification results it carries. Always returns something a user can act on, since the whole point is
    /// that this failure must not reach them blank.
    /// </summary>
    private static string DescribeSignatureFailure(SignatureException ex, CollectingLogger logger)
    {
        if (!string.IsNullOrWhiteSpace(logger.LastErrorMessage))
        {
            return logger.LastErrorMessage;
        }

        var issues = ex.Results?
            .SelectMany(result => result.Issues ?? [])
            .Select(issue => issue.Message)
            .Where(message => !string.IsNullOrWhiteSpace(message))
            .ToList();

        if (issues is { Count: > 0 })
        {
            return string.Join("; ", issues);
        }

        return string.IsNullOrWhiteSpace(ex.Message)
            ? "the package failed the configured signature/trust policy."
            : ex.Message;
    }

    /// <summary>
    /// An <see cref="ILogger"/> that captures the most recent warning/error message emitted by a NuGet
    /// operation. Used to recover the underlying reason (e.g. a 401/403 on a package-content endpoint)
    /// when an API such as <c>CopyNupkgToStreamAsync</c> reports failure by returning <c>false</c> and
    /// logging rather than throwing, so the failure is not later misreported as a plain "not found".
    /// Exposed as <c>internal</c> (rather than private) purely so its message-capture logic can be
    /// unit-tested directly; production still constructs and uses it exactly the same way.
    /// </summary>
    internal sealed class CollectingLogger : LoggerBase
    {
        public string? LastErrorMessage { get; private set; }

        public override void Log(ILogMessage message)
        {
            if (message.Level >= LogLevel.Warning)
            {
                LastErrorMessage = message.Message;
            }
        }

        public override Task LogAsync(ILogMessage message)
        {
            Log(message);
            return Task.CompletedTask;
        }
    }
}
