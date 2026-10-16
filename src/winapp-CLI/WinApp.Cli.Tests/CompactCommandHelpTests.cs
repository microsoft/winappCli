// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.CommandLine.Help;
using System.Text.RegularExpressions;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class CompactCommandHelpTests : BaseCommandTests
{
    private IEnumerable<Command> CompactCommands =>
        GetRequiredService<WinAppRootCommand>().Subcommands.Where(c => c is ICompactHelpCommand);

    /// <summary>
    /// Parses a help command line (starting with <c>winapp</c>) against the real CLI, replacing
    /// <c>&lt;placeholders&gt;</c>, and fails on unknown commands or options.
    /// </summary>
    internal static ParseResult AssertParses(RootCommand root, string commandLine)
    {
        var tokens = Regex.Matches(commandLine, "\"[^\"]*\"|\\S+")
            .Select(m => m.Value.Trim('"'))
            .ToList();
        Assert.AreEqual("winapp", tokens[0], commandLine);
        var args = tokens.Skip(1).Select(t => t.StartsWith('<') ? "placeholder" : t).ToArray();
        var result = root.Parse(args);
        // Only command and option names matter here; example paths don't exist on the test machine.
        var errors = result.Errors.Where(e => !e.Message.Contains("does not exist", StringComparison.Ordinal)).ToList();
        Assert.IsEmpty(errors, $"{commandLine}: {string.Join("; ", errors.Select(e => e.Message))}");
        Assert.AreNotSame(root, result.CommandResult.Command, commandLine);
        // 'run' absorbs unknown tokens as app arguments instead of reporting a parse error.
        if (result.CommandResult.Command is RunCommand && !args.Contains("--"))
        {
            Assert.IsEmpty(result.GetValue(RunCommand.PassthroughArgument) ?? [], commandLine);
        }
        return result;
    }

    private static readonly string[] CompactCommandNames = ["run", "package", "init"];

    [TestMethod]
    public void RunPackageAndInit_UseCompactHelp()
    {
        CollectionAssert.AreEquivalent(CompactCommandNames, CompactCommands.Select(c => c.Name).ToArray());
    }

    [TestMethod]
    public void EveryVisibleOption_IsInExactlyOneGroup()
    {
        foreach (var command in CompactCommands)
        {
            var grouped = ((ICompactHelpCommand)command).OptionGroups.SelectMany(g => g.Options).ToList();
            CollectionAssert.AllItemsAreUnique(grouped, $"'{command.Name}' lists an option in more than one group.");
            var visible = command.Options.Where(o => !o.Hidden && o is not HelpOption
                && o != WinAppRootCommand.VerboseOption && o != WinAppRootCommand.QuietOption
                && !ExecutionTargetSelection.IsSelectorOption(o));
            foreach (var option in visible)
            {
                CollectionAssert.Contains(grouped, option, $"'winapp {command.Name} {option.Name}' is not in any help group.");
            }
            foreach (var option in grouped)
            {
                CollectionAssert.Contains(command.Options.ToList(), option, $"'{option.Name}' is grouped but not an option of '{command.Name}'.");
            }
        }
    }

    [TestMethod]
    public void ExamplesParse_AndOwnExamplesReachTheCommand()
    {
        var root = GetRequiredService<WinAppRootCommand>();
        foreach (var command in CompactCommands)
        {
            var examples = ((ICompactHelpCommand)command).Examples;
            Assert.IsTrue(examples.Count is >= 3 and <= 8, $"'{command.Name}' should have 3-8 examples.");
            Assert.IsTrue(examples.All(e => e.Description.Length > 0), $"Every '{command.Name}' example needs a description.");
            foreach (var (example, _) in examples.Where(e => e.Command.StartsWith("winapp ", StringComparison.Ordinal)))
            {
                var result = AssertParses(root, example);
                if (example.StartsWith($"winapp {command.Name} ", StringComparison.Ordinal) || example == $"winapp {command.Name}")
                {
                    Assert.AreSame(command, result.CommandResult.Command, example);
                }
            }
        }
    }

    [TestMethod]
    [DataRow("run", true)]
    [DataRow("package", false)]
    [DataRow("init", false)]
    public async Task Help_IsCompactAndFitsTheWidth(string name, bool acceptsOn)
    {
        Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(), [name, "--help"]));
        var output = TestAnsiConsole.Output.Replace("\r\n", "\n");

        StringAssert.StartsWith(output, $"winapp {name} - ");
        StringAssert.Contains(output, "\nExamples:\n");
        Assert.IsLessThan(output.IndexOf("\nExamples:", StringComparison.Ordinal), output.IndexOf("\nUsage:", StringComparison.Ordinal));
        Assert.DoesNotContain("\nOptions:\n", output, "Every option belongs to a named group.");
        Assert.AreEqual(1, Regex.Count(output, "^Global options:", RegexOptions.Multiline));
        StringAssert.Contains(output, acceptsOn ? CompactHelpRenderer.GlobalOptionsLine : CompactHelpRenderer.LocalGlobalOptionsLine);
        Assert.DoesNotContain("--verbose", output);
        Assert.DoesNotContain("\u001b[", output, "Redirected help has no color codes.");
        var tooLong = output.Split('\n').Where(line => line.Length > CompactHelpRenderer.Width).ToList();
        Assert.IsEmpty(tooLong, string.Join("\n", tooLong));
    }

    [TestMethod]
    public void EveryCommand_HelpListsItsSubcommandsAndOptionsAndFitsTheWidth()
    {
        foreach (var command in AllCommands(GetRequiredService<WinAppRootCommand>()))
        {
            var output = CompactHelpRenderer.Render(command).Replace("\r\n", "\n");
            StringAssert.StartsWith(output, CompactHelpTitlePrefix(command), command.Name);
            StringAssert.Contains(output, "\nUsage: ", command.Name);
            // ui and devtools list their shared options instead of a global-options line.
            Assert.AreEqual(command is ICompactHelpGroup ? 0 : 1, Regex.Count(output, "^Global options:", RegexOptions.Multiline), command.Name);
            foreach (var sub in command.Subcommands.Where(c => !c.Hidden))
            {
                Assert.IsTrue(Regex.IsMatch(output, $"\n  {Regex.Escape(sub.Name)}[ \n]"), $"'{command.Name}' help doesn't list '{sub.Name}'.");
            }
            foreach (var option in command is ICompactHelpGroup ? [] : command.Options.Where(o => !o.Hidden && o is not HelpOption
                && o != WinAppRootCommand.VerboseOption && o != WinAppRootCommand.QuietOption
                && !ExecutionTargetSelection.IsSelectorOption(o)))
            {
                StringAssert.Contains(output, option.Name, $"'{command.Name}' help doesn't list '{option.Name}'.");
            }
            // Usage and example lines are never wrapped, and the ui/devtools overviews are hand-formatted.
            var tooLong = command is ICompactHelpGroup ? [] : output.Split('\n').Where(line => line.Length > CompactHelpRenderer.Width
                && !line.StartsWith("Usage: ", StringComparison.Ordinal) && !line.StartsWith("  winapp ", StringComparison.Ordinal)).ToList();
            Assert.IsEmpty(tooLong, $"{command.Name}:\n{string.Join("\n", tooLong)}");
        }
    }

    [TestMethod]
    public void StyledHelp_HasTheSameTextAsPlainHelp()
    {
        foreach (var command in AllCommands(GetRequiredService<WinAppRootCommand>()))
        {
            var plain = CompactHelpRenderer.Render(command);
            var styled = CompactHelpRenderer.Render(command, styled: true);
            static string[] Text(string help) => Regex.Replace(help, "\u001b\\[[0-9;]*m", "")
                .Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Length > 0).ToArray();
            CollectionAssert.AreEqual(Text(plain), Text(styled), command.Name);
        }
        StringAssert.Contains(CompactHelpRenderer.Render(GetRequiredService<WinAppRootCommand>().Subcommands.Single(c => c.Name == "run"), styled: true), "\u001b[");
    }

    [TestMethod]
    [DataRow("cert generate", "--if-exists <Error|Overwrite|Skip>")]
    [DataRow("cert generate", "[default: 365]")]
    [DataRow("manifest generate", "--template <Packaged|Sparse>")]
    [DataRow("perf mark", "(REQUIRED)")]
    [DataRow("find-ui", "Usage: winapp find-ui [<query>] [options]")]
    [DataRow("target exec", "<command>...")]
    [DataRow("store", "[[--] <additional arguments>...]")]
    [DataRow("tool", "[[--] <additional arguments>...]")]
    public void NonAgentHelp_KeepsDefaultsAllowedValuesAndArgumentShapes(string path, string expected)
    {
        Command command = GetRequiredService<WinAppRootCommand>();
        foreach (var name in path.Split(' '))
        {
            command = command.Subcommands.Single(c => c.Name == name);
        }
        var help = Regex.Replace(CompactHelpRenderer.Render(command), @"\s+", " ");
        StringAssert.Contains(help, expected);
    }

    [TestMethod]
    public void UiHelp_KeepsItsExistingLayout()
    {
        var inspect = GetRequiredService<WinAppRootCommand>().Subcommands.Single(c => c.Name == "ui").Subcommands.Single(c => c.Name == "inspect");
        Assert.DoesNotContain("[default:", CompactHelpRenderer.Render(inspect));
    }

    private static string CompactHelpTitlePrefix(Command command)
    {
        var parts = new List<string>();
        for (var current = command; current is not null; current = current.Parents.OfType<Command>().FirstOrDefault())
        {
            parts.Add(current is RootCommand ? "winapp" : current.Name);
        }
        parts.Reverse();
        return string.Join(" ", parts) + " - ";
    }

    private static IEnumerable<Command> AllCommands(Command parent) =>
        parent.Subcommands.Where(c => !c.Hidden).SelectMany(c => AllCommands(c).Prepend(c));
}
