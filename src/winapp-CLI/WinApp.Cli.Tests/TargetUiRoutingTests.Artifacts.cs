// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text;
using System.Text.Json;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;

namespace WinApp.Cli.Tests;

public partial class TargetUiRoutingTests
{
    [TestMethod]
    [DataRow("screenshot")]
    [DataRow("record")]
    public void Rewrite_CaptureWithoutOutput_DeclaresAndForwardsAHostDefault(string command)
    {
        var routed = Rewrite(["ui", command, "--on", "sandbox", "-a", "app", "--", "selector"]);
        Assert.IsNotNull(routed.Artifact);
        Assert.IsTrue(Path.IsPathFullyQualified(routed.Artifact.HostDestination));
        if (command == "screenshot")
        {
            Assert.AreEqual("screenshot.png", Path.GetFileName(routed.Artifact.HostDestination));
        }
        else
        {
            StringAssert.StartsWith(Path.GetFileName(routed.Artifact.HostDestination), "recording-");
            Assert.IsFalse(routed.Artifact.Overwrite);
        }
        Assert.AreEqual(routed.Artifact.GuestFullPath,
            routed.Arguments[routed.Arguments.IndexOf("--output") + 1]);
        Assert.IsLessThan(routed.Arguments.IndexOf("--"), routed.Arguments.IndexOf("--output"));
    }

    [TestMethod]
    [DataRow("screenshot")]
    [DataRow("record")]
    public async Task CaptureDefault_IsPublishedOnTheHost(string verb)
    {
        await using var harness = new Harness(_guestManaged);
        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        var routed = UiArgvRouter.Rewrite(["ui", verb, "--on", "sandbox"], GuestArtifacts,
            path => Path.Join(_hostOutput, path));
        Assert.IsNotNull(routed.Artifact);
        await WriteGuestArtifactAsync(scope, routed.Artifact.GuestRelativePath, "capture");
        await TargetArtifactService.PublishAsync(
            harness.Channel, scope, routed.Artifact, TestContext.CancellationToken);
        Assert.AreEqual("capture",
            await File.ReadAllTextAsync(routed.Artifact.HostDestination, TestContext.CancellationToken));
    }

    [TestMethod]
    [DataRow("get-value")]
    [DataRow("yield")]
    public void ReadOnlyRequirements_DoNotReconnectForValueReadsOrYield(string verb)
    {
        var command = new Command(verb);
        var requirements = TargetUiRequirements.For(command.Parse([]));
        Assert.IsFalse(requirements.RequiresRealInput);
        Assert.IsFalse(requirements.RequiresInteractiveDesktop);
        Assert.AreEqual(verb, requirements.CommandName);
    }

    [TestMethod]
    public void Rewrite_RecordingFlags_KeepOverwriteExplicitAndHonorFalse()
    {
        var routed = Rewrite(["ui", "record", "--overwrite", "--frames", "-o", "take.mp4"]);
        Assert.IsTrue(routed.Artifact!.Overwrite);
        Assert.IsTrue(routed.Artifact.Frames);
        routed = Rewrite(["ui", "record", "--overwrite=false", "--frames=false", "-o", "take.mp4"]);
        Assert.IsFalse(routed.Artifact!.Overwrite);
        Assert.IsFalse(routed.Artifact.Frames);
    }

    [TestMethod]
    public async Task Publish_RecordingDoesNotOverwriteAFileCreatedAfterPreflight()
    {
        await using var harness = new Harness(_guestManaged);
        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        var artifact = RecordingArtifact("take.mp4");
        TargetArtifactService.ValidateDestination(artifact);
        await WriteGuestArtifactAsync(scope, "take.mp4", "new take");
        await File.WriteAllTextAsync(artifact.HostDestination, "concurrent take", TestContext.CancellationToken);

        var failure = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            TargetArtifactService.PublishAsync(harness.Channel, scope, artifact, TestContext.CancellationToken));

        Assert.AreEqual("concurrent take", await File.ReadAllTextAsync(artifact.HostDestination, TestContext.CancellationToken));
        var recovery = failure.Error.Context!["hostRecoveryPath"];
        Assert.AreEqual("new take", await File.ReadAllTextAsync(Path.Join(recovery, "take.mp4"), TestContext.CancellationToken));
        Assert.IsTrue(Directory.Exists(Path.Join(_guestManaged, "artifacts", scope.Scope!)));
    }

    [TestMethod]
    public async Task Publish_OverwriteReplacesOnlyAfterReceivingTheWholeFrameBundle()
    {
        await using var harness = new Harness(_guestManaged);
        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        var artifact = RecordingArtifact("take.mp4") with { Frames = true, Overwrite = true };
        await File.WriteAllTextAsync(artifact.HostDestination, "previous video", TestContext.CancellationToken);
        Directory.CreateDirectory(artifact.HostFramesDirectory);
        await File.WriteAllTextAsync(Path.Join(artifact.HostFramesDirectory, "sentinel.txt"), "previous evidence", TestContext.CancellationToken);
        await WriteGuestBundleAsync(scope, artifact);

        await TargetArtifactService.PublishAsync(harness.Channel, scope, artifact, TestContext.CancellationToken);

        Assert.AreEqual("new video", await File.ReadAllTextAsync(artifact.HostDestination, TestContext.CancellationToken));
        Assert.IsTrue(File.Exists(Path.Join(artifact.HostFramesDirectory, "frames", "image.jpg")));
        Assert.IsTrue(File.Exists(Path.Join(artifact.HostFramesDirectory, "frames.ndjson")));
        Assert.IsFalse(File.Exists(Path.Join(artifact.HostFramesDirectory, "sentinel.txt")));
        var archived = Directory.GetDirectories(_hostOutput, "take.frames.previous-*").Single();
        Assert.AreEqual("previous evidence",
            await File.ReadAllTextAsync(Path.Join(archived, "sentinel.txt"), TestContext.CancellationToken));
        using var manifest = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Join(artifact.HostFramesDirectory, "manifest.json"), TestContext.CancellationToken));
        Assert.AreEqual(artifact.HostDestination, manifest.RootElement.GetProperty("video").GetProperty("path").GetString());
    }

    [TestMethod]
    public async Task Publish_MissingIndexedFrame_PreservesPriorVideoAndGuestEvidence()
    {
        await using var harness = new Harness(_guestManaged);
        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        var artifact = RecordingArtifact("take.mp4") with { Frames = true, Overwrite = true };
        await WriteGuestBundleAsync(scope, artifact);
        File.Delete(Path.Join(_guestManaged, "artifacts", scope.Scope!, "take.frames", "frames", "image.jpg"));
        await File.WriteAllTextAsync(artifact.HostDestination, "previous video", TestContext.CancellationToken);

        var error = await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() =>
            TargetArtifactService.PublishAsync(harness.Channel, scope, artifact, TestContext.CancellationToken));

        Assert.AreEqual("previous video", await File.ReadAllTextAsync(artifact.HostDestination, TestContext.CancellationToken));
        Assert.IsTrue(File.Exists(Path.Join(error.Error.Context!["hostRecoveryPath"], "take.mp4")));
        Assert.IsTrue(File.Exists(Path.Join(_guestManaged, "artifacts", scope.Scope!, "take.mp4")));
    }

    [TestMethod]
    public async Task Publish_PartialRecordingCopiesEvidenceWithoutReplacingThePreviousTake()
    {
        await using var harness = new Harness(_guestManaged);
        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        var artifact = RecordingArtifact("take.mp4") with { Frames = true, Overwrite = true };
        await WriteGuestBundleAsync(scope, artifact);
        await File.WriteAllTextAsync(artifact.HostDestination, "previous video", TestContext.CancellationToken);

        var recovery = await TargetArtifactService.PublishAsync(
            harness.Channel, scope, artifact, TestContext.CancellationToken, partial: true);

        Assert.IsNotNull(recovery);
        Assert.AreEqual("previous video", await File.ReadAllTextAsync(artifact.HostDestination, TestContext.CancellationToken));
        Assert.IsTrue(File.Exists(Path.Join(recovery, "take.frames", "frames", "image.jpg")));
        using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
            Path.Join(recovery, "take.frames", "manifest.json"), TestContext.CancellationToken));
        Assert.AreEqual(Path.Join(recovery, "take.mp4"),
            manifest.RootElement.GetProperty("video").GetProperty("path").GetString());
    }

    [TestMethod]
    public async Task Router_StoppedRecordingPublishesBeforeReportingSuccessAndRemovingGuestEvidence()
    {
        await using var harness = new Harness(_guestManaged);
        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        var artifact = RecordingArtifact("stopped.mp4") with { Frames = true };
        await WriteGuestBundleAsync(scope, artifact);
        var console = new TestConsole();
        var router = new ExecutionTargetUiRouter(null!, console);
        using var errors = new StringWriter();
        var relay = new ExecutionTargetUiRouter.ArtifactErrorRelay(errors, artifact);
        relay.Write(Encoding.UTF8.GetBytes(
            $"{{\"event\":\"recording-started\",\"path\":{JsonSerializer.Serialize(artifact.GuestFullPath)}}}\n"));
        using var output = new MemoryStream(Encoding.UTF8.GetBytes(
            $"{{\"path\":{JsonSerializer.Serialize(artifact.GuestFullPath)},\"stopReason\":\"cancelled\"}}"));

        await router.PublishArtifactAsync(harness.Channel, scope, artifact, output, relay, exitCode: 0);

        using var result = JsonDocument.Parse(console.Output);
        Assert.AreEqual("cancelled", result.RootElement.GetProperty("stopReason").GetString());
        Assert.AreEqual(artifact.HostDestination, result.RootElement.GetProperty("path").GetString());
        Assert.IsTrue(File.Exists(artifact.HostDestination));
        Assert.IsTrue(File.Exists(Path.Join(artifact.HostFramesDirectory, "manifest.json")));
        Assert.IsFalse(Directory.Exists(Path.Join(_guestManaged, "artifacts", scope.Scope!)));
        using var started = JsonDocument.Parse(errors.ToString());
        Assert.AreEqual("recording-started", started.RootElement.GetProperty("event").GetString());
        Assert.AreEqual(artifact.HostDestination, started.RootElement.GetProperty("path").GetString());
    }

    [TestMethod]
    public async Task Router_PublicationFailureNeverRemovesGuestEvidenceOrPrintsSuccess()
    {
        await using var harness = new Harness(_guestManaged);
        var scope = TargetArtifactService.ScopeFor(Guid.NewGuid());
        var artifact = RecordingArtifact("take.mp4");
        await WriteGuestArtifactAsync(scope, "take.mp4", "new video");
        await File.WriteAllTextAsync(artifact.HostDestination, "existing", TestContext.CancellationToken);
        var console = new TestConsole();
        var router = new ExecutionTargetUiRouter(null!, console);
        using var errors = new StringWriter();
        using var output = new MemoryStream(Encoding.UTF8.GetBytes("{\"success\":true}"));
        await Assert.ThrowsExactlyAsync<ExecutionTargetException>(() => router.PublishArtifactAsync(
            harness.Channel, scope, artifact, output,
            new ExecutionTargetUiRouter.ArtifactErrorRelay(errors, artifact), 0));
        Assert.AreEqual("", console.Output);
        Assert.IsTrue(File.Exists(Path.Join(_guestManaged, "artifacts", scope.Scope!, "take.mp4")));
    }

    [TestMethod]
    public void RewriteOutputPaths_RewritesFramePathsAndPartialRecoveryPaths()
    {
        var artifact = RecordingArtifact("take.mp4") with { Frames = true };
        var guest = JsonSerializer.Serialize(new[] { artifact.GuestFullPath, artifact.GuestFramesDirectory,
            Path.Join(artifact.GuestFramesDirectory, "manifest.json") });
        var paths = JsonSerializer.Deserialize<string[]>(ExecutionTargetUiRouter.RewriteOutputPaths(guest, artifact))!;
        Assert.AreEqual(artifact.HostDestination, paths[0]);
        Assert.AreEqual(artifact.HostFramesDirectory, paths[1]);
        Assert.AreEqual(Path.Join(artifact.HostFramesDirectory, "manifest.json"), paths[2]);
    }

    private RoutedArtifact RecordingArtifact(string name) =>
        Artifact(name, Path.Join(_hostOutput, name)) with { IsRecording = true, Overwrite = false };

    private async Task WriteGuestBundleAsync(GuestPathScope scope, RoutedArtifact artifact)
    {
        var frames = Path.GetFileName(artifact.GuestFramesDirectory);
        await WriteGuestArtifactAsync(scope, artifact.GuestRelativePath, "new video");
        await WriteGuestArtifactAsync(scope, Path.Join(frames, "frames", "image.jpg"), "jpeg");
        await WriteGuestArtifactAsync(scope, Path.Join(frames, "frames.ndjson"),
            """{"sampleIndex":0,"elapsedMs":0,"mediaTimeMs":0,"imageIndex":0,"file":"frames/image.jpg","changed":true}""" + "\n");
        await WriteGuestArtifactAsync(scope, Path.Join(frames, "manifest.json"),
            JsonSerializer.Serialize(new RecordFrameBundleManifest
            {
                Video = new RecordFrameVideoManifest { Path = artifact.GuestFullPath },
            }, RecordingJsonContext.Default.RecordFrameBundleManifest));
    }
}
