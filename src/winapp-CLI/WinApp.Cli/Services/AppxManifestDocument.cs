// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;
using System.Xml;
using System.Xml.Linq;
using WinApp.Cli.Helpers;
using WinApp.Cli.Models;

namespace WinApp.Cli.Services;

/// <summary>
/// XDocument-based wrapper for reading and manipulating AppxManifest.xml files.
/// This is a pure data class with no DI dependencies.
/// </summary>
internal class AppxManifestDocument
{
    // AppxManifest XML namespaces
    public static readonly XNamespace DefaultNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    public static readonly XNamespace UapNs = "http://schemas.microsoft.com/appx/manifest/uap/windows10";
    public static readonly XNamespace Uap5Ns = "http://schemas.microsoft.com/appx/manifest/uap/windows10/5";
    public static readonly XNamespace Uap10Ns = "http://schemas.microsoft.com/appx/manifest/uap/windows10/10";
    public static readonly XNamespace RescapNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    public static readonly XNamespace BuildNs = "http://schemas.microsoft.com/developer/appx/2015/build";
    public static readonly XNamespace DesktopNs = "http://schemas.microsoft.com/appx/manifest/desktop/windows10";
    public static readonly XNamespace Desktop6Ns = "http://schemas.microsoft.com/appx/manifest/desktop/windows10/6";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly XDocument _document;

    private AppxManifestDocument(XDocument document)
    {
        _document = document ?? throw new ArgumentNullException(nameof(document));
    }

    /// <summary>
    /// Direct access to the underlying XDocument for advanced operations.
    /// </summary>
    public XDocument Document => _document;

    #region Static Factory Methods

    /// <summary>
    /// Loads an AppxManifest from a file path.
    /// </summary>
    public static AppxManifestDocument Load(string path)
    {
        var doc = XDocument.Load(path);
        return new AppxManifestDocument(doc);
    }

    /// <summary>
    /// Loads an AppxManifest from a stream.
    /// </summary>
    public static AppxManifestDocument Load(Stream stream)
    {
        var doc = XDocument.Load(stream);
        return new AppxManifestDocument(doc);
    }

    /// <summary>
    /// Parses an AppxManifest from an XML string.
    /// </summary>
    public static AppxManifestDocument Parse(string xml)
    {
        var doc = XDocument.Parse(xml);
        return new AppxManifestDocument(doc);
    }

    #endregion

    #region Save / Serialize

    /// <summary>
    /// Saves the manifest to a file with UTF-8 (no BOM) encoding.
    /// </summary>
    public void Save(string path)
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = Utf8NoBom,
            OmitXmlDeclaration = _document.Declaration == null,
        };

        using var memoryStream = new MemoryStream();
        using (var writer = XmlWriter.Create(memoryStream, settings))
        {
            _document.Save(writer);
        }

        File.WriteAllBytes(path, memoryStream.ToArray());
    }

    /// <summary>
    /// Serializes the manifest to an XML string.
    /// </summary>
    public string ToXml()
    {
        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "  ",
            Encoding = Utf8NoBom,
            OmitXmlDeclaration = _document.Declaration == null,
        };

        using var memoryStream = new MemoryStream();
        using (var writer = XmlWriter.Create(memoryStream, settings))
        {
            _document.Save(writer);
        }

        return Utf8NoBom.GetString(memoryStream.ToArray());
    }

    #endregion

    #region Element Accessors

    /// <summary>
    /// Gets the Identity element.
    /// </summary>
    public XElement? GetIdentityElement() =>
        _document.Root?.Element(DefaultNs + "Identity");

    /// <summary>
    /// Gets the first Application element.
    /// </summary>
    public XElement? GetFirstApplicationElement() =>
        _document.Root?.Element(DefaultNs + "Applications")?.Element(DefaultNs + "Application");

    /// <summary>
    /// Gets the uap:VisualElements element from the first Application.
    /// </summary>
    public XElement? GetVisualElements() =>
        GetFirstApplicationElement()?.Element(UapNs + "VisualElements");

    /// <summary>
    /// Gets the Resources element.
    /// </summary>
    public XElement? GetResourcesElement() =>
        _document.Root?.Element(DefaultNs + "Resources");

    /// <summary>
    /// Gets the Dependencies element.
    /// </summary>
    public XElement? GetDependenciesElement() =>
        _document.Root?.Element(DefaultNs + "Dependencies");

    /// <summary>
    /// Gets the package-level Extensions element (child of Package, after Applications).
    /// </summary>
    public XElement? GetExtensionsElement() =>
        _document.Root?.Element(DefaultNs + "Extensions");

    /// <summary>
    /// Gets the Capabilities element.
    /// </summary>
    public XElement? GetCapabilitiesElement() =>
        _document.Root?.Element(DefaultNs + "Capabilities");

    /// <summary>
    /// Gets the Properties element.
    /// </summary>
    public XElement? GetPropertiesElement() =>
        _document.Root?.Element(DefaultNs + "Properties");

    /// <summary>
    /// True when the manifest declares <c>uap10:AllowExternalContent=true</c>, which marks it
    /// as a sparse identity package whose binaries/assets are resolved from an external location.
    /// </summary>
    public bool AllowsExternalContent
    {
        get
        {
            var el = GetPropertiesElement()?.Element(Uap10Ns + "AllowExternalContent");
            return el != null && string.Equals(el.Value.Trim(), "true", StringComparison.OrdinalIgnoreCase);
        }
    }

    #endregion

    #region Identity Properties

    /// <summary>
    /// Gets or sets the Identity Name attribute.
    /// </summary>
    public string? IdentityName
    {
        get => GetIdentityElement()?.Attribute("Name")?.Value;
        set => SetIdentityAttribute("Name", value);
    }

    /// <summary>
    /// Gets or sets the Identity Publisher attribute.
    /// </summary>
    public string? IdentityPublisher
    {
        get => GetIdentityElement()?.Attribute("Publisher")?.Value;
        set => SetIdentityAttribute("Publisher", value);
    }

    /// <summary>
    /// Gets or sets the Identity Version attribute.
    /// </summary>
    public string? IdentityVersion
    {
        get => GetIdentityElement()?.Attribute("Version")?.Value;
        set => SetIdentityAttribute("Version", value);
    }

    /// <summary>
    /// Gets or sets the Identity ProcessorArchitecture attribute.
    /// </summary>
    public string? IdentityProcessorArchitecture
    {
        get => GetIdentityElement()?.Attribute("ProcessorArchitecture")?.Value;
        set => SetIdentityAttribute("ProcessorArchitecture", value);
    }

    public string? IdentityResourceId
    {
        get => GetIdentityElement()?.Attribute("ResourceId")?.Value;
        set => SetIdentityAttribute("ResourceId", value);
    }

    private void SetIdentityAttribute(string attributeName, string? value)
    {
        var identity = GetIdentityElement();
        if (identity == null)
        {
            if (value == null)
            {
                return;
            }

            identity = new XElement(DefaultNs + "Identity");
            _document.Root?.AddFirst(identity);
        }

        if (value == null)
        {
            identity.Attribute(attributeName)?.Remove();
        }
        else
        {
            identity.SetAttributeValue(attributeName, value);
        }
    }

    #endregion

    #region Development Identity

    /// <summary>
    /// Rejects package shapes and public registrations that cannot safely coexist under a new name.
    /// This allowlist deliberately does not treat an unfamiliar namespace or category as harmless.
    /// </summary>
    public void ValidateUniqueIdentitySupport()
    {
        var root = _document.Root;
        if (root?.Name != DefaultNs + "Package")
        {
            throw UnsupportedIdentity(root, "only a Windows 10 application Package is supported, not a bundle");
        }
        if (root.Elements(DefaultNs + "Identity").Count() != 1)
        {
            throw UnsupportedIdentity(root, "exactly one Identity is required");
        }
        var applications = root.Descendants().Where(e => e.Name.LocalName == "Application").ToList();
        if (root.Elements(DefaultNs + "Applications").Count() != 1 || applications.Count != 1
            || applications[0].Name != DefaultNs + "Application"
            || applications[0].Parent != root.Element(DefaultNs + "Applications"))
        {
            throw UnsupportedIdentity(root, "exactly one application in Applications is supported");
        }
        if (IdentityResourceId == "~")
        {
            throw UnsupportedIdentity(GetIdentityElement(), "bundle identities are not supported");
        }

        foreach (var element in root.DescendantsAndSelf())
        {
            if (element.Name.LocalName is "Bundle" or "MainPackageDependency" or "MainBundleDependency"
                or "OptionalPackage" or "ExternalLocation")
            {
                throw UnsupportedIdentity(element, "bundle, optional, and external-content packages are not supported");
            }
            if (element.Name.LocalName is "AllowExternalContent" or "Framework" or "ResourcePackage")
            {
                var expectedNamespace = element.Name.LocalName == "AllowExternalContent" ? Uap10Ns : DefaultNs;
                if (element.Name.Namespace != expectedNamespace || element.Parent != GetPropertiesElement()
                    || element.Value.Trim() is not ("false" or "0"))
                {
                    throw UnsupportedIdentity(element, "sparse/external-content, framework, and resource packages are not supported");
                }
            }
            if (element.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Name.LocalName == "ExternalLocation"))
            {
                throw UnsupportedIdentity(element, "external locations are not supported");
            }
        }

        var validatedExtensions = new HashSet<XElement>();
        foreach (var extensions in root.Descendants().Where(e => e.Name.LocalName == "Extensions"))
        {
            if (extensions.Name != DefaultNs + "Extensions"
                || (extensions.Parent != root && extensions.Parent != applications[0]))
            {
                throw UnsupportedIdentity(extensions, "the extension namespace or placement is not supported");
            }
            foreach (var extension in extensions.Elements())
            {
                var category = extension.Attribute("Category")?.Value;
                if (category == "windows.appExecutionAlias" && extensions.Parent == applications[0])
                {
                    ValidateUniqueAliasExtension(extension);
                }
                else if (category == "windows.activatableClass.inProcessServer" && extensions.Parent == root)
                {
                    ValidateUniqueInProcessExtension(extension);
                }
                else
                {
                    throw UnsupportedIdentity(extension, $"extension category '{category ?? "(missing)"}' is not supported");
                }
                validatedExtensions.Add(extension);
            }
        }
        foreach (var element in root.Descendants())
        {
            if ((element.Name.LocalName == "Extension" && !validatedExtensions.Contains(element))
                || (element.Name.LocalName is "AppExecutionAlias" or "ExecutionAlias"
                    && !element.Ancestors().Any(validatedExtensions.Contains)))
            {
                throw UnsupportedIdentity(element, "the extension namespace or placement is not supported");
            }
        }

        var originalName = IdentityName
            ?? throw UnsupportedIdentity(GetIdentityElement(), "Identity/@Name is required");
        var originalFamily = DevelopmentIdentityHelper.ComputeFamilyName(originalName,
            IdentityPublisher ?? throw UnsupportedIdentity(GetIdentityElement(), "Identity/@Publisher is required"));
        foreach (var element in root.DescendantsAndSelf())
        {
            foreach (var value in element.Attributes().Where(a => !a.IsNamespaceDeclaration).Select(a => a.Value)
                .Concat(element.Nodes().OfType<XText>().Select(text => text.Value)))
            {
                DevelopmentIdentityHelper.ValidateResourceReference(value, originalName, originalFamily);
            }
        }
    }

    /// <summary>Changes only the staged package name and the supported authored alias attributes.</summary>
    public void ApplyDevelopmentIdentity(DevelopmentIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (identity.Mode == "Original")
        {
            return;
        }
        if (identity.Mode != "Unique")
        {
            throw new InvalidOperationException($"Unknown development identity mode '{identity.Mode}'.");
        }
        ValidateUniqueIdentitySupport();
        if (IdentityName != identity.OriginalPackageName || IdentityPublisher != identity.Publisher
            || IdentityVersion != identity.Version || (IdentityProcessorArchitecture ?? "neutral") != identity.Architecture
            || (IdentityResourceId ?? string.Empty) != identity.ResourceId || ApplicationId != identity.ApplicationId)
        {
            throw new InvalidOperationException("The development identity does not match the original manifest. Prepare the identity from the current source manifest again.");
        }
        if (DevelopmentIdentityHelper.ComputeFamilyName(identity.EffectivePackageName, identity.Publisher) != identity.PackageFamilyName)
        {
            throw new InvalidOperationException("The development identity's package family does not match its name and publisher.");
        }
        var aliasAttributes = GetFirstApplicationElement()!.Descendants()
            .Where(e => e.Name.LocalName == "ExecutionAlias"
                && (e.Name.Namespace == Uap5Ns || e.Name.Namespace == DesktopNs))
            .Select(e => e.Attribute("Alias")!).ToList();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var originalNames = aliasAttributes.Select(a => a.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (identity.Aliases.Count != aliasAttributes.Count)
        {
            throw new InvalidOperationException("The development identity must rename every authored execution alias.");
        }
        foreach (var alias in aliasAttributes)
        {
            if (!identity.Aliases.TryGetValue(alias.Value, out var effective)
                || originalNames.Contains(effective)
                || !ExecutionAliasResolver.IsSafeAliasName(effective) || !names.Add(effective))
            {
                throw new InvalidOperationException($"No distinct, safe unique execution alias was prepared for '{alias.Value}'.");
            }
        }

        IdentityName = identity.EffectivePackageName;
        foreach (var alias in aliasAttributes)
        {
            alias.Value = identity.Aliases[alias.Value];
        }
    }

    private static void ValidateUniqueAliasExtension(XElement extension)
    {
        var ns = extension.Name.Namespace;
        if (extension.Name.LocalName != "Extension" || (ns != Uap5Ns && ns != DesktopNs))
        {
            throw UnsupportedIdentity(extension, "only plain uap5 or desktop execution-alias extensions are supported");
        }
        ValidateUniqueAttributes(extension, "Category", "Executable", "EntryPoint", "StartPage", "RuntimeType",
            Uap10Ns + "RuntimeBehavior", Uap10Ns + "TrustLevel");
        var containers = extension.Elements().ToList();
        if (containers.Count != 1 || containers[0].Name != ns + "AppExecutionAlias")
        {
            throw UnsupportedIdentity(extension, "exactly one plain AppExecutionAlias is required");
        }
        var container = containers[0];
        ValidateUniqueAttributes(container,
            (XNamespace)"http://schemas.microsoft.com/appx/manifest/desktop/windows10/4" + "Subsystem",
            (XNamespace)"http://schemas.microsoft.com/appx/manifest/iot/windows10/2" + "Subsystem",
            Uap10Ns + "Subsystem");
        if (container.Attributes().Any(a => !a.IsNamespaceDeclaration && a.Value is not ("console" or "windows")))
        {
            throw UnsupportedIdentity(container, "Subsystem must be console or windows");
        }
        if (!container.HasElements)
        {
            throw UnsupportedIdentity(container, "at least one execution alias is required");
        }
        foreach (var alias in container.Elements())
        {
            if (alias.Name != ns + "ExecutionAlias" || alias.HasElements)
            {
                throw UnsupportedIdentity(alias, "only plain ExecutionAlias elements without additional public contracts are supported");
            }
            ValidateUniqueAttributes(alias, "Alias");
            if (!ExecutionAliasResolver.IsSafeAliasName(alias.Attribute("Alias")?.Value))
            {
                throw UnsupportedIdentity(alias, "Alias must be a safe .exe filename");
            }
        }
    }

    private static void ValidateUniqueInProcessExtension(XElement extension)
    {
        if (extension.Name != DefaultNs + "Extension")
        {
            throw UnsupportedIdentity(extension, "only foundation-namespace, package-scoped in-process WinRT registration is supported");
        }
        ValidateUniqueAttributes(extension, "Category");
        var servers = extension.Elements().ToList();
        if (servers.Count != 1 || servers[0].Name != DefaultNs + "InProcessServer")
        {
            throw UnsupportedIdentity(extension, "exactly one foundation InProcessServer is required");
        }
        var server = servers[0];
        ValidateUniqueAttributes(server);
        var paths = server.Elements(DefaultNs + "Path").ToList();
        if (paths.Count != 1 || string.IsNullOrWhiteSpace(paths[0].Value) || paths[0].HasElements
            || !server.Elements(DefaultNs + "ActivatableClass").Any())
        {
            throw UnsupportedIdentity(server, "a Path and in-process ActivatableClass registrations are required");
        }
        ValidateUniqueAttributes(paths[0]);
        foreach (var child in server.Elements())
        {
            if (child == paths[0])
            {
                continue;
            }
            if (child.Name != DefaultNs + "ActivatableClass" || child.HasElements)
            {
                throw UnsupportedIdentity(child, "only ordinary in-process WinRT classes are supported, not packaged COM servers");
            }
            ValidateUniqueAttributes(child, "ActivatableClassId", "ThreadingModel");
            if (string.IsNullOrWhiteSpace(child.Attribute("ActivatableClassId")?.Value)
                || child.Attribute("ThreadingModel")?.Value is not ("both" or "STA" or "MTA"))
            {
                throw UnsupportedIdentity(child, "ActivatableClassId and a valid ThreadingModel are required");
            }
        }
    }

    private static void ValidateUniqueAttributes(XElement element, params XName[] supported)
    {
        var unsupported = element.Attributes().FirstOrDefault(a => !a.IsNamespaceDeclaration && !supported.Contains(a.Name));
        if (unsupported != null)
        {
            throw UnsupportedIdentity(element, $"attribute '{unsupported.Name}' is not supported");
        }
    }

    private static InvalidOperationException UnsupportedIdentity(XElement? element, string reason)
    {
        var category = element?.AncestorsAndSelf().Select(e => e.Attribute("Category")?.Value).FirstOrDefault(v => v != null);
        return new InvalidOperationException(
            $"--unique-identity cannot transform element '{element?.Name.ToString() ?? "(missing Package)"}'"
            + (category == null ? string.Empty : $" (category '{category}')")
            + $": {reason}. Remove the unsupported declaration or run without --unique-identity.");
    }

    #endregion

    #region Application Properties

    /// <summary>
    /// Gets or sets the first Application's Id attribute.
    /// </summary>
    public string? ApplicationId
    {
        get => GetFirstApplicationElement()?.Attribute("Id")?.Value;
        set => SetApplicationAttribute("Id", value);
    }

    /// <summary>
    /// Gets or sets the first Application's Executable attribute.
    /// </summary>
    public string? ApplicationExecutable
    {
        get => GetFirstApplicationElement()?.Attribute("Executable")?.Value;
        set => SetApplicationAttribute("Executable", value);
    }

    /// <summary>
    /// Gets or sets the first Application's EntryPoint attribute.
    /// </summary>
    public string? ApplicationEntryPoint
    {
        get => GetFirstApplicationElement()?.Attribute("EntryPoint")?.Value;
        set => SetApplicationAttribute("EntryPoint", value);
    }

    private void SetApplicationAttribute(string attributeName, string? value)
    {
        var app = GetFirstApplicationElement();
        if (app == null)
        {
            return;
        }

        if (value == null)
        {
            app.Attribute(attributeName)?.Remove();
        }
        else
        {
            app.SetAttributeValue(attributeName, value);
        }
    }

    #endregion

    #region VisualElements Properties

    /// <summary>
    /// Gets or sets the uap:VisualElements DisplayName attribute.
    /// </summary>
    public string? VisualElementsDisplayName
    {
        get => GetVisualElements()?.Attribute("DisplayName")?.Value;
        set
        {
            var ve = GetVisualElements();
            if (ve == null)
            {
                return;
            }

            if (value == null)
            {
                ve.Attribute("DisplayName")?.Remove();
            }
            else
            {
                ve.SetAttributeValue("DisplayName", value);
            }
        }
    }

    #endregion

    #region Resource Languages

    /// <summary>
    /// Extracts all Resource Language values.
    /// </summary>
    public List<string> GetResourceLanguages()
    {
        var resources = GetResourcesElement();
        if (resources == null)
        {
            return [];
        }

        return resources.Elements(DefaultNs + "Resource")
            .Select(r => r.Attribute("Language")?.Value)
            .Where(lang => lang != null)
            .Cast<string>()
            .ToList();
    }

    /// <summary>
    /// Replaces the Resources block with the given languages.
    /// </summary>
    public void SetResourceLanguages(IList<string> languages)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var resources = GetResourcesElement();
        if (resources == null)
        {
            resources = new XElement(DefaultNs + "Resources");
            // Insert after Dependencies if present, otherwise after Identity
            var dependencies = GetDependenciesElement();
            if (dependencies != null)
            {
                dependencies.AddAfterSelf(resources);
            }
            else
            {
                var identity = GetIdentityElement();
                if (identity != null)
                {
                    identity.AddAfterSelf(resources);
                }
                else
                {
                    root.Add(resources);
                }
            }
        }
        else
        {
            resources.RemoveAll();
        }

        foreach (var lang in languages)
        {
            resources.Add(new XElement(DefaultNs + "Resource", new XAttribute("Language", lang)));
        }
    }

    #endregion

    #region Namespace Management

    /// <summary>
    /// Adds a prefix to the IgnorableNamespaces attribute on the Package element if not already present.
    /// </summary>
    public void AddIgnorableNamespace(string prefix)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var ignorableAttr = root.Attribute("IgnorableNamespaces");
        if (ignorableAttr != null)
        {
            var namespaces = ignorableAttr.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!namespaces.Contains(prefix, StringComparer.OrdinalIgnoreCase))
            {
                ignorableAttr.Value = ignorableAttr.Value + " " + prefix;
            }
        }
        else
        {
            root.SetAttributeValue("IgnorableNamespaces", prefix);
        }
    }

    /// <summary>
    /// Adds an xmlns:prefix declaration to the Package element if not already present.
    /// </summary>
    public void EnsureNamespace(string prefix, XNamespace ns)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var existing = root.Attribute(XNamespace.Xmlns + prefix);
        if (existing == null)
        {
            root.Add(new XAttribute(XNamespace.Xmlns + prefix, ns.NamespaceName));
        }
    }

    #endregion

    #region Capabilities

    /// <summary>
    /// Adds a capability if not already present. Uses the default namespace unless a specific namespace is provided.
    /// </summary>
    public void EnsureCapability(string capabilityName, XNamespace? ns = null)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var capabilities = GetCapabilitiesElement();
        if (capabilities == null)
        {
            capabilities = new XElement(DefaultNs + "Capabilities");
            root.Add(capabilities);
        }

        var targetNs = ns ?? DefaultNs;

        // Check all child elements for a matching Name attribute regardless of namespace
        var existing = capabilities.Elements()
            .FirstOrDefault(e => string.Equals(e.Attribute("Name")?.Value, capabilityName, StringComparison.OrdinalIgnoreCase));

        if (existing == null)
        {
            capabilities.Add(new XElement(targetNs + "Capability", new XAttribute("Name", capabilityName)));
        }
    }

    /// <summary>
    /// Declares a capability resolved by <see cref="AppxCapabilityCatalog"/>, including its XML namespace,
    /// its <c>IgnorableNamespaces</c> entry, and any <c>MaxVersionTested</c> floor it requires.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Element ORDER matters: the schema requires every <c>DeviceCapability</c> to come after all
    /// <c>Capability</c> elements, so a <c>Capability</c> is inserted ahead of the first
    /// <c>DeviceCapability</c> rather than appended. Appending regardless produces a manifest Windows
    /// rejects as soon as one device capability precedes one ordinary capability.
    /// </para>
    /// <para>
    /// Matching is by name only, across namespaces, and case-insensitively: the same capability declared
    /// twice is a schema violation regardless of which namespaces or spellings were used. This mirrors the
    /// sparse-manifest check in <c>MsixService</c>.
    /// </para>
    /// </remarks>
    public void EnsureCapability(AppxCapability capability)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        if (capability.Prefix is { Length: > 0 } prefix)
        {
            EnsureNamespace(prefix, capability.Namespace);
            AddIgnorableNamespace(prefix);
        }

        if (capability.MinimumMaxVersionTested is { Length: > 0 } floor)
        {
            EnsureMinimumMaxVersionTested(floor);
        }

        var capabilities = GetCapabilitiesElement();
        if (capabilities == null)
        {
            capabilities = new XElement(DefaultNs + "Capabilities");
            root.Add(capabilities);
        }

        var alreadyDeclared = capabilities.Elements()
            .Any(e => string.Equals(e.Attribute("Name")?.Value, capability.Name, StringComparison.OrdinalIgnoreCase));
        if (alreadyDeclared)
        {
            return;
        }

        var element = new XElement(
            capability.Namespace + capability.ElementName,
            new XAttribute("Name", capability.Name));

        var firstDeviceCapability = capabilities.Elements()
            .FirstOrDefault(e => e.Name.LocalName == "DeviceCapability");

        if (!capability.IsDeviceCapability && firstDeviceCapability != null)
        {
            firstDeviceCapability.AddBeforeSelf(element);
        }
        else
        {
            capabilities.Add(element);
        }
    }

    /// <summary>
    /// Returns the execution aliases the given application declares, or all of them when
    /// <paramref name="appId"/> is null.
    /// </summary>
    /// <remarks>
    /// Matches <c>ExecutionAlias</c> in either the <c>uap5</c> or the <c>desktop</c> namespace, as
    /// <see cref="MsixService.ExtractExecutionAliases"/> does. Both are valid, and recognizing only
    /// <c>uap5</c> would read a legacy <c>desktop:ExecutionAlias</c> as "no alias declared" — so winapp
    /// would stage a second, generated one instead of using the command name the author chose.
    /// </remarks>
    public IReadOnlyList<string> GetExecutionAliases(string? appId = null)
    {
        var app = FindApplication(appId);
        if (app == null)
        {
            return [];
        }

        return [.. app.Descendants()
            .Where(e => e.Name.LocalName == "ExecutionAlias"
                && (e.Name.Namespace == Uap5Ns || e.Name.Namespace == DesktopNs))
            .Select(e => e.Attribute("Alias")?.Value)
            .Where(v => !string.IsNullOrEmpty(v))
            .Select(v => v!)];
    }

    /// <summary>
    /// Declares <paramref name="aliasName"/> as an execution alias, unless the application already
    /// declares one. Returns the alias now in effect, or <see langword="null"/> if none could be added.
    /// </summary>
    /// <remarks>
    /// An alias the app author wrote is always left alone — they chose the command name deliberately, and
    /// replacing it would rename a command their users already type. This exists so winapp can add one to
    /// the <b>staged</b> manifest in the AppX layout, which lets a console app's output reach the terminal
    /// without editing the manifest the user has checked in.
    /// </remarks>
    public string? EnsureExecutionAlias(string aliasName, string? appId = null)
    {
        var root = _document.Root;
        var app = FindApplication(appId);
        if (root == null || app == null || !ExecutionAliasResolver.IsSafeAliasName(aliasName))
        {
            return null;
        }

        var existing = GetExecutionAliases(appId);
        if (existing.Count > 0)
        {
            return existing[0];
        }

        EnsureNamespace("uap5", Uap5Ns);
        AddIgnorableNamespace("uap5");

        var extensions = app.Element(DefaultNs + "Extensions");
        if (extensions == null)
        {
            extensions = new XElement(DefaultNs + "Extensions");
            app.Add(extensions);
        }

        var aliasElement = new XElement(Uap5Ns + "ExecutionAlias", new XAttribute("Alias", aliasName));

        var aliasExtension = extensions.Elements(Uap5Ns + "Extension")
            .FirstOrDefault(e => string.Equals(e.Attribute("Category")?.Value, "windows.appExecutionAlias", StringComparison.OrdinalIgnoreCase));

        if (aliasExtension == null)
        {
            extensions.Add(new XElement(
                Uap5Ns + "Extension",
                new XAttribute("Category", "windows.appExecutionAlias"),
                new XElement(Uap5Ns + "AppExecutionAlias", aliasElement)));
        }
        else
        {
            var appExecAlias = aliasExtension.Element(Uap5Ns + "AppExecutionAlias");
            if (appExecAlias == null)
            {
                aliasExtension.Add(new XElement(Uap5Ns + "AppExecutionAlias", aliasElement));
            }
            else
            {
                appExecAlias.Add(aliasElement);
            }
        }

        return aliasName;
    }

    private XElement? FindApplication(string? appId)
    {
        var applications = _document.Root?.Descendants(DefaultNs + "Application").ToList() ?? [];
        if (applications.Count == 0)
        {
            return null;
        }

        return string.IsNullOrEmpty(appId)
            ? applications[0]
            : applications.FirstOrDefault(a => string.Equals(a.Attribute("Id")?.Value, appId, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Raises every <c>TargetDeviceFamily/@MaxVersionTested</c> that is below <paramref name="minimum"/>.
    /// </summary>
    /// <remarks>
    /// Some capabilities are only honored from a given OS version — <c>systemAIModels</c> needs
    /// 10.0.26226.0 — and a manifest declaring one below its floor registers successfully while the API
    /// still fails, which is the least debuggable outcome. Only ever raises: an app that already tested
    /// against something newer keeps its value.
    /// </remarks>
    public void EnsureMinimumMaxVersionTested(string minimum)
    {
        if (!Version.TryParse(minimum, out var required))
        {
            return;
        }

        var families = _document.Root?.Element(DefaultNs + "Dependencies")
            ?.Elements(DefaultNs + "TargetDeviceFamily") ?? [];

        foreach (var attribute in families.Select(static family => family.Attribute("MaxVersionTested")))
        {
            if (attribute == null)
            {
                continue;
            }

            if (!Version.TryParse(attribute.Value, out var current) || current < required)
            {
                attribute.Value = minimum;
            }
        }
    }

    #endregion

    #region Build Metadata

    /// <summary>
    /// Adds or updates a build:Item entry in the build:Metadata section.
    /// </summary>
    public void SetBuildMetadata(string toolName, string version)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var metadata = root.Element(BuildNs + "Metadata");
        if (metadata == null)
        {
            metadata = new XElement(BuildNs + "Metadata");
            root.Add(metadata);
        }

        var existingItem = metadata.Elements(BuildNs + "Item")
            .FirstOrDefault(e => string.Equals(e.Attribute("Name")?.Value, toolName, StringComparison.OrdinalIgnoreCase));

        if (existingItem != null)
        {
            existingItem.SetAttributeValue("Version", version);
        }
        else
        {
            metadata.Add(new XElement(BuildNs + "Item",
                new XAttribute("Name", toolName),
                new XAttribute("Version", version)));
        }
    }

    #endregion

    #region Package-level Extensions

    /// <summary>
    /// Gets or creates the Package-level Extensions element (direct child of Package root).
    /// This is distinct from Application-level Extensions which live inside Application elements.
    /// </summary>
    public XElement GetOrCreatePackageLevelExtensionsElement()
    {
        var root = _document.Root ?? throw new InvalidOperationException("Document has no root element");

        // root.Element() only returns direct children, so this correctly gets
        // Package-level Extensions (not Application > Extensions)
        var extensions = root.Element(DefaultNs + "Extensions");
        if (extensions != null)
        {
            return extensions;
        }

        extensions = new XElement(DefaultNs + "Extensions");

        // Insert after Applications (standard AppxManifest element order)
        var applications = root.Element(DefaultNs + "Applications");
        if (applications != null)
        {
            applications.AddAfterSelf(extensions);
        }
        else
        {
            root.Add(extensions);
        }

        return extensions;
    }

    /// <summary>
    /// Collects all DLL paths registered in InProcessServer or ProxyStub extensions
    /// (from Package-level <c>&lt;Path&gt;</c> elements). Used for dedup when adding new entries.
    /// </summary>
    public HashSet<string> GetRegisteredExtensionDllPaths()
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = _document.Root;
        if (root == null)
        {
            return result;
        }

        foreach (var path in root.Descendants(DefaultNs + "Path"))
        {
            var text = path.Value?.Trim();
            if (!string.IsNullOrEmpty(text))
            {
                result.Add(text);
            }
        }

        return result;
    }

    /// <summary>
    /// Adds an InProcessServer extension entry to the Package-level Extensions element.
    /// </summary>
    public void AddInProcessServerExtension(string dllPath, IEnumerable<string> activatableClasses)
    {
        var extensions = GetOrCreatePackageLevelExtensionsElement();

        var extension = new XElement(DefaultNs + "Extension",
            new XAttribute("Category", "windows.activatableClass.inProcessServer"),
            new XElement(DefaultNs + "InProcessServer",
                new XElement(DefaultNs + "Path", dllPath),
                activatableClasses.Select(cls =>
                    new XElement(DefaultNs + "ActivatableClass",
                        new XAttribute("ActivatableClassId", cls),
                        new XAttribute("ThreadingModel", "both")))));

        extensions.Add(extension);
    }

    #endregion

    #region Package Dependencies

    /// <summary>
    /// Checks if a PackageDependency with the given name prefix exists.
    /// </summary>
    public bool HasPackageDependency(string namePrefix)
    {
        var dependencies = GetDependenciesElement();
        if (dependencies == null)
        {
            return false;
        }

        return dependencies.Elements(DefaultNs + "PackageDependency")
            .Any(e => e.Attribute("Name")?.Value?.StartsWith(namePrefix, StringComparison.OrdinalIgnoreCase) == true);
    }

    /// <summary>
    /// Adds or updates a PackageDependency element.
    /// </summary>
    public void SetPackageDependency(string name, string minVersion, string publisher)
    {
        var root = _document.Root;
        if (root == null)
        {
            return;
        }

        var dependencies = GetDependenciesElement();
        if (dependencies == null)
        {
            dependencies = new XElement(DefaultNs + "Dependencies");
            root.Add(dependencies);
        }

        var existing = dependencies.Elements(DefaultNs + "PackageDependency")
            .FirstOrDefault(e => string.Equals(e.Attribute("Name")?.Value, name, StringComparison.Ordinal));

        if (existing != null)
        {
            existing.SetAttributeValue("MinVersion", minVersion);
            existing.SetAttributeValue("Publisher", publisher);
        }
        else
        {
            dependencies.Add(new XElement(DefaultNs + "PackageDependency",
                new XAttribute("Name", name),
                new XAttribute("MinVersion", minVersion),
                new XAttribute("Publisher", publisher)));
        }
    }

    #endregion
}
