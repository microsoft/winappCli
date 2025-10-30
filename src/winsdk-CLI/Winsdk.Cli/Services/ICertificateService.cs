// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using static Winsdk.Cli.Services.CertificateService;

namespace Winsdk.Cli.Services;

internal interface ICertificateService
{
    public Task<CertificateResult?> GenerateDevCertificateWithInferenceAsync(
        FileInfo outputPath,
        string? explicitPublisher = null,
        FileInfo? manifestPath = null,
        string password = "password",
        int validDays = 365,
        bool skipIfExists = true,
        bool updateGitignore = true,
        bool install = false,
        CancellationToken cancellationToken = default);

    public Task<CertificateResult> GenerateDevCertificateAsync(
        string publisher,
        FileInfo outputPath,
        string password = "password",
        int validDays = 365,
        CancellationToken cancellationToken = default);

    public bool InstallCertificate(FileInfo certPath, string password, bool force);

    public Task SignFileAsync(FileInfo filePath, FileInfo certificatePath, string? password = "password", string? timestampUrl = null, CancellationToken cancellationToken = default);
}
