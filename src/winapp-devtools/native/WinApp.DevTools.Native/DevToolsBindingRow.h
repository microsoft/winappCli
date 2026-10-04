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

// Properties changed with a live edit, per element wire. An {x:Bind} keeps reporting its source value after one,
// so the edit is the evidence that what the app shows is no longer the binding's value. UI thread only.
void DevToolsBindingRow_NoteLiveWrite(unsigned long long wire, const std::wstring& prop);
void DevToolsBindingRow_ForgetLiveWrite(unsigned long long wire, const std::wstring& prop);
bool DevToolsBindingRow_WasWrittenLive(unsigned long long wire, const std::wstring& prop);

// Turns a working summary into "Overridden by a live edit" when a live edit put a different value on screen than
// the binding resolves to. Returns whether it did.
bool DevToolsBindingRow_ApplyLiveOverride(DevToolsBindingSummary& summary, bool writtenLive, const std::wstring& shown);

// The status as a screen reader should say it: "\u2713 Works" -> "Works".
std::wstring DevToolsBindingRow_SpokenStatus(const std::wstring& status);
