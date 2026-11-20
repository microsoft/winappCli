// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.HotReload.Tests;

[TestClass]
public sealed class SaveTests
{
    private DirectoryInfo _root = null!;
    private string _app = null!;
    private RecordingEngine _engine = null!;
    private Save _save = null!;

    [TestInitialize]
    public void SetUp()
    {
        _root = Directory.CreateTempSubdirectory("winapp-save-");
        _app = Path.Combine(_root.FullName, "App", "App.csproj");
        var library = Path.Combine(_root.FullName, "Core", "Core.csproj");
        var capture = Path.Combine(_root.FullName, "capture");
        Directory.CreateDirectory(Path.GetDirectoryName(_app)!);
        Directory.CreateDirectory(Path.GetDirectoryName(library)!);
        Directory.CreateDirectory(capture);
        File.WriteAllLines(Path.Combine(capture, "projects.txt"), [library]);
        _engine = new RecordingEngine();
        _save = new Save(_engine, new ProjectGraph(_app, capture));
    }

    [TestCleanup]
    public void TearDown() => _root.Delete(recursive: true);

    private string Write(string relative, string text)
    {
        var path = Path.Combine(_root.FullName, relative);
        File.WriteAllText(path, text);
        return path;
    }

    [TestMethod]
    public async Task AppCodeGoesThroughTheEngine()
    {
        var page = Write(@"App\Page.cs", "class Page {}");

        var result = await _save.RunAsync([page, _app], CancellationToken.None);

        Assert.AreEqual(Outcome.Applied, result.Outcome);
        CollectionAssert.AreEqual(new[] { page, _app }, _engine.Files.ToArray());
    }

    [TestMethod]
    public async Task XamlRestartsWithAReasonOnStockWinUI()
    {
        var page = Write(@"App\Page.cs", "class Page {}");
        var xaml = Write(@"App\Page.xaml", "<Page />");

        var result = await _save.RunAsync([page, xaml], CancellationToken.None);

        Assert.AreEqual(new SaveResult(Outcome.Restart, "Page.xaml: XAML changes take effect when the app restarts (this app's WinUI can't reload XAML while it runs)"), result);
        Assert.AreEqual(0, _engine.Files.Count);
    }

    [TestMethod]
    public async Task HalfTypedXamlIsAnErrorNotARestart()
    {
        var xaml = Write(@"App\Page.xaml", "<Page><Grid></Page>");

        var result = await _save.RunAsync([xaml], CancellationToken.None);

        Assert.AreEqual(Outcome.Error, result.Outcome);
        StringAssert.StartsWith(result.Detail, "Page.xaml(1,");
    }

    [TestMethod]
    public async Task LibraryCodeRestartsAndNamesTheLibrary()
    {
        var greeter = Write(@"Core\Greeter.cs", "class Greeter {}");

        var result = await _save.RunAsync([greeter], CancellationToken.None);

        Assert.AreEqual(new SaveResult(Outcome.Restart, @"..\Core\Greeter.cs: edits to Core, a project the app references, take effect when the app restarts"), result);
    }

    private sealed class RecordingEngine : ICSharpEngine
    {
        public List<string> Files { get; } = [];

        public Task<CSharpResult> ApplyAsync(CSharpChange change, CancellationToken cancellationToken)
        {
            Files.AddRange(change.Files);
            return Task.FromResult(new CSharpResult(CSharpOutcome.Applied));
        }

        public Task<byte[]> AppImageAsync(CancellationToken cancellationToken) => Task.FromResult(Array.Empty<byte>());
    }
}
