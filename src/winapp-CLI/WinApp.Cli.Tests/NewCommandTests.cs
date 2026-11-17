// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for the NewCommand: option/argument parsing, template alias mapping, and defaults.
/// </summary>
[TestClass]
public class NewCommandTests : BaseCommandTests
{
    [TestMethod]
    public void ScaffoldStatusDelay_IsTenSeconds()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), NewCommand.Handler.ScaffoldStatusDelay);
    }

    [TestMethod]
    public async Task AwaitWithDelayedActionAsync_DelayCompletesFirst_InvokesActionAndReturnsResult()
    {
        var operation = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionInvoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var resultTask = NewCommand.Handler.AwaitWithDelayedActionAsync(
            operation.Task,
            delay.Task,
            () => actionInvoked.SetResult());

        delay.SetResult();
        await actionInvoked.Task.WaitAsync(TestContext.CancellationToken);
        Assert.IsFalse(resultTask.IsCompleted);

        operation.SetResult(42);
        Assert.AreEqual(42, await resultTask);
    }

    [TestMethod]
    public async Task AwaitWithDelayedActionAsync_OperationCompletesFirst_DoesNotInvokeAction()
    {
        var delay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actionInvoked = false;

        var result = await NewCommand.Handler.AwaitWithDelayedActionAsync(
            Task.FromResult(42),
            delay.Task,
            () => actionInvoked = true);

        Assert.AreEqual(42, result);
        Assert.IsFalse(actionInvoked);
    }

    [TestMethod]
    public void Parse_NoArgs_HasNoErrors()
    {
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse([]);

        Assert.IsEmpty(parseResult.Errors,
            $"'winapp new' with no args should parse. Errors: {string.Join("; ", parseResult.Errors)}");
    }

    [TestMethod]
    public void Parse_NameOption_BindsName()
    {
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse(["--name", "MyApp"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.AreEqual("MyApp", parseResult.GetValue(NewCommand.NameOption));
    }

    [TestMethod]
    [DataRow("winui")]
    [DataRow("winui-navview")]
    [DataRow("winui-lib")]
    public void Parse_TemplateOption_BindsShortName(string shortName)
    {
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse(["--template", shortName]);

        Assert.IsEmpty(parseResult.Errors,
            $"Template '{shortName}' should parse. Errors: {string.Join("; ", parseResult.Errors)}");
        Assert.AreEqual(shortName, parseResult.GetValue(NewCommand.TemplateOption));
    }

    [TestMethod]
    public void Parse_TemplateOption_AcceptsArbitraryValueAtParseTime()
    {
        // Templates are now dynamic (enumerated from the installed pack), so --template is validated
        // against the live list at run time, not parse time. The parser therefore accepts any string.
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse(["--template", "bogus"]);

        Assert.IsEmpty(parseResult.Errors, "An unknown --template is validated at run time, not parse time.");
        Assert.AreEqual("bogus", parseResult.GetValue(NewCommand.TemplateOption));
    }

    [TestMethod]
    public void Parse_ListOption_BindsTrue()
    {
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse(["--list"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.IsTrue(parseResult.GetValue(NewCommand.ListOption));
    }

    [TestMethod]
    [DataRow("latest")]
    [DataRow("installed")]
    [DataRow("1.2.3")]
    public void Parse_TemplateVersionOption_BindsKeywordOrVersion(string value)
    {
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse(["--template-version", value]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.AreEqual(value, parseResult.GetValue(NewCommand.TemplateVersionOption));
    }

    [TestMethod]
    public void Parse_NoPromptAlias_MapsToUseDefaults()
    {
        var command = GetRequiredService<NewCommand>();

        var parseResult = command.Parse(["--no-prompt"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.IsTrue(parseResult.GetValue(NewCommand.UseDefaultsOption),
            "--no-prompt should map to the --use-defaults option.");
    }

    [TestMethod]
    [DataRow("0.0.6-alpha", true)]
    [DataRow("1.2.3", true)]
    [DataRow("1.0.0-preview.2", true)]
    [DataRow("1.0", true)]
    [DataRow("1.0.0+build.5", true)]  // valid build metadata
    [DataRow("1.2.3.4", true)]        // four numeric components is the NuGet maximum
    [DataRow("", false)]
    [DataRow("   ", false)]
    [DataRow("latest", false)]      // does not start with a digit
    [DataRow("1.0 --add-source http://evil", false)] // whitespace / injection shape
    [DataRow("1.0\"", false)]       // quote
    [DataRow("1.0-", false)]        // empty prerelease label
    [DataRow("1.0+", false)]        // empty build metadata
    [DataRow("1..0", false)]        // repeated separator / empty part
    [DataRow("1.0.0-", false)]      // trailing dash, empty prerelease
    [DataRow("1.0.0-alpha..1", false)] // empty prerelease identifier
    [DataRow("-alpha", false)]      // no numeric release
    [DataRow("1.2.3.4.5", false)]   // more than four numeric components
    public void IsPlausibleVersion_AcceptsOnlyVersionShapedInput(string version, bool expected)
    {
        Assert.AreEqual(expected, NuGetVersionHelper.IsPlausibleVersion(version));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_EquivalentNormalizedVersion_ReturnsTrue()
    {
        // NuGet normalizes equal versions and `dotnet new uninstall` prints the normalized form. A
        // request for "1.0" must match an installed "1.0.0.0"; otherwise the documented idempotency
        // breaks and every run repeats the install/network operation.
        var listing =
            "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "   Version: 1.0.0.0\n";

        Assert.IsTrue(NewCommand.IsTemplatePackInstalled(listing, "1.0"),
            "Version-equivalent spellings (1.0 vs 1.0.0.0) must be treated as installed.");
        Assert.IsTrue(NewCommand.IsTemplatePackInstalled(listing, "1.0.0"));
    }

    [TestMethod]
    [DataRow("1.0", "1.0.0")]
    [DataRow("1.0", "1.0.0.0")]
    [DataRow("1.2.3", "1.2.3.0")]
    [DataRow("1.0.0-Alpha", "1.0.0-alpha")]
    [DataRow("1.0.0+build.5", "1.0.0")]
    public void NuGetVersionsEquivalent_EqualVersions_ReturnsTrue(string a, string b)
    {
        Assert.IsTrue(NuGetVersionHelper.NuGetVersionsEquivalent(a, b), $"'{a}' and '{b}' should be equivalent.");
        Assert.IsTrue(NuGetVersionHelper.NuGetVersionsEquivalent(b, a), $"'{b}' and '{a}' should be equivalent.");
    }

    [TestMethod]
    [DataRow("1.0.0", "1.0.1")]
    [DataRow("1.0.0", "2.0.0")]
    [DataRow("1.0.0-alpha", "1.0.0")]
    [DataRow("1.0.0.1", "1.0.0")]
    [DataRow("1.0-", "1.0")]   // malformed request must not normalize into a match
    [DataRow("1.0+", "1.0")]   // malformed build metadata must not normalize into a match
    public void NuGetVersionsEquivalent_DifferentVersions_ReturnsFalse(string a, string b)
    {
        Assert.IsFalse(NuGetVersionHelper.NuGetVersionsEquivalent(a, b), $"'{a}' and '{b}' should not be equivalent.");
    }

    [TestMethod]
    public void IsTemplatePackInstalled_MatchingVersion_ReturnsTrue()
    {
        var listing =
            "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "   Version: 0.0.6-alpha\n" +
            "   Details: ...\n";

        Assert.IsTrue(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_DifferentVersion_ReturnsFalse()
    {
        var listing =
            "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "   Version: 0.0.5-alpha\n";

        Assert.IsFalse(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"),
            "An installed-but-different version must not satisfy an explicit --template-version.");
    }

    [TestMethod]
    public void IsTemplatePackInstalled_NotInstalled_ReturnsFalse()
    {
        var listing = "Some.Other.Template.Pack\n   Version: 1.0.0\n";

        Assert.IsFalse(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_MatchAfterOtherPackage_ReturnsTrue()
    {
        // The WinUI pack is listed after another package; its own indented Version line must be read.
        var listing =
            "Some.Other.Template.Pack\n" +
            "   Version: 9.9.9\n" +
            "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "   Version: 0.0.6-alpha\n";

        Assert.IsTrue(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_MalformedBlockWithoutVersion_ReturnsFalse()
    {
        // The WinUI pack header has no Version line before the next package header. The next
        // package's version must NOT be misattributed to the WinUI pack.
        var listing =
            "Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "Some.Other.Template.Pack\n" +
            "   Version: 0.0.6-alpha\n";

        Assert.IsFalse(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"),
            "A following package's version line must not be read as the WinUI pack version.");
    }

    [TestMethod]
    [DataRow("MyApp", true)]
    [DataRow("My.App", true)]
    [DataRow("My_App-1", true)]
    [DataRow("", false)]
    [DataRow("   ", false)]
    [DataRow(".", false)]
    [DataRow("..", false)]
    [DataRow(@"..\Escaped", false)]     // path traversal
    [DataRow("sub/dir", false)]          // forward-slash separator
    [DataRow(@"C:\rooted", false)]       // rooted path (contains ':' and '\')
    [DataRow("bad*name", false)]         // invalid filename char
    [DataRow("CON", false)]              // reserved device name
    [DataRow("con", false)]              // reserved device name (case-insensitive)
    [DataRow("LPT1", false)]             // reserved device name
    [DataRow("NUL.txt", false)]          // reserved device name with extension
    [DataRow("MyApp.", false)]           // trailing dot (invalid on Windows)
    [DataRow("MyApp ", false)]           // trailing space (invalid on Windows)
    public void IsValidProjectName_RejectsPathSeparatorsAndInvalidChars(string name, bool expected)
    {
        Assert.AreEqual(expected, NewCommand.IsValidProjectName(name));
    }

    [TestMethod]
    public void IsValidProjectName_EnforcesMaxLengthAccountingForCsprojSuffix()
    {
        // The scaffold writes "<name>.csproj", so the longest accepted name still fits within the
        // 255-character Windows path-component limit once ".csproj" is appended, and one character
        // longer is rejected up front instead of failing mid-scaffold.
        var maxName = new string('a', NewCommand.MaxProjectNameLength);
        var tooLong = new string('a', NewCommand.MaxProjectNameLength + 1);

        Assert.AreEqual(255, NewCommand.MaxProjectNameLength + ".csproj".Length,
            "MaxProjectNameLength must leave exactly room for the .csproj suffix within 255 chars.");
        Assert.IsTrue(NewCommand.IsValidProjectName(maxName), "A name at the maximum length must be accepted.");
        Assert.IsFalse(NewCommand.IsValidProjectName(tooLong), "A name one character over the limit must be rejected.");
    }

    [TestMethod]
    public void IsTemplatePackInstalled_RealDotnetFormat_ReturnsTrue()
    {
        // Mirrors actual `dotnet new uninstall` output: a "Currently installed items:" banner, then
        // the package id indented, with Version/Details/Templates nested one level deeper.
        var listing =
            "Currently installed items:\n" +
            "   Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "      Version: 0.0.6-alpha\n" +
            "      Details:\n" +
            "         Author: Microsoft\n" +
            "      Templates:\n" +
            "         WinUI Blank App (winui) C#\n";

        Assert.IsTrue(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"));
        Assert.IsFalse(NewCommand.IsTemplatePackInstalled(listing, "0.0.7-alpha"));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_CrlfLineEndings_ReturnsTrue()
    {
        // dotnet output on Windows is CRLF; splitting on '\n' leaves a trailing '\r' that must not
        // break header matching, indentation detection, or version parsing.
        var listing =
            "Currently installed items:\r\n" +
            "   Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\r\n" +
            "      Version: 0.0.6-alpha\r\n";

        Assert.IsTrue(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"));
    }

    [TestMethod]
    public void IsTemplatePackInstalled_IndentedNextPackage_DoesNotMisreadVersion()
    {
        // The WinUI pack header has no Version line before the next (equally indented) package. The
        // sibling package's version must NOT be attributed to the WinUI pack.
        var listing =
            "Currently installed items:\n" +
            "   Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "   Some.Other.Template.Pack\n" +
            "      Version: 0.0.6-alpha\n";

        Assert.IsFalse(NewCommand.IsTemplatePackInstalled(listing, "0.0.6-alpha"),
            "A sibling package's version line must not be read as the WinUI pack version.");
    }

    [TestMethod]
    public void TryGetInstalledPackVersion_ReturnsInstalledVersion()
    {
        var listing =
            "Currently installed items:\n" +
            "   Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
            "      Version: 0.0.7-alpha\n";

        Assert.IsTrue(NewCommand.TryGetInstalledPackVersion(listing, out var version));
        Assert.AreEqual("0.0.7-alpha", version);
    }

    [TestMethod]
    public void TryGetInstalledPackVersion_NotInstalled_ReturnsFalse()
    {
        Assert.IsFalse(NewCommand.TryGetInstalledPackVersion("Currently installed items:\n", out var version));
        Assert.IsNull(version);
    }

    private const string TwoPackCopiesListing =
        "Currently installed items:\n" +
        "   Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
        "      Version: 0.0.7-alpha\n" +
        "   Microsoft.WindowsAppSDK.WinUI.CSharp.Templates\n" +
        "      Version: 0.0.5-alpha\n";

    [TestMethod]
    public void TryGetInstalledPackVersion_TwoCopies_ReturnsHighestVersion()
    {
        Assert.IsTrue(NewCommand.TryGetInstalledPackVersion(TwoPackCopiesListing, out var version));
        Assert.AreEqual("0.0.7-alpha", version);
    }

    [TestMethod]
    public void GetInstalledPackVersions_TwoCopies_ReturnsBothInListingOrder()
    {
        CollectionAssert.AreEqual(
            TwoPackCopiesVersions,
            NewCommand.GetInstalledPackVersions(TwoPackCopiesListing).ToArray());
    }

    private static readonly string[] TwoPackCopiesVersions = ["0.0.7-alpha", "0.0.5-alpha"];

    [TestMethod]
    [DataRow("1.0.0", "1.0.0", 0)]
    [DataRow("1.0.0", "2.0.0", -1)]
    [DataRow("2.0.0", "1.0.0", 1)]
    [DataRow("1.2.0", "1.10.0", -1)]
    [DataRow("0.0.7-alpha", "0.0.6-alpha", 1)]
    [DataRow("0.0.6-alpha", "0.0.7-alpha", -1)]
    // A prerelease has lower precedence than the otherwise-equal release.
    [DataRow("1.0.0-alpha", "1.0.0", -1)]
    [DataRow("1.0.0", "1.0.0-alpha", 1)]
    // Numeric prerelease identifiers rank below alphanumeric ones and compare numerically.
    [DataRow("1.0.0-alpha.2", "1.0.0-alpha.10", -1)]
    [DataRow("1.0.0-1", "1.0.0-alpha", -1)]
    public void Compare_OrdersVersionsBySemVerPrecedence(string a, string b, int expectedSign)
    {
        var result = NuGetVersionHelper.Compare(a, b);
        Assert.IsNotNull(result);
        // Use GetValueOrDefault() rather than .Value: the IsNotNull assert already fails the test on
        // null, and this avoids a nullable-dereference the analyzer can't see through the assertion.
        Assert.AreEqual(expectedSign, Math.Sign(result.GetValueOrDefault()), $"Compare('{a}','{b}')");
    }

    [TestMethod]
    public void Compare_MalformedVersion_ReturnsNull()
    {
        Assert.IsNull(NuGetVersionHelper.Compare("not-a-version", "1.0.0"));
        Assert.IsNull(NuGetVersionHelper.Compare("1.0.0", ""));
    }
}
