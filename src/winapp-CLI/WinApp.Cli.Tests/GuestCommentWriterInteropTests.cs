// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.ExecutionTargets.GuestAgent;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Services;
using WinApp.Cli.Services.DevTools.Comments;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public sealed class GuestCommentWriterInteropTests
{
    [TestMethod]
    [TestCategory("NativeIntegration")]
    [DataRow("available", 0)]
    [DataRow("stale", 0)]
    [DataRow("capture-refused", GuestCommentContext.CaptureFailureExitCode)]
    [DataRow("host-rejected", GuestCommentContext.HostWriteFailureExitCode)]
    [DataRow("context-refused", GuestCommentContext.ContextFailureExitCode)]
    [DataRow("host-read-refused", GuestCommentContext.HostReadFailureExitCode)]
    [DataRow("ack-missing", GuestCommentContext.MissingAcknowledgementExitCode)]
    [DataRow("arguments-refused", 2)]
    [DataRow("overlay", 0, false)]
    [DataRow("overlay", 0, true)]
    [DataRow("retired-after-command", GuestCommentContext.CaptureFailureExitCode, false)]
    [DataRow("retired-after-command", GuestCommentContext.CaptureFailureExitCode, true)]
    [DataRow("reused-after-command", GuestCommentContext.CaptureFailureExitCode, false)]
    [DataRow("reused-after-command", GuestCommentContext.CaptureFailureExitCode, true)]
    [DataRow("new-session-after-command", GuestCommentContext.CaptureFailureExitCode, false)]
    [DataRow("new-session-after-command", GuestCommentContext.CaptureFailureExitCode, true)]
    public async Task NativeWriterArguments_ReachActualProgramCaptureAndAuthenticatedHostOwner(
        string authoredState, int expectedExit, bool composer = false)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var root = Directory.CreateTempSubdirectory("guest-writer-interop-");
        var hostRoot = root.CreateSubdirectory("host");
        var guestRoot = root.CreateSubdirectory("guest");
        File.WriteAllText(Path.Combine(hostRoot.FullName, "App.csproj"), "<Project/>");
        File.WriteAllText(Path.Combine(hostRoot.FullName, "Main.xaml"), "<TextBlock x:Name=\"BoundTitle\"/>");
        File.Copy(Path.Combine(hostRoot.FullName, "Main.xaml"), Path.Combine(guestRoot.FullName, "Main.xaml"));
        using var app = Process.GetCurrentProcess();
        var process = new GuestProcessStart(app.Id, app.StartTime.ToUniversalTime().Ticks);
        var binding = new GuestCommentBinding(new("sandbox", "default"), new("opaque.epoch"), process,
            new(Path.Combine(hostRoot.FullName, "App.csproj")), ["Main.xaml"], guestRoot.FullName);
        var store = new CommentStore();
        var owner = new GuestCommentOwner(binding, store);
        var session = new GuestCommentSession(binding.Id, binding.TargetId, binding.Epoch, process);
        var pair = DuplexStreamPair.Create();
        using var host = pair.Client;
        using var guest = pair.Server;
        var endpoint = new GuestCommentEndpoint(session).RunAsync(guest, guest, _ => { }, timeout.Token);
        var broker = ServeOwnerAsync();
        var originalOutput = Console.Out;
        var originalError = Console.Error;
        var originalTelemetry = Environment.GetEnvironmentVariable(Telemetry.Telemetry.OptOutEnvironmentVariable);
        try
        {
            using var agent = new FakeDevToolsProtocolAgent();
            if (authoredState == "capture-refused")
            {
                agent.Error("VisualTree.enumerate", -32000, "not-found", "private-error-marker");
            }
            var sourceFile = authoredState == "host-rejected" ? "Foreign.xaml" : "Main.xaml";
            var nativeOverlay = authoredState == "overlay" || authoredState.EndsWith("-after-command", StringComparison.Ordinal);
            if (!nativeOverlay)
            {
                agent.Answer("VisualTree.enumerate", """[{"handle":"42","type":"TextBlock","name":"BoundTitle","children":[]}]""");
            }
            agent.Answer("Property.get", """{"props":[{"name":"Text","value":"Binding ready","valueType":"String"}]}""")
                .Answer("Source.get", $$"""{"fileName":"ms-appx:///{{sourceFile}}","lineNumber":1,"columnNumber":1,"authoredState":"{{authoredState}}"}""")
                .Answer("Internal.elementAnchor", """{"anchor":"Main.xaml:1:TextBlock:BoundTitle"}""")
                .Answer("Internal.sourceRoot", JsonSerializer.Serialize(new { sourceRoot = guestRoot.FullName }))
                .Answer("Internal.setComments", """{"total":1,"placed":1}""");
            var operation = Guid.NewGuid().ToString("N");
            var startTicks = process.StartTicksUtc + (authoredState == "context-refused" ? 1 : 0);
            var token = $"{process.ProcessId}.{startTicks}.{operation}..{binding.Id}.{binding.Epoch}";
            const string text = "  native \"writer\"\r\nhost-backed note  ";
            var args = await NativeArgumentsAsync(process.ProcessId, text, token, guestRoot.FullName, timeout.Token);
            string? expectedNativeWire = null;
            if (nativeOverlay)
            {
                using var native = await OverlayArgumentsAsync(process.ProcessId, text, token, guestRoot.FullName,
                    composer, authoredState, timeout.Token);
                var result = native.RootElement;
                Assert.AreEqual(1, result.GetProperty("spawns").GetInt32());
                args = result.GetProperty("args").EnumerateArray().Select(row => row.GetString()!).ToArray();
                var wire = result.GetProperty("initialWire").GetString();
                Assert.AreNotEqual(result.GetProperty("raw").GetString(), wire);
                expectedNativeWire = wire;
                agent.Answer("VisualTree.enumerate", result.GetProperty("tree").GetRawText());
            }
            if (authoredState == "arguments-refused")
            {
                args = [.. args, "--not-a-comment-option"];
            }
            using var output = new TestConsole();
            using var errors = new StringWriter();
            Console.SetOut(errors);
            Console.SetError(errors);
            Environment.SetEnvironmentVariable(Telemetry.Telemetry.OptOutEnvironmentVariable, "1");
            var exit = await Program.RunAsync(args, services => services
                .AddSingleton<IAnsiConsole>(output)
                .AddSingleton<IUiTargetResolver>(new CommentTestTargetResolver(app.Id))
                .AddSingleton<ICurrentDirectoryProvider>(new CurrentDirectoryProvider(guestRoot.FullName))
                .AddSingleton<IFirstRunService, NoNotices>()
                .AddSingleton<IUpdateNotificationService, NoNotices>());
            Assert.AreEqual(expectedExit, exit, output.Output + errors);
            if (expectedNativeWire is not null)
            {
                Assert.AreEqual(expectedNativeWire, args[Array.IndexOf(args, "--from-element") + 1],
                    "The actual overlay writer must use the wire captured when the editor opened, never its raw handle.");
            }
            if (expectedExit == 0)
            {
                var persisted = store.Get(binding.StorePath, "note");
                Assert.IsNotNull(persisted);
                Assert.AreEqual(text, persisted.Text);
                Assert.AreEqual("Main.xaml", persisted.Anchor.SourceFile);
                Assert.AreEqual(hostRoot.FullName, persisted.ProjectRoot);
            }
            else
            {
                Assert.AreEqual(authoredState == "ack-missing", File.Exists(binding.StorePath),
                    "A missing acknowledgement must not claim that persistence did or did not happen.");
                var status = await RunNativeAsync(["--writer-failure", exit.ToString(CultureInfo.InvariantCulture)], timeout.Token);
                StringAssert.Contains(status, expectedExit switch
                {
                    2 => "arguments",
                    GuestCommentContext.ContextFailureExitCode => "context",
                    GuestCommentContext.CaptureFailureExitCode => "capture",
                    GuestCommentContext.HostReadFailureExitCode => "host-read",
                    GuestCommentContext.MissingAcknowledgementExitCode => "host-acknowledgement",
                    _ => "host-write",
                });
                StringAssert.Contains(status, "Draft retained");
                Assert.IsFalse(status.Contains("private-error-marker") || status.Contains(text) ||
                    status.Contains(token) || status.Contains(binding.Epoch) || status.Contains(binding.Id));
            }
            Assert.IsFalse(Directory.Exists(Path.Combine(guestRoot.FullName, ".winapp")));
        }
        finally
        {
            await timeout.CancelAsync();
            try { await Task.WhenAll(endpoint, broker); }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested) { }
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            Environment.SetEnvironmentVariable(Telemetry.Telemetry.OptOutEnvironmentVariable, originalTelemetry);
            root.Delete(recursive: true);
        }

        async Task ServeOwnerAsync()
        {
            _ = await GuestCommentFrames.ReadAsync(host, timeout.Token);
            while (!timeout.IsCancellationRequested)
            {
                var bytes = await GuestCommentFrames.ReadAsync(host, timeout.Token);
                var envelope = JsonSerializer.Deserialize(bytes, GuestCommentsJsonContext.Default.GuestCommentEnvelope)!;
                session.Validate(envelope.Request);
                GuestCommentReply reply;
                try
                {
                    reply = envelope.Kind == "read"
                        ? new GuestCommentReply(envelope.Request.OperationId, Comments: owner.Read(),
                            HostStorePath: binding.StorePath, GuestSourceRoot: authoredState == "host-read-refused" ? hostRoot.FullName : guestRoot.FullName)
                        : new GuestCommentReply(envelope.Request.OperationId, Commit: owner.Apply(envelope.Request));
                    if (envelope.Kind == "apply" && authoredState == "ack-missing")
                    {
                        reply = new(envelope.Request.OperationId);
                    }
                }
                catch (InvalidOperationException ex)
                {
                    reply = new(envelope.Request.OperationId, Error: ex.Message);
                }
                await GuestCommentFrames.WriteAsync(host, reply, GuestCommentsJsonContext.Default.GuestCommentReply, timeout.Token);
            }
        }
    }

    [TestMethod]
    [TestCategory("NativeIntegration")]
    [DataRow("untracked", false)]
    [DataRow("untracked", true)]
    [DataRow("retired", false)]
    [DataRow("retired", true)]
    [DataRow("reused", false)]
    [DataRow("reused", true)]
    [DataRow("new-session", false)]
    [DataRow("new-session", true)]
    [DataRow("local", false)]
    [DataRow("local", true)]
    public async Task OverlayEditorLifetime_RefusesInvalidWitnessBeforeSpawnAndPreservesLocalElement(string outcome, bool composer)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var result = await OverlayArgumentsAsync(Environment.ProcessId, "retained draft",
            $"{Environment.ProcessId}.638936747284321987.0123456789abcdef0123456789abcdef..0123456789abcdef0123456789abcdef.test-epoch",
            @"C:\snapshot", composer, outcome, timeout.Token);
        var local = outcome == "local";
        Assert.AreEqual(local ? 1 : 0, result.RootElement.GetProperty("spawns").GetInt32());
        var args = result.RootElement.GetProperty("args").EnumerateArray().Select(row => row.GetString()!).ToArray();
        if (local)
        {
            CollectionAssert.Contains(args, "--from-element");
            Assert.AreEqual(result.RootElement.GetProperty("initialWire").GetString(),
                args[Array.IndexOf(args, "--from-element") + 1], "The writer must retain the editor's exact element.");
            Assert.AreNotEqual(result.RootElement.GetProperty("raw").GetString(),
                result.RootElement.GetProperty("initialWire").GetString());
            CollectionAssert.DoesNotContain(args, "--from-selection");
            CollectionAssert.DoesNotContain(args, "--guest-comments");
        }
        else
        {
            Assert.IsEmpty(args, "An untracked or expired editor must not start a writer.");
        }
    }

    [TestMethod]
    [TestCategory("NativeIntegration")]
    [DataRow(2, "arguments")]
    [DataRow(GuestCommentContext.ContextFailureExitCode, "context")]
    [DataRow(GuestCommentContext.CaptureFailureExitCode, "capture")]
    [DataRow(GuestCommentContext.HostReadFailureExitCode, "host-read")]
    [DataRow(GuestCommentContext.HostWriteFailureExitCode, "host-write")]
    [DataRow(GuestCommentContext.MissingAcknowledgementExitCode, "host-acknowledgement")]
    [DataRow(1, "writer-exit")]
    public async Task NativeFailureFormatting_UsesOnlyKnownStageAndNumericCode(int code, string stage)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var status = await RunNativeAsync(["--writer-failure", code.ToString(CultureInfo.InvariantCulture)], timeout.Token);
        Assert.AreEqual($"Host save not confirmed (stage: {stage}, code: {code}). Draft retained; reconnect or refresh before retrying.", status);
    }

    private static async Task<string[]> NativeArgumentsAsync(int pid, string text, string token, string sourceRoot, CancellationToken cancellationToken)
        => JsonSerializer.Deserialize<string[]>(await RunNativeAsync(
            ["--comment-command", @"C:\verified bundle\winapp.exe", pid.ToString(CultureInfo.InvariantCulture),
                "42", text, "note", token, sourceRoot], cancellationToken))!;

    private static async Task<JsonDocument> OverlayArgumentsAsync(int pid, string text, string token, string sourceRoot,
        bool composer, string outcome, CancellationToken cancellationToken) =>
        JsonDocument.Parse(await RunNativeAsync(["--overlay-comment-command", @"C:\verified bundle\winapp.exe",
            pid.ToString(CultureInfo.InvariantCulture), composer ? "composer" : "inline", outcome, text, token, sourceRoot],
            cancellationToken));

    private static async Task<string> RunNativeAsync(string[] args, CancellationToken cancellationToken)
    {
        var fixture = NativeTestFixture.Resolve();
        var start = new ProcessStartInfo(fixture)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in args)
        {
            start.ArgumentList.Add(argument);
        }
        using var process = Process.Start(start)!;
        try
        {
            var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var error = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            Assert.AreEqual(0, process.ExitCode, await error);
            return await output;
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
        }
    }

    private sealed class NoNotices : IFirstRunService, IUpdateNotificationService
    {
        public bool CheckAndDisplayFirstRunNotice() => false;
        public void CheckAndNotify() { }
    }
}
