using System.Buffers.Binary;
using System.Globalization;
using System.Text.Json;

namespace AccessLoop;

public sealed class AuditRunner(IAuditPlatform platform)
{
    public async Task<AuditReport> RunAsync(
        AuditOptions options, AuditTarget target, string version, string output, CancellationToken token)
    {
        var started = DateTimeOffset.UtcNow;
        var results = new List<AuditCheckResult>();
        var artifacts = new List<AuditArtifact>();
        JsonElement? snapshot = null;
        JsonElement? observation = null;
        AccessibilityScan? axe = null;
        var errors = new List<string>();
        var partial = false;
        var timeout = TimeSpan.FromMilliseconds(options.TimeoutMs + 5000);

        void Save(string name, JsonElement value, string source)
        {
            AuditFiles.WriteNew(Path.Combine(output, name), value);
            artifacts.Add(AuditFiles.Describe(output, name, "application/json", source));
        }
        void Failed(string checkId, string operation, Exception error)
        {
            partial = true;
            var message = error is OperationCanceledException ? $"{operation}: cancelled." : $"{operation}: {error.Message}";
            errors.Add(message);
            results.Add(AuditRules.Result(checkId, "blocked", message));
        }

        AuditFiles.WriteNew(Path.Combine(output, "request.json"), new
        {
            requestedProcessId = options.ProcessId,
            requestedWindowHandle = options.WindowHandle,
            requestedApp = options.App,
            options.Depth, options.MaxElements, options.TimeoutMs,
            options.ObserveSeconds, options.TabSteps, options.AcceptInput,
            screenshot = options.Screenshot,
            screenshotPolicy = "Exact window, Windows Graphics Capture only, no activation or owned-window expansion."
        });
        artifacts.Add(AuditFiles.Describe(output, "request.json", "application/json", "AccessLoop request"));

        try
        {
            var response = await platform.UiAsync(target,
                ["audit-snapshot", "--depth", Number(options.Depth), "--max-elements", Number(options.MaxElements),
                    "--timeout", Number(options.TimeoutMs)], timeout, token);
            var data = AuditJson.Object(response, "ui audit-snapshot");
            AuditRules.ValidateSnapshot(data, target);
            if (response.ExitCode != 0 && AuditJson.String(data, "status") != "partial")
                throw new InvalidDataException("Snapshot exit status contradicts its completion status.");
            var evaluated = AuditRules.SnapshotResults(data).ToArray();
            Save("snapshot.json", data, "WinAppCLI UIA snapshot");
            snapshot = data;
            target = target with { RootRuntimeId = AuditJson.OptionalString(data, "rootRuntimeId") };
            partial |= AuditJson.String(data, "status") != "captured";
            results.AddRange(evaluated);
        }
        catch (Exception ex) when (IsExecutionFailure(ex))
        {
            Failed("capture.tree", "UIA snapshot", ex);
        }

        if (!token.IsCancellationRequested)
        {
            try
            {
                platform.VerifyTarget(target);
                var scan = AuditPrivacy.Redact(await platform.AxeAsync(target, TimeSpan.FromMilliseconds(options.TimeoutMs), token));
                var evaluated = AuditRules.AxeResults(scan, snapshot, target).ToArray();
                AuditFiles.WriteNew(Path.Combine(output, "axe.json"), scan);
                artifacts.Add(AuditFiles.Describe(output, "axe.json", "application/json", "Axe.Windows (sensitive properties filtered)"));
                axe = scan;
                results.AddRange(evaluated);
            }
            catch (Exception ex) when (IsExecutionFailure(ex))
            {
                Failed("axe.scan", "Axe scan", ex);
            }
        }

        if (options.Screenshot && !token.IsCancellationRequested)
        {
            try
            {
                var imagePath = Path.Combine(output, "screenshot.png");
                var response = await platform.UiAsync(target,
                    ["audit-screenshot", "--output", imagePath, "--timeout", Number(options.TimeoutMs)], timeout, token);
                if (response.ExitCode != 0)
                    throw new InvalidOperationException($"Passive screenshot failed ({response.ExitCode}). {response.Stderr.Trim()}");
                var data = AuditJson.Object(response, "ui audit-screenshot");
                if (AuditJson.Integer(data, "schemaVersion") != 1 || AuditJson.Integer(data, "processId") != target.ProcessId
                    || AuditJson.Long(data, "windowHandle") != target.WindowHandle
                    || AuditJson.String(data, "status") != "captured"
                    || AuditJson.String(data, "captureMethod") != "windows-graphics-capture"
                    || AuditJson.Boolean(data, "activationRequested") != false
                    || !Path.GetFullPath(AuditJson.String(data, "filePath")).Equals(imagePath, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Screenshot scope or passive capture policy does not match the request.");
                ValidatePng(imagePath, AuditJson.Integer(data, "width"), AuditJson.Integer(data, "height"));
                Save("screenshot.json", data, "WinAppCLI passive capture metadata");
                artifacts.Add(AuditFiles.Describe(output, "screenshot.png", "image/png", "Windows Graphics Capture, exact-window"));
                results.Add(AuditRules.Result("capture.screenshot", "observed",
                    "Captured the selected window without requesting activation, desktop capture or other-window discovery.",
                    ["screenshot.json", "screenshot.png"]));
                if (snapshot is not null && results.Any(AuditVisuals.IsCandidate))
                {
                    var verification = await platform.UiAsync(target,
                        ["audit-snapshot", "--depth", Number(options.Depth), "--max-elements", Number(options.MaxElements),
                            "--timeout", Number(options.TimeoutMs)], timeout, token);
                    var verifiedSnapshot = AuditJson.Object(verification, "ui audit-snapshot (after screenshot)");
                    AuditRules.ValidateSnapshot(verifiedSnapshot, target);
                    if (verification.ExitCode != 0 && AuditJson.String(verifiedSnapshot, "status") != "partial")
                        throw new InvalidDataException("Screenshot verification exit status contradicts its snapshot status.");
                    Save(AuditVisuals.VerificationPath, verifiedSnapshot, "WinAppCLI post-screenshot element identity and bounds");
                }
            }
            catch (Exception ex) when (IsExecutionFailure(ex))
            {
                Failed("capture.screenshot", "Passive screenshot", ex);
            }
        }

        if (options.ObserveSeconds > 0 && !token.IsCancellationRequested)
        {
            try
            {
                var arguments = new List<string>
                {
                    "audit-observe", "--duration-sec", Number(options.ObserveSeconds),
                    "--tab-steps", Number(options.TabSteps), "--max-events", "500"
                };
                if (options.AcceptInput)
                    arguments.Add("--accept-input");
                var response = await platform.UiAsync(target, arguments,
                    TimeSpan.FromSeconds(options.ObserveSeconds + 15), token);
                var data = AuditJson.Object(response, "ui audit-observe");
                AuditRules.ValidateObservation(data, target, options);
                if (response.ExitCode != 0 && AuditJson.String(data, "status") != "partial")
                    throw new InvalidDataException("Observation exit status contradicts its completion status.");
                var evaluated = AuditRules.ObservationResults(data, snapshot, options).ToArray();
                Save("observation.json", data, "WinAppCLI bounded UIA observation");
                observation = data;
                partial |= AuditJson.String(data, "status") != "completed";
                results.AddRange(evaluated);
            }
            catch (Exception ex) when (IsExecutionFailure(ex))
            {
                Failed("events.timeline", "UIA observation", ex);
                if (options.TabSteps > 0)
                    results.Add(AuditRules.Result("keyboard.path", "blocked", "No valid probe trace was returned."));
            }
        }

        try
        {
            platform.VerifyTarget(target);
            if (!results.Any(result => result.CheckId == "capture.scope"))
            {
                Save("scope.json", JsonSerializer.SerializeToElement(new
                {
                    target,
                    verifiedAtUtc = DateTimeOffset.UtcNow,
                    scope = "native-window-and-process-identity",
                    note = "This identity check does not establish UIA tree completeness."
                }, Json.Options), "AccessLoop native target identity");
                results.Add(AuditRules.Result("capture.scope", "observed",
                    "The native window and process still match the attached identity; tree and engine coverage are reported separately.",
                    ["scope.json"]));
            }
        }
        catch (Exception ex) when (IsExecutionFailure(ex))
        {
            Failed("capture.scope", "Final target identity", ex);
        }
        foreach (var check in AuditCatalog.Checks)
        {
            if (results.Any(result => result.CheckId == check.Id))
                continue;
            results.Add(AuditRules.Result(check.Id,
                check.Availability == "unsupported" ? "unsupported" : "not-tested",
                check.Availability switch
                {
                    "unsupported" => "This check is not implemented in this build; it is not counted as passed or inapplicable.",
                    "assisted" => "No human or AI assessment has been attached. Automatic evidence does not establish content or business meaning.",
                    _ => token.IsCancellationRequested ? "Not executed because the audit was cancelled."
                        : "Not requested, or prerequisite evidence was unavailable. No conclusion is inferred."
                }));
        }

        return new AuditReport
        {
            RunId = Guid.NewGuid().ToString("N"),
            StartedAtUtc = started,
            FinishedAtUtc = DateTimeOffset.UtcNow,
            Status = snapshot is null && axe is null ? "blocked" : partial || token.IsCancellationRequested ? "partial" : "completed",
            Mode = options.TabSteps > 0 ? "snapshot+keyboard+events" : options.ObserveSeconds > 0 ? "snapshot+events" : "snapshot",
            Target = target,
            WinappVersion = version,
            Results = results,
            Artifacts = artifacts,
            Snapshot = snapshot,
            Observation = observation,
            Axe = axe,
            Error = errors.Count == 0 ? null : string.Join(Environment.NewLine, errors)
        };
    }

    public static bool IsExecutionFailure(Exception exception) =>
        ScenarioRunner.IsExecutionFailure(exception) || exception is NotSupportedException or OverflowException;

    public static int ExitCode(AuditReport report) =>
        report.Status != "completed" ? 2 : report.RuleFailureCount > 0 ? 1 : 0;

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void ValidatePng(string path, int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > 16_000_000)
            throw new InvalidDataException("Screenshot dimensions exceed the capture budget.");
        using var stream = File.OpenRead(path);
        Span<byte> header = stackalloc byte[24];
        stream.ReadExactly(header);
        if (!header[..8].SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
            || BinaryPrimitives.ReadInt32BigEndian(header[16..20]) != width
            || BinaryPrimitives.ReadInt32BigEndian(header[20..24]) != height)
            throw new InvalidDataException("Screenshot file and reported PNG dimensions disagree.");
    }
}
