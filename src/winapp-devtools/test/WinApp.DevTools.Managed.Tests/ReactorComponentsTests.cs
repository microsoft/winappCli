// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class ReactorComponentsTests
{
    public enum Filter { All, Active, Done }

    public sealed record TaskItem(int Id, string Title);

    private sealed class Counter
    {
        public object Render() => (Action)Increment;
        public static void Increment() { }
        public Action Lambda() => () => Increment();
        public Action Local()
        {
            return Step;
            static void Step() { }
        }
    }

    [TestMethod]
    public void Pdb_LocatesALambdaAtItsLine()
    {
        var span = ReactorPdb.Locate(new Counter().Lambda().Method);
        Assert.IsNotNull(span);
        var line = File.ReadAllLines(span.File)[span.Line - 1];
        StringAssert.Contains(line, "public Action Lambda()");
    }

    [TestMethod]
    public void TryParse_ConvertsToTheHookTypeOrExplainsWhyNot()
    {
        Assert.IsTrue(ReactorComponents.TryParse("42", typeof(int), out var number, out _));
        Assert.AreEqual(42, number);
        Assert.IsTrue(ReactorComponents.TryParse("done", typeof(Filter), out var filter, out _));
        Assert.AreEqual(Filter.Done, filter);
        Assert.IsTrue(ReactorComponents.TryParse("null", typeof(int?), out var none, out _));
        Assert.IsNull(none);
        Assert.IsTrue(ReactorComponents.TryParse("1.5", typeof(double), out var real, out _));
        Assert.AreEqual(1.5, real);

        Assert.IsFalse(ReactorComponents.TryParse("maybe", typeof(bool), out _, out var error));
        StringAssert.Contains(error, "true or false");
        Assert.IsFalse(ReactorComponents.TryParse("Later", typeof(Filter), out _, out error));
        StringAssert.Contains(error, "All, Active, Done");
        Assert.IsFalse(ReactorComponents.TryParse("x", typeof(int), out _, out error));
        StringAssert.Contains(error, "not a valid int");
        Assert.IsFalse(ReactorComponents.TryParse("{}", typeof(TaskItem), out _, out error));
        StringAssert.Contains(error, "cannot be typed as text");
        Assert.IsFalse(ReactorComponents.TryParse("null", typeof(int), out _, out _));
    }

    [TestMethod]
    public void TryParse_IsStrictAboutNumbersAndEnums()
    {
        Assert.IsFalse(ReactorComponents.TryParse("3,5", typeof(double), out _, out var error), "a group separator is not a decimal point");
        StringAssert.Contains(error, "not a valid double");
        Assert.IsFalse(ReactorComponents.TryParse("2,5", typeof(decimal), out _, out _));
        Assert.IsFalse(ReactorComponents.TryParse("1,000", typeof(int), out _, out _));
        Assert.IsFalse(ReactorComponents.TryParse("3.4e39", typeof(float), out _, out _), "out of range is not Infinity");
        Assert.IsFalse(ReactorComponents.TryParse("1e999", typeof(double), out _, out _));
        Assert.IsFalse(ReactorComponents.TryParse("300", typeof(byte), out _, out _));
        Assert.IsTrue(ReactorComponents.TryParse("-1.5e3", typeof(double), out var real, out _));
        Assert.AreEqual(-1500.0, real);
        Assert.IsTrue(ReactorComponents.TryParse("2.5", typeof(decimal), out var money, out _));
        Assert.AreEqual(2.5m, money);

        Assert.IsFalse(ReactorComponents.TryParse("All,Done", typeof(Filter), out _, out error), "a plain enum holds one member");
        StringAssert.Contains(error, "All, Active, Done");
        Assert.IsTrue(ReactorComponents.TryParse("Read,Write", typeof(Access), out var access, out _));
        Assert.AreEqual(Access.Read | Access.Write, access);
    }

    [Flags]
    private enum Access { None = 0, Read = 1, Write = 2 }

    [TestMethod]
    public void FriendlyTypeName_ReadsAsCSharp()
    {
        Assert.AreEqual("string?", ReactorComponents.FriendlyTypeName(typeof(string)) + "?");
        Assert.AreEqual("int?", ReactorComponents.FriendlyTypeName(typeof(int?)));
        Assert.AreEqual("Dictionary<string, int>", ReactorComponents.FriendlyTypeName(typeof(Dictionary<string, int>)));
    }

    [TestMethod]
    public void HandlerName_DemanglesLambdasLocalFunctionsAndMethodGroups()
    {
        Assert.AreEqual("Counter.Increment", ReactorComponents.HandlerName((Action)Counter.Increment));
        Assert.AreEqual("lambda in Counter.Lambda", ReactorComponents.HandlerName(new Counter().Lambda()));
        Assert.AreEqual("Step in Counter.Local", ReactorComponents.HandlerName(new Counter().Local()));
    }

    private sealed record ButtonElement(string Label)
    {
        public Action? OnClick { get; init; }
        public Action<string>? OnRename { get; init; }
        public Action<TaskItem>? OnDrop { get; init; }
        public ButtonModifiers? Modifiers { get; init; }
    }

    private sealed record ButtonModifiers
    {
        public Action<int>? OnPointerWheel { get; init; }
    }

    [TestMethod]
    public void Handlers_ListsRecordAndModifierHandlersAndSynthesizesOnlyHonestArguments()
    {
        var clicks = 0;
        string? renamed = null;
        var tag = new ButtonElement("Save")
        {
            OnClick = () => clicks++,
            OnRename = text => renamed = text,
            OnDrop = _ => { },
            Modifiers = new ButtonModifiers { OnPointerWheel = _ => { } },
        };

        var handlers = ReactorHandlers.Read(tag);
        CollectionAssert.AreEquivalent(new[] { "OnClick", "OnRename", "OnDrop", "OnPointerWheel" }, handlers.Select(h => h.Event).ToArray());
        Assert.IsTrue(handlers.Single(h => h.Event == "OnPointerWheel").Modifier);
        Assert.AreSame(handlers[0], ReactorHandlers.Find(handlers, "click"));

        Assert.IsTrue(ReactorHandlers.TryArguments(tag.OnClick, null, "", out var args, out _));
        tag.OnClick.DynamicInvoke(args);
        Assert.AreEqual(1, clicks);

        Assert.IsFalse(ReactorHandlers.TryArguments(tag.OnRename, null, "", out _, out var why));
        StringAssert.Contains(why, "--value");
        Assert.IsTrue(ReactorHandlers.TryArguments(tag.OnRename, null, "Draft", out args, out _));
        tag.OnRename.DynamicInvoke(args);
        Assert.AreEqual("Draft", renamed);

        Assert.IsFalse(ReactorHandlers.TryArguments(tag.OnDrop, null, "", out _, out why));
        StringAssert.Contains(why, "cannot create a TaskItem");
        Assert.IsFalse(ReactorHandlers.TryArguments(tag.OnClick, null, "extra", out _, out why));
        Assert.AreEqual("(string obj)", ReactorHandlers.Signature(tag.OnRename));
    }

    [TestMethod]
    public void Resources_ParseColorsByHex()
    {
        Assert.IsTrue(ResourceOps.TryColor("#FF0067C0", out var color));
        Assert.AreEqual((byte)0x67, color.G);
        Assert.IsTrue(ResourceOps.TryColor("#C42B1C", out color));
        Assert.AreEqual((byte)0xFF, color.A);
        Assert.IsFalse(ResourceOps.TryColor("#12", out _));
    }

    [TestMethod]
    public void Resources_RevertedWhenTheLiveValueIsNotTheOneWritten()
    {
        Assert.IsTrue(ResourceOps.Reverted("elem value 2", "scoped plain", found: true), "the app wrote the key again");
        Assert.IsTrue(ResourceOps.Reverted("elem value 2", null, found: false), "the app removed the key");
        Assert.IsFalse(ResourceOps.Reverted("elem value 2", "elem value 2", found: true));
        var written = new object();
        Assert.IsFalse(ResourceOps.Reverted(written, written, found: true));
    }

    [TestMethod]
    public void Resources_ReadOnlyAppResourcesNamesTheNextStep()
    {
        StringAssert.Contains(ResourceOps.ReadOnlyAppResources, "--scope element");
        StringAssert.Contains(ResourceOps.ReadOnlyAppResources, "MergedDictionaries");
        Assert.IsFalse(ResourceOps.ReadOnlyAppResources.Contains("COMException", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ErrorScreen_RecognizesReactorsBuiltInHeadlinesAndSaysWhatToDo()
    {
        Assert.IsTrue(ReactorApi.IsErrorScreenHeadline("Render error: ArgumentException: ShowAttached failed."));
        Assert.IsTrue(ReactorApi.IsErrorScreenHeadline("Something went wrong."));
        Assert.IsFalse(ReactorApi.IsErrorScreenHeadline("⚠ Render error: in-tree placeholder"), "a component's in-tree placeholder is not the host's screen");
        Assert.IsFalse(ReactorApi.IsErrorScreenHeadline("Render errors: 2"));
        Assert.IsFalse(ReactorApi.IsErrorScreenHeadline(null));

        var reason = ReactorApi.ErrorScreenReason("MainShell", "Render error: ArgumentException: ShowAttached failed.");
        StringAssert.StartsWith(reason, "this window shows Reactor's error screen instead of <MainShell>");
        StringAssert.Contains(reason, "Render error: ArgumentException: ShowAttached failed. Fix the error");
        StringAssert.Contains(ReactorApi.ErrorScreenReason(null, "Something went wrong."), "instead of its root component");
    }
}
