// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace WinApp.HotReload.Tests;

[TestClass]
public sealed class EngineTests
{
    private const string Before = """
        class Page
        {
            int Added() => 1; // added this session
            int Other() => 2;
        }
        """;

    [TestMethod]
    [DataRow("int Added() => 1; // edited comment", true, DisplayName = "A comment edit inside a line changes nothing")]
    [DataRow("int  Added()  =>  1; // added this session", true, DisplayName = "Whitespace inside a line changes nothing")]
    [DataRow("// a new line\n    int Added() => 1; // added this session", false, DisplayName = "A new comment line moves code: a real update")]
    [DataRow("int Added() => 3; // added this session", false, DisplayName = "A code change is a real update")]
    public async Task OnlyTriviaChanged(string line, bool expected)
    {
        using var workspace = new AdhocWorkspace();
        var project = workspace.AddProject("App", LanguageNames.CSharp);
        var document = workspace.AddDocument(project.Id, "Page.cs", SourceText.From(Before));
        var edited = document.WithText(SourceText.From(Before.Replace("int Added() => 1; // added this session", line)));

        var changes = edited.Project.Solution.GetChanges(document.Project.Solution).GetProjectChanges();

        Assert.AreEqual(expected, await RoslynEngine.OnlyTriviaChangedAsync(changes, CancellationToken.None));
    }

    [TestMethod]
    public void AnSdkWithoutTheHotReloadServiceSaysSo()
    {
        var sdk = Directory.CreateTempSubdirectory("winapp-sdk9-");
        try
        {
            Directory.CreateDirectory(Path.Combine(sdk.FullName, "Roslyn", "bincore"));
            File.WriteAllText(Path.Combine(sdk.FullName, "Roslyn", "bincore", "Microsoft.CodeAnalysis.dll"), "");

            var reason = HotReloadApi.Register(sdk.FullName, "9.0.308");

            Assert.AreEqual("C# hot reload isn't supported on .NET SDK 9.0.308 yet (it has no dotnet-watch hot reload service; " +
                ".NET SDK 10.0.100 or later has one), so C# saves restart the app", reason);
        }
        finally
        {
            sdk.Delete(recursive: true);
        }
    }
}
