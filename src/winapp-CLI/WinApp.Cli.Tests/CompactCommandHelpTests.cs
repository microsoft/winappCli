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
    public void ExamplesAndWorkflowSteps_Parse()
    {
        var root = GetRequiredService<WinAppRootCommand>();
        foreach (var command in CompactCommands)
        {
            var help = (ICompactHelpCommand)command;
            Assert.IsTrue(help.Examples.Count is >= 1 and <= 3, $"'{command.Name}' should have 1-3 examples.");
            foreach (var example in help.Examples)
            {
                Assert.AreSame(command, AssertParses(root, example).CommandResult.Command, example);
            }
            foreach (var (step, _) in help.Workflow.Where(s => s.Command.StartsWith("winapp ", StringComparison.Ordinal)))
            {
                AssertParses(root, step);
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
        StringAssert.Contains(output, "\nTypical workflow:\n");
        StringAssert.Contains(output, "\nExamples:\n");
        Assert.IsLessThan(output.IndexOf("\nUsage:", StringComparison.Ordinal), output.IndexOf("\nTypical workflow:", StringComparison.Ordinal));
        Assert.DoesNotContain("\nOptions:\n", output, "Every option belongs to a named group.");
        Assert.AreEqual(1, Regex.Count(output, "^Global options:", RegexOptions.Multiline));
        StringAssert.Contains(output, acceptsOn ? CompactHelpRenderer.GlobalOptionsLine : CompactHelpRenderer.LocalGlobalOptionsLine);
        Assert.DoesNotContain("--verbose", output);
        var tooLong = output.Split('\n').Where(line => line.Length > CompactHelpRenderer.Width).ToList();
        Assert.IsEmpty(tooLong, string.Join("\n", tooLong));
    }
}
