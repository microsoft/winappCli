using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

public sealed partial class LoopReportRendererTests
{
    private LoopTestEvidence evidence = null!;
    private AuditTestFixture audit => evidence.Audit;
    private LoopReport report { get => evidence.Report; set => evidence.Report = value; }
    private string Root => evidence.Root;

    [TestInitialize]
    public void Initialize() => evidence = new();

    [TestCleanup]
    public void Cleanup() => evidence.Dispose();

    private LoopRoundResult Round(string id, params string[] fixedTargets) => evidence.AddRound(id, fixedTargets);
    private LoopRepairResult Repair(string id, string afterId, string source = "reviewed-file") => evidence.AddRepair(id, afterId, source);
    private void SaveScan(LoopStateResult state, AuditReport scan, bool initial = false) => evidence.SaveScan(state, scan, initial);
    private static void SaveActions(LoopRoundResult round, bool initial = false) => LoopTestEvidence.SaveActions(round, initial);
    private void SetProfile(LoopProfile profile) => evidence.SetProfile(profile);
    private static string WriteSources(string directory, IReadOnlyCollection<string> fixedTargets) => LoopTestEvidence.WriteSources(directory, fixedTargets);
    private static JsonElement Properties(string id, string type, string name) => LoopTestEvidence.Properties(id, type, name);
    private static AuditCheckResult Failure(string nodeId) => LoopTestEvidence.Failure(nodeId);
    private static AccessibilityFinding NativeFailure(string id, string? runtime = null) => LoopTestEvidence.NativeFailure(id, runtime);

    private string Render(string filename = "loop-report.html")
    {
        var path = Path.Combine(Root, filename);
        LoopReportRenderer.Write(path, report);
        return File.ReadAllText(path);
    }
}
