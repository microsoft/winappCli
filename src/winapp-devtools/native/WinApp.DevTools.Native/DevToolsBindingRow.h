// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>

struct DevToolsBindingRowView {
    bool show = false;

    bool isFault = false;

    std::wstring head;
    std::wstring headEmphasis;
    std::wstring headTail;

    std::wstring subtitle;

    bool unavailable = false;
    bool suppressNativeWalk = false;
};

DevToolsBindingRowView DevToolsBindingRow_FromJson(const std::wstring& json);

const wchar_t* DevToolsBindingRow_PendingText();

enum class DevToolsBindingTone { Unknown, Works, Broken, Warning, NotBound };

// A diagnosis reduced to a few labelled lines. The engine's caveats stay in the JSON.
struct DevToolsBindingSummary {
    DevToolsBindingTone tone = DevToolsBindingTone::Unknown;
    std::wstring status;   // "\u2713 Works", "\u2715 Broken at Titel", ...
    std::wstring reason;   // a few words, empty when the status says it all
    std::wstring source;   // the DataContext type or the x:Bind owner
    std::wstring pathMode; // "Greeting \u00b7 OneWay"
    std::wstring value;    // the resolved value
    std::wstring kind;     // "{x:Bind}" or "{Binding}"
};

DevToolsBindingSummary DevToolsBindingRow_Summary(const std::wstring& json);
