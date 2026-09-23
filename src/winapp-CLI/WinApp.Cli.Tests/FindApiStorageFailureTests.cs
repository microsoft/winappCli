// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class FindApiStorageFailureTests : BaseCommandTests
{
    [TestMethod]
    [DataRow("Button")]
    [DataRow("projects")]
    [DataRow("refresh")]
    [DataRow("members Button")]
    [DataRow("check-property Button Background")]
    [DataRow("types Microsoft.UI.Xaml")]
    [DataRow("enums Microsoft.UI.Xaml.Visibility")]
    [DataRow("namespaces")]
    [DataRow("packages")]
    [DataRow("stats")]
    public async Task ExplicitCacheFailure_IsOneFlatJsonError_NotAStackTrace(string commandLine)
    {
        var blocked = Path.Join(_tempDirectory.FullName, "cache-is-a-file");
        File.WriteAllText(blocked, "unchanged");
        GetRequiredService<IWinappDirectoryService>().SetCacheDirectoryForTesting(new DirectoryInfo(blocked));
        var arguments = commandLine.Split(' ').Append("--json").ToArray();

        var exit = await ParseAndInvokeWithCaptureAsync(GetRequiredService<FindApiCommand>(), arguments);

        Assert.AreEqual(1, exit);
        using var document = JsonDocument.Parse(TestAnsiConsole.Output);
        StringAssert.Contains(document.RootElement.GetProperty("error").GetString()!, "WINAPP_CLI_CACHE_DIRECTORY");
        Assert.AreEqual(string.Empty, ConsoleStdErr.ToString());
        Assert.AreEqual("unchanged", File.ReadAllText(blocked));
    }
}
