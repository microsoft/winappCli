// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

internal sealed class UiSelectorParser : IUiSelectorParser
{
    public UiSelector Parse(string selector)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(selector);

        // Semantic slug format: btn-minimize-c4b9 (lowercase, dashes, ends with 4-char hex)
        var slugParsed = SlugGenerator.ParseSlug(selector);
        if (slugParsed is not null)
        {
            return new UiSelector { Slug = selector };
        }

        // Everything else is a plain text search query
        return new UiSelector { Query = selector };
    }
}
