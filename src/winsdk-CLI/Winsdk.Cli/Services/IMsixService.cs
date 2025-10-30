// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Winsdk.Cli.Models;

namespace Winsdk.Cli.Services;

internal interface IMsixService
{
    public Task<CreateMsixPackageResult> CreateMsixPackageAsync(
        DirectoryInfo inputFolder,
        FileSystemInfo? outputPath,
        string? packageName = null,
        bool skipPri = false,
        bool autoSign = false,
        FileInfo? certificatePath = null,
        string certificatePassword = "password",
        bool generateDevCert = false,
        bool installDevCert = false,
        string? publisher = null,
        FileInfo? manifestPath = null,
        bool selfContained = false,
        CancellationToken cancellationToken = default);

    public Task<MsixIdentityResult> AddMsixIdentityAsync(
        string? entryPointPath,
        FileInfo appxManifestPath,
        bool noInstall,
        CancellationToken cancellationToken = default);
}
