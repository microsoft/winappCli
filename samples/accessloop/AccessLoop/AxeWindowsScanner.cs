using System.Reflection;
using System.Runtime.InteropServices;
using Axe.Windows.Automation;
using Axe.Windows.Automation.Data;

namespace AccessLoop;

public interface IAccessibilityScanner
{
    Task<AccessibilityScan> ScanAsync(int processId, long windowHandle, CancellationToken cancellationToken);
}

public sealed class AxeWindowsScanner : IAccessibilityScanner
{
    public async Task<AccessibilityScan> ScanAsync(int processId, long windowHandle, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
            throw new InvalidOperationException("Axe.Windows scanning requires Windows.");
        if (processId <= 0 || windowHandle == 0)
            throw new InvalidDataException("Axe.Windows requires the owned process and a nonzero scenario window handle.");
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            // Keep SDK results in memory; screenshots come from winapp, not Axe's desktop-wide export.
            var config = Config.Builder.ForProcessId(processId).Build();
            var scanner = ScannerFactory.CreateScanner(config);
            var output = await scanner.ScanAsync(new ScanOptions("accessloop", new IntPtr(windowHandle)), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var version = typeof(ScannerFactory).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? throw new InvalidDataException("Cannot determine the loaded Axe.Windows engine version.");
            var groups = output.WindowScanOutputs.Select(window => new AccessibilityResultGroup(
                window.ErrorCount,
                window.Errors.Select(error => new AccessibilityFinding(
                    error.Rule.ID.ToString(),
                    error.Rule.Description,
                    error.Rule.HowToFix,
                    error.Rule.Standard.ToString(),
                    new Dictionary<string, string>(error.Element.Properties, StringComparer.Ordinal),
                    error.Element.Patterns.ToArray())).ToArray())).ToArray();
            return AccessibilityScan.Create(processId, windowHandle, version, groups);
        }
        catch (Exception ex) when (ex is AxeWindowsAutomationException or COMException or NotSupportedException)
        {
            throw new InvalidOperationException($"Axe.Windows could not complete the scan: {ex.Message}", ex);
        }
    }
}

public sealed record AccessibilityFinding(
    string RuleId, string Description, string HowToFix, string Standard,
    IReadOnlyDictionary<string, string> ElementProperties, IReadOnlyList<string> Patterns)
{
    public string? AutomationId => ElementProperties.GetValueOrDefault("AutomationId");
}

public sealed record AccessibilityResultGroup(int ErrorCount, IReadOnlyList<AccessibilityFinding> Findings);

public sealed record AccessibilityScan
{
    public string Engine { get; init; } = "Axe.Windows";
    public required string EngineVersion { get; init; }
    public required int ProcessId { get; init; }
    public required long WindowHandle { get; init; }
    public string Checkpoint { get; init; } = "ready";
    public required IReadOnlyList<int> ResultGroupErrorCounts { get; init; }
    public required IReadOnlyList<AccessibilityFinding> Findings { get; init; }
    public int RawErrorCount => ResultGroupErrorCounts.Sum();

    public static AccessibilityScan Create(
        int processId, long windowHandle, string version, IReadOnlyList<AccessibilityResultGroup> groups)
    {
        if (processId <= 0 || windowHandle == 0 || string.IsNullOrWhiteSpace(version))
            throw new InvalidDataException("The scanner did not identify its process, window, and engine version.");
        if (groups.Count == 0)
            throw new InvalidDataException("Axe.Windows returned no scan result groups; an empty scan is not a pass.");
        var findings = new List<AccessibilityFinding>();
        var seen = new HashSet<(string RuleId, string RuntimeId)>();
        foreach (var group in groups)
        {
            if (group.ErrorCount != group.Findings.Count || group.ErrorCount < 0)
                throw new InvalidDataException("Axe.Windows error counts and detailed results disagree.");
            foreach (var finding in group.Findings)
            {
                if (string.IsNullOrWhiteSpace(finding.RuleId))
                    throw new InvalidDataException("Axe.Windows returned a finding without a rule ID.");
                var runtimeId = finding.ElementProperties.GetValueOrDefault("RuntimeId");
                // Nested SDK result groups can report the same element twice. Never dedupe by AutomationId.
                if (string.IsNullOrEmpty(runtimeId) || seen.Add((finding.RuleId, runtimeId)))
                    findings.Add(finding);
            }
        }
        return new AccessibilityScan
        {
            EngineVersion = version,
            ProcessId = processId,
            WindowHandle = windowHandle,
            ResultGroupErrorCounts = groups.Select(g => g.ErrorCount).ToArray(),
            Findings = findings
        };
    }
}
