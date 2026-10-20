using System.Globalization;
using System.Text.Json;

namespace AccessLoop;

internal interface ITabNavigationClient
{
    Task<TabNavigationSnapshot> ReadAsync(CancellationToken token, TabSelection? selection = null, string? rootRuntimeId = null);
}

internal sealed class TabNavigationClient(
    IAuditPlatform platform, AuditTarget target, AuditOptions options, string output,
    List<AuditArtifact> artifacts) : ITabNavigationClient
{
    private int commandCount;
    private string? rootRuntimeId;

    public async Task<TabNavigationSnapshot> ReadAsync(
        CancellationToken token, TabSelection? selection = null, string? rootRuntimeId = null)
    {
        token.ThrowIfCancellationRequested();
        if (++commandCount > 32 + options.MaxPages * 64)
            throw new InvalidOperationException("The bounded tab-navigation command budget was exhausted.");
        var arguments = new List<string>
        {
            "audit-tabs", "--depth", Number(options.Depth), "--max-elements", Number(options.MaxElements),
            "--timeout", Number(options.TimeoutMs)
        };
        if (selection is not null)
        {
            if (options.Navigation != "standard-tabs" || !options.AcceptNavigation || string.IsNullOrEmpty(rootRuntimeId))
                throw new InvalidOperationException("Exact tab selection requires navigation approval and an observed root identity.");
            arguments.AddRange(["--select", selection.ItemRuntimeId, "--container", selection.ContainerRuntimeId,
                "--root-runtime-id", rootRuntimeId, "--accept-navigation"]);
        }
        var response = await platform.UiAsync(target, arguments, TimeSpan.FromMilliseconds(options.TimeoutMs + 5000), token);
        var document = AuditJson.Object(response, "ui audit-tabs");
        TabNavigationSnapshot snapshot;
        try
        {
            snapshot = document.Deserialize<TabNavigationSnapshot>(Json.Options)
                ?? throw new InvalidDataException("Tab discovery returned an empty document.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Tab discovery did not return the required typed navigation fields. " +
                "Use winapp.exe and the accessibility directory from the same bundle. " + ex.Message, ex);
        }
        Validate(snapshot, target, options);
        if (snapshot.Status == "captured" && response.ExitCode != 0)
            throw new InvalidDataException("Tab discovery exit code contradicts its captured status.");
        if (this.rootRuntimeId is not null && snapshot.RootRuntimeId != this.rootRuntimeId)
            throw new InvalidOperationException("The original UI Automation root changed; tab navigation stopped.");
        this.rootRuntimeId ??= snapshot.RootRuntimeId;
        if (selection is null && snapshot.Selection is not null)
            throw new InvalidDataException("Read-only tab discovery unexpectedly returned a selection action.");
        if (selection is not null && (snapshot.Selection is not { } receipt
            || receipt.ContainerRuntimeId != selection.ContainerRuntimeId || receipt.ItemRuntimeId != selection.ItemRuntimeId
            || receipt.Status is not ("selected" or "already-selected" or "blocked")))
            throw new InvalidDataException("The exact tab-selection receipt does not match the approved action.");

        var directory = Path.Combine(output, "navigation");
        Directory.CreateDirectory(directory);
        var relative = Path.Combine("navigation", $"{commandCount:D4}.json");
        AuditFiles.WriteNew(Path.Combine(output, relative), new
        {
            observedAtUtc = DateTimeOffset.UtcNow,
            command = arguments,
            exitCode = response.ExitCode,
            result = snapshot
        });
        artifacts.Add(AuditFiles.Describe(output, relative, "application/json", "WinAppCLI exact tab discovery/selection"));
        if (selection is not null && snapshot.Selection!.Status == "blocked")
            throw new InvalidOperationException("Exact tab selection was blocked. " + string.Join(" ", snapshot.Issues));
        return snapshot;
    }

    internal static void Validate(TabNavigationSnapshot snapshot, AuditTarget target, AuditOptions options)
    {
        if (snapshot.SchemaVersion != 1 || snapshot.ProcessId != target.ProcessId || snapshot.WindowHandle != target.WindowHandle
            || string.IsNullOrWhiteSpace(snapshot.RootRuntimeId) || snapshot.RootRuntimeId.Length > 2048
            || snapshot.MaxDepth != options.Depth || snapshot.MaxElements != options.MaxElements
            || snapshot.Status is not ("captured" or "partial" or "blocked")
            || snapshot.Containers is null || snapshot.Issues is null
            || snapshot.Containers.Count > options.MaxElements || snapshot.Issues.Count > options.MaxElements + 32)
            throw new InvalidDataException("Tab discovery target, schema or bounds do not match the requested scan.");
        if (snapshot.Status == "captured")
        {
            AuditFiles.ValidateHash(snapshot.TreeSignature, "tab structure signature");
            if (snapshot.DepthLimitReached || snapshot.ElementLimitReached)
                throw new InvalidDataException("Truncated tab discovery cannot be reported as captured.");
        }
        var identities = new HashSet<string>(StringComparer.Ordinal);
        foreach (var container in snapshot.Containers)
        {
            RequireIdentity(container.RuntimeId);
            if (container.Items is null || container.Items.Count > options.MaxElements
                || container.AutomationId?.Length > 2048 || container.Reason?.Length > 4096)
                throw new InvalidDataException("Invalid tab-container metadata.");
            foreach (var item in container.Items)
            {
                RequireIdentity(item.RuntimeId);
                if (item.AutomationId?.Length > 2048 || item.Reason?.Length > 4096)
                    throw new InvalidDataException("Invalid tab-item metadata.");
            }
        }
        if (identities.Count > options.MaxElements)
            throw new InvalidDataException("Tab discovery exceeded its element budget.");
        var byId = snapshot.Containers.ToDictionary(group => group.RuntimeId, StringComparer.Ordinal);
        foreach (var container in snapshot.Containers)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal) { container.RuntimeId };
            for (var parent = container.ParentContainerRuntimeId; parent is not null;)
            {
                if (!visited.Add(parent) || !byId.TryGetValue(parent, out var ancestor))
                    throw new InvalidDataException("Tab-container ancestry is missing or cyclic.");
                parent = ancestor.ParentContainerRuntimeId;
            }
        }

        void RequireIdentity(string identity)
        {
            if (string.IsNullOrWhiteSpace(identity) || identity.Length > 2048 || !identities.Add(identity))
                throw new InvalidDataException("Tab discovery returned a missing or ambiguous runtime identity.");
        }
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
