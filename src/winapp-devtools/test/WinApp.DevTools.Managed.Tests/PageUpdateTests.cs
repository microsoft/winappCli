// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.ComponentModel;
using WinApp.DevTools.Managed.HotReload;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class PageUpdateTests
{
    public sealed class Vm : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Title { get; set; } = "start";
        public int Count { get; set; } = 1;
        public void Raise() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
    }

    public sealed class FakePage
    {
        private int _count;
        private readonly Action _handler;
        public Vm Model { get; } = new();
        public string Late { get; set; } = "ctor";
        public List<string> Items { get; } = ["a"];
        public string Summary => $"{_count} items";
        public string Bindings = "compiler";
        private bool _contentLoaded;

        public FakePage()
        {
            _handler = () => Late = "loaded";
        }

        public void Load() => _handler();

        public void Click()
        {
            _count++;
            _contentLoaded = !_contentLoaded;
        }
    }

    [TestMethod]
    public void PageDataNamesFieldsAsWrittenAndReadsOwnedViewModels()
    {
        var data = PageData.Take(new FakePage(), getters: true);

        CollectionAssert.AreEqual(new[] { "_count", "Model", "Late", "Items", "Summary" }, data.TopLevel);
        Assert.AreEqual("start", data.Values["Model.Title"]);
        Assert.AreEqual("[1]", data.Values["Items"]);
        Assert.AreEqual("0 items", data.Values["Summary"]);
        Assert.IsFalse(data.Values.ContainsKey("Bindings"));
        Assert.IsFalse(data.Values.ContainsKey("_contentLoaded"));
        Assert.AreEqual(typeof(Vm), data.Members["Model.Title"].Owner);
    }

    [TestMethod]
    public void ChangedListsWhatAFreshInstanceStartsOver()
    {
        var used = new FakePage();
        used.Click();
        used.Load();
        used.Model.Title = "set";

        var changed = PageData.Take(used, getters: false).Changed(PageData.Take(new FakePage(), getters: false));

        CollectionAssert.AreEqual(new[] { "_count", "Model", "Late" }, changed);
    }

    [TestMethod]
    public void WhereSetFindsTheAssignmentOutsideTheConstructor()
    {
        var site = SourceSites.WhereSet(typeof(FakePage), nameof(FakePage.Late), typeof(FakePage));

        Assert.IsNotNull(site);
        StringAssert.StartsWith(site, "PageUpdateTests.cs:");
        var line = int.Parse(site.Split(':')[1]);
        Assert.AreEqual("_handler = () => Late = \"loaded\";", File.ReadAllLines(SourceFile())[line - 1].Trim());
    }

    [TestMethod]
    public void RaisesChangesTellsANotifyingOwner()
    {
        Assert.IsTrue(PageData.RaisesChanges(typeof(Vm)));
        Assert.IsFalse(PageData.RaisesChanges(typeof(FakePage)));
    }

    private static string SourceFile([System.Runtime.CompilerServices.CallerFilePath] string path = "") => path;
}
