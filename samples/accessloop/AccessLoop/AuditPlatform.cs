using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AccessLoop;

public interface IAuditPlatform
{
    Task<string> VersionAsync(CancellationToken token);
    Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token);
    void VerifyTarget(AuditTarget target);
    Task<CommandResult> UiAsync(AuditTarget target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token);
    Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token);
}

public sealed class NativeAuditPlatform(string winapp) : IAuditPlatform
{
    public async Task<string> VersionAsync(CancellationToken token)
    {
        var result = await ProcessCommand.RunAsync(winapp, ["--version"], Environment.CurrentDirectory,
            TimeSpan.FromSeconds(20), token);
        var match = Regex.Match(result.Stdout, @"\d+\.\d+\.\d+");
        if (result.ExitCode != 0 || !match.Success || !Version.TryParse(match.Value, out var version)
            || version < new Version(0, 6, 0))
            throw new InvalidOperationException("Use --winapp with a current native build containing ui audit-snapshot and ui audit-observe.");
        return result.Stdout.Trim();
    }

    public async Task<AuditTarget> AttachAsync(AuditOptions options, CancellationToken token)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Running-window audits require Windows.");
        IReadOnlyList<string> args = options.WindowHandle is { } window
            ? ["ui", "status", "-w", window.ToString(CultureInfo.InvariantCulture), "--json"]
            : ["ui", "status", "-a", options.App ?? options.ProcessId!.Value.ToString(CultureInfo.InvariantCulture), "--json"];
        var result = await ProcessCommand.RunAsync(winapp, args, Environment.CurrentDirectory,
            TimeSpan.FromMilliseconds(options.TimeoutMs), token);
        var root = WinappLauncher.ParseResult(result, "status");
        var resolvedPid = AuditJson.Integer(root, "processId");
        var hwnd = AuditJson.Long(root, "hwnd");
        if (resolvedPid <= 0 || hwnd <= 0 || (options.ProcessId is { } requestedPid && requestedPid != resolvedPid)
            || (options.WindowHandle is { } requestedWindow && requestedWindow != hwnd))
            throw new InvalidDataException("The resolved process/window does not match the requested audit target.");
        using var process = Process.GetProcessById(resolvedPid);
        var target = new AuditTarget(resolvedPid, hwnd, process.ProcessName,
            process.StartTime.ToUniversalTime(), AuditJson.OptionalString(root, "windowTitle"), null);
        VerifyTarget(target);
        return target;
    }

    public void VerifyTarget(AuditTarget target) => AuditWindowIdentity.Verify(target);

    public async Task<CommandResult> UiAsync(
        AuditTarget target, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken token)
    {
        VerifyTarget(target);
        var command = new List<string> { "ui" };
        command.AddRange(arguments);
        command.AddRange(["--window", target.WindowHandle.ToString(CultureInfo.InvariantCulture), "--json"]);
        var result = await ProcessCommand.RunAsync(winapp, command, Environment.CurrentDirectory, timeout, token);
        VerifyTarget(target);
        return result;
    }

    public async Task<AccessibilityScan> AxeAsync(AuditTarget target, TimeSpan timeout, CancellationToken token)
    {
        VerifyTarget(target);
        AuditWindowIdentity.RequireIsolatedAxeWindow(target);
        var assembly = typeof(Program).Assembly.Location;
        if (string.IsNullOrEmpty(assembly))
            throw new InvalidOperationException("Axe isolation requires the AccessLoop assembly on disk, not a single-file bundle.");
        var appHost = Path.ChangeExtension(assembly, ".exe");
        var executable = File.Exists(appHost) ? appHost : "dotnet";
        var arguments = new List<string>();
        if (executable == "dotnet")
            arguments.Add(assembly);
        arguments.AddRange(["_axe-worker", target.ProcessId.ToString(CultureInfo.InvariantCulture),
            target.WindowHandle.ToString(CultureInfo.InvariantCulture),
            target.ProcessStartedAtUtc.UtcTicks.ToString(CultureInfo.InvariantCulture)]);
        var result = await ProcessCommand.RunAsync(executable, arguments, Environment.CurrentDirectory, timeout, token);
        VerifyTarget(target);
        AuditWindowIdentity.RequireIsolatedAxeWindow(target);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Axe worker failed ({result.ExitCode}): {result.Stderr.Trim()}");
        return JsonSerializer.Deserialize<AccessibilityScan>(result.Stdout, Json.Options)
            ?? throw new InvalidDataException("The Axe worker returned an empty document.");
    }

    public static async Task<int> RunAxeWorkerAsync(IReadOnlyList<string> args, CancellationToken token)
    {
        if (args.Count != 4
            || !int.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0
            || !long.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var hwnd) || hwnd <= 0
            || !long.TryParse(args[3], NumberStyles.None, CultureInfo.InvariantCulture, out var ticks))
            throw new ArgumentException("Invalid internal Axe worker target.");
        using var process = Process.GetProcessById(pid);
        var target = new AuditTarget(pid, hwnd, process.ProcessName, new DateTimeOffset(ticks, TimeSpan.Zero), null, null);
        AuditWindowIdentity.Verify(target);
        AuditWindowIdentity.RequireIsolatedAxeWindow(target);
        var scan = await new AxeWindowsScanner().ScanAsync(pid, hwnd, token);
        AuditWindowIdentity.Verify(target);
        AuditWindowIdentity.RequireIsolatedAxeWindow(target);
        Console.WriteLine(JsonSerializer.Serialize(AuditPrivacy.Redact(scan), Json.Options));
        return 0;
    }
}

public static class AuditWindowIdentity
{
    public static void RequireIsolatedAxeWindow(AuditTarget target)
    {
        Verify(target);
        var visited = new HashSet<nint>();
        for (var candidate = GetTopWindow(0); candidate != 0; candidate = GetWindow(candidate, 2))
        {
            if (!visited.Add(candidate) || visited.Count > 10000)
                throw new InvalidOperationException("The native window list changed or exceeded the Axe scope-check budget.");
            if (candidate == (nint)target.WindowHandle || !IsWindowVisible(candidate))
                continue;
            if (HasOwner(candidate, (nint)target.WindowHandle, hwnd => GetWindow(hwnd, 4)))
                throw new InvalidOperationException(
                    "Axe.Windows can traverse visible owned windows beneath this window. " +
                    "To preserve exact-window scope, choose the dialog's HWND or close its owned windows before scanning. " +
                    "No Axe result for the wider subtree is accepted.");
        }
    }

    internal static bool HasOwner(nint window, nint expectedOwner, Func<nint, nint> ownerOf)
    {
        var seen = new HashSet<nint>();
        for (var owner = ownerOf(window); owner != 0; owner = ownerOf(owner))
        {
            if (owner == expectedOwner)
                return true;
            if (!seen.Add(owner) || seen.Count > 64)
                throw new InvalidOperationException("The native window ownership chain could not be bounded.");
        }
        return false;
    }

    public static void Verify(AuditTarget target)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Window identity checks require Windows.");
        var hwnd = checked((nint)target.WindowHandle);
        if (target.ProcessId <= 0 || hwnd == 0 || !IsWindow(hwnd) || GetAncestor(hwnd, 2) != hwnd)
            throw new InvalidOperationException("The exact top-level audit window no longer exists.");
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != target.ProcessId)
            throw new InvalidOperationException("The audit window belongs to a different process.");
        using var process = Process.GetProcessById(target.ProcessId);
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != target.ProcessStartedAtUtc.UtcTicks
            || !string.Equals(process.ProcessName, target.ProcessName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The audit process exited or its identity changed.");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);
}

public static class AuditPrivacy
{
    private static readonly HashSet<string> AllowedProperties = new(StringComparer.Ordinal)
    {
        "RuntimeId", "BoundingRectangle", "ProcessId", "ControlType", "LocalizedControlType",
        "HasKeyboardFocus", "IsKeyboardFocusable", "IsEnabled", "AutomationId", "ClassName",
        "Culture", "IsControlElement", "IsContentElement", "IsPassword", "NativeWindowHandle",
        "IsOffscreen", "Orientation", "FrameworkId", "IsRequiredForForm", "IsDataValidForForm",
        "LiveSetting", "IsPeripheral", "HeadingLevel", "IsDialog", "LogicalSize", "ClickablePoint"
    };

    public static AccessibilityScan Redact(AccessibilityScan scan) => scan with
    {
        Findings = scan.Findings.Select(finding =>
        {
            var knownNonPassword = string.Equals(finding.ElementProperties.GetValueOrDefault("IsPassword"),
                "False", StringComparison.OrdinalIgnoreCase);
            var properties = finding.ElementProperties
                .Where(pair => AllowedProperties.Contains(pair.Key) || (knownNonPassword && pair.Key == "Name"))
                .ToDictionary(pair => pair.Key,
                    pair => pair.Value.Length <= 2048 ? pair.Value : pair.Value[..2048] + " [truncated]",
                    StringComparer.Ordinal);
            return finding with { ElementProperties = properties };
        }).ToArray()
    };
}

public static class AuditJson
{
    public static string String(JsonElement value, string name)
    {
        var result = OptionalString(value, name);
        return result ?? throw new InvalidDataException($"Missing string property '{name}' in native audit output.");
    }

    public static string? OptionalString(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        return property.ValueKind == JsonValueKind.String ? property.GetString()
            : throw new InvalidDataException($"Expected string property '{name}' in native audit output.");
    }

    public static long Long(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.TryGetInt64(out var result) ? result
            : throw new InvalidDataException($"Missing integer property '{name}' in native audit output.");

    public static int Integer(JsonElement value, string name) => checked((int)Long(value, name));

    public static bool? Boolean(JsonElement value, string name)
    {
        if (!value.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null)
            return null;
        return property.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException($"Expected boolean property '{name}' in native audit output.")
        };
    }

    public static JsonElement Array(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.Array ? property
            : throw new InvalidDataException($"Missing array property '{name}' in native audit output.");

    public static JsonElement Object(CommandResult result, string operation)
    {
        try
        {
            using var document = JsonDocument.Parse(result.Stdout);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"{operation} did not return an object.");
            return document.RootElement.Clone();
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{operation} did not return its audit JSON document (exit {result.ExitCode}). " +
                $"Use the current native winapp build. {result.Stderr.Trim()}", ex);
        }
    }
}
