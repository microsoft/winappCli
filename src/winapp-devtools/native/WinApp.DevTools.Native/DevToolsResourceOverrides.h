// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <cwctype>
#include <string>
#include <utility>
#include <vector>

// Case-insensitive glob over resource keys: `*` matches any run, `?` one character. An empty pattern matches all.
inline bool DevToolsResourceKeyMatches(const std::wstring& pattern, const std::wstring& key)
{
    if (pattern.empty()) return true;
    size_t p = 0, k = 0, star = std::wstring::npos, mark = 0;
    while (k < key.size()) {
        if (p < pattern.size() && (pattern[p] == L'?' ||
            std::towlower(pattern[p]) == std::towlower(key[k]))) { ++p; ++k; continue; }
        if (p < pattern.size() && pattern[p] == L'*') { star = p++; mark = k; continue; }
        if (star == std::wstring::npos) return false;
        p = star + 1;
        k = ++mark;
    }
    while (p < pattern.size() && pattern[p] == L'*') ++p;
    return p == pattern.size();
}

// The undo record for live resource replacements. The first replacement of a key in a dictionary keeps the app's
// original value; later replacements only move ownership to the latest writer, so reset always returns to what
// the app had. Owner 0 is a one-shot client: only an explicit reset undoes its change. UI-thread only.
template <typename Original>
class DevToolsResourceOverrides
{
public:
    struct Entry
    {
        std::wstring       scope;  // the owning dictionary's identity
        std::wstring       key;
        unsigned long long owner = 0;
        Original           original{};
    };

    // Returns true when this is the first replacement, i.e. `original` was kept.
    bool RecordSet(const std::wstring& scope, const std::wstring& key, unsigned long long owner, Original original)
    {
        for (Entry& entry : _entries) {
            if (entry.scope == scope && entry.key == key) { entry.owner = owner; return false; }
        }
        _entries.push_back(Entry{ scope, key, owner, std::move(original) });
        return true;
    }

    const Entry* Find(const std::wstring& scope, const std::wstring& key) const
    {
        for (const Entry& entry : _entries) if (entry.scope == scope && entry.key == key) return &entry;
        return nullptr;
    }

    // Removes and returns every entry for `key`, or every entry when `key` is empty.
    std::vector<Entry> TakeKey(const std::wstring& key)
    {
        return TakeIf([&](const Entry& entry) { return key.empty() || entry.key == key; });
    }

    // Removes and returns what a disconnecting session established. A one-shot owner (0) never releases.
    std::vector<Entry> TakeOwned(unsigned long long owner)
    {
        if (owner == 0) return {};
        return TakeIf([&](const Entry& entry) { return entry.owner == owner; });
    }

    size_t Count() const { return _entries.size(); }

private:
    template <typename Predicate>
    std::vector<Entry> TakeIf(Predicate predicate)
    {
        std::vector<Entry> taken, kept;
        for (Entry& entry : _entries) (predicate(entry) ? taken : kept).push_back(std::move(entry));
        _entries = std::move(kept);
        return taken;
    }

    std::vector<Entry> _entries;
};
