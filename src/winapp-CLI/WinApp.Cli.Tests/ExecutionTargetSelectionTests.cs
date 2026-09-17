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
