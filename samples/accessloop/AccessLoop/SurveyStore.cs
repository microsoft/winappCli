using System.Security.Cryptography;
using System.Text.Json;

namespace AccessLoop;

public sealed class SurveyStore
{
    public string Root { get; }
    private const string StateFile = "survey-state.json";
    private const string RequestFile = "survey-request.json";

    public SurveyStore(string root)
    {
        Root = Path.GetFullPath(root);
        AuditFiles.EnsureDirectory(Root);
    }

    public IDisposable Lock()
    {
        var path = Path.Combine(Root, ".survey-lock");
        if (File.Exists(path))
            _ = AuditFiles.ResolveArtifact(Root, ".survey-lock");
        try
        {
            return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        }
        catch (IOException exception)
        {
            throw new IOException("This survey is already running or its lock cannot be acquired. Do not run two navigators against the same job.", exception);
        }
    }

    public void Initialize(SurveyProfileFile profile, SurveyState state)
    {
        AuditFiles.WriteNewBytes(Path.Combine(Root, "profile.json"), profile.Bytes);
        AuditFiles.WriteNew(Path.Combine(Root, RequestFile), Request(state));
        Save(state);
    }

    public void FreezeInventory(SurveyState state, SurveyInventory inventory, SurveyProfile profile)
    {
        ValidateInventory(inventory, profile);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(inventory, Json.Options);
        AuditFiles.WriteNewBytes(Path.Combine(Root, "inventory.json"), bytes);
        state.Inventory = inventory;
        state.InventorySha256 = Convert.ToHexString(SHA256.HashData(bytes));
        state.OriginalNavigation = inventory.OriginalNavigation;
        state.Pages.AddRange(inventory.Pages.Select(page => new SurveyPageState { Page = page }));
        Save(state);
    }

    public (SurveyState State, SurveyProfileFile Profile) Load()
    {
        using var json = AuditFiles.ParseStrict(AuditFiles.ReadBytes(Path.Combine(Root, StateFile), 8 * 1024 * 1024));
        AuditFiles.RequireMembers(json.RootElement, "survey state", "schemaVersion", "runId", "name", "startedAtUtc",
            "target", "profileSha256", "tools", "context", "screenshot", "status", "revision", "pages", "invocations");
        var state = AuditFiles.Deserialize<SurveyState>(json.RootElement);
        var profile = SurveyProfileFile.Read(AuditFiles.ResolveArtifact(Root, "profile.json"));
        if (profile.Sha256 != state.ProfileSha256)
            throw new InvalidDataException("The frozen navigation profile changed; a new survey is required.");
        using var request = AuditFiles.ParseStrict(AuditFiles.ReadBytes(AuditFiles.ResolveArtifact(Root, RequestFile), 1024 * 1024));
        if (!JsonElement.DeepEquals(request.RootElement, JsonSerializer.SerializeToElement(Request(state), Json.Options)))
            throw new InvalidDataException("The immutable survey request and mutable state disagree.");
        ValidateState(state, profile.Profile);
        if (state.Inventory is not null)
        {
            var bytes = AuditFiles.ReadBytes(AuditFiles.ResolveArtifact(Root, "inventory.json"), 1024 * 1024);
            if (Convert.ToHexString(SHA256.HashData(bytes)) != state.InventorySha256)
                throw new InvalidDataException("The frozen navigation inventory changed.");
            using var inventory = AuditFiles.ParseStrict(bytes);
            if (!JsonElement.DeepEquals(inventory.RootElement, JsonSerializer.SerializeToElement(state.Inventory, Json.Options)))
                throw new InvalidDataException("The saved page inventory was redefined in run state.");
        }
        foreach (var page in state.Pages)
        {
            foreach (var attempt in page.Attempts.Where(attempt => attempt.ScanSha256 is not null))
            {
                var scanPath = AuditFiles.ResolveArtifact(Root, Path.Combine(attempt.Directory, "scan.json"));
                var scan = AuditFiles.ReadVerified(scanPath);
                if (AuditFiles.Hash(scanPath) != attempt.ScanSha256 || scan.Target.ProcessId != state.Target.ProcessId
                    || scan.Target.WindowHandle != state.Target.WindowHandle
                    || scan.Target.ProcessStartedAtUtc != state.Target.ProcessStartedAtUtc
                    || scan.RuleFailureCount != attempt.RuleFailures || scan.Status != attempt.CaptureStatus)
                    throw new InvalidDataException($"Saved evidence changed or disagrees with its page attempt: {page.Page.Id}.");
            }
        }
        return (state, profile);
    }

    public string CreateAttempt(SurveyPageState page, int pageIndex, out SurveyAttempt attempt)
    {
        if (page.Attempts.Count >= 20)
            throw new InvalidDataException("A page has reached the 20-attempt history limit; start a new survey.");
        var relative = Path.Combine("pages", $"{pageIndex + 1:D3}", $"attempt-{page.Attempts.Count + 1:D2}");
        var full = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        AuditFiles.EnsureDirectory(Path.GetDirectoryName(full)!);
        AuditFiles.CreateNewDirectory(full);
        attempt = new() { Number = page.Attempts.Count + 1, Directory = relative, StartedAtUtc = DateTimeOffset.UtcNow };
        page.Attempts.Add(attempt);
        page.Status = "running";
        return full;
    }

    public void Save(SurveyState state, bool render = true)
    {
        state.Revision++;
        AtomicJson(Path.Combine(Root, StateFile), state);
        if (!render)
            return;
        var temporary = Path.Combine(Root, $"survey-report-{Guid.NewGuid():N}.html");
        try
        {
            SurveyReportRenderer.Write(temporary, state);
            File.Move(temporary, Path.Combine(Root, "survey-report.html"), overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static void AtomicJson<T>(string path, T value)
    {
        AuditFiles.EnsureDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path))
            _ = AuditFiles.ResolveArtifact(Path.GetDirectoryName(path)!, Path.GetFileName(path));
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            AuditFiles.WriteNew(temporary, value);
            File.Move(temporary, path, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    private static object Request(SurveyState state) => new
    {
        state.SchemaVersion, state.RunId, state.Name, state.StartedAtUtc, state.Target,
        state.ProfileSha256, state.Tools, state.Context, state.Screenshot,
        targetMode = "existing-app",
        effects = "Only profiled navigation and audit evidence; no source changes, application restart or settings-value operations."
    };

    public static void ValidateInventory(SurveyInventory inventory, SurveyProfile profile)
    {
        if (inventory.Pages is null || inventory.Pages.Count is < 1 || inventory.Pages.Count > profile.MaxPages
            || inventory.Pages.Select(page => page.Id).Distinct(StringComparer.Ordinal).Count() != inventory.Pages.Count)
            throw new InvalidDataException("Inventory pages must be nonempty, bounded and uniquely identified.");
        SurveyProfileFile.ValidateIds(inventory.NavigationIds, "inventory navigation IDs", 200);
        if (inventory.Excluded is null || inventory.Excluded.Count > 200 || inventory.OriginalNavigation is null)
            throw new InvalidDataException("Inventory exclusions/restoration data are missing or unbounded.");
        ValidateNavigation(inventory.OriginalNavigation, profile);
        foreach (var page in inventory.Pages)
        {
            SurveyProfileFile.Id(page.Id, "page ID");
            AuditFiles.Text(page.Name, "page name", 256);
            SurveyProfileFile.ValidateIds(page.GroupIds, "page group ancestry", 20);
            if (!inventory.NavigationIds.Contains(page.Id, StringComparer.Ordinal)
                || profile.ExcludedIds.Contains(page.Id, StringComparer.Ordinal) || profile.GroupIds.Contains(page.Id, StringComparer.Ordinal)
                || page.GroupIds.Any(group => !profile.GroupIds.Contains(group, StringComparer.Ordinal)))
                throw new InvalidDataException($"Page '{page.Id}' is outside the approved navigation profile.");
        }
        if (inventory.OriginalNavigation.SelectedPageId is { } selected
            && !inventory.Pages.Any(page => page.Id == selected))
            throw new InvalidDataException("The original selected page is outside the frozen internal inventory.");
    }

    private static void ValidateState(SurveyState state, SurveyProfile profile)
    {
        if (state.SchemaVersion != 1 || state.Revision < 0 || state.StartedAtUtc == default
            || state.Status is not ("created" or "discovering" or "running" or "paused" or "finished")
            || state.Target is null || state.Target.ProcessId <= 0 || state.Target.WindowHandle <= 0
            || state.Target.ProcessName != profile.ProcessName || state.Tools is null || state.Context is null)
            throw new InvalidDataException("Invalid survey state or target identity.");
        AuditFiles.Text(state.RunId, "survey run ID", 128);
        AuditFiles.ValidateHash(state.ProfileSha256, "profile hash");
        AuditFiles.ValidateHash(state.Tools.NativeSha256, "native tool hash");
        AuditFiles.ValidateHash(state.Tools.RunnerSha256, "runner hash");
        if (state.Pages is null || state.Pages.Count > profile.MaxPages || state.Invocations is null || state.Invocations.Count > 200)
            throw new InvalidDataException("Survey page or invocation history is missing or unbounded.");
        foreach (var (invocation, index) in state.Invocations.Select((invocation, index) => (invocation, index)))
        {
            if (invocation is null || invocation.Number != index + 1 || invocation.StartedAtUtc == default
                || invocation.FinishedAtUtc < invocation.StartedAtUtc || invocation.Status is not ("running" or "paused" or "finished"))
                throw new InvalidDataException("Invalid survey invocation history.");
            SurveyProfileFile.ValidateIds(invocation.RetryPages, "retry pages", 100);
            if (invocation.Resuming && !invocation.OperatorConfirmedUnchangedState)
                throw new InvalidDataException("Resumed invocations must preserve the operator's unchanged-state confirmation.");
        }
        if (state.OriginalNavigation is not null)
            ValidateNavigation(state.OriginalNavigation, profile);
        if (state.Inventory is not null)
        {
            ValidateInventory(state.Inventory, profile);
            if (state.Pages.Count != state.Inventory.Pages.Count)
                throw new InvalidDataException("A page was removed from the frozen inventory.");
            if (!JsonElement.DeepEquals(JsonSerializer.SerializeToElement(state.OriginalNavigation, Json.Options),
                JsonSerializer.SerializeToElement(state.Inventory.OriginalNavigation, Json.Options)))
                throw new InvalidDataException("Original navigation restoration data changed after the inventory was frozen.");
        }
        else if (state.Pages.Count > 0)
            throw new InvalidDataException("Pages cannot exist without a frozen navigation inventory.");
        if (state.Status == "finished" && (state.Inventory is null || state.Pages.Count == 0 || state.Pages.Any(page => page.Status != "captured")))
            throw new InvalidDataException("A finished survey must retain a captured latest attempt for every frozen page.");
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (page, index) in state.Pages.Select((page, index) => (page, index)))
        {
            if (page is null || page.Page is null || !SamePage(page.Page, state.Inventory!.Pages[index])
                || page.Status is not ("pending" or "running" or "captured" or "blocked")
                || page.Attempts is null || page.Attempts.Count > 20)
                throw new InvalidDataException("Page state or ordering differs from the frozen inventory.");
            foreach (var (attempt, attemptIndex) in page.Attempts.Select((attempt, attemptIndex) => (attempt, attemptIndex)))
            {
                var expected = Path.Combine("pages", $"{index + 1:D3}", $"attempt-{attemptIndex + 1:D2}");
                if (attempt is null || attempt.Number != attemptIndex + 1
                    || AuditFiles.NormalizeArtifactPath(attempt.Directory) != expected || !directories.Add(expected)
                    || attempt.StartedAtUtc == default || attempt.FinishedAtUtc < attempt.StartedAtUtc
                    || attempt.Status is not ("running" or "captured" or "blocked" or "interrupted"))
                    throw new InvalidDataException("Invalid or unsafe page attempt history.");
                if (attempt.Status == "captured" && (attempt.ScanSha256 is null || !Ready(profile, page.Page, attempt.Before)
                    || !Ready(profile, page.Page, attempt.After) || attempt.FinishedAtUtc is null))
                    throw new InvalidDataException("A captured page requires recorded pre/post navigation and heading evidence.");
                if (attempt.ScanSha256 is not null)
                    AuditFiles.ValidateHash(attempt.ScanSha256, "page scan hash");
            }
            if (page.Status == "captured" && page.Attempts.LastOrDefault()?.Status != "captured")
                throw new InvalidDataException("Page completion disagrees with its latest attempt.");
        }
    }

    internal static bool Ready(SurveyProfile profile, SurveyPageDefinition page, SurveyPageReady? ready) =>
        ready is { Selected: true, HeadingMatched: true } && SurveyProfileFile.MatchesHeading(profile, page, ready.Heading)
        && (ready.EvidenceKind == "automation-id-and-name"
            || (ready.EvidenceKind == "visible-name-outside-navigation"
                && profile.HeadingWithoutIdPageIds.Contains(page.Id, StringComparer.Ordinal)));

    internal static bool SamePage(SurveyPageDefinition left, SurveyPageDefinition right) =>
        left.Id == right.Id && left.Name == right.Name && left.GroupIds.SequenceEqual(right.GroupIds, StringComparer.Ordinal);

    private static void ValidateNavigation(SurveyNavigationState navigation, SurveyProfile profile)
    {
        if (navigation.ExpandedGroups is null || navigation.ExpandedGroups.Count != profile.GroupIds.Count
            || navigation.ExpandedGroups.Keys.Any(id => !profile.GroupIds.Contains(id, StringComparer.Ordinal)))
            throw new InvalidDataException("Original navigation group states do not match the profile.");
        if (navigation.SelectedPageId is not null)
            SurveyProfileFile.Id(navigation.SelectedPageId, "original selection");
    }
}
