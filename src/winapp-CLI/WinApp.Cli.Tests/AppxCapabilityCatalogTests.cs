// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="AppxCapabilityCatalog"/> — the mapping from capability names onto the exact
/// element and XML namespace an appxmanifest requires. Getting a name into the wrong one produces a
/// manifest Windows rejects, or one it accepts while silently not granting the capability.
/// </summary>
[TestClass]
public class AppxCapabilityCatalogTests
{
    private const string FoundationNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
    private const string RescapNs = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";
    private const string SystemAiNs = "http://schemas.microsoft.com/appx/manifest/systemai/windows10";
    private const string UapNs = "http://schemas.microsoft.com/appx/manifest/uap/windows10";

    private static AppxCapability ParseOne(string value)
    {
        Assert.IsTrue(AppxCapabilityCatalog.TryParse(value, out var caps, out var error), error);
        return caps.Single();
    }

    #region Namespace selection

    [TestMethod]
    public void SystemAIModels_UsesTheSystemAiNamespace_NotRescap()
    {
        // The capability that forced this feature. 'rescap' is the intuitive guess — it is a restricted
        // capability — but the documented element is systemai:Capability, and the rescap spelling
        // registers successfully while never granting AI model access.
        var capability = ParseOne("systemAIModels");

        Assert.AreEqual("Capability", capability.ElementName);
        Assert.AreEqual("systemai", capability.Prefix);
        Assert.AreEqual(SystemAiNs, capability.Namespace.NamespaceName);
    }

    [TestMethod]
    public void SystemAIModels_CarriesItsMaxVersionTestedFloor()
    {
        // Below 10.0.26226.0 the manifest registers but the capability is not honored — the least
        // debuggable failure, since everything reports success.
        Assert.AreEqual("10.0.26226.0", ParseOne("systemAIModels").MinimumMaxVersionTested);
    }

    [TestMethod]
    public void GeneralCapabilities_UseTheFoundationNamespaceWithNoPrefix()
    {
        foreach (var name in new[] { "internetClient", "internetClientServer", "privateNetworkClientServer", "allJoyn", "codeGeneration" })
        {
            var capability = ParseOne(name);
            Assert.AreEqual("Capability", capability.ElementName, name);
            Assert.IsNull(capability.Prefix, name);
            Assert.AreEqual(FoundationNs, capability.Namespace.NamespaceName, name);
        }
    }

    [TestMethod]
    public void LibraryCapabilities_UseTheUapNamespace()
    {
        foreach (var name in new[] { "picturesLibrary", "videosLibrary", "musicLibrary", "documentsLibrary" })
        {
            var capability = ParseOne(name);
            Assert.AreEqual("uap", capability.Prefix, name);
            Assert.AreEqual(UapNs, capability.Namespace.NamespaceName, name);
        }
    }

    [TestMethod]
    public void DeviceCapabilities_UseADifferentElement_NotANamespacedCapability()
    {
        // microphone/webcam are DeviceCapability elements. Emitting them as <Capability> — the naive
        // reading of "a capability" — is invalid.
        foreach (var name in new[] { "microphone", "webcam", "location", "bluetooth" })
        {
            var capability = ParseOne(name);
            Assert.AreEqual("DeviceCapability", capability.ElementName, name);
            Assert.IsTrue(capability.IsDeviceCapability, name);
            Assert.IsNull(capability.Prefix, name);
        }
    }

    [TestMethod]
    public void RunFullTrust_UsesRescap()
    {
        var capability = ParseOne("runFullTrust");
        Assert.AreEqual("rescap", capability.Prefix);
        Assert.AreEqual(RescapNs, capability.Namespace.NamespaceName);
    }

    #endregion

    #region Parsing

    [TestMethod]
    public void Parse_AcceptsSemicolonAndCommaSeparators()
    {
        Assert.IsTrue(AppxCapabilityCatalog.TryParse("internetClient;microphone", out var semi, out _));
        Assert.IsTrue(AppxCapabilityCatalog.TryParse("internetClient,microphone", out var comma, out _));

        Assert.AreEqual(2, semi.Count);
        Assert.AreEqual(2, comma.Count);
    }

    [TestMethod]
    public void Parse_TrimsWhitespaceAndIgnoresEmptyEntries()
    {
        Assert.IsTrue(AppxCapabilityCatalog.TryParse(" internetClient ; ; microphone ", out var caps, out var error), error);

        Assert.AreEqual(2, caps.Count);
        Assert.AreEqual("internetClient", caps[0].Name);
        Assert.AreEqual("microphone", caps[1].Name);
    }

    [TestMethod]
    public void Parse_EmptyOrNull_YieldsNoCapabilities()
    {
        Assert.IsTrue(AppxCapabilityCatalog.TryParse(null, out var fromNull, out _));
        Assert.IsTrue(AppxCapabilityCatalog.TryParse("   ", out var fromBlank, out _));

        Assert.AreEqual(0, fromNull.Count);
        Assert.AreEqual(0, fromBlank.Count);
    }

    [TestMethod]
    public void Parse_DropsDuplicates()
    {
        // The same capability declared twice is a schema violation, even though the intent is harmless.
        Assert.IsTrue(AppxCapabilityCatalog.TryParse("internetClient;internetClient", out var caps, out _));

        Assert.AreEqual(1, caps.Count);
    }

    [TestMethod]
    public void Parse_PreservesDeclarationOrder()
    {
        Assert.IsTrue(AppxCapabilityCatalog.TryParse("microphone;internetClient", out var caps, out _));

        Assert.AreEqual("microphone", caps[0].Name);
        Assert.AreEqual("internetClient", caps[1].Name);
    }

    #endregion

    #region Explicit qualification

    [TestMethod]
    public void Parse_ExplicitPrefix_ResolvesAnUncataloguedName()
    {
        // The escape hatch: the restricted set grows, so a name winapp has never heard of must still be
        // declarable rather than blocking the user.
        var capability = ParseOne("rescap:someFutureCapability");

        Assert.AreEqual("someFutureCapability", capability.Name);
        Assert.AreEqual("rescap", capability.Prefix);
        Assert.AreEqual(RescapNs, capability.Namespace.NamespaceName);
    }

    [TestMethod]
    public void Parse_DevicePrefix_SelectsTheDeviceCapabilityElement()
    {
        var capability = ParseOne("device:someSensor");

        Assert.AreEqual("DeviceCapability", capability.ElementName);
        Assert.IsNull(capability.Prefix);
    }

    [TestMethod]
    public void Parse_AppPrefix_ForcesTheFoundationNamespace()
    {
        var capability = ParseOne("app:internetClient");

        Assert.AreEqual("Capability", capability.ElementName);
        Assert.IsNull(capability.Prefix);
        Assert.AreEqual(FoundationNs, capability.Namespace.NamespaceName);
    }

    [TestMethod]
    public void Parse_ExplicitPrefixOnAKnownName_KeepsItsVersionFloor()
    {
        // Spelling the namespace out must not lose the MaxVersionTested requirement.
        Assert.AreEqual("10.0.26226.0", ParseOne("systemai:systemAIModels").MinimumMaxVersionTested);
    }

    [TestMethod]
    public void Parse_DeviceCapabilityGuidName_IsAccepted()
    {
        var capability = ParseOne("device:{A5DCBF10-6530-11D2-901F-00C04FB951ED}");

        Assert.AreEqual("DeviceCapability", capability.ElementName);
    }

    #endregion

    #region Rejection

    [TestMethod]
    public void Parse_UnknownBareName_IsRejectedWithGuidance()
    {
        // Guessing a namespace here is what produces an invalid manifest, so this must fail rather than
        // default to one — and the message has to name the way forward.
        Assert.IsFalse(AppxCapabilityCatalog.TryParse("someFutureCapability", out _, out var error));

        StringAssert.Contains(error, "someFutureCapability");
        StringAssert.Contains(error, "rescap:someFutureCapability");
    }

    [TestMethod]
    public void Parse_UnknownPrefix_IsRejectedAndListsTheSupportedOnes()
    {
        Assert.IsFalse(AppxCapabilityCatalog.TryParse("nope:something", out _, out var error));

        StringAssert.Contains(error, "nope");
        StringAssert.Contains(error, "rescap");
    }

    [TestMethod]
    public void Parse_NameWithMarkup_IsRejected()
    {
        // The value lands in an XML attribute; rejecting beats escaping.
        foreach (var bad in new[] { "rescap:a<b", "rescap:a\"b", "rescap:a b", "app:a/b" })
        {
            Assert.IsFalse(AppxCapabilityCatalog.TryParse(bad, out _, out _), bad);
        }
    }

    [TestMethod]
    public void Parse_PrefixWithNoName_IsRejected()
    {
        Assert.IsFalse(AppxCapabilityCatalog.TryParse("rescap:", out _, out var error));
        StringAssert.Contains(error, "prefix");
    }

    [TestMethod]
    public void Parse_OneBadEntry_RejectsTheWholeList()
    {
        // Partially applying a capability list would leave the app registered with some of what it asked
        // for and no indication which.
        Assert.IsFalse(AppxCapabilityCatalog.TryParse("internetClient;bogusName", out var caps, out _));
        Assert.AreEqual(0, caps.Count);
    }

    [TestMethod]
    public void Parse_UnknownFoundationCapability_IsRejected()
    {
        // The foundation <Capability> set is closed by the schema, so an unknown name there is invalid
        // rather than merely uncatalogued. Accepting it would defer the failure to registration, which
        // reports only an opaque schema error naming no capability.
        Assert.IsFalse(AppxCapabilityCatalog.TryParse("app:notARealCapability", out _, out var error));
        StringAssert.Contains(error, "closed at", "The error should name the closed foundation set");
    }

    [TestMethod]
    [DataRow("rescap:systemAIModels", "systemai", DisplayName = "systemAIModels is not a rescap capability")]
    [DataRow("rescap:microphone", "DeviceCapability", DisplayName = "microphone is a DeviceCapability")]
    [DataRow("app:broadFileSystemAccess", "rescap", DisplayName = "a restricted capability is not a general one")]
    [DataRow("uap:runFullTrust", "rescap", DisplayName = "runFullTrust is a rescap capability")]
    [DataRow("device:internetClient", "Capability", DisplayName = "a general capability is not a DeviceCapability")]
    public void Parse_KnownNameWithConflictingPrefix_IsRejected(string value, string expectedInError)
    {
        // The whole point of the catalog: a capability emitted in the wrong namespace or element makes
        // Windows register the app and silently not grant it. Honoring an explicit-but-wrong prefix
        // would reintroduce exactly that failure.
        Assert.IsFalse(AppxCapabilityCatalog.TryParse(value, out var caps, out var error));
        Assert.AreEqual(0, caps.Count);
        StringAssert.Contains(error, expectedInError, "The error should name the correct declaration");
    }

    [TestMethod]
    [DataRow("systemai:systemAIModels", DisplayName = "matching namespace prefix")]
    [DataRow("device:microphone", DisplayName = "matching device prefix")]
    [DataRow("app:internetClient", DisplayName = "matching foundation prefix")]
    [DataRow("rescap:runFullTrust", DisplayName = "matching rescap prefix")]
    public void Parse_KnownNameWithMatchingPrefix_IsAccepted(string value)
    {
        // Spelling out the correct prefix is redundant, not wrong — it must keep working.
        Assert.IsTrue(AppxCapabilityCatalog.TryParse(value, out var caps, out _));
        Assert.AreEqual(1, caps.Count);
    }

    [TestMethod]
    [DataRow("rescap:RUNFULLTRUST", "runFullTrust", DisplayName = "upper-cased rescap name")]
    [DataRow("device:Microphone", "microphone", DisplayName = "title-cased device name")]
    [DataRow("app:InternetClient", "internetClient", DisplayName = "title-cased foundation name")]
    [DataRow("SYSTEMAI:systemaimodels", "systemAIModels", DisplayName = "lower-cased systemai name")]
    public void Parse_KnownNameInAnyCasing_EmitsTheCanonicalSpelling(string value, string expected)
    {
        // Lookup is case-insensitive but the manifest schema's enumerations are ordinal, so carrying the
        // caller's casing through produces a capability Windows registers and does not grant.
        Assert.IsTrue(AppxCapabilityCatalog.TryParse(value, out var caps, out var error), error);
        Assert.AreEqual(1, caps.Count);
        Assert.AreEqual(expected, caps[0].Name);
    }

    [TestMethod]
    [DataRow("{A5DCBF10-6530-11D2-901F-00C04FB951ED}", DisplayName = "canonical device-interface GUID")]
    [DataRow("{a5dcbf10-6530-11d2-901f-00c04fb951ed}", DisplayName = "lower case")]
    public void Parse_DeviceCapabilityGuid_IsAccepted(string interfaceClass)
    {
        Assert.IsTrue(AppxCapabilityCatalog.TryParse($"device:{interfaceClass}", out var caps, out var error), error);
        Assert.AreEqual("DeviceCapability", caps[0].ElementName);
    }

    [TestMethod]
    [DataRow("{A5DCBF10-65306-11D2-901F-00C04FB951E}", DisplayName = "hyphens in the wrong places")]
    [DataRow("{A5DCBF10653011D2901F00C04FB951ED--}", DisplayName = "no hyphens, padded to 36")]
    [DataRow("{A5DCBF10-6530-11D2-901F-00C04FB951EG}", DisplayName = "non-hex character")]
    [DataRow("{A5DCBF10-6530-11D2-901F-00C04FB951E}", DisplayName = "too short")]
    public void Parse_MalformedDeviceCapabilityGuid_IsRejected(string interfaceClass)
    {
        // A character-class regex accepts any 36 hex-or-hyphen characters, so these used to pass here and
        // fail only at registration — which reports an opaque schema error naming no capability.
        Assert.IsFalse(AppxCapabilityCatalog.TryParse($"device:{interfaceClass}", out _, out _), interfaceClass);
    }

    [TestMethod]
    public void Parse_KnownFoundationCapabilityUnderAppPrefix_IsAccepted()
    {
        Assert.IsTrue(AppxCapabilityCatalog.TryParse("app:internetClient", out var caps, out _));
        Assert.AreEqual(1, caps.Count);
        Assert.AreEqual("internetClient", caps[0].Name);
        Assert.IsNull(caps[0].Prefix, "A foundation capability is emitted unprefixed");
    }

    [TestMethod]
    [DataRow("usb", DisplayName = "usb needs Device/Function children")]
    [DataRow("humaninterfacedevice", DisplayName = "HID needs Device/Function children")]
    [DataRow("serialcommunication", DisplayName = "serial needs Device/Function children")]
    [DataRow("device:usb", DisplayName = "the device: prefix is not a bypass")]
    public void Parse_CapabilityNeedingChildElements_IsRejected(string value)
    {
        // A bare <DeviceCapability Name="usb" /> grants nothing: the device class and function live in
        // child elements a flat property cannot carry. Emitting it would produce a manifest that either
        // fails schema validation or registers and silently grants no access.
        Assert.IsFalse(AppxCapabilityCatalog.TryParse(value, out _, out var error));
        StringAssert.Contains(error, "WinAppManifestPath", "The error should point at the authored-manifest escape hatch");
    }

    [TestMethod]
    public void Parse_DeviceCapabilityWithoutChildElements_IsStillAccepted()
    {
        // Only the ones that genuinely need children are rejected; the rest are complete on their own.
        Assert.IsTrue(AppxCapabilityCatalog.TryParse("microphone;webcam;location", out var caps, out _));
        Assert.AreEqual(3, caps.Count);
        Assert.IsTrue(caps.All(c => c.IsDeviceCapability));
    }

    #endregion
}
