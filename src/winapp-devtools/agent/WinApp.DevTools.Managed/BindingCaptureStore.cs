// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace WinApp.DevTools.Managed;

internal sealed class BindingCaptureStore
{
    private readonly Func<FrameworkElement, Dictionary<string, BindingBase?>?> read;
    private readonly Action<FrameworkElement, Dictionary<string, BindingBase?>?> write;

    internal BindingCaptureStore()
        : this(element => (Dictionary<string, BindingBase?>?)element.GetValue(AttachedProperty.Value),
            (element, value) =>
            {
                if (value is null) element.ClearValue(AttachedProperty.Value);
                else element.SetValue(AttachedProperty.Value, value);
            })
    {
    }

    internal BindingCaptureStore(
        Func<FrameworkElement, Dictionary<string, BindingBase?>?> read,
        Action<FrameworkElement, Dictionary<string, BindingBase?>?> write)
    {
        this.read = read;
        this.write = write;
    }

    private static class AttachedProperty
    {
        internal static readonly DependencyProperty Value = DependencyProperty.RegisterAttached(
            "WinAppDevToolsCapturedBindings", typeof(object), typeof(FrameworkElement), new PropertyMetadata(null));
    }

    internal void Set(FrameworkElement element, string property, BindingBase? binding)
    {
        var properties = read(element);
        if (properties is null)
        {
            properties = new(StringComparer.Ordinal);
            write(element, properties);
        }
        properties[property] = binding;
    }

    internal bool TryGet(FrameworkElement element, string property, out BindingBase? binding)
    {
        binding = null;
        return read(element)?.TryGetValue(property, out binding) == true;
    }

// The original a DevTools edit replaced, held until Binding.restore puts it back, so a later capture of the
    // edited state cannot overwrite it. A null binding means the original was an {x:Bind}, restored by rebinding.
    private static string HeldKey(string property) => "\u0001held:" + property;

    internal void Hold(FrameworkElement element, string property, BindingBase? original) => Set(element, HeldKey(property), original);

    internal bool TryGetHeld(FrameworkElement element, string property, out BindingBase? original)
        => TryGet(element, HeldKey(property), out original);

    internal void Release(FrameworkElement element, string property) => Remove(element, HeldKey(property));

    internal void Remove(FrameworkElement element, string property)
    {
        if (read(element) is { } properties)
        {
            properties.Remove(property);
            if (properties.Count == 0)
                write(element, null);
        }
    }
}
