// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.CommandLine;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class UiAuditTabsCommandTests : IDisposable
{
    private ServiceProvider _services = null!;
    private TestConsole _console = null!;
    private StringWriter _output = null!;
    private StringWriter _error = null!;
    private TabsFake _tabs = null!;
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Initialize()
    {
        _console = new TestConsole();
        _output = new StringWriter();
        _error = new StringWriter();
        _tabs = new TabsFake();
        _services = new ServiceCollection().ConfigureCommands()
            .AddSingleton<IAnsiConsole>(_console).AddSingleton<IUiAuditTabsService>(_tabs).BuildServiceProvider();
    }
    [TestCleanup]
    public void Cleanup() => Dispose();
    public void Dispose() { _services?.Dispose(); _console?.Dispose(); _output?.Dispose(); _error?.Dispose(); }

    [TestMethod]
    public async Task DefaultDiscoveryUsesOnlyTheExactHwndAndIsReadOnly()
    {
        Assert.AreEqual(0, await Invoke(["--window", "42", "--json"]));
        Assert.AreEqual(42L, _tabs.Window);
        Assert.IsNotNull(_tabs.Options);
        Assert.AreEqual(20, _tabs.Options.MaxDepth);
        Assert.AreEqual(2000, _tabs.Options.MaxElements);
        Assert.AreEqual(15000, _tabs.Options.TimeoutMs);
        Assert.IsNull(_tabs.Options.Select);
        Assert.IsNull(_tabs.Options.Container);
        Assert.IsNull(_tabs.Options.RootRuntimeId);
        Assert.IsFalse(_tabs.Options.AcceptNavigation);
    }

    [TestMethod]
    public async Task ExactApprovedIdentitiesAndBoundsArePreserved()
    {
        Assert.AreEqual(0, await Invoke(["--window", "42", "--json", "--select", "[2A,4]",
            "--container", "[2A,3]", "--root-runtime-id", "[2A,2]", "--accept-navigation",
            "--depth", "50", "--max-elements", "10000", "--timeout", "120000"]));
        Assert.IsNotNull(_tabs.Options);
        Assert.AreEqual("[2A,4]", _tabs.Options.Select);
        Assert.AreEqual("[2A,3]", _tabs.Options.Container);
        Assert.AreEqual("[2A,2]", _tabs.Options.RootRuntimeId);
        Assert.IsTrue(_tabs.Options.AcceptNavigation);
        Assert.AreEqual(50, _tabs.Options.MaxDepth);
        Assert.AreEqual(10000, _tabs.Options.MaxElements);
        Assert.AreEqual(120000, _tabs.Options.TimeoutMs);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("--window 0")]
    [DataRow("--window -1")]
    [DataRow("--window 42 --depth 0")]
    [DataRow("--window 42 --depth 51")]
    [DataRow("--window 42 --max-elements 0")]
    [DataRow("--window 42 --max-elements 10001")]
    [DataRow("--window 42 --timeout 999")]
    [DataRow("--window 42 --timeout 120001")]
    [DataRow("--window 42 --select item")]
    [DataRow("--window 42 --container tab")]
    [DataRow("--window 42 --root-runtime-id root")]
    [DataRow("--window 42 --accept-navigation")]
    [DataRow("--window 42 --select item --container tab --root-runtime-id root")]
    [DataRow("--window 42 --select item --container tab --root-runtime-id root --accept-navigation=false")]
    [DataRow("--window 42 --select \" \" --container tab --root-runtime-id root --accept-navigation")]
    public async Task MissingConsentOrInvalidArgumentsCannotReachTheService(string arguments)
    {
        var parsed = _services.GetRequiredService<UiAuditTabsCommand>().Parse(arguments + " --json", WinAppParserConfiguration.Default);
        Assert.AreNotEqual(0, await Invoke(parsed));
        Assert.AreEqual(0, _tabs.Calls);
        Assert.IsEmpty(_output.ToString());
        using var error = JsonDocument.Parse(_error.ToString());
        Assert.AreEqual("invalid_arguments", error.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [TestMethod]
    [DataRow("--app Fixture")]
    [DataRow("--selector #Tab")]
    [DataRow("--focus")]
    [DataRow("--via click")]
    [DataRow("--window nope")]
    [DataRow("--select item --accept-navigation=invalid")]
    public void NoAppSelectorOrInputFallbackOptionsAreExposed(string arguments)
    {
        Assert.IsNotEmpty(_services.GetRequiredService<UiAuditTabsCommand>().Parse(arguments).Errors);
        Assert.AreEqual(0, _tabs.Calls);
    }

    [TestMethod]
    [DataRow("captured", 0)]
    [DataRow("partial", 1)]
    [DataRow("blocked", 1)]
    public async Task AllOperationalOutcomesKeepTheExactTypedJsonShape(string status, int expectedCode)
    {
        _tabs.Result = new()
        {
            ProcessId = 123, WindowHandle = 42, RootRuntimeId = "[2A,2]", Status = status, MaxDepth = 20, MaxElements = 2000,
            DepthLimitReached = status == "partial", Issues = status == "captured" ? [] : ["explicit_issue"],
            Containers =
            [
                new()
                {
                    RuntimeId = "[2A,3]", IsEnabled = true, IsOffscreen = false, Supported = true,
                    Items = [new() { RuntimeId = "[2A,4]", IsEnabled = true, IsOffscreen = false, IsSelected = true, Supported = true }],
                },
            ],
        };
        Assert.AreEqual(expectedCode, await Invoke(["--window", "42", "--json"]));
        using var document = JsonDocument.Parse(_output.ToString());
        var root = document.RootElement;
        string[] expected =
        [
            "schemaVersion", "processId", "windowHandle", "rootRuntimeId", "treeSignature", "status", "maxDepth", "maxElements",
            "depthLimitReached", "elementLimitReached", "containers", "issues", "selection",
        ];
        CollectionAssert.AreEquivalent(expected, root.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(_tabs.Result.TreeSignature, root.GetProperty("treeSignature").GetString());
        Assert.HasCount(64, root.GetProperty("treeSignature").GetString()!);
        Assert.AreEqual(JsonValueKind.Null, root.GetProperty("selection").ValueKind);
        var container = root.GetProperty("containers")[0];
        string[] containerFields = ["runtimeId", "parentContainerRuntimeId", "automationId", "isEnabled", "isOffscreen", "supported", "reason", "items"];
        CollectionAssert.AreEquivalent(containerFields, container.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(JsonValueKind.Null, container.GetProperty("parentContainerRuntimeId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, container.GetProperty("automationId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, container.GetProperty("reason").ValueKind);
        var item = container.GetProperty("items")[0];
        string[] itemFields = ["runtimeId", "automationId", "isEnabled", "isOffscreen", "isSelected", "supported", "reason"];
        CollectionAssert.AreEquivalent(itemFields, item.EnumerateObject().Select(property => property.Name).ToArray());
        Assert.AreEqual(JsonValueKind.Null, item.GetProperty("automationId").ValueKind);
        Assert.AreEqual(JsonValueKind.Null, item.GetProperty("reason").ValueKind);
        Assert.IsFalse(item.TryGetProperty("name", out _));
        Assert.IsFalse(item.TryGetProperty("value", out _));
        Assert.IsTrue(item.GetProperty("isSelected").GetBoolean());
    }

    [TestMethod]
    public async Task RequiredBooleanAndListMembersRemainPresentWhenFalseOrEmpty()
    {
        UiAuditTabContainer[][] cases =
        [
            [],
            [new() { RuntimeId = "tab", Reason = "single_selection_pattern_required" }],
            [new() { RuntimeId = "tab", Reason = "single_selection_pattern_required",
                Items = [new() { RuntimeId = "item", Reason = "selection_item_pattern_required" }] }],
        ];
        foreach (var containers in cases)
        {
            _output.GetStringBuilder().Clear();
            _tabs.Result = new()
            {
                ProcessId = 123, WindowHandle = 42, RootRuntimeId = "[2A,2]", Status = "captured",
                MaxDepth = 20, MaxElements = 2000, Containers = containers,
            };
            Assert.AreEqual(0, await Invoke(["--window", "42", "--json"]));
            using var document = JsonDocument.Parse(_output.ToString());
            var root = document.RootElement;
            Assert.AreEqual(JsonValueKind.False, root.GetProperty("depthLimitReached").ValueKind);
            Assert.AreEqual(JsonValueKind.False, root.GetProperty("elementLimitReached").ValueKind);
            Assert.AreEqual(0, root.GetProperty("issues").GetArrayLength());
            Assert.AreEqual(containers.Length, root.GetProperty("containers").GetArrayLength());
            foreach (var container in root.GetProperty("containers").EnumerateArray())
            {
                foreach (var field in new[] { "isEnabled", "isOffscreen", "supported" })
                {
                    Assert.AreEqual(JsonValueKind.False, container.GetProperty(field).ValueKind);
                }
                Assert.AreEqual(containers[0].Items.Count, container.GetProperty("items").GetArrayLength());
                foreach (var item in container.GetProperty("items").EnumerateArray())
                {
                    foreach (var field in new[] { "isEnabled", "isOffscreen", "isSelected", "supported" })
                    {
                        Assert.AreEqual(JsonValueKind.False, item.GetProperty(field).ValueKind);
                    }
                }
            }
        }
    }

    [TestMethod]
    public async Task OperationalFailureAndCancellationStillReturnBlockedTypedEvidence()
    {
        foreach (var failure in new Exception[] { new InvalidOperationException("provider details withheld"), new OperationCanceledException() })
        {
            _output.GetStringBuilder().Clear();
            _tabs.Failure = failure;
            Assert.AreEqual(1, await Invoke(["--window", "42", "--json", "--select", "item",
                "--container", "tab", "--root-runtime-id", "root", "--accept-navigation"]));
            using var document = JsonDocument.Parse(_output.ToString());
            Assert.AreEqual("blocked", document.RootElement.GetProperty("status").GetString());
            Assert.HasCount(64, document.RootElement.GetProperty("treeSignature").GetString()!);
            var selection = document.RootElement.GetProperty("selection");
            string[] receiptFields = ["containerRuntimeId", "itemRuntimeId", "status"];
            CollectionAssert.AreEquivalent(receiptFields, selection.EnumerateObject().Select(property => property.Name).ToArray());
            Assert.AreEqual("tab", selection.GetProperty("containerRuntimeId").GetString());
            Assert.AreEqual("item", selection.GetProperty("itemRuntimeId").GetString());
            Assert.AreEqual("blocked", selection.GetProperty("status").GetString());
            Assert.DoesNotContain("provider details", _output.ToString());
        }
    }

    [TestMethod]
    public async Task HelpAndSchemaAreNativeAndDoNotResolveOrCallTheService()
    {
        using var services = new ServiceCollection().ConfigureCommands().AddSingleton<IAnsiConsole>(_console).BuildServiceProvider();
        var root = services.GetRequiredService<WinAppRootCommand>();
        var ui = services.GetRequiredService<UiCommand>();
        Assert.IsTrue(ui.Subcommands.Contains(services.GetRequiredService<UiAuditTabsCommand>()));
        foreach (var flag in new[] { "--help", "--cli-schema" })
        {
            _output.GetStringBuilder().Clear();
            var parsed = root.Parse(["ui", "audit-tabs", flag]);
            Assert.AreEqual(0, await Invoke(parsed));
            Assert.IsNotEmpty(_output.ToString());
        }
        using var schema = JsonDocument.Parse(_output.ToString());
        var options = schema.RootElement.GetProperty("options");
        Assert.AreEqual(15000, options.GetProperty("--timeout").GetProperty("defaultValue").GetInt32());
        Assert.IsTrue(options.TryGetProperty("--select", out _));
        Assert.IsTrue(options.TryGetProperty("--root-runtime-id", out _));
        Assert.IsFalse(options.TryGetProperty("--app", out _));
        Assert.AreEqual(0, _tabs.Calls);
    }

    private Task<int> Invoke(string[] arguments) => Invoke(_services.GetRequiredService<UiAuditTabsCommand>().Parse(arguments, WinAppParserConfiguration.Default));
    private Task<int> Invoke(ParseResult parsed)
    {
        parsed.InvocationConfiguration.Output = _output;
        parsed.InvocationConfiguration.Error = _error;
        return parsed.InvokeAsync(parsed.InvocationConfiguration, TestContext.CancellationToken);
    }

    private sealed class TabsFake : IUiAuditTabsService
    {
        internal int Calls { get; private set; }
        internal long Window { get; private set; }
        internal UiAuditTabsOptions? Options { get; private set; }
        internal Exception? Failure { get; set; }
        internal UiAuditTabsResult Result { get; set; } = new() { Status = "captured", WindowHandle = 42, RootRuntimeId = "[2A,2]" };
        public Task<UiAuditTabsResult> CaptureAsync(long windowHandle, UiAuditTabsOptions options, CancellationToken cancellationToken = default)
        {
            Calls++;
            Window = windowHandle;
            Options = options;
            if (Failure is not null) { throw Failure; }
            return Task.FromResult(Result);
        }
    }
}
