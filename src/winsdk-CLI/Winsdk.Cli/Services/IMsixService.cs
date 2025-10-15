using Winsdk.Cli.Models;

namespace Winsdk.Cli.Services;

internal interface IMsixService
{
    public Task<CreateMsixPackageResult> CreateMsixPackageAsync(
        string inputFolder,
        string? outputPath,
        string? packageName = null,
        bool skipPri = false,
        bool autoSign = false,
        string? certificatePath = null,
        string certificatePassword = "password",
        bool generateDevCert = false,
        bool installDevCert = false,
        string? publisher = null,
        string? manifestPath = null,
        bool selfContained = false,
        bool verbose = true,
        CancellationToken cancellationToken = default);

    public Task<MsixIdentityResult> AddMsixIdentityToExeAsync(
        string exePath,
        string appxManifestPath,
        bool noInstall,
        string? applicationLocation = null,
        bool verbose = true,
        CancellationToken cancellationToken = default);
}
