namespace Winsdk.Cli.Services;

internal interface IWorkspaceSetupService
{
    public string? FindWindowsAppSdkMsixDirectory(Dictionary<string, string>? usedVersions = null);
    public Task<int> SetupWorkspaceAsync(WorkspaceSetupOptions options, CancellationToken cancellationToken = default);
    public Task InstallWindowsAppRuntimeAsync(string msixDir, bool verbose, CancellationToken cancellationToken);
}
