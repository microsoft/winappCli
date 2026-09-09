// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace WinApp.Cli.Helpers;

/// <summary>
/// One capability to declare in an appxmanifest, resolved to the exact element and namespace it must
/// use.
/// </summary>
/// <param name="Name">The capability name written to the <c>Name</c> attribute.</param>
/// <param name="ElementName"><c>Capability</c> or <c>DeviceCapability</c> — they are different elements, not a namespace variation.</param>
/// <param name="Prefix">The XML namespace prefix, or <see langword="null"/> for the default (foundation) namespace.</param>
/// <param name="Namespace">The namespace URI the element is emitted in.</param>
/// <param name="MinimumMaxVersionTested">The lowest <c>TargetDeviceFamily/@MaxVersionTested</c> this capability is honored at, or <see langword="null"/> when it has no floor.</param>
internal sealed record AppxCapability(
    string Name,
    string ElementName,
    string? Prefix,
    XNamespace Namespace,
    string? MinimumMaxVersionTested = null)
{
    /// <summary>True for <c>DeviceCapability</c>, which the schema requires to come after every <c>Capability</c>.</summary>
    public bool IsDeviceCapability => ElementName == "DeviceCapability";
}

/// <summary>
/// Maps capability names onto the element and XML namespace an appxmanifest requires for them.
/// </summary>
/// <remarks>
/// <para>
/// Capabilities do NOT all live in one place: they span <c>Capability</c> in the foundation namespace
/// (a closed set of five), <c>uap*:Capability</c> across several revisions, <c>rescap:Capability</c>,
/// <c>systemai:Capability</c>, and the separate <c>DeviceCapability</c> element. Emitting a name in the
/// wrong one produces a manifest Windows rejects, so a name is only auto-mapped when its namespace is
/// documented; anything else must be written with an explicit prefix.
/// </para>
/// <para>
/// The table is deliberately conservative rather than exhaustive. A missing name costs the user a
/// prefix (<c>rescap:someName</c>) and is reported as such; a WRONG name would silently produce an
/// invalid manifest, so uncertain entries are left out on purpose.
/// </para>
/// </remarks>
internal static partial class AppxCapabilityCatalog
{
    public const string DefaultPrefixToken = "app";
    public const string DeviceElementName = "DeviceCapability";
    public const string CapabilityElementName = "Capability";

    /// <summary>Namespace URIs by prefix, for the prefixes a capability may be declared in.</summary>
    private static readonly Dictionary<string, XNamespace> Namespaces = new(StringComparer.OrdinalIgnoreCase)
    {
        ["uap"] = "http://schemas.microsoft.com/appx/manifest/uap/windows10",
        ["uap2"] = "http://schemas.microsoft.com/appx/manifest/uap/windows10/2",
        ["uap3"] = "http://schemas.microsoft.com/appx/manifest/uap/windows10/3",
        ["uap4"] = "http://schemas.microsoft.com/appx/manifest/uap/windows10/4",
        ["uap6"] = "http://schemas.microsoft.com/appx/manifest/uap/windows10/6",
        ["uap7"] = "http://schemas.microsoft.com/appx/manifest/uap/windows10/7",
        ["uap11"] = "http://schemas.microsoft.com/appx/manifest/uap/windows10/11",
        ["rescap"] = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities",
        ["systemai"] = "http://schemas.microsoft.com/appx/manifest/systemai/windows10",
        ["mobile"] = "http://schemas.microsoft.com/appx/manifest/mobile/windows10",
        ["desktop"] = "http://schemas.microsoft.com/appx/manifest/desktop/windows10",
    };

    private static readonly XNamespace FoundationNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";

    /// <summary>
    /// The foundation <c>&lt;Capability&gt;</c> set, which the AppX schema closes at exactly these names.
    /// </summary>
    /// <remarks>
    /// Declared before <see cref="Known"/> because static field initializers run in textual order and
    /// <see cref="BuildKnown"/> reads this array.
    /// </remarks>
    private static readonly string[] FoundationCapabilityNames =
        ["internetClient", "internetClientServer", "privateNetworkClientServer", "allJoyn", "codeGeneration"];

    /// <summary>
    /// Capability name to its documented element/namespace. Only names whose namespace is stated in
    /// Microsoft's capability documentation appear here.
    /// </summary>
    private static readonly Dictionary<string, AppxCapability> Known = BuildKnown();

    private static Dictionary<string, AppxCapability> BuildKnown()
    {
        var known = new Dictionary<string, AppxCapability>(StringComparer.OrdinalIgnoreCase);

        // General capabilities — the foundation <Capability> set is closed at exactly these five.
        foreach (var name in FoundationCapabilityNames)
        {
            known[name] = new AppxCapability(name, CapabilityElementName, null, FoundationNs);
        }

        // <uap:Capability>
        foreach (var name in new[]
        {
            "documentsLibrary", "picturesLibrary", "videosLibrary", "musicLibrary", "removableStorage",
            "appointments", "contacts", "phoneCall", "userAccountInformation", "voipCall", "objects3D",
            "enterpriseAuthentication", "sharedUserCertificates", "chat", "blockedChatMessages",
        })
        {
            known[name] = Prefixed(name, "uap");
        }

        known["graphicsCapture"] = Prefixed("graphicsCapture", "uap6");
        known["globalMediaControl"] = Prefixed("globalMediaControl", "uap7");
        known["graphicsCaptureWithoutBorder"] = Prefixed("graphicsCaptureWithoutBorder", "uap11");
        known["graphicsCaptureProgrammatic"] = Prefixed("graphicsCaptureProgrammatic", "uap11");

        // <rescap:Capability> — the restricted set is large and grows; only the ones a packaged desktop
        // app routinely needs are mapped, and the rest are reachable as 'rescap:<name>'.
        foreach (var name in new[]
        {
            "runFullTrust", "broadFileSystemAccess", "unvirtualizedResources", "allowElevation", "packageQuery",
        })
        {
            known[name] = Prefixed(name, "rescap");
        }

        // <systemai:Capability> — the Windows AI APIs (Phi Silica and friends). Deliberately NOT rescap,
        // which is the intuitive guess and produces a manifest that does not grant the capability.
        // Requires MaxVersionTested >= 10.0.26226.0 to be honored.
        known["systemAIModels"] = Prefixed("systemAIModels", "systemai") with { MinimumMaxVersionTested = "10.0.26226.0" };

        // <DeviceCapability> — a DIFFERENT element, not a namespaced Capability.
        foreach (var name in new[]
        {
            "location", "microphone", "webcam", "proximity", "bluetooth", "radios", "activity", "optical",
            "pointOfService", "wiFiControl",
        })
        {
            known[name] = new AppxCapability(name, DeviceElementName, null, FoundationNs);
        }

        return known;
    }

    /// <summary>
    /// Device capabilities whose declaration is incomplete without nested <c>Device</c>/<c>Function</c>
    /// children naming the specific hardware.
    /// </summary>
    /// <remarks>
    /// A bare <c>&lt;DeviceCapability Name="usb" /&gt;</c> grants nothing — the device class and function
    /// live in child elements that a flat, comma-separated property cannot carry. Emitting it anyway
    /// produces a manifest that either fails schema validation or registers and silently grants no
    /// access, so these are rejected here and pointed at an authored manifest instead.
    /// </remarks>
    private static readonly string[] CapabilitiesRequiringChildElements =
        ["usb", "humaninterfacedevice", "serialcommunication"];

    private static AppxCapability Prefixed(string name, string prefix) =>
        new(name, CapabilityElementName, prefix, Namespaces[prefix]);

    /// <summary>
    /// Reports whether an explicitly written prefix agrees with the catalogued declaration for that name.
    /// </summary>
    private static bool MatchesCataloguedDeclaration(AppxCapability catalogued, string prefix)
    {
        if (catalogued.IsDeviceCapability)
        {
            return string.Equals(prefix, "device", StringComparison.OrdinalIgnoreCase);
        }

        return catalogued.Prefix is { Length: > 0 } cataloguedPrefix
            ? string.Equals(prefix, cataloguedPrefix, StringComparison.OrdinalIgnoreCase)
            : string.Equals(prefix, DefaultPrefixToken, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Names the element a catalogued capability belongs in, for an error message.</summary>
    private static string DescribeDeclaration(AppxCapability catalogued) => catalogued switch
    {
        { IsDeviceCapability: true } => "<DeviceCapability>",
        { Prefix: { Length: > 0 } prefix } => $"<{prefix}:Capability>",
        _ => "<Capability>",
    };

    /// <summary>The value a user should write to get the catalogued declaration.</summary>
    private static string DescribeUsage(AppxCapability catalogued) => catalogued switch
    {
        { IsDeviceCapability: true } => $"device:{catalogued.Name}",
        { Prefix: { Length: > 0 } prefix } => $"{prefix}:{catalogued.Name}",
        _ => catalogued.Name,
    };

    /// <summary>
    /// Reports whether a capability needs nested child elements this property cannot carry, and if so
    /// produces a message pointing at the authored-manifest escape hatch.
    /// </summary>
    private static bool RequiresChildElements(string name, out string? error)
    {
        if (!CapabilitiesRequiringChildElements.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            error = null;
            return false;
        }

        error = $"The '{name}' capability needs nested <Device> and <Function> elements naming the specific " +
                "hardware, which a comma-separated property cannot express — declaring it alone would grant " +
                "no access. Author a manifest and point at it with '#:property WinAppManifestPath=<path>'.";
        return true;
    }

    /// <summary>
    /// Parses a separated capability list into resolved declarations, preserving order and dropping
    /// duplicates.
    /// </summary>
    /// <param name="value">Names separated by <c>;</c> or <c>,</c>, each either a known bare name or <c>prefix:name</c>.</param>
    /// <param name="error">An actionable message naming the offending entry, when parsing fails.</param>
    /// <returns><see langword="true"/> when every entry resolved.</returns>
    public static bool TryParse(string? value, out IReadOnlyList<AppxCapability> capabilities, out string? error)
    {
        error = null;
        var resolved = new List<AppxCapability>();
        capabilities = resolved;

        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in value.Split([';', ','], StringSplitOptions.RemoveEmptyEntries).Select(static raw => raw.Trim()))
        {
            if (entry.Length == 0)
            {
                continue;
            }

            if (!TryResolve(entry, out var capability, out error))
            {
                capabilities = [];
                return false;
            }

            // Same name twice is harmless intent, but a duplicate element is a schema violation.
            if (seen.Add(capability!.Name))
            {
                resolved.Add(capability);
            }
        }

        return true;
    }

    private static bool TryResolve(string entry, out AppxCapability? capability, out string? error)
    {
        capability = null;
        error = null;

        var separator = entry.IndexOf(':');
        if (separator < 0)
        {
            if (RequiresChildElements(entry, out error))
            {
                return false;
            }

            if (Known.TryGetValue(entry, out var known))
            {
                // Re-key to the catalog's casing so the manifest matches the schema exactly.
                capability = known;
                return true;
            }

            if (!IsSafeName(entry))
            {
                error = $"'{entry}' is not a valid capability name.";
                return false;
            }

            error = $"Unknown capability '{entry}'. winapp only auto-selects the XML namespace for capabilities whose " +
                    "namespace is documented, because emitting one in the wrong namespace produces a manifest Windows " +
                    $"rejects. Qualify it explicitly — for example 'rescap:{entry}', 'uap:{entry}', 'systemai:{entry}', " +
                    $"or 'device:{entry}' for a DeviceCapability.";
            return false;
        }

        var prefix = entry[..separator].Trim();
        var name = entry[(separator + 1)..].Trim();

        if (name.Length == 0 || !IsSafeName(name))
        {
            error = $"'{entry}' is not a valid capability declaration. Expected <prefix>:<name>, for example 'rescap:broadFileSystemAccess'.";
            return false;
        }

        // A catalogued name has exactly one correct element and namespace. Honoring a conflicting prefix
        // would recreate the failure this catalog exists to prevent: 'rescap:systemAIModels' registers
        // successfully and grants nothing, and 'rescap:microphone' emits a Capability where the schema
        // wants a DeviceCapability. Checked before every prefix branch, including 'device:'.
        AppxCapability? catalogued = Known.TryGetValue(name, out var found) ? found : null;
        if (catalogued is not null && !MatchesCataloguedDeclaration(catalogued, prefix))
        {
            error = $"'{entry}' declares the wrong namespace for '{catalogued.Name}', which belongs in " +
                    $"{DescribeDeclaration(catalogued)}. Windows would register the app and silently not grant it, " +
                    $"so declare it as '{DescribeUsage(catalogued)}' instead.";
            return false;
        }

        // The prefix agrees with the catalog, so hand back the catalogued entry itself. Lookup is
        // case-insensitive, so 'rescap:RUNFULLTRUST' and 'device:Microphone' arrive with the caller's
        // spelling, and rebuilding from `name` would carry that spelling into the manifest — where the
        // schema's enumerations are ordinal. A wrong-cased DeviceCapability registers and grants nothing,
        // and 'app:InternetClient' is rejected outright by the foundation check below. No catalogued name
        // needs child elements, so nothing downstream is skipped by returning here.
        if (catalogued is not null)
        {
            capability = catalogued;
            return true;
        }

        // 'device:' selects the DeviceCapability ELEMENT rather than a namespace, and 'app:' spells the
        // default foundation namespace so a general capability can be forced explicitly.
        if (string.Equals(prefix, "device", StringComparison.OrdinalIgnoreCase))
        {
            // Checked here too: the prefix is an escape hatch for uncatalogued names, not a way to
            // bypass a declaration this property genuinely cannot express.
            if (RequiresChildElements(name, out error))
            {
                return false;
            }

            capability = new AppxCapability(name, DeviceElementName, null, FoundationNs);
            return true;
        }

        if (string.Equals(prefix, DefaultPrefixToken, StringComparison.OrdinalIgnoreCase))
        {
            // The foundation set is closed by the schema, so an unknown name here cannot be a capability
            // winapp merely hasn't catalogued — it is invalid. Rejecting it now beats registration
            // failing later with an opaque 0xC00CE169 schema error.
            if (!FoundationCapabilityNames.Contains(name, StringComparer.Ordinal))
            {
                error = $"'{entry}' is not a general capability. The foundation <Capability> set is closed at " +
                        $"{string.Join(", ", FoundationCapabilityNames)}. Restricted capabilities need their own " +
                        $"prefix, for example 'rescap:{name}'.";
                return false;
            }

            capability = new AppxCapability(name, CapabilityElementName, null, FoundationNs);
            return true;
        }

        if (!Namespaces.TryGetValue(prefix, out var ns))
        {
            error = $"Unknown capability namespace '{prefix}' in '{entry}'. Supported prefixes: " +
                    $"{string.Join(", ", Namespaces.Keys.Order(StringComparer.OrdinalIgnoreCase))}, device, {DefaultPrefixToken}.";
            return false;
        }

        // Uncatalogued by this point, so there is no documented version floor to carry over — the prefix
        // is the escape hatch for restricted capabilities winapp has not mapped.
        capability = new AppxCapability(name, CapabilityElementName, prefix, ns);
        return true;
    }

    /// <summary>
    /// Capability names are letters and digits; a DeviceCapability may also be a device-interface class
    /// GUID in braces. The value lands in XML, so anything else is rejected rather than escaped.
    /// </summary>
    /// <remarks>
    /// The GUID is checked with <see cref="Guid.TryParseExact(string, string, out Guid)"/> in <c>B</c>
    /// format rather than by regex: a character-class pattern accepts any 36 hex-or-hyphen characters, so a
    /// misplaced hyphen passes here and only fails later, when registration reports an opaque schema error
    /// naming no capability.
    /// </remarks>
    private static bool IsSafeName(string name) =>
        name.StartsWith('{')
            ? Guid.TryParseExact(name, "B", out _)
            : SafeNameRegex().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z][A-Za-z0-9]*$")]
    private static partial Regex SafeNameRegex();
}
