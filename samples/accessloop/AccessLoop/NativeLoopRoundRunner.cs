using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AccessLoop;

public sealed partial class NativeLoopRoundRunner : ILoopRoundRunner
{
    private readonly string version;
    private readonly bool screenshot;
    private readonly Action<LoopProgress>? progress;
    private readonly Func<LoopProfile, string, CancellationToken, Task<IAppSession>> launch;
    private readonly Func<IAuditPlatform> platforms;
    private readonly Func<ISurveyNativeContext> contexts;
    private readonly Func<IExploreWindowSource> windows;

    public NativeLoopRoundRunner(string winapp, string version, bool screenshot, Action<LoopProgress>? progress = null)
        : this(version, screenshot, progress,
            (profile, output, token) => new WinappLauncher(winapp).LaunchProjectAsync(
                profile.Project, profile.TimeoutSeconds, output, token),
            () => new NativeAuditPlatform(winapp), () => new SurveyNativeContext(), () => new ExploreNativeWindowSource())
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(winapp);
    }

    internal NativeLoopRoundRunner(
        string version, bool screenshot, Action<LoopProgress>? progress,
        Func<LoopProfile, string, CancellationToken, Task<IAppSession>> launch,
        Func<IAuditPlatform> platforms, Func<ISurveyNativeContext> contexts, Func<IExploreWindowSource> windows)
    {
        this.version = version;
        this.screenshot = screenshot;
        this.progress = progress;
        this.launch = launch;
        this.platforms = platforms;
        this.contexts = contexts;
        this.windows = windows;
    }

    public Task<LoopRoundResult> RunAsync(LoopProfile profile, string roundId, string outputDirectory, CancellationToken token)
    {
        var frozen = JsonSerializer.Deserialize<LoopProfile>(JsonSerializer.Serialize(profile, Json.Options), Json.Options)
            ?? throw new InvalidDataException("A resolved loop profile is required.");
        return new RoundExecution(this, frozen, roundId, outputDirectory).RunAsync(token);
    }

    private sealed partial class RoundExecution
    {
        private readonly NativeLoopRoundRunner owner;
        private readonly LoopProfile profile;
        private readonly LoopRoundResult round;
        private readonly IAuditPlatform inner;
        private readonly IExploreWindowSource windowSource;
        private readonly ExploreScopeMonitor monitor;
        private readonly AuditAdapter auditPlatform;
        private AuditTarget target = null!;
        private LoopActionResult? action;
        private string commandDirectory = null!;
        private string? currentPage;
        private int commandSequence;
        private int commandCount;

        internal RoundExecution(NativeLoopRoundRunner owner, LoopProfile profile, string roundId, string output)
        {
            this.owner = owner;
            this.profile = profile;
            round = new() { Id = roundId, Directory = Path.GetFullPath(output) };
            inner = owner.platforms();
            windowSource = owner.windows();
            monitor = new(inner, owner.contexts(), windowSource);
            auditPlatform = new(this);
        }

        internal async Task<LoopRoundResult> RunAsync(CancellationToken token)
        {
            using var monitorLifetime = monitor;
            ValidateRequest();
            AuditFiles.CreateNewDirectory(round.Directory);
            IAppSession? app = null;
            CancellationTokenSource? duration = null;
            CancellationTokenSource? running = null;
            try
            {
                AuditFiles.WriteNew(Path.Combine(round.Directory, "route.json"), profile);
                SaveProgress();
                round.Sources = JourneyInput.CaptureSources(profile.Project);
                Require(round.Sources.Count > 0, "The owned source project has no source inventory.");
                commandDirectory = NewDirectory("startup");
                var launchDirectory = NewDirectory("launch");
                token.ThrowIfCancellationRequested();
                app = await owner.launch(profile, launchDirectory, token);
                Require(app.ProcessId > 0, "The launcher did not return a newly owned process.");
                target = await inner.AttachAsync(Options(processId: app.ProcessId), token);
                Require(target.ProcessId == app.ProcessId && target.ProcessName == profile.ProcessName,
                    "The attached native process is not the newly owned declared application.");
                inner.VerifyTarget(target);
                round.Target = target;
                AuditFiles.WriteNew(Path.Combine(commandDirectory, "target.json"), target);
                if (windowSource.Read(target).ForegroundWindow != target.WindowHandle)
                {
                    Emit("waiting-for-foreground", message: "Activate the newly opened demo window; no route input has been sent.");
                    var deadline = DateTimeOffset.UtcNow.AddSeconds(profile.TimeoutSeconds);
                    while (DateTimeOffset.UtcNow < deadline && windowSource.Read(target).ForegroundWindow != target.WindowHandle)
                    {
                        inner.VerifyTarget(target);
                        await Task.Delay(100, token);
                    }
                }
                monitor.Start(target);
                using (var startup = CancellationTokenSource.CreateLinkedTokenSource(token, monitor.StopToken))
                {
                    startup.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
                    var tree = await WaitForPageAsync(null, startup.Token);
                    currentPage = ActivePage(tree);
                }
                duration = new(TimeSpan.FromSeconds(profile.MaxDurationSeconds));
                running = CancellationTokenSource.CreateLinkedTokenSource(token, monitor.StopToken, duration.Token);
                Emit("round-started", message: "The newly owned process, exact HWND and initial ready heading are verified.");

                foreach (var step in profile.Steps)
                {
                    running.Token.ThrowIfCancellationRequested();
                    await RunStepAsync(step, running.Token);
                }
                monitor.Check(target);
                RequireSourcesUnchanged();
                Require(round.Actions.Count == profile.Steps.Count && round.Actions.All(item => item.Status == "completed")
                    && round.States.Count == profile.Steps.Count(step => step.Kind == "scan")
                    && round.States.All(state => state.Status == "captured"), "The declared route or checkpoint coverage is incomplete.");
                round.Status = "completed";
            }
            catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
            {
                round.Status = "blocked";
                round.Error = monitor.Violation?.Message
                    ?? (duration?.IsCancellationRequested == true && !token.IsCancellationRequested
                        ? "The total interaction duration was exhausted; completed evidence was retained." : Error(ex));
            }
            catch (Exception ex)
            {
                round.Status = "blocked";
                round.Error = $"Unexpected {ex.GetType().Name}: {ex.Message}";
                throw;
            }
            finally
            {
                running?.Dispose();
                duration?.Dispose();
                // Owned-process disposal is lifecycle cleanup, not route completion or UI restoration.
                monitor.Dispose();
                round.ScopeEvents.AddRange(monitor.Events);
                if (monitor.Violation is { } stopped)
                {
                    round.Status = "blocked";
                    round.Error ??= stopped.Message;
                }
                try
                {
                    if (app is not null)
                        await app.DisposeAsync();
                }
                catch (Exception ex) when (AuditRunner.IsExecutionFailure(ex))
                {
                    round.Status = "blocked";
                    round.Error = JoinError(round.Error, "Owned-process cleanup failed: " + Error(ex));
                }
                catch (Exception ex)
                {
                    round.Status = "blocked";
                    round.Error = JoinError(round.Error, $"Unexpected owned-process cleanup failure ({ex.GetType().Name}): {ex.Message}");
                    throw;
                }
                finally
                {
                    round.FinishedAtUtc = DateTimeOffset.UtcNow;
                    SaveProgress();
                    var log = Path.Combine(round.Directory, "actions.json");
                    AuditFiles.WriteNew(log, round.Actions);
                    round.ActionLogSha256 = AuditFiles.Hash(log);
                    AuditFiles.WriteNew(Path.Combine(round.Directory, "round.json"), round);
                }
            }
            Emit("round-finished", message: round.Error ?? "Every prescribed action and checkpoint completed; this is not a whole-app accessibility verdict.");
            return round;
        }

        private async Task RunStepAsync(LoopStep step, CancellationToken token)
        {
            action = new() { Step = step };
            round.Actions.Add(action);
            commandDirectory = NewDirectory(Path.Combine("actions", step.Id));
            SaveProgress();
            try
            {
                Emit("step-started", step.Id, step.Name);
                await ExecuteStepAsync(step, token);
                monitor.Check(target);
                action.Status = "completed";
            }
            catch (Exception ex)
            {
                action.Status = "blocked";
                action.Error = monitor.Violation?.Message ?? Error(ex);
                throw;
            }
            finally
            {
                action.FinishedAtUtc = DateTimeOffset.UtcNow;
                AuditFiles.WriteNew(Path.Combine(commandDirectory, "action.json"), action);
                SaveProgress();
            }
            Emit("step-completed", step.Id, step.Name);
            action = null;
        }

        private void ValidateRequest()
        {
            Require(profile.SchemaVersion == 1 && profile.TimeoutSeconds is >= 1 and <= 120
                && profile.MaxDurationSeconds is >= 1 and <= 3600 && profile.TreeDepth is >= 1 and <= 100
                && profile.MaxElements is >= 1 and <= 10000 && profile.Steps.Count is > 0 and <= 1000
                && profile.Pages.Count is > 0 and <= 100, "The fixed route has invalid execution bounds.");
            Require(Path.IsPathFullyQualified(profile.Project), "The source project must be resolved before execution.");
            var sourceRoot = Path.GetDirectoryName(Path.GetFullPath(profile.Project))!;
            Require(!round.Directory.Equals(sourceRoot, StringComparison.OrdinalIgnoreCase)
                && !round.Directory.StartsWith(sourceRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase),
                "Round artifacts must not be created inside the source project being fingerprinted.");
            Require(profile.Pages.Select(page => page.Id).Distinct(StringComparer.Ordinal).Count() == profile.Pages.Count,
                "Declared page IDs must be unique.");
            Require(profile.Steps.Select(step => step.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == profile.Steps.Count,
                "Step IDs must be unique on the Windows filesystem.");
            foreach (var step in profile.Steps)
            {
                var normalized = AuditFiles.NormalizeArtifactPath(step.Id);
                Require(normalized == step.Id && !normalized.Contains(Path.DirectorySeparatorChar), "Step IDs must be safe single path components.");
                Require(profile.Pages.Count(page => page.Id == step.PageId) == 1, "Every step must name a declared page.");
                Require(step.Kind is "navigate" or "key" or "scroll" or "expand-collapse" or "invoke" or "assert" or "scan",
                    "The fixed route contains an unsupported action kind.");
                if (step.Kind == "key") _ = ReviewedKey(step.Keys);
                foreach (var expectation in step.Expectations)
                    ValidateExpectation(expectation);
            }
        }

        private void RequireSourcesUnchanged()
        {
            var actual = JourneyInput.CaptureSources(profile.Project);
            Require(actual.Count == round.Sources.Count && actual.All(stamp =>
                round.Sources.Count(original => original.RelativePath == stamp.RelativePath && original.Sha256 == stamp.Sha256) == 1),
                "Source files changed during the recorded round; its source binding is no longer valid.");
        }

        private string NewDirectory(string relative)
        {
            var normalized = AuditFiles.NormalizeArtifactPath(relative);
            MakeParents(round.Directory, normalized);
            return AuditFiles.CreateNewDirectory(AuditFiles.ArtifactDestination(round.Directory, normalized));
        }

        private static void MakeParents(string root, string relative)
        {
            var parent = root;
            foreach (var part in AuditFiles.NormalizeArtifactPath(relative).Split(Path.DirectorySeparatorChar)[..^1])
            {
                parent = Path.Combine(parent, part);
                if (!Directory.Exists(parent)) AuditFiles.CreateNewDirectory(parent);
                AuditFiles.EnsureDirectory(parent);
            }
        }

        private void SaveProgress()
        {
            var staged = Path.Combine(round.Directory, "actions-progress.pending.json");
            var destination = Path.Combine(round.Directory, "actions-progress.json");
            AuditFiles.WriteNew(staged, round.Actions);
            try { File.Move(staged, destination, overwrite: true); }
            finally { if (File.Exists(staged)) File.Delete(staged); }
        }

        private void Emit(string stage, string? step = null, string? message = null) =>
            owner.progress?.Invoke(new(round.Id, stage, step, round.Target, message));

        private AuditOptions Options(int? processId = null) =>
            new(processId, processId is null ? target.WindowHandle : null, "winapp", round.Directory,
                profile.TreeDepth, profile.MaxElements, checked(profile.TimeoutSeconds * 1000), 0, 0, false, owner.screenshot);

        private async Task<JsonElement> UiAsync(IReadOnlyList<string> arguments, CancellationToken token, bool input = false)
        {
            if (input)
            {
                var page = await InspectAsync(token);
                Require(currentPage is not null, "Input requires an observed current declared page.");
                RequirePage(page, currentPage!);
            }
            var result = await ExecuteUiAsync(target, arguments, TimeSpan.FromSeconds(profile.TimeoutSeconds), token, input);
            var json = AuditJson.Object(result, arguments[0]);
            Require(result.ExitCode == 0 && !json.EnumerateObject().Any(property =>
                property.Name.Equals("error", StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind != JsonValueKind.Null),
                $"Native '{arguments[0]}' failed (exit {result.ExitCode}); its stdout/stderr were retained.");
            return json;
        }

        private async Task<CommandResult> ExecuteUiAsync(
            AuditTarget requested, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token, bool input = false, bool audit = false)
        {
            CheckCommand(requested, token, input, audit);
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, monitor.StopToken);
            bounded.CancelAfter(TimeSpan.FromSeconds(profile.TimeoutSeconds));
            try
            {
                var result = await inner.UiAsync(requested, arguments,
                    TimeSpan.FromSeconds(Math.Min(timeout.TotalSeconds, profile.TimeoutSeconds)), bounded.Token);
                RecordResponse(arguments[0], arguments, result.Stdout, result.Stderr);
                return result;
            }
            catch (OperationCanceledException ex) when (!token.IsCancellationRequested
                && bounded.IsCancellationRequested && monitor.Violation is null)
            {
                throw new TimeoutException($"Native '{arguments[0]}' exceeded the {profile.TimeoutSeconds}-second command timeout.", ex);
            }
            finally { monitor.Check(requested); }
        }

        private void CheckCommand(AuditTarget requested, CancellationToken token, bool input = false, bool audit = false)
        {
            token.ThrowIfCancellationRequested();
            monitor.Check(requested, input, audit);
            Require(++commandCount <= 128 + profile.Steps.Count * 100, "The fixed route exhausted its bounded native-command budget.");
        }

        private void RecordResponse(string operation, IReadOnlyList<string> arguments, string stdout, string stderr)
        {
            Require(Encoding.UTF8.GetByteCount(stdout) <= AuditFiles.MaxJsonBytes && Encoding.UTF8.GetByteCount(stderr) <= AuditFiles.MaxJsonBytes,
                "Native command output exceeded the evidence size limit.");
            var prefix = Path.Combine(commandDirectory, $"{++commandSequence:D4}-{operation}");
            var response = prefix + ".json";
            AuditFiles.WriteNewBytes(response, Encoding.UTF8.GetBytes(stdout));
            string? errors = null;
            if (stderr.Length > 0)
            {
                errors = prefix + ".stderr.txt";
                AuditFiles.WriteNewBytes(errors, Encoding.UTF8.GetBytes(stderr));
            }
            action?.Commands.Add(new(operation, arguments.ToArray(), response, errors, AuditFiles.Hash(response),
                errors is null ? null : AuditFiles.Hash(errors)));
            if (action is not null) SaveProgress();
        }

        private static string Error(Exception ex) => ex is OperationCanceledException ? "Execution cancelled; completed evidence was retained." : ex.Message;
        private static string JoinError(string? original, string next) => original is null ? next : original + Environment.NewLine + next;
        private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);
        private static bool Near(double left, double right) => Math.Abs(left - right) <= 0.1;
        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidDataException(message);
        }
    }
}
