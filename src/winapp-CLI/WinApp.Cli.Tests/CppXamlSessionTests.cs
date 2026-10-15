// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

[TestClass]
public class CppXamlSessionTests
{
    private DirectoryInfo _tempDir = null!;

    [TestInitialize]
    public void Setup() => _tempDir = Directory.CreateDirectory(Path.Join(Path.GetTempPath(), $"CppXamlSessionTests_{Guid.NewGuid():N}"));

    [TestCleanup]
    public void Cleanup() => _tempDir.Delete(recursive: true);

    [TestMethod]
    public async Task HotReloadSetting_TheBuildsValueReachesTheServiceInputs()
    {
        // Older packages' InputSerializer can't write EnableXamlHotReload; the build records it beside the inputs.
        var inputs = Path.Join(_tempDir.FullName, "xaml-inputs.json");
        await File.WriteAllTextAsync(inputs, """{"Language":"CppWinRT","EnableXamlHotReload":false}""");
        await File.WriteAllTextAsync(inputs + ".hotreload", "true\r\n");
        var work = Directory.CreateDirectory(Path.Join(_tempDir.FullName, "xaml")).FullName;

        var served = await CppXamlSession.WithHotReloadSettingAsync(inputs, work, CancellationToken.None);

        Assert.AreNotEqual(inputs, served);
        using var document = JsonDocument.Parse(await File.ReadAllTextAsync(served));
        Assert.IsTrue(document.RootElement.GetProperty("EnableXamlHotReload").GetBoolean());
        Assert.AreEqual("CppWinRT", document.RootElement.GetProperty("Language").GetString());
    }

    [TestMethod]
    public async Task HotReloadSetting_InputsAreUsedAsIsWhenTheyAgreeOrNothingWasRecorded()
    {
        var inputs = Path.Join(_tempDir.FullName, "xaml-inputs.json");
        await File.WriteAllTextAsync(inputs, """{"EnableXamlHotReload":true}""");
        var work = Directory.CreateDirectory(Path.Join(_tempDir.FullName, "xaml")).FullName;

        Assert.AreEqual(inputs, await CppXamlSession.WithHotReloadSettingAsync(inputs, work, CancellationToken.None));
        await File.WriteAllTextAsync(inputs + ".hotreload", "True");
        Assert.AreEqual(inputs, await CppXamlSession.WithHotReloadSettingAsync(inputs, work, CancellationToken.None));
    }
}
