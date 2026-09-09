// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="NugetErrorMessage"/>. NuGet embeds the full source URL in its error text, and feeds
/// commonly authenticate with a signed query string or embedded user-info, so forwarding that text verbatim
/// prints a credential to the console and into CI logs.
/// </summary>
[TestClass]
public class NugetErrorMessageTests
{
    [TestMethod]
    public void Redact_QueryStringToken_IsRemovedButSourceStillIdentifiable()
    {
        var message = "Unable to load the service index for source https://contoso.pkgs.visualstudio.com/_packaging/feed/nuget/v3/index.json?sig=SECRETVALUE.";

        var redacted = NugetErrorMessage.Redact(message);

        Assert.DoesNotContain("SECRETVALUE", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("sig=", redacted, StringComparison.Ordinal);
        // The failure is only actionable if the user can still tell which feed it was.
        StringAssert.Contains(redacted, "contoso.pkgs.visualstudio.com", StringComparison.Ordinal);
        StringAssert.Contains(redacted, "/_packaging/feed/nuget/v3/index.json", StringComparison.Ordinal);
        StringAssert.Contains(redacted, "?<redacted>", StringComparison.Ordinal);
        // The sentence-ending period must survive rather than being parsed as part of the URI.
        Assert.EndsWith(".", redacted, StringComparison.Ordinal);
    }

    [TestMethod]
    public void Redact_EmbeddedUserInfo_IsRemoved()
    {
        var message = "Response status code does not indicate success: 401 for https://user:hunter2@nuget.contoso.com/v3/index.json";

        var redacted = NugetErrorMessage.Redact(message);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("user:", redacted, StringComparison.Ordinal);
        StringAssert.Contains(redacted, "https://nuget.contoso.com/v3/index.json", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Redact_NonDefaultPort_IsPreserved()
    {
        var redacted = NugetErrorMessage.Redact("Unable to load the service index for source https://127.0.0.1:8443/v3/index.json?token=SECRET.");

        Assert.DoesNotContain("SECRET", redacted, StringComparison.Ordinal);
        StringAssert.Contains(redacted, "https://127.0.0.1:8443/v3/index.json", StringComparison.Ordinal);
    }

    /// <summary>
    /// A URL with nothing sensitive must read exactly as NuGet wrote it, so the common failure keeps its
    /// original wording and stays greppable.
    /// </summary>
    [TestMethod]
    public void Redact_UrlWithoutSecrets_IsUnchanged()
    {
        var message = "Unable to load the service index for source https://api.nuget.org/v3/index.json.";

        Assert.AreEqual(message, NugetErrorMessage.Redact(message));
    }

    [TestMethod]
    public void Redact_MessageWithoutUri_IsUnchanged()
    {
        var message = "Package 'Contoso.Widgets' is not found on source(s).";

        Assert.AreEqual(message, NugetErrorMessage.Redact(message));
    }

    [TestMethod]
    public void Redact_LocalFeedPath_IsUnchanged()
    {
        var message = @"The local source 'C:\feeds\local?weird' doesn't exist.";

        Assert.AreEqual(message, NugetErrorMessage.Redact(message));
    }

    [TestMethod]
    public void Redact_MultipleSources_AllAreRedacted()
    {
        var message = "Tried https://a.example.com/v3/index.json?k=AAA and https://b.example.com/v3/index.json?k=BBB.";

        var redacted = NugetErrorMessage.Redact(message);

        Assert.DoesNotContain("AAA", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("BBB", redacted, StringComparison.Ordinal);
        StringAssert.Contains(redacted, "a.example.com", StringComparison.Ordinal);
        StringAssert.Contains(redacted, "b.example.com", StringComparison.Ordinal);
    }

    /// <summary>
    /// The helper is applied at several message-forwarding sites, and a message can pass through more than
    /// one of them, so redacting an already-redacted message must be a no-op.
    /// </summary>
    [TestMethod]
    public void Redact_IsIdempotent()
    {
        var once = NugetErrorMessage.Redact("Unable to load the service index for source https://contoso.com/v3/index.json?sig=SECRETVALUE.");

        Assert.AreEqual(once, NugetErrorMessage.Redact(once));
    }

    [TestMethod]
    public void Redact_NullOrEmpty_ReturnsEmpty()
    {
        Assert.AreEqual(string.Empty, NugetErrorMessage.Redact(null));
        Assert.AreEqual(string.Empty, NugetErrorMessage.Redact(string.Empty));
    }
}
