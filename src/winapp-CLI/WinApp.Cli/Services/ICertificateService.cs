// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.ConsoleTasks;
using static WinApp.Cli.Services.CertificateService;

namespace WinApp.Cli.Services;

internal interface ICertificateService
{
    public Task<CertificateResult?> GenerateDevCertificateWithInferenceAsync(
        FileInfo outputPath,
        TaskContext taskContext,
        string? explicitPublisher = null,
        FileInfo? manifestPath = null,
        string password = "password",
        int validDays = 365,
        bool skipIfExists = true,
        bool useDefaults = false,
        bool updateGitignore = true,
        bool install = false,
        CancellationToken cancellationToken = default);

    public Task<CertificateResult> GenerateDevCertificateAsync(
        string publisher,
        FileInfo outputPath,
        TaskContext taskContext,
        string password = "password",
        int validDays = 365,
        CancellationToken cancellationToken = default);

    public bool InstallCertificate(FileInfo certPath, string password, bool force, TaskContext taskContext);

    public Task SignFileAsync(FileInfo filePath, FileInfo certificatePath, TaskContext taskContext, string? password = "password", string? timestampUrl = null, CancellationToken cancellationToken = default);
}
