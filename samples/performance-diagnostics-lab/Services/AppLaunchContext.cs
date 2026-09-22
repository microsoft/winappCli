namespace PerformanceDiagnosticsLab.Services;

public sealed record AppLaunchContext(
    LaunchOptions Options,
    StartupOrchestrator Startup,
    FeaturePageLoader FeaturePages);
