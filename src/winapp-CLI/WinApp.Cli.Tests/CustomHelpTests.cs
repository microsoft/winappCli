// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
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

    [TestMethod]
    [DataRow("python", "winapp-py")]
    [DataRow("PYTHON", "winapp-py")]
    [DataRow("npm", "winapp")]
    [DataRow("nuget-package", "winapp")]
    [DataRow("other", "winapp")]
    public async Task RootHelp_UsesTheCallingEntrypoint(string caller, string name)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        var originalName = root.Name;

        Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(root, ["--caller", caller, "--help"]));

        StringAssert.Contains(TestAnsiConsole.Output, $"Usage: {name} <command>");
        StringAssert.Contains(TestAnsiConsole.Output, $"Start with '{name} ui --help'.");
        Assert.AreEqual(originalName, root.Name);
    }

    [TestMethod]
    [DataRow(new[] { "init" })]
    [DataRow(new[] { "manifest", "generate" })]
    [DataRow(new[] { "cert", "info" })]
    public async Task PythonSubcommandHelp_ChangesDisplayOnly(string[] command)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        var originalName = root.Name;
        var parsed = root.Parse([.. command, "--caller", "python", "--help"]);
        using var output = new StringWriter();
        parsed.InvocationConfiguration.Output = output;
        var description = parsed.CommandResult.Command.Description;

        Assert.AreEqual(0, await parsed.InvokeAsync());

        StringAssert.Contains(output.ToString(), $"winapp-py {string.Join(" ", command)}");
        Assert.AreSame(output, parsed.InvocationConfiguration.Output);
        Assert.AreEqual(originalName, root.Name);
        Assert.AreEqual(description, parsed.CommandResult.Command.Description);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task PythonEnvironment_SelectsHelpName_AndExplicitCallerOverridesIt()
    {
        var previous = Environment.GetEnvironmentVariable("WINAPP_CLI_CALLER");
        try
        {
            Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", "python");
            var root = GetRequiredService<WinAppRootCommand>();
            await ParseAndInvokeWithCaptureAsync(root, ["--help"]);
            StringAssert.Contains(TestAnsiConsole.Output, "Usage: winapp-py <command>");

            using var output = new StringWriter();
            var parsed = root.Parse(["init", "--caller", "npm", "--help"]);
            parsed.InvocationConfiguration.Output = output;
            Assert.AreEqual(0, await parsed.InvokeAsync());
            Assert.DoesNotContain("winapp-py", output.ToString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("WINAPP_CLI_CALLER", previous);
        }
    }

    [TestMethod]
    public async Task PythonCaller_DoesNotChangeTheCliSchema()
    {
        var root = GetRequiredService<WinAppRootCommand>();
        using var native = new StringWriter();
        using var python = new StringWriter();
        var nativeParse = root.Parse(["--caller", "npm", "--cli-schema"]);
        nativeParse.InvocationConfiguration.Output = native;
        var pythonParse = root.Parse(["--caller", "python", "--cli-schema"]);
        pythonParse.InvocationConfiguration.Output = python;

        Assert.AreEqual(0, await nativeParse.InvokeAsync());
        Assert.AreEqual(0, await pythonParse.InvokeAsync());
        Assert.AreEqual(native.ToString(), python.ToString());
    }

    [TestMethod]
    public void HelpReferences_ChangeCommandsButPreserveProductNamesAndPaths()
    {
        var root = GetRequiredService<WinAppRootCommand>();
        var text = @"winapp ui --help; winapp init .; winapp pack .\publish; winapp --version;
winapp CLI; winapp.yaml; .winapp\python; winapp.exe; C:\tools\winapp.exe; winapp-cli";

        var formatted = HelpCommandName.FormatReferences(text, root, "winapp-py");

        StringAssert.Contains(formatted, "winapp-py ui --help");
        StringAssert.Contains(formatted, "winapp-py init .");
        StringAssert.Contains(formatted, @"winapp-py pack .\publish");
        StringAssert.Contains(formatted, "winapp-py --version");
        StringAssert.Contains(formatted,
            @"winapp CLI; winapp.yaml; .winapp\python; winapp.exe; C:\tools\winapp.exe; winapp-cli");
        Assert.AreEqual(text, HelpCommandName.FormatReferences(text, root, "winapp"));
    }

    [TestMethod]
    [DataRow("--version")]
    [DataRow("-V")]
    public void VersionAliases_ShouldInvokeVersionAction(string flag)
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var parseResult = rootCommand.Parse([flag]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.AreSame(rootCommand.Options.OfType<VersionOption>().Single().Action, parseResult.Action);
    }

    [TestMethod]
    public void LowercaseV_ShouldStillMeanVerbose()
    {
        var rootCommand = GetRequiredService<WinAppRootCommand>();

        var parseResult = rootCommand.Parse(["restore", "-v"]);

        Assert.IsEmpty(parseResult.Errors);
        Assert.IsTrue(parseResult.GetValue(WinAppRootCommand.VerboseOption));
    }
}
