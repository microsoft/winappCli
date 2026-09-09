// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;
using WinApp.Cli.ExecutionTargets.Abstractions;
using WinApp.Cli.ExecutionTargets.Orchestration;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

/// <summary>
/// What the parser must never let through when a command names an execution target.
/// </summary>
/// <remarks>
/// <para>
/// These are release-blocking. System.CommandLine binds an unrecognised token to a nearby optional
/// positional argument instead of failing, and the version this build uses does exactly that with a
/// misspelt <c>--on</c>: <c>winapp ui inspect --onn=sandbox -a MyApp</c> parsed cleanly, quietly
/// became an element selector, drove the <em>host</em> desktop, and exited zero. The user asked for
/// another machine and was told it worked.
/// </para>
/// <para>
/// So the rules asserted here are all shapes of one rule: a command line that does not
/// unambiguously name a usable target must fail before anything runs, and must never fall back to
/// this machine.
/// </para>
/// </remarks>
[TestClass]
public class TargetSelectionParserTests : BaseCommandTests
{
    protected override IServiceCollection ConfigureServices(IServiceCollection services) => services;

    // ---- '--on' is understood by every command ------------------------------------

    /// <summary>
    /// The core fail-open case. Every target-aware verb must parse <c>--on</c> as an option rather
    /// than letting its value fall into a positional argument.
    /// </summary>
    [TestMethod]
    [DataRow("ui", "inspect")]
    [DataRow("ui", "screenshot")]
    [DataRow("ui", "click")]
    [DataRow("ui", "send-keys")]
    [DataRow("ui", "list-windows")]
    [DataRow("ui", "record")]
    [DataRow("ui", "wait-for")]
    [DataRow("ui", "set-value")]
    public void TargetAwareUiVerb_AcceptsTheSelectorInEverySpelling(string group, string verb)
    {
        foreach (var form in (string[][])[
            [group, verb, "--on", "sandbox", "-a", "MyApp"],
            [group, verb, "--on=sandbox", "-a", "MyApp"]])
        {
            var parsed = Parse(form);

            Assert.IsNull(
                ExecutionTargetSelection.Validate(parsed),
                $"'{string.Join(' ', form)}' names a supported target and must be accepted.");
            Assert.AreEqual(
                ExecutionTargetRef.SandboxKind,
                ExecutionTargetSelection.Resolve(parsed).Kind,
                string.Join(' ', form));
        }
    }

    [TestMethod]
    [DataRow("run")]
    [DataRow("unregister")]
    public void TargetAwareCommand_ResolvesTheSelector(string command)
    {
        var parsed = Parse([command, "--on", "sandbox"]);

        Assert.IsNull(ExecutionTargetSelection.Validate(parsed));
        Assert.AreEqual(ExecutionTargetRef.SandboxKind, ExecutionTargetSelection.Resolve(parsed).Kind);
    }

    [TestMethod]
    public void OmittedSelector_MeansThisMachine()
    {
        var parsed = Parse(["ui", "inspect", "-a", "MyApp"]);

        Assert.IsNull(ExecutionTargetSelection.Validate(parsed));
        Assert.IsTrue(ExecutionTargetSelection.Resolve(parsed).IsLocal);
    }

    // ---- Bad selectors fail closed -------------------------------------------------

    /// <summary>
    /// A kind this build cannot run against is refused, including the ones the design reserves for
    /// later. Reserving a name in the parser without a provider behind it would only produce a
    /// worse error further in.
    /// </summary>
    [TestMethod]
    [DataRow("bogus")]
    [DataRow("desktop")]
    [DataRow("hyperv")]
    [DataRow("hyperv:WinAppTest")]
    [DataRow("sandbox:other")]
    [DataRow(":")]
    [DataRow(":sandbox")]
    [DataRow("sandbox:")]
    public void UnusableSelector_IsRefusedBeforeAnythingRuns(string selector)
    {
        var parsed = Parse(["ui", "inspect", "--on", selector, "-a", "MyApp"]);
        var error = ExecutionTargetSelection.Validate(parsed);

        Assert.IsNotNull(error, $"'{selector}' does not name a usable target.");
        Assert.AreEqual(ExecutionTargetErrorCodes.TargetInvalid, error.Code);
    }

    /// <summary>A selector is matched case-insensitively on the kind, as the design states.</summary>
    [TestMethod]
    [DataRow("SANDBOX")]
    [DataRow("Sandbox")]
    [DataRow("sandbox:DEFAULT")]
    public void SelectorKindMatching_IsCaseInsensitive(string selector)
    {
        var parsed = Parse(["ui", "inspect", "--on", selector, "-a", "MyApp"]);

        Assert.IsNull(ExecutionTargetSelection.Validate(parsed));
        Assert.AreEqual(ExecutionTargetRef.SandboxKind, ExecutionTargetSelection.Resolve(parsed).Kind);
    }

    /// <summary>
    /// A missing value is a parse error, and a parse error never dispatches. The important part is
    /// that nothing resolves to this machine on the way past.
    /// </summary>
    [TestMethod]
    public void SelectorWithNoValue_IsAParseError()
    {
        var parsed = Parse(["ui", "inspect", "--on"]);

        Assert.IsGreaterThan(0, parsed.Errors.Count, "'--on' with no value must not parse.");
    }

    // ---- Commands that cannot honour a target say so -------------------------------

    /// <summary>
    /// Silently ignoring <c>--on</c> would run the command here after the user asked for somewhere
    /// else, which is the one outcome the option exists to prevent.
    /// </summary>
    [TestMethod]
    [DataRow("get-winapp-path")]
    [DataRow("tool")]
    [DataRow("restore")]
    [DataRow("update")]
    [DataRow("complete")]
    public void NonTargetAwareCommand_RefusesTheSelector(string command)
    {
        var parsed = Parse([command, "--on", "sandbox"]);
        var error = ExecutionTargetSelection.Validate(parsed);

        Assert.IsNotNull(error, $"'winapp {command}' must reject --on rather than ignore it.");
        Assert.AreEqual(ExecutionTargetErrorCodes.TargetInvalid, error.Code);
        StringAssert.Contains(error.Message, "does not accept --on");
    }

    /// <summary>
    /// When the command line is broken in more than one way, the parser's own error is reported
    /// first. What matters is that the command still fails rather than running here.
    /// </summary>
    [TestMethod]
    public void CommandWithItsOwnParseError_StillFailsClosed()
    {
        var parsed = Parse(["cert", "--on", "sandbox"]);

        Assert.IsGreaterThan(0, parsed.Errors.Count);
        Assert.IsNull(
            ExecutionTargetSelection.Validate(parsed),
            "A command line the parser already rejected is reported by its own error path.");
    }

    /// <summary>
    /// <c>winapp target</c> takes its target as an argument, so <c>--on</c> there would be a second,
    /// competing way to say the same thing.
    /// </summary>
    [TestMethod]
    public void TargetNamespace_RefusesTheSelectorOption()
    {
        var parsed = Parse(["target", "exec", "sandbox", "--on", "sandbox", "--", "cmd"]);

        Assert.IsNotNull(ExecutionTargetSelection.Validate(parsed));
    }

    // ---- Misspelt options never become positional values ---------------------------

    /// <summary>
    /// The reported defect, pinned. Each of these previously bound to an optional positional and
    /// ran on the host desktop.
    /// </summary>
    [TestMethod]
    [DataRow("ui", "inspect", "--onn=sandbox")]
    [DataRow("ui", "inspect", "--ON=sandbox")]
    [DataRow("ui", "inspect", "--on-sandbox")]
    [DataRow("ui", "inspect", "--sandbox")]
    [DataRow("ui", "search", "--onn=sandbox")]
    [DataRow("ui", "set-value", "--onn=sandbox")]
    public void MisspeltOption_IsNeverBoundToAPositional(string group, string verb, string typo)
    {
        var parsed = Parse([group, verb, typo, "-a", "MyApp"]);

        var stray = WindowsCommandLine.FindOptionLikePositionals(parsed);

        Assert.IsTrue(
            stray.Contains(typo) || parsed.Errors.Count > 0,
            $"'{typo}' must be rejected, not silently accepted as a positional value.");
    }

    /// <summary>
    /// The escape hatch, and the reason the guard only looks before <c>--</c>: a value that really
    /// does start with a dash is still expressible.
    /// </summary>
    [TestMethod]
    public void ValueAfterTheSeparator_IsNotTreatedAsAMisspeltOption()
    {
        var parsed = Parse(["ui", "set-value", "-a", "MyApp", "Field", "--", "--not-an-option"]);

        Assert.IsEmpty(WindowsCommandLine.FindOptionLikePositionals(parsed));
    }

    /// <summary>A negative number is an ordinary value for a slider, not a misspelt option.</summary>
    [TestMethod]
    public void NegativeNumberValue_IsNotTreatedAsAMisspeltOption()
    {
        var parsed = Parse(["ui", "set-value", "-a", "MyApp", "Slider", "-5"]);

        Assert.IsEmpty(WindowsCommandLine.FindOptionLikePositionals(parsed));
    }

    /// <summary>An ordinary command line has nothing for the guard to report.</summary>
    [TestMethod]
    public void WellFormedCommand_HasNoStrayPositionals()
    {
        Assert.IsEmpty(WindowsCommandLine.FindOptionLikePositionals(
            Parse(["ui", "inspect", "--on", "sandbox", "-a", "MyApp", "--depth", "8"])));
    }

    // ---- 'winapp target' selector validation ---------------------------------------

    /// <summary>
    /// The omitted-selector case that matters most: without validation, the first path would become
    /// the target and the copy would silently go somewhere else.
    /// </summary>
    [TestMethod]
    public void TargetPush_WithoutASelector_NeverBindsAPathAsTheTarget()
    {
        var parsed = Parse(["target", "push", @".\setup.ps1", @"Setup\setup.ps1"]);

        var selector = parsed.GetValue(TargetPushCommand.SelectorArgument);

        Assert.ThrowsExactly<ExecutionTargetException>(
            () => ExecutionTargetSelector.Parse(selector),
            "A path must never be accepted as a target selector.");
    }

    /// <summary>
    /// The same for <c>exec</c>, where the value that would be stolen is the executable.
    /// </summary>
    [TestMethod]
    public void TargetExec_WithoutASelector_NeverBindsTheExecutableAsTheTarget()
    {
        var parsed = Parse(["target", "exec", "--", "dotnet", "--info"]);

        Assert.ThrowsExactly<ExecutionTargetException>(
            () => ExecutionTargetSelector.Parse(parsed.GetValue(TargetExecCommand.SelectorArgument)));
    }

    [TestMethod]
    public void TargetExec_WithASelector_KeepsEverythingAfterTheSeparatorVerbatim()
    {
        var parsed = Parse(["target", "exec", "sandbox", "--", "dotnet", "--info", "--on", "sandbox"]);

        CollectionAssert.AreEqual(
            (string[])["dotnet", "--info", "--on", "sandbox"],
            parsed.GetValue(TargetExecCommand.CommandArgument));
    }

    /// <summary>A command with no <c>--</c> still runs, because its first token is not option-like.</summary>
    [TestMethod]
    public void TargetExec_WithoutASeparator_StillTakesTheCommand()
    {
        var parsed = Parse(["target", "exec", "sandbox", "dotnet"]);

        Assert.AreEqual("sandbox", parsed.GetValue(TargetExecCommand.SelectorArgument));
        CollectionAssert.AreEqual((string[])["dotnet"], parsed.GetValue(TargetExecCommand.CommandArgument));
    }

    /// <summary>Paths with spaces and non-ASCII characters survive as ordinary values.</summary>
    [TestMethod]
    [DataRow(@"C:\Program Files\My App\setup.ps1", @"Setup\my app\setup.ps1")]
    [DataRow(@"C:\Ünïcödé\ファイル.txt", @"Ünïcödé\ファイル.txt")]
    public void TargetPush_PreservesSpacesAndUnicodeInBothPaths(string source, string destination)
    {
        var parsed = Parse(["target", "push", "sandbox", source, destination]);

        Assert.AreEqual("sandbox", parsed.GetValue(TargetPushCommand.SelectorArgument));
        Assert.AreEqual(source, parsed.GetValue(TargetPushCommand.SourceArgument));
        Assert.AreEqual(destination, parsed.GetValue(TargetPushCommand.DestinationArgument));
    }

    /// <summary>
    /// An option-looking path is refused rather than silently dropped, and <c>--</c> is how a caller
    /// who genuinely has one says so.
    /// </summary>
    [TestMethod]
    public void TargetPush_OptionLookingPath_IsRefusedUnlessItFollowsTheSeparator()
    {
        var refused = Parse(["target", "push", "sandbox", "--weird-name", "dest.txt"]);

        Assert.IsTrue(
            WindowsCommandLine.FindOptionLikePositionals(refused).Contains("--weird-name") ||
            refused.Errors.Count > 0);
    }

    // ---- Transfers reject a bad command line before touching the target ------------

    /// <summary>
    /// Preparing a target starts or adopts a Windows Sandbox, takes the mutation lock, and changes
    /// machine state. None of that should happen because a path was mistyped — and the failure a
    /// caller sees must say "your command line is wrong", not "the target could not be used".
    /// </summary>
    [TestMethod]
    [DataRow(@"C:\Setup\setup.ps1", DisplayName = "drive-absolute target path")]
    [DataRow(@"\\server\share\setup.ps1", DisplayName = "UNC target path")]
    [DataRow(@"\rooted\setup.ps1", DisplayName = "rooted target path")]
    [DataRow(@"..\outside\setup.ps1", DisplayName = "target path leaving the work root")]
    public void TargetTransfer_UnusableTargetPath_IsRefusedWhileBuildingTheRequest(string targetPath)
    {
        Assert.ThrowsExactly<ExecutionTargetException>(() => TargetTransferRequest.Create(
            TargetTransferDirection.ToTarget, hostPath: ".", targetPath: targetPath));
    }

    [TestMethod]
    public void TargetPush_SourceThatDoesNotExist_IsRefusedWhileBuildingTheRequest()
    {
        var missing = Path.Join(Path.GetTempPath(), $"winapp-missing-{Guid.NewGuid():n}.txt");

        var failure = Assert.ThrowsExactly<ExecutionTargetException>(() => TargetTransferRequest.Create(
            TargetTransferDirection.ToTarget, hostPath: missing, targetPath: @"work\file.txt"));

        Assert.AreEqual(ExecutionTargetErrorCodes.TargetInvalid, failure.Error.Code);
    }

    /// <summary>
    /// A linked source is refused twice: here, so a bad path costs nothing, and again during the
    /// walk, which is the check that actually holds against a link planted mid-copy.
    /// </summary>
    [TestMethod]
    public void TargetPush_LinkedSource_IsRefusedWhileBuildingTheRequest()
    {
        var root = Path.Join(Path.GetTempPath(), $"winapp-link-{Guid.NewGuid():n}");
        var real = Path.Join(root, "real");
        var link = Path.Join(root, "link");

        Directory.CreateDirectory(real);

        try
        {
            Directory.CreateSymbolicLink(link, real);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Directory.Delete(root, recursive: true);
            Assert.Inconclusive("Creating a symbolic link needs Developer Mode or elevation.");
            return;
        }

        try
        {
            var failure = Assert.ThrowsExactly<ExecutionTargetException>(() => TargetTransferRequest.Create(
                TargetTransferDirection.ToTarget, hostPath: link, targetPath: "work"));

            Assert.AreEqual(ExecutionTargetErrorCodes.TargetInvalid, failure.Error.Code);
        }
        finally
        {
            Directory.Delete(link);
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A pull's host side is a destination, so it does not have to exist yet. Only the target side
    /// is checkable without the target, and only that is checked.
    /// </summary>
    [TestMethod]
    public void TargetPull_DestinationThatDoesNotExistYet_IsAccepted()
    {
        var destination = Path.Join(Path.GetTempPath(), $"winapp-pull-{Guid.NewGuid():n}");

        var request = TargetTransferRequest.Create(
            TargetTransferDirection.FromTarget, hostPath: destination, targetPath: "Results");

        Assert.AreEqual(TargetTransferDirection.FromTarget, request.Direction);
        Assert.AreEqual(Path.GetFullPath(destination), request.HostPath);
        Assert.AreEqual("Results", request.TargetPath);
    }

    /// <summary>The verb decides the direction, and each side keeps the path it was given.</summary>
    [TestMethod]
    public void TargetTransfer_MapsEachPathToTheSideItsVerbNames()
    {
        var here = Directory.GetCurrentDirectory();

        var push = TargetTransferRequest.Create(TargetTransferDirection.ToTarget, here, @"work\in");
        Assert.AreEqual(Path.GetFullPath(here), push.HostPath);
        Assert.AreEqual(@"work\in", push.TargetPath);

        var pull = TargetTransferRequest.Create(TargetTransferDirection.FromTarget, here, @"work\out");
        Assert.AreEqual(Path.GetFullPath(here), pull.HostPath);
        Assert.AreEqual(@"work\out", pull.TargetPath);
    }

    /// <summary>
    /// A command line the parser rejected must be reported here, not on the target. Routing first
    /// means a mistyped `--depth` spends minutes preparing a Sandbox, fails with a transport error
    /// that never mentions `--depth`, and leaves the Sandbox running.
    /// </summary>
    [TestMethod]
    [DataRow("--depth", "notanumber")]
    [DataRow("--timeout", "notanumber")]
    [DataRow("--max", "notanumber")]
    public void CommandWithAParseError_IsNotRoutedToTheTarget(string option, string value)
    {
        var parsed = Parse(["ui", "inspect", "--on", "sandbox", "-a", "MyApp", option, value]);

        Assert.IsGreaterThan(0, parsed.Errors.Count, $"'{option} {value}' must not parse.");

        // ShouldRoute answers only about the selector; Program gates it on there being no parse
        // error, which is what this asserts alongside.
        Assert.IsTrue(
            ExecutionTargetUiRouter.ShouldRoute(parsed),
            "The selector is still valid, so the guard has to be the parse-error check.");
    }

    /// <summary>
    /// An option-looking positional is caught even when the selector itself is fine, so the target
    /// is never prepared for a command line that was going to fail anyway.
    /// </summary>
    [TestMethod]
    public void MisspeltOptionAlongsideAValidSelector_IsStillRejected()
    {
        var parsed = Parse(["ui", "inspect", "--on", "sandbox", "--dpeth=8", "-a", "MyApp"]);

        Assert.IsTrue(
            WindowsCommandLine.FindOptionLikePositionals(parsed).Contains("--dpeth=8") ||
            parsed.Errors.Count > 0);
    }

    private ParseResult Parse(string[] arguments) =>
        GetRequiredService<WinAppRootCommand>().Parse(arguments, WinAppParserConfiguration.Default);
}
