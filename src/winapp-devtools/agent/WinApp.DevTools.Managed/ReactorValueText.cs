// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Collections;
using System.Reflection;

namespace WinApp.DevTools.Managed;

/// <summary>
/// How DevTools reads a Reactor value as text without running more app code than it must: a <c>ToString</c> that
/// throws is reported, and a collection is counted, never enumerated.
/// </summary>
internal static class ReactorValueText
{
    /// <summary>
    /// A value's <c>ToString</c>, contained: when it throws, <c>TaskItem (ToString() threw InvalidOperationException)</c>.
    /// </summary>
    internal static string SafeText(object value, Func<object, string?> text)
    {
        try { return text(value) ?? ""; }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var error = ex is TargetInvocationException { InnerException: { } inner } ? inner : ex;
            return $"{ReactorComponents.FriendlyTypeName(value.GetType())} (ToString() threw {error.GetType().Name})";
        }
    }

    /// <summary>
    /// How many items a collection holds, without enumerating it (which could run app code or never end):
    /// <c>ICollection.Count</c> or a public <c>int Count</c> with a public getter; null when neither answers.
    /// </summary>
    internal static int? Count(object value)
    {
        try
        {
            if (value is ICollection collection)
            {
                return collection.Count;
            }
            var getter = value.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance, null, typeof(int), Type.EmptyTypes, null)
                ?.GetGetMethod(nonPublic: false);
            return getter?.Invoke(value, null) as int?;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }
}
