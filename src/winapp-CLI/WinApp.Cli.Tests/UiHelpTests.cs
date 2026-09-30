// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using System.Text.RegularExpressions;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class UiHelpTests : BaseCommandTests
{
    private UiCommand UiGroup => GetRequiredService<WinAppRootCommand>().Subcommands.OfType<UiCommand>().Single();

    [TestMethod]
    public void AllUiCommands_AreInExactlyOneHelpCategory()
    {
        var categorized = UiCommand.HelpCategories.SelectMany(c => c.CommandTypes).ToList();
        CollectionAssert.AllItemsAreUnique(categorized, "A ui command is listed in more than one help category.");

        foreach (var command in UiGroup.Subcommands.Where(c => !c.Hidden))
        {
            CollectionAssert.Contains(categorized, command.GetType(),
                $"'winapp ui {command.Name}' is not in any help category. Add it to UiCommand.HelpCategories.");
        }
    }

    [TestMethod]
    public void EveryUiCommand_HasExamplesThatParseAgainstIt()
    {
        var root = GetRequiredService<WinAppRootCommand>();
        foreach (var command in UiGroup.Subcommands)
        {
            Assert.IsInstanceOfType<IHelpExamples>(command, $"'winapp ui {command.Name}' needs help examples (IHelpExamples).");
            var examples = ((IHelpExamples)command).Examples;
            Assert.IsTrue(examples.Count is >= 1 and <= 3, $"'winapp ui {command.Name}' should have 1-3 examples.");

            foreach (var example in examples)
            {
                var tokens = Tokenize(example);
                CollectionAssert.AreEqual(new[] { "winapp", "ui", command.Name }, tokens.Take(3).ToArray(),
                    $"Example '{example}' must start with 'winapp ui {command.Name}'.");
                foreach (var token in tokens)
                {
                    Assert.DoesNotMatchRegex(new Regex("^[a-z]+-[a-z0-9]+-[0-9a-f]{4}$"), token,
                        $"Example '{example}' uses a real slug; use <selector>.");
                }

                var parsed = root.Parse(tokens.Skip(1).Select(SubstitutePlaceholder).ToArray());
                Assert.IsEmpty(parsed.Errors,
                    $"Example '{example}' does not parse: {string.Join("; ", parsed.Errors.Select(e => e.Message))}");
                Assert.AreSame(command, parsed.CommandResult.Command, $"Example '{example}' parsed as a different command.");
            }
        }
    }

    [TestMethod]
    public void EverySelectorCommand_AcceptsElementFilters()
    {
        var offenders = new List<string>();
        foreach (var command in Enumerate(UiGroup))
        {
            if (command.Arguments.Any(a => a.Name == "selector")
                && !(command.Options.Contains(UiQueryOptions.Type)
                     && command.Options.Contains(UiQueryOptions.Root)
                     && command.Options.Contains(UiQueryOptions.ClassName)))
            {
                offenders.Add(command.Name);
            }
        }

        Assert.IsEmpty(offenders,
            $"Commands that take a selector must accept --type, --root, and --class-name (UiQueryOptions.AddTo): {string.Join(", ", offenders)}");
    }

    [TestMethod]
    public async Task UiGroupHelp_IsCompactPlainTextWithGoldenPathFirst()
    {
        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(), ["ui", "--help"]);

        Assert.AreEqual(0, exitCode);
        var output = TestAnsiConsole.Output.Replace("\r\n", "\n");
        Assert.IsLessThanOrEqualTo(3072, TestAnsiConsole.Output.Length, $"'winapp ui --help' grew to {TestAnsiConsole.Output.Length} chars.");
        Assert.IsFalse(output.Any(c => c is '╭' or '│' or '─' or '\u001b'), "ui help must be plain text.");
        Assert.IsTrue(output.StartsWith("winapp ui - ", StringComparison.Ordinal));
        Assert.IsLessThan(output.IndexOf("\nDiscover", StringComparison.Ordinal),
            output.IndexOf("winapp ui inspect -a <app> --interactive", StringComparison.Ordinal),
            "The golden path must come before the command list.");
        foreach (var (category, _) in UiCommand.HelpCategories)
        {
            StringAssert.Contains(output, "\n" + category + "\n");
        }

        Assert.DoesNotMatchRegex(new Regex(@"^\s+(find|tree)\s", RegexOptions.Multiline), output, "Aliases stay out of ui help.");
        Assert.DoesNotContain("--json", output);
    }

    [TestMethod]
    public async Task EveryUiCommandHelp_CollapsesGlobalOptionsToOneLine()
    {
        foreach (var command in UiGroup.Subcommands)
        {
            var output = CompactHelpRenderer.Render(command).Replace("\r\n", "\n");

            Assert.IsTrue(output.StartsWith($"winapp ui {command.Name} - ", StringComparison.Ordinal), output);
            StringAssert.Contains(output, "\nExamples:\n");
            Assert.AreEqual(1, Regex.Count(output, "^Global options:", RegexOptions.Multiline), command.Name);
            Assert.DoesNotContain("--verbose", output, command.Name);
            Assert.DoesNotContain("--quiet", output, command.Name);
            Assert.DoesNotMatchRegex(new Regex(@"^\s+--cli-schema", RegexOptions.Multiline), output, command.Name);
            Assert.DoesNotContain("--caller", output, command.Name);
        }

        var exitCode = await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(), ["ui", "invoke", "--help"]);
        Assert.AreEqual(0, exitCode);
        StringAssert.StartsWith(TestAnsiConsole.Output, "winapp ui invoke - ");
    }

    [TestMethod]
    [DataRow("tree", typeof(UiInspectCommand))]
    [DataRow("find", typeof(UiSearchCommand))]
    public async Task Aliases_RunTheTargetCommandIncludingHelp(string alias, Type target)
    {
        var root = GetRequiredService<WinAppRootCommand>();
        Assert.IsInstanceOfType(root.Parse(["ui", alias, "Save", "-a", "app"]).CommandResult.Command, target);

        var exitCode = await ParseAndInvokeWithCaptureAsync(root, ["ui", alias, "--help"]);
        Assert.AreEqual(0, exitCode);
        var name = UiGroup.Subcommands.Single(c => c.GetType() == target).Name;
        StringAssert.StartsWith(TestAnsiConsole.Output, $"winapp ui {name} - ");
    }

    [TestMethod]
    public async Task RootHelp_PointsToUiAndDevToolsHelp()
    {
        await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(), ["--help"]);
        var output = TestAnsiConsole.Output;
        StringAssert.Contains(output, "Start here:");
        Assert.IsLessThan(output.IndexOf("winapp devtools --help", StringComparison.Ordinal),
            output.IndexOf("winapp ui --help", StringComparison.Ordinal));
        StringAssert.Contains(output, "Inspect or change a running WinUI app's XAML live");
    }

    [TestMethod]
    public async Task OtherCommandGroups_KeepDefaultHelp()
    {
        await ParseAndInvokeWithCaptureAsync(GetRequiredService<WinAppRootCommand>(), ["cert", "--help"]);
        Assert.DoesNotContain(CompactHelpRenderer.GlobalOptionsLine, TestAnsiConsole.Output);
    }

    [TestMethod]
    [DataRow("dump", new[] { "inspect" })]
    [DataRow("snapshot", new[] { "inspect" })]
    [DataRow("query", new[] { "search" })]
    [DataRow("type", new[] { "send-keys" })]
    [DataRow("read", new[] { "get-value" })]
    [DataRow("windows", new[] { "list-windows" })]
    [DataRow("wait", new[] { "wait-for" })]
    [DataRow("press", new[] { "invoke" })]
    [DataRow("lst-windows", new[] { "list-windows" })]
    [DataRow("Inspect", new[] { "inspect" })]
    [DataRow("xyzzy", new string[0])]
    public void Suggest_UsesSynonymsThenEditDistance(string token, string[] expected)
    {
        var suggestions = UnknownGroupCommand.Suggest(token, UiGroup);
        Assert.IsLessThanOrEqualTo(2, suggestions.Length);
        CollectionAssert.AreEqual(expected, suggestions.Take(expected.Length).ToArray());
        if (expected.Length == 0)
        {
            Assert.IsEmpty(suggestions);
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task UnknownCommand_FailsWithSuggestionEvenWithHelp()
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(["ui", "dump", "--help"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout), stdout);
        StringAssert.StartsWith(stderr, "Unknown command 'dump'. Did you mean 'inspect'?");
        StringAssert.Contains(stderr, "Run 'winapp ui --help' for the full list.");
        Assert.DoesNotContain("Discover", stderr, "The full help must not be printed.");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task UnknownCommand_Json_EmitsEnvelopeWithSuggestionsBeforeRouting()
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(
            ["ui", "dump", "--help", "--json", "--on", "sandbox"]);

        Assert.AreEqual(1, exitCode);
        Assert.IsTrue(string.IsNullOrWhiteSpace(stdout), stdout);
        using var document = JsonDocument.Parse(stderr);
        var error = document.RootElement.GetProperty("error");
        Assert.AreEqual(UiJsonError.CodeInvalidArguments, error.GetProperty("code").GetString());
        Assert.AreEqual("Unknown command 'dump'.", error.GetProperty("message").GetString());
        Assert.AreEqual("inspect", error.GetProperty("suggestions")[0].GetString());
        Assert.AreEqual(UnknownGroupCommand.RecoveryHint(UiGroup), error.GetProperty("recoveryHint").GetString());
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task UnknownCommand_JsonFalse_StaysText()
    {
        var (_, stderr, exitCode) = await InvokeProgramAsync(["ui", "dump", "--json=false"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.StartsWith(stderr, "Unknown command 'dump'.");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task UnknownCommand_OnlyCommandPositionIsChecked()
    {
        var (_, stderr, exitCode) = await InvokeProgramAsync(["ui", "invoke", "--help"]);

        Assert.AreEqual(0, exitCode);
        Assert.DoesNotContain("Unknown command", stderr);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task MissingOptionValue_PointsToHelpInsteadOfPrintingIt()
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(["ui", "invoke", "Cancel", "-w"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(stderr, "'-w'");
        StringAssert.Contains(stderr, "Run 'winapp ui invoke --help' for usage.");
        Assert.DoesNotContain("Examples:", stdout + stderr);
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task BareUiGroup_StillShowsItsHelp()
    {
        var (stdout, stderr, exitCode) = await InvokeProgramAsync(["ui"]);

        Assert.AreEqual(1, exitCode);
        StringAssert.Contains(stdout + stderr, "Discover");
        Assert.DoesNotContain("Unknown command", stderr);
    }

    [TestMethod]
    [DoNotParallelize]
    [DataRow("-a", "Notepad")]
    [DataRow("--", "inspect")]
    public async Task MissingCommand_IsNotReportedAsUnknown(string first, string second)
    {
        var (_, stderr, exitCode) = await InvokeProgramAsync(["ui", first, second]);

        Assert.AreEqual(1, exitCode);
        Assert.DoesNotContain("Unknown command", stderr);
    }

    private static string SubstitutePlaceholder(string token) => token
        .Replace("<hwnd>", "4242")
        .Replace("<app>", "app")
        .Replace("<selector>", "sel")
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

    private static IEnumerable<Command> Enumerate(Command command)
    {
        foreach (var child in command.Subcommands)
        {
            yield return child;
            foreach (var descendant in Enumerate(child))
            {
                yield return descendant;
            }
        }
    }
}
