// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.Reflection;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class BindingCloneControlTests
{
    // Property-bag seam for the copy operation: no WinUI activation or binding engine is used.
    public sealed class Configuration
    {
        public readonly List<string> SelectorWrites = [];
        private string? elementName;
        private object? relativeSource;
        private object? source;
        public object? Converter { get; set; }
        public object? ConverterParameter { get; set; }
        public object? ConverterLanguage { get; set; }
        public string? ElementName { get => elementName; set { Select(nameof(ElementName)); elementName = value; } }
        public object? FallbackValue { get; set; }
        public object? Mode { get; set; }
        public object? Path { get; set; }
        public object? RelativeSource { get => relativeSource; set { Select(nameof(RelativeSource)); relativeSource = value; } }
        public object? Source { get => source; set { Select(nameof(Source)); source = value; } }
        public object? TargetNullValue { get; set; }
        public object? UpdateSourceTrigger { get; set; }

        private void Select(string name)
        {
            Assert.IsEmpty(SelectorWrites, "Source selectors are mutually exclusive, even when assigned null.");
            SelectorWrites.Add(name);
        }
    }

    [TestMethod]
    [DataRow("Source")]
    [DataRow("RelativeSource")]
    [DataRow("ElementName")]
    [DataRow("DataContext")]
    public void QuickRestoreCopiesEveryBindingConfigurationPropertyOnEveryRestore(string selector)
    {
        var original = new Configuration();
        var fields = typeof(Binding).GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(p => p.CanRead && p.CanWrite).ToArray();
        CollectionAssert.AreEquivalent(typeof(Configuration).GetProperties().Select(p => p.Name).ToArray(),
            fields.Select(p => p.Name).ToArray(), "Every Binding configuration property needs a copy control.");
        foreach (var field in fields)
        {
            var property = typeof(Configuration).GetProperty(field.Name);
            Assert.IsNotNull(property, "Uncovered Binding property: " + field.Name);
            if (field.Name is "Source" or "RelativeSource" or "ElementName")
            {
                if (field.Name == selector) property.SetValue(original, field.Name == "ElementName" ? "OtherElement" : new object());
            }
            else property.SetValue(original, new object());
        }
        var captured = new Configuration();
        BindingDiagnosis.CopyBindingProperties(original, captured);
        for (int i = 0; i < 3; i++)
        {
            var restored = new Configuration();
            BindingDiagnosis.CopyBindingProperties(captured, restored);
            foreach (var field in fields)
            {
                var property = typeof(Configuration).GetProperty(field.Name)!;
                Assert.AreSame(property.GetValue(original), property.GetValue(restored), field.Name);
            }
            CollectionAssert.AreEqual(original.SelectorWrites, restored.SelectorWrites);
            restored.Converter = new object();
            Assert.AreSame(original.Converter, captured.Converter);
        }
    }
}
