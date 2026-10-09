// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// Keeps the set of commands that honour <c>--on</c> honest, and where it is advertised.
/// </summary>
[TestClass]
public partial class ExecutionTargetSelectionTests : BaseCommandTests
{
    /// <summary>The trees that dispatch commands to an execution target.</summary>
    private static readonly string[] Expected = ["run", "ui", "unregister", "devtools"];

    protected override IServiceCollection ConfigureServices(IServiceCollection services) => services;

    [TestMethod]
    [DataRow("ui inspect --on sandbox --help")]
    [DataRow("ui inspect --on sandbox -h")]
    [DataRow("ui inspect --on sandbox -?")]
    [DataRow("ui inspect --on sandbox --depth notanumber")]
    [DataRow("run --on sandbox --help")]
    [DataRow("devtools inspect --on sandbox --help")]
    public void MetaActionsAndParserErrors_DoNotRouteOrProvision(string arguments)
    {
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(arguments);
        Assert.IsFalse(ExecutionTargetSelection.IsCommandInvocation(parsed));
        Assert.IsFalse(ExecutionTargetUiRouter.ShouldRoute(parsed));
    }

    [TestMethod]
    [DataRow("list")]
    [DataRow("get c1")]
    [DataRow("update c1 --status stale")]
    [DataRow("delete c1")]
    public void SavedComments_RejectTargetsWithoutLiveWork(string command)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        var local = root.Parse("devtools comments " + command);
        Assert.IsEmpty(local.Errors);
        Assert.IsNull(ExecutionTargetSelection.Validate(local));
        Assert.IsFalse(ExecutionTargetDevToolsRouter.ShouldRoute(local));
        foreach (var target in new[] { "local", "sandbox" })
        {
            var parsed = root.Parse($"devtools comments {command} --on {target}");
            Assert.IsEmpty(parsed.Errors);
            var error = ExecutionTargetSelection.Validate(parsed);
            Assert.IsNotNull(error);
            StringAssert.Contains(error.UserAction, "--source-root");
            Assert.IsFalse(ExecutionTargetDevToolsRouter.ShouldRoute(parsed));
        }
    }

    [TestMethod]
    [DataRow("list")]
    [DataRow("get c1")]
    public void SavedCommentReads_ReadTheLocalStoreEvenWithAnApp(string command)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        var parsed = root.Parse($"devtools comments {command} --app guest:{new string('a', 32)}");
        Assert.IsEmpty(parsed.Errors);
        Assert.IsFalse(ExecutionTargetDevToolsRouter.ShouldRoute(parsed), "--app names a local app's project; it never routes to a guest.");
    }

    [TestMethod]
    [DataRow("update c1 --status stale")]
    [DataRow("delete c1")]
    public void ExplicitCommentRefresh_RemainsTargetAware(string command)
    {
        var parsed = GetRequiredService<WinAppRootCommand>().Parse(
            $"devtools comments {command} --on sandbox --app guest:{new string('a', 32)}");
        Assert.IsEmpty(parsed.Errors);
        Assert.IsNull(ExecutionTargetSelection.Validate(parsed));
        Assert.IsTrue(ExecutionTargetDevToolsRouter.ShouldRoute(parsed));
    }

    [TestMethod]
    public void OrdinaryGuestInspection_StillRoutesBeforeLocalHandler()
    {
        var parsed = GetRequiredService<WinAppRootCommand>().Parse("ui inspect --on sandbox --json");
        Assert.IsTrue(ExecutionTargetSelection.IsCommandInvocation(parsed));
        Assert.IsTrue(ExecutionTargetUiRouter.ShouldRoute(parsed));
    }

    /// <summary>
    /// Adding <c>ITargetAwareCommand</c> to a command is a public promise that it can run somewhere
    /// else. Making that deliberate is the point: a command that claims it without a routing path
    /// would accept <c>--on</c> and then run here anyway.
    /// </summary>
    [TestMethod]
    public void OnlyTheDesignedCommandsAreTargetAware()
    {
        var root = GetRequiredService<WinAppRootCommand>();

        var actual = root.Subcommands
            .OfType<ITargetAwareCommand>()
            .Cast<Command>()
            .Select(command => command.Name)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(Expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(), actual);
    }

    /// <summary>
    /// <c>--on</c> is shown only where it works. Every command still parses it (so it can be
    /// rejected rather than absorbed by a positional argument), but help, completion, and the CLI
    /// schema advertise it only on target-aware commands.
    /// </summary>
    [TestMethod]
    [DataRow(new[] { "run", "--help" }, true)]
    [DataRow(new[] { "unregister", "--help" }, true)]
    [DataRow(new[] { "ui", "--help" }, true)]
    [DataRow(new[] { "ui", "click", "--help" }, true)]
    [DataRow(new[] { "--help" }, false)]
    [DataRow(new[] { "init", "--help" }, false)]
    [DataRow(new[] { "cert", "generate", "--help" }, false)]
    [DataRow(new[] { "target", "exec", "--help" }, false)]
    public async Task Help_ShowsTheSelectorOnlyOnTargetAwareCommands(string[] args, bool expected)
    {
        var root = GetRequiredService<WinAppRootCommand>();

        Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(root, args));

        Assert.AreEqual(
            expected,
            SelectorInHelpPattern().IsMatch(TestAnsiConsole.Output),
            $"'winapp {string.Join(' ', args)}' {(expected ? "must" : "must not")} list --on.");
    }

    [TestMethod]
    public void TargetAwareCommands_DeclareTheVisibleSelector()
    {
        var root = GetRequiredService<WinAppRootCommand>();

        foreach (var command in root.Subcommands.OfType<ITargetAwareCommand>().Cast<Command>())
        {
            CollectionAssert.Contains(command.Options.ToList(), ExecutionTargetSelection.OnOption, command.Name);
        }

        Assert.IsTrue(ExecutionTargetSelection.UnsupportedOnOption.Hidden);
        CollectionAssert.DoesNotContain(root.Options.ToList(), ExecutionTargetSelection.OnOption);
    }

    [GeneratedRegex(@"(^|\s)--on(\s|,|$)", RegexOptions.Multiline)]
    private static partial Regex SelectorInHelpPattern();
}
