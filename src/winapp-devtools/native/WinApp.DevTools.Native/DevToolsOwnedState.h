// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <cstddef>
#include <cstdint>
#include <mutex>
#include <string>
#include <vector>

// The axes a protocol client can establish. Values are dense and stable so they can index a fixed array.
enum class DevToolsStateAxis : int
{
    Toolbar = 0,        // DevToolsOverlay_SetToolbarVisible: 0/1
    Highlight = 1,      // the selected/highlighted element, as a WIRE handle (0 = nothing)
    LayoutAdorners = 2, // DevToolsOverlay_SetLayoutAdorners: 0/1
    PickArm = 3,        // DevToolsOverlay_ArmPick / DisarmPick: 0/1
    Window = 4,         // the in-process inspector window: 0/1
    Count = 5,
};

inline const wchar_t* DevToolsStateAxisName(DevToolsStateAxis axis)
{
    switch (axis) {
        case DevToolsStateAxis::Toolbar:        return L"overlayToolbar";
        case DevToolsStateAxis::Highlight:      return L"selection";
        case DevToolsStateAxis::LayoutAdorners: return L"layoutAdorners";
        case DevToolsStateAxis::PickArm:        return L"pickArm";
        case DevToolsStateAxis::Window:         return L"inspectorWindow";
        default:                           return L"unknown";
    }
}

struct DevToolsOwnedRestore
{
    DevToolsStateAxis       axis = DevToolsStateAxis::Toolbar;
    unsigned long long restoreTo = 0;
    unsigned long long expectedValue = 0;
};

struct DevToolsOwnedReleaseOutcome
{
    std::vector<DevToolsOwnedRestore> released;
    std::vector<DevToolsStateAxis>    retained;
    unsigned long long           revision = 0;
};

// Thread-safe because UI-thread mutations and pipe-thread getState/disconnect paths all touch ownership.
class DevToolsOwnedState
{
public:
    // Record before publishing a UI change; a no-op write must not steal an axis from its current owner.
    void RecordSet(DevToolsStateAxis axis, unsigned long long owner,
                   unsigned long long priorValue, unsigned long long newValue)
    {
        const size_t i = Index(axis);
        if (i >= kCount || owner == 0) return;
        std::lock_guard<std::mutex> guard(_gate);
        if (priorValue == newValue) {
            if (_axes[i].owner == owner) _axes[i].ownedValue = newValue;
            return;
        }
        if (_axes[i].owner != owner) {
            _axes[i].owner = owner;
            _axes[i].priorValue = priorValue;
        }
        if (newValue == _axes[i].priorValue) {
            _axes[i].owner = 0;
            _axes[i].priorValue = 0;
            _axes[i].ownedValue = newValue;
            ++_revision;
            return;
        }
        _axes[i].ownedValue = newValue;
        ++_revision;
    }

    // Reconcile only values observed under the same UI-thread serialization as mutations.
    bool Reconcile(DevToolsStateAxis axis, unsigned long long currentValue)
    {
        const size_t i = Index(axis);
        if (i >= kCount) return false;
        std::lock_guard<std::mutex> guard(_gate);
        if (_axes[i].owner == 0 || _axes[i].ownedValue == currentValue) return false;
        _axes[i].owner = 0;
        _axes[i].priorValue = 0;
        _axes[i].ownedValue = currentValue;
        ++_revision;
        return true;
    }

    // Release restores only axes still owned by this connection; newer writers are reported as retained.
    DevToolsOwnedReleaseOutcome Release(unsigned long long owner)
    {
        DevToolsOwnedReleaseOutcome outcome;
        std::lock_guard<std::mutex> guard(_gate);
        for (size_t i = 0; i < kCount; ++i) {
            const DevToolsStateAxis axis = static_cast<DevToolsStateAxis>(i);
            if (_axes[i].owner == owner && owner != 0) {
                outcome.released.push_back(DevToolsOwnedRestore{ axis, _axes[i].priorValue, _axes[i].ownedValue });
                _axes[i].owner = 0;
                _axes[i].ownedValue = _axes[i].priorValue;
                _axes[i].priorValue = 0;
            }
            else if (_axes[i].owner != 0) {
                outcome.retained.push_back(axis);
            }
        }
        if (!outcome.released.empty()) ++_revision;
        outcome.revision = _revision;
        return outcome;
    }

    unsigned long long Owner(DevToolsStateAxis axis) const
    {
        const size_t i = Index(axis);
        if (i >= kCount) return 0;
        std::lock_guard<std::mutex> guard(_gate);
        return _axes[i].owner;
    }

    unsigned long long Revision() const
    {
        std::lock_guard<std::mutex> guard(_gate);
        return _revision;
    }

    std::wstring OwnerToken(DevToolsStateAxis axis) const
    {
        const unsigned long long owner = Owner(axis);
        return owner == 0 ? std::wstring() : std::to_wstring(owner);
    }

private:
    static constexpr size_t kCount = static_cast<size_t>(DevToolsStateAxis::Count);

    static size_t Index(DevToolsStateAxis axis)
    {
        const int value = static_cast<int>(axis);
        return (value < 0 || value >= static_cast<int>(kCount)) ? kCount : static_cast<size_t>(value);
    }

    struct AxisRecord
    {
        unsigned long long owner = 0;       // connection id, 0 = the app itself / nobody
        unsigned long long priorValue = 0;  // value in effect before `owner` took the axis
        unsigned long long ownedValue = 0;  // value `owner` established; a mismatch means someone else wrote
    };

    mutable std::mutex _gate;
    AxisRecord         _axes[kCount]{};
    unsigned long long _revision = 0;
};
