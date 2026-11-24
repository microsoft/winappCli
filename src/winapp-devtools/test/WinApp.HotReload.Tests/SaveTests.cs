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
    public async Task XamlOnStockWinUISaysItNeedsARestartWithoutRestarting()
    {
        var xaml = Write(@"App\Page.xaml", "<Page><Grid></Page>");

        var result = await _save.RunAsync([xaml], CancellationToken.None);

        Assert.AreEqual(new SaveResult(Outcome.NotApplied, "Page.xaml: XAML changes need a restart on this WinUI version (press Ctrl+R)"), result);
        Assert.AreEqual(0, _engine.Files.Count);
    }

    [TestMethod]
    public async Task XamlSavedWithCodeAppliesTheCodeAndNotesTheXaml()
    {
        var page = Write(@"App\Page.cs", "class Page {}");
        var xaml = Write(@"App\Page.xaml", "<Page />");

        var result = await _save.RunAsync([page, xaml], CancellationToken.None);

        Assert.AreEqual(Outcome.Applied, result.Outcome);
        Assert.AreEqual("Page.cs", result.Detail);
        CollectionAssert.AreEqual(new[] { "Page.xaml: XAML changes need a restart on this WinUI version (press Ctrl+R)" }, result.Notes!.ToArray());
        CollectionAssert.AreEqual(new[] { page }, _engine.Files.ToArray());
    }

    [TestMethod]
    [DataRow("InitializeComponent", true)]
    [DataRow("Banner", true)]
    [DataRow("Baner", false)]
    public async Task CodeThatNeedsUncompiledXamlSaysToRestart(string name, bool uncompiled)
    {
        Write(@"App\Page.xaml", "<Page><TextBlock x:Name=\"Banner\" /></Page>");
        var code = Write(@"App\Page.xaml.cs", "partial class Page {}");
        var error = $"Page.xaml.cs(9,13): CS0103: The name '{name}' does not exist in the current context";
        _engine.Result = new CSharpResult(CSharpOutcome.Error, error);

        var result = await _save.RunAsync([code], CancellationToken.None);

        Assert.AreEqual(uncompiled
            ? new SaveResult(Outcome.NotApplied, "Page.xaml.cs: new XAML isn't compiled on this WinUI version (press Ctrl+R to restart)")
            : new SaveResult(Outcome.Error, error), result);
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

        public CSharpResult Result { get; set; } = new(CSharpOutcome.Applied);

        public Task<CSharpResult> ApplyAsync(CSharpChange change, CancellationToken cancellationToken)
        {
            Files.AddRange(change.Files);
            return Task.FromResult(Result);
        }

        public Task<byte[]?> AppImageAsync(IReadOnlyList<string> files, CancellationToken cancellationToken) => Task.FromResult<byte[]?>(null);
    }
}
