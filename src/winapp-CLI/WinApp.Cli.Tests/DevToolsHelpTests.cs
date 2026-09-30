// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class DevToolsHelpTests : BaseCommandTests
{
    private DevToolsCommand DevTools => GetRequiredService<WinAppRootCommand>().Subcommands.OfType<DevToolsCommand>().Single();

    private IEnumerable<Command> Groups => [DevTools, DevTools.Subcommands.OfType<DevToolsCommentsCommand>().Single()];

    private IEnumerable<Command> Leaves => Groups.SelectMany(g => g.Subcommands).Where(c => c is not ICompactHelpGroup);

    [TestMethod]
    public void EveryDevToolsCommand_IsInExactlyOneHelpCategory()
    {
        foreach (var group in Groups)
        {
            var categorized = ((ICompactHelpGroup)group).Categories.SelectMany(c => c.CommandTypes).ToList();
            CollectionAssert.AllItemsAreUnique(categorized, group.Name);
            foreach (var command in group.Subcommands.Where(c => !c.Hidden))
            {
                CollectionAssert.Contains(categorized, command.GetType(), $"'{command.Name}' is not in any help category of '{group.Name}'.");
            }
        }
    }

    [TestMethod]
    public void EveryDevToolsCommand_HasExamplesThatParseAgainstIt()
    {
        var root = GetRequiredService<WinAppRootCommand>();
        foreach (var command in Leaves)
        {
            Assert.IsInstanceOfType<IHelpExamples>(command, $"'{Path(command)}' needs help examples (IHelpExamples).");
            var examples = ((IHelpExamples)command).Examples;
            Assert.IsTrue(examples.Count is >= 1 and <= 3, $"'{Path(command)}' should have 1-3 examples.");
            foreach (var example in examples)
            {
                StringAssert.StartsWith(example + " ", Path(command) + " ", example);
                var parsed = root.Parse(Tokenize(example).Skip(1).Select(Substitute).ToArray());
                Assert.IsEmpty(parsed.Errors, $"Example '{example}' does not parse: {string.Join("; ", parsed.Errors.Select(e => e.Message))}");
                Assert.AreSame(command, parsed.CommandResult.Command, $"Example '{example}' parsed as a different command.");
            }
        }
    }

    [TestMethod]
    public async Task DevToolsGroupHelp_IsCompactPlainTextWithWorkflowFirst()
    {
        Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(), ["devtools", "--help"]));
        var output = TestAnsiConsole.Output.Replace("\r\n", "\n");

        Assert.IsLessThanOrEqualTo(3072, output.Length, $"'winapp devtools --help' grew to {output.Length} chars.");
        Assert.IsFalse(output.Any(c => c is '╭' or '│' or '─' or '\u001b'), "devtools help must be plain text.");
        Assert.IsTrue(output.StartsWith("winapp devtools - Inspect and change a running WinUI 3 app's XAML live.", StringComparison.Ordinal), output);
        StringAssert.Contains(Regex.Replace(output, @"\s+", " "), "Changes are not written to source.");
        var workflow = output.IndexOf("winapp devtools set-property <selector> <prop> <value> -a <app>", StringComparison.Ordinal);
        Assert.IsGreaterThan(0, workflow);
        Assert.IsLessThan(output.IndexOf("\nDiscover\n", StringComparison.Ordinal), workflow, "The workflow comes before the command list.");
        StringAssert.Contains(output, "  -a <app>");
        StringAssert.Contains(output, "  <selector>");
        StringAssert.Contains(output, "Use 'winapp ui' to click, type, and read values in any app.");
        foreach (var (category, _) in DevToolsCommand.HelpCategories)
        {
            StringAssert.Contains(output, "\n" + category + "\n");
        }
        Assert.IsFalse(output.Split('\n').Any(line => line.Length > CompactHelpRenderer.Width), "Lines fit the help width.");
    }

    [TestMethod]
    public async Task SetPropertyHelp_LeadsWithUsageAndExamples()
    {
        Assert.AreEqual(0, await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(), ["devtools", "set-property", "--help"]));
        var output = TestAnsiConsole.Output.Replace("\r\n", "\n");

        StringAssert.StartsWith(output, "winapp devtools set-property - ");
        StringAssert.Contains(output, "Usage: winapp devtools set-property <selector> <property> <value> [-a <app> | -w <hwnd>] [options]");
        StringAssert.Contains(output, "\nExamples:\n  winapp devtools set-property <selector> Text \"<text>\" -a <app>\n");
        Assert.AreEqual(1, Regex.Count(output, "^Global options:", RegexOptions.Multiline));
        Assert.DoesNotContain("--verbose", output);
    }

    [TestMethod]
    [DataRow("list", false)]
    [DataRow("get", false)]
    [DataRow("update", true)]
    public void CommentsHelp_MentionsOnOnlyWhereItIsAccepted(string verb, bool shown)
    {
        var command = Groups.Last().Subcommands.Single(c => c.Name == verb);
        Assert.AreEqual(shown, CompactHelpRenderer.Render(command).Contains("--on", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("set-text", "set-property")]
    [DataRow("tree", "inspect")]
    [DataRow("get-propery", "get-property")]
    public void Suggest_UsesSynonymsThenEditDistance(string token, string expected)
        => Assert.AreEqual(expected, UnknownGroupCommand.Suggest(token, DevTools).FirstOrDefault());

    [TestMethod]
    [DoNotParallelize]
    public async Task UnknownCommand_FailsWithSuggestionEvenWithHelp()
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(["devtools", "set-text", "--help"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout), stdout);
        StringAssert.StartsWith(stderr, "Unknown command 'set-text'. Did you mean 'set-property'?");
        StringAssert.Contains(stderr, "Run 'winapp devtools --help' for the full list.");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task UnknownCommand_Json_IsADevToolsError()
    {
        var (stdout, _, exitCode) = await InvokeProgramAsync(["devtools", "set-text", "--json"]);

        Assert.AreEqual(1, exitCode);
        using var document = JsonDocument.Parse(stdout);
        Assert.IsFalse(document.RootElement.GetProperty("ok").GetBoolean());
        var error = document.RootElement.GetProperty("error");
        Assert.AreEqual("unknown-command", error.GetProperty("token").GetString());
        StringAssert.Contains(error.GetProperty("message").GetString(), "Did you mean 'set-property'?");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task UnknownCommentsCommand_NamesTheCommentsGroup()
    {
        var (_, stderr, exitCode) = await InvokeProgramAsync(["devtools", "comments", "resolve"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.StartsWith(stderr, "Unknown command 'resolve'. Did you mean 'update'?");
        StringAssert.Contains(stderr, "Run 'winapp devtools comments --help' for the full list.");
    }

    private static string Path(Command command)
    {
        var names = new List<string>();
        for (var current = command; current is not null and not RootCommand; current = current.Parents.OfType<Command>().FirstOrDefault())
        {
            names.Insert(0, current.Name);
        }
        return "winapp " + string.Join(" ", names);
    }

    private static string Substitute(string token) => token
        .Replace("<hwnd>", "4242")
        .Replace("<pid>", "4242")
        .Replace("<handle>", "4242")
        .Replace("<app>", "app")
        .Replace("<selector>", "sel")
        .Replace("<id>", "cmt_0123456789ab")
        .Replace("<text>", "text");

    private static List<string> Tokenize(string commandLine)
    {
        var tokens = new List<string>();
        foreach (Match match in Regex.Matches(commandLine, "\"([^\"]*)\"|(\\S+)"))
        {
            tokens.Add(match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value);
        }
        return tokens;
    }
}
