// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Parses selector strings into structured expressions.
/// Supports semantic slugs (e.g., btn-ok-a1b2) and plain-text substring queries
/// matched against element Name and AutomationId.
/// </summary>
public interface IUiSelectorParser
{
    /// <summary>
    /// Parses a selector string into either a semantic slug selector or a plain text query.
    /// </summary>
    /// <param name="selector">Selector text to parse. Must not be <see langword="null"/>, empty, or whitespace.</param>
    /// <returns>The parsed selector expression.</returns>
    /// <exception cref="ArgumentException"><paramref name="selector"/> is <see langword="null"/>, empty, or whitespace.</exception>
    UiSelector Parse(string selector);
}
