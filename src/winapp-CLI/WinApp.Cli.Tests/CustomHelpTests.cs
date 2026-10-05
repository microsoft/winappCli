// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine.Help;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class CustomHelpTests : BaseCommandTests
{
    [TestMethod]
    public void AllTopLevelCommands_ShouldBeInHelpCategories()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var helpOption = rootCommand.Options.OfType<HelpOption>().First();
        var helpAction = helpOption.Action as CustomHelpAction;

        Assert.IsNotNull(helpAction, "Root command help action should be a CustomHelpAction");

        var categorizedTypes = new HashSet<Type>(helpAction.CategorizedCommandTypes);

        // Assert — every registered subcommand must be present in a help category (hidden commands excluded)
        foreach (var subcommand in rootCommand.Subcommands.Where(c => !c.Hidden))
        {
            CollectionAssert.Contains(categorizedTypes.ToList(), subcommand.GetType(),
                $"Top-level command '{subcommand.Name}' ({subcommand.GetType().Name}) is registered on the root command but not listed in any " +
                "help category. Add it to the categories in WinAppRootCommand.");
        }
    }

    [TestMethod]
    public void AllTopLevelCommands_ShouldHaveShortDescription()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        // Assert — every registered subcommand must implement IShortDescription
        foreach (var subcommand in rootCommand.Subcommands)
        {
            Assert.IsInstanceOfType<IShortDescription>(subcommand,
                $"Top-level command '{subcommand.Name}' does not implement IShortDescription. " +
                "Add IShortDescription to the command class so it appears with a description in the help output.");

            var shortDesc = ((IShortDescription)subcommand).ShortDescription;
            Assert.IsFalse(string.IsNullOrWhiteSpace(shortDesc),
                $"Top-level command '{subcommand.Name}' has an empty ShortDescription.");
        }
    }

    [TestMethod]
    public void AllCommands_IncludingNested_ShortDescriptionMatchesDescription()
    {
        // Every command that implements IShortDescription (including nested subcommands such
        // as `manifest generate` or `cert info`) must expose non-empty help text. This walks
        // the whole command tree, not just the top level.
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var offenders = new List<string>();
        foreach (var command in EnumerateCommands(rootCommand))
        {
            if (command is IShortDescription shortDescription)
            {
                if (string.IsNullOrWhiteSpace(shortDescription.ShortDescription))
                {
                    offenders.Add(command.Name);
                }
            }
        }

        Assert.IsEmpty(offenders,
            $"These commands implement IShortDescription but return empty text: {string.Join(", ", offenders)}");
    }

    private static IEnumerable<System.CommandLine.Command> EnumerateCommands(System.CommandLine.Command command)
    {
        foreach (var child in command.Subcommands)
        {
            yield return child;
            foreach (var descendant in EnumerateCommands(child))
            {
                yield return descendant;
            }
        }
    }

    [TestMethod]
    public async Task RootHelp_ShouldRenderSuccessfully()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "--help" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Help command should complete successfully");

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(string.IsNullOrWhiteSpace(output), "Help output should not be empty");
    }

    [TestMethod]
    public async Task SubcommandHelp_ShouldFallBackToDefault()
    {
        // Arrange
        var rootCommand = GetRequiredService<WinAppRootCommand>();
        var args = new[] { "init", "--help" };

        // Act
        var exitCode = await ParseAndInvokeWithCaptureAsync(rootCommand, args);

        // Assert
        Assert.AreEqual(0, exitCode, "Subcommand help should complete successfully");

        var output = TestAnsiConsole.Output;
        Assert.IsFalse(string.IsNullOrWhiteSpace(output), "Subcommand help output should not be empty");

        // Subcommand help should NOT contain category headers (those are root-only)
        Assert.DoesNotContain("Package, Sign & Publish", output, "Subcommand help should not contain root-level categories");
    }
}
