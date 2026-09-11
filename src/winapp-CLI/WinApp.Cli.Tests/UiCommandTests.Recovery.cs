// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;
using WinApp.Cli.Services.InteractiveDesktop;

namespace WinApp.Cli.Tests;

public partial class UiCommandTests
{
    [TestMethod]
    [DataRow("set-value")]
    [DataRow("send-keys")]
    [DataRow("yield")]
    public async Task UsageAndYieldRecoveryAdvice_RetainsGuestDiagnosticTarget(string verb)
    {
        var previous = Environment.GetEnvironmentVariable(UiCommandAdvice.TargetVariable);
        var previousError = Console.Error;
        Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, "test-provider:agent");
        try
        {
            Console.SetError(ConsoleStdErr);
            _fakeDesktopLock.YieldResult = UiYieldResult.Busy;
            var (command, arguments) = verb switch
            {
                "set-value" => ((System.CommandLine.Command)GetRequiredService<UiSetValueCommand>(),
                    new[] { "SearchBox", "-a", "4416", "--json" }),
                "send-keys" => (GetRequiredService<UiSendKeysCommand>(), new[] { "-a", "4416", "--json" }),
                _ => (GetRequiredService<UiYieldCommand>(), new[] { "--json" }),
            };

            var exitCode = await ParseAndInvokeWithCaptureAsync(command, arguments);

            Assert.AreEqual(1, exitCode);
            var stderr = ConsoleStdErr.ToString();
            using var document = JsonDocument.Parse(stderr[stderr.IndexOf('{')..]);
            var error = document.RootElement.GetProperty("error");
            var advice = error.GetProperty(verb == "yield" ? "recoveryHint" : "message").GetString()!;
            StringAssert.Contains(advice, $"winapp ui {verb}");
            StringAssert.Contains(advice, "--on test-provider:agent");
        }
        finally
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, previous);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task SetValue_RecoveryAdviceRetainsGuestDiagnosticTarget(bool json)
    {
        var previous = Environment.GetEnvironmentVariable(UiCommandAdvice.TargetVariable);
        Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, "test-provider:agent");
        var previousError = Console.Error;
        try
        {
            Console.SetError(ConsoleStdErr);
            _fakeUia.FindSingleResult = new UiElement { Id = "e1", Type = "Edit", Selector = "SearchBox" };
            _fakeUia.SetValueThrow = new UiValueSetException(_fakeUia.FindSingleResult);

            var arguments = new List<string> { "SearchBox", "Alpine", "-a", "4416" };
            if (json) { arguments.Add("--json"); }
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                GetRequiredService<UiSetValueCommand>(), arguments.ToArray());

            Assert.AreEqual(1, exitCode);
            var stderr = ConsoleStdErr.ToString();
            var message = stderr;
            if (json)
            {
                var jsonStart = stderr.IndexOf('{');
                using var document = JsonDocument.Parse(stderr[jsonStart..]);
                var error = document.RootElement.GetProperty("error");
                Assert.AreEqual("internal_error", error.GetProperty("code").GetString());
                Assert.AreEqual("InvalidOperationException", error.GetProperty("details").GetString());
                message = error.GetProperty("message").GetString()!;
            }
            StringAssert.Contains(message,
                "winapp ui send-keys --verbatim \"<value>\" --target \"SearchBox\" --via send-input -a <app> --on test-provider:agent");
        }
        finally
        {
            Console.SetError(previousError);
            Environment.SetEnvironmentVariable(UiCommandAdvice.TargetVariable, previous);
        }
    }
}
