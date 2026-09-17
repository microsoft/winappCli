// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

extern alias uia;

using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using WinApp.Cli.Commands;
using uia::Windows.Win32.Foundation;
using uia::Windows.Win32.UI.Accessibility;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    public static IEnumerable<object[]> QueryGeneralPropertyFailures()
    {
        foreach (var command in new[] { "get-property", "wait-for" })
        {
            foreach (var property in new[] { "HasKeyboardFocus", "IsKeyboardFocusable", "AcceleratorKey", "AccessKey", "HelpText", "IsPassword" })
            {
                foreach (var hresult in new[] { unchecked((int)0x80040201), unchecked((int)0x80004005) })
                {
                    yield return [command, property, hresult];
                }
            }
        }
    }

    [TestMethod]
    [DynamicData(nameof(QueryGeneralPropertyFailures))]
    public async Task QueryOptions_GeneralPropertyFaultSurfacesOrRetries(string name, string property, int hresult)
    {
        var reads = 0;
        var getter = property == "IsPassword" ? "get_CurrentIsContentElement" : $"get_Current{property}";
        var provider = PropertyProxy<IUIAutomationElement>((method, _) =>
        {
            if (method.Name == getter && ++reads == 1) { throw new COMException("Property getter failed.", hresult); }
            return method.Name switch
            {
                "get_CurrentProcessId" => Environment.ProcessId,
                "get_CurrentControlType" => UIA_CONTROLTYPE_ID.UIA_EditControlTypeId,
                "get_CurrentAcceleratorKey" or "get_CurrentAccessKey" or "get_CurrentHelpText" => PropertyBstr("ready"),
                "GetCurrentPattern" => throw new COMException("Pattern unsupported.", unchecked((int)0x80040204)),
                _ => new BOOL(false),
            };
        });
        _fakeUia.FindSingleResult = new UiElement
        {
            Type = "Edit", Selector = "txt-value-a123", RequiresCurrentIdentity = true,
            Context = new UiElementContext(provider),
        };
        var real = new UiAutomationService(GetRequiredService<ILogger<UiAutomationService>>(), new UiSelectorParser());
        var service = PropertyProxy<IUiAutomation>((method, args) => method.Name switch
        {
            nameof(IUiAutomation.FindSingleElementAsync) =>
                _fakeUia.FindSingleElementAsync((UiTarget)args![0]!, (UiSelector)args[1]!, (CancellationToken)args[2]!),
            nameof(IUiAutomation.GetPropertiesAsync) =>
                real.GetPropertiesAsync((UiTarget)args![0]!, (UiElement)args[1]!, (string?)args[2], (CancellationToken)args[3]!),
            _ => throw new NotSupportedException(method.Name),
        });
        var command = QueryCommand(name);
        System.CommandLine.Invocation.AsynchronousCommandLineAction handler = name == "get-property"
            ? new UiGetPropertyCommand.Handler(_fakeTargetResolver, service, new UiSelectorParser(),
                TestAnsiConsole, _fakeDesktopLock, GetRequiredService<ILogger<UiGetPropertyCommand>>())
            : new UiWaitForCommand.Handler(_fakeTargetResolver, service, new UiSelectorParser(),
                _fakePollDelay, TestAnsiConsole, _fakeDesktopLock, GetRequiredService<ILogger<UiWaitForCommand>>());
        command.SetAction((result, ct) => handler.InvokeAsync(result, ct));
        var args = new List<string>
        {
            "txtValue", "-a", "TestApp", "--root", "MailRow", "--type", "Edit", "--property", property, "--json",
        };
        if (name == "wait-for")
        {
            var expected = property is "AcceleratorKey" or "AccessKey" or "HelpText" ? "ready" : "False";
            args.AddRange(["--value", expected, "--timeout", "1000"]);
        }
        var exit = await ParseAndInvokeWithCaptureAsync(command, args.ToArray());

        var retries = name == "wait-for" && hresult == unchecked((int)0x80040201);
        Assert.AreEqual(retries ? 0 : 1, exit, $"{TestAnsiConsole.Output} {ConsoleStdErr}");
        Assert.AreEqual(retries ? 2 : 1, reads);
        Assert.AreEqual(retries ? 1 : 0, _fakePollDelay.CallCount);
        Assert.HasCount(retries ? 2 : 1, _fakeUia.Queries);
        Assert.IsTrue(_fakeUia.Queries.All(q => q.Root?.Query == "MailRow" && q.ControlType == "Edit"));
        if (retries) { StringAssert.Contains(TestAnsiConsole.Output, "\"found\": true"); }
        else if (name == "get-property")
        {
            StringAssert.Contains(ConsoleStdErr.ToString(), "no longer accessible");
            Assert.DoesNotContain("\"properties\"", TestAnsiConsole.Output);
        }
        else { AssertJsonErrorCode("stale_element"); }
    }

    private static unsafe BSTR PropertyBstr(string value) => new((char*)Marshal.StringToBSTR(value));

    private static T PropertyProxy<T>(Func<MethodInfo, object?[]?, object?> handler) where T : class
    {
        var proxy = DispatchProxy.Create<T, PropertyDispatchProxy>();
        ((PropertyDispatchProxy)(object)proxy).Handler = handler;
        return proxy;
    }

    private class PropertyDispatchProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Handler { get; set; } = (_, _) => null;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!, args);
    }
}
