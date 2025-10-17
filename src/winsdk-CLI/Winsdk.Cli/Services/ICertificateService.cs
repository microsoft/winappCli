using static Winsdk.Cli.Services.CertificateService;

namespace Winsdk.Cli.Services;

internal interface ICertificateService
{
    public Task<CertificateResult?> GenerateDevCertificateWithInferenceAsync(
        string outputPath,
        string? explicitPublisher = null,
        string? manifestPath = null,
        string password = "password",
        int validDays = 365,
        bool skipIfExists = true,
        bool updateGitignore = true,
        bool install = false,
        CancellationToken cancellationToken = default);

    public Task<CertificateResult> GenerateDevCertificateAsync(
        string publisher,
        string outputPath,
        string password = "password",
        int validDays = 365,
        CancellationToken cancellationToken = default);

    public bool InstallCertificate(string certPath, string password, bool force);

    public Task SignFileAsync(string filePath, string certificatePath, string? password = "password", string? timestampUrl = null, CancellationToken cancellationToken = default);
}
