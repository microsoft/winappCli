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
    /// <summary>The trees the design says accept a target in Stage 1.</summary>
    private static readonly string[] Expected = ["run", "ui", "unregister"];

    protected override IServiceCollection ConfigureServices(IServiceCollection services) => services;

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
