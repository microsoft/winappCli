// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class BindingCaptureStoreTests
{
    public sealed class CollidingElement : FrameworkElement
    {
        public override int GetHashCode() => 42;
    }

    private static T Uninitialized<T>() where T : class
        => (T)RuntimeHelpers.GetUninitializedObject(typeof(T));

    private static BindingCaptureStore CreateStore()
    {
        var values = new Dictionary<FrameworkElement, Dictionary<string, BindingBase>>(ReferenceEqualityComparer.Instance);
        return new(element => values.GetValueOrDefault(element),
            (element, value) =>
            {
                if (value is null) values.Remove(element);
                else values[element] = value;
            });
    }

    [TestMethod]
    public void CapturesForDifferentElementsWithTheSameHashRemainIndependent()
    {
        var store = CreateStore();
        var first = Uninitialized<CollidingElement>();
        var second = Uninitialized<CollidingElement>();
        var firstBinding = Uninitialized<Binding>();
        var secondBinding = Uninitialized<Binding>();

        store.Set(first, "Text", firstBinding);
        store.Set(second, "Text", secondBinding);

        Assert.IsTrue(store.TryGet(first, "Text", out var firstSaved));
        Assert.AreSame(firstBinding, firstSaved);
        Assert.IsTrue(store.TryGet(second, "Text", out var secondSaved));
        Assert.AreSame(secondBinding, secondSaved);

        store.Remove(first, "Text");
        Assert.IsFalse(store.TryGet(first, "Text", out _));
        Assert.IsTrue(store.TryGet(second, "Text", out var remaining));
        Assert.AreSame(secondBinding, remaining);
    }

    [TestMethod]
    public void RecaptureAndRemovalOnlyAffectTheSelectedProperty()
    {
        var store = CreateStore();
        var element = Uninitialized<CollidingElement>();
        var text = Uninitialized<Binding>();
        var other = Uninitialized<Binding>();
        var replacement = Uninitialized<Binding>();
        store.Set(element, "Text", text);
        store.Set(element, "Tag", other);
        store.Set(element, "Text", replacement);
        Assert.IsTrue(store.TryGet(element, "Text", out var saved));
        Assert.AreSame(replacement, saved);
        store.Remove(element, "Text");
        Assert.IsFalse(store.TryGet(element, "Text", out _));
        Assert.IsTrue(store.TryGet(element, "Tag", out var unchanged));
        Assert.AreSame(other, unchanged);
    }

    [TestMethod]
    public void ReadingDoesNotConsumeTheCapturedBinding()
    {
        var store = CreateStore();
        var element = Uninitialized<CollidingElement>();
        var binding = Uninitialized<Binding>();
        store.Set(element, "Text", binding);
        Assert.IsTrue(store.TryGet(element, "Text", out var first));
        Assert.IsTrue(store.TryGet(element, "Text", out var second));
        Assert.AreSame(binding, first);
        Assert.AreSame(binding, second);
    }
}
