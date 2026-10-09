// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once
#include <string>
#include <utility>
#include <vector>
#include "DevToolsOverlay.h"

// What the inspector does when the selected element reports property changes: the decision only, so it can be
// tested without a XAML runtime. `fresh` holds a value read of just the properties that changed.
namespace DevToolsLiveRows {

enum class Action { None, Defer, Rebuild, UpdateRows };

struct Plan
{
    Action action = Action::None;
    std::vector<std::pair<size_t, size_t>> changed;   // (shown row, fresh row) pairs, for UpdateRows
};

inline bool SameValue(const DevToolsCardRow& a, const DevToolsCardRow& b)
{
    return a.value == b.value && a.valueState == b.valueState && a.source == b.source;
}

// `editing`: an editor has focus or a write is pending, so nothing is replaced yet. `expanded`: whether a row's
// details are open; those, and a property the pane does not list yet, are shown by rebuilding the pane, which keeps
// scroll, filter and expansion.
template <typename Expanded>
inline Plan Decide(const std::vector<DevToolsCardRow>& shown, const std::vector<DevToolsCardRow>& fresh, bool editing,
                   const Expanded& expanded)
{
    Plan plan;
    bool rebuild = false;
    for (size_t k = 0; k < fresh.size(); ++k) {
        size_t match = shown.size();
        for (size_t i = 0; i < shown.size(); ++i) if (shown[i].name == fresh[k].name) { match = i; break; }
        if (match == shown.size()) { rebuild = true; continue; }
        if (SameValue(shown[match], fresh[k])) continue;
        plan.changed.emplace_back(match, k);
        rebuild = rebuild || expanded(shown[match].name);
    }
    if (!rebuild && plan.changed.empty()) return plan;
    if (editing) { plan.action = Action::Defer; plan.changed.clear(); return plan; }
    plan.action = rebuild ? Action::Rebuild : Action::UpdateRows;
    return plan;
}

} // namespace DevToolsLiveRows
