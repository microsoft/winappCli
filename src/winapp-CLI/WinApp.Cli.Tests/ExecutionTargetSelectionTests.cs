// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// Keeps the set of commands that honour <c>--on</c> honest, in both languages that describe it.
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
    /// The npm generator emits an <c>on</c> property only for these trees, because everywhere else
    /// the option exists solely to be rejected. The list is duplicated across a language boundary,
    /// so it is asserted rather than assumed.
    /// </summary>
    [TestMethod]
    public async Task TargetAwareCommands_MatchTheGeneratorList()
    {
        var generator = Path.Join(
            FindRepositoryRoot(), "src", "winapp-npm", "scripts", "generate-commands.mjs");

        var source = await File.ReadAllTextAsync(generator, TestContext.CancellationToken);
        var match = GeneratorListPattern().Match(source);

        Assert.IsTrue(match.Success, $"Could not find TARGET_AWARE_COMMANDS in {generator}.");

        var listed = match.Groups[1].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(entry => entry.Trim('\'', '"'))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        CollectionAssert.AreEqual(
            Expected.OrderBy(name => name, StringComparer.Ordinal).ToArray(),
            listed,
            "The npm generator's target-aware list has drifted from ITargetAwareCommand.");
    }


    [GeneratedRegex(@"const TARGET_AWARE_COMMANDS = \[([^\]]*)\]")]
    private static partial Regex GeneratorListPattern();

    private static string FindRepositoryRoot()
    {
        // Anchored on a file the repository is known to have rather than on `.git`, which is a file
        // rather than a directory inside a worktree.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null &&
               !File.Exists(Path.Join(directory.FullName, "scripts", "build-cli.ps1")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate the repository root from the test output directory.");
        return directory.FullName;
    }
}
