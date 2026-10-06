// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for DevToolsResourceOverrides.h -- the undo record behind Resource.set / Resource.reset. The contract a live
// replacement depends on: reset returns to the APP's value, never to an intermediate override, and a disconnecting
// client undoes only what it was the last to change.
//
// Build/run: scripts/test-native-units.ps1 (also invoked by src/winapp-devtools/build-devtools.ps1).

#include "DevToolsResourceOverrides.h"

#include <cstdio>
#include <string>

namespace {

int g_resourceOverrideFailures = 0;

void Check(bool condition, const char* message)
{
    if (condition) std::printf("  ok    %s\n", message);
    else { ++g_resourceOverrideFailures; std::printf("  FAIL  %s\n", message); }
}

void TestFirstOriginalWins()
{
    DevToolsResourceOverrides<std::wstring> store;
    Check(store.RecordSet(L"app", L"Accent", 0, L"#FF0000FF"), "first set keeps the original");
    Check(!store.RecordSet(L"app", L"Accent", 0, L"#FFFF0000"), "second set of the same key is not a new original");
    const auto* entry = store.Find(L"app", L"Accent");
    Check(entry && entry->original == L"#FF0000FF", "original is the app's value, not the intermediate override");
    Check(store.Count() == 1, "one entry per dictionary and key");
    Check(store.RecordSet(L"app/Dark", L"Accent", 0, L"#FF000080"), "same key in another dictionary is separate");
    Check(store.Find(L"app/Dark", L"Accent") && !store.Find(L"app", L"Other"), "Find matches dictionary and key");
}

void TestResetByKeyAndAll()
{
    DevToolsResourceOverrides<int> store;
    store.RecordSet(L"app", L"A", 0, 1);
    store.RecordSet(L"app/Dark", L"A", 0, 2);
    store.RecordSet(L"app", L"B", 0, 3);
    auto a = store.TakeKey(L"A");
    Check(a.size() == 2 && store.Count() == 1, "reset of a key restores it in every dictionary");
    Check(store.TakeKey(L"Missing").empty() && store.Count() == 1, "reset of an unchanged key restores nothing");
    auto all = store.TakeKey(L"");
    Check(all.size() == 1 && all[0].original == 3 && store.Count() == 0, "reset with no key restores everything");
}

void TestOwnerRelease()
{
    DevToolsResourceOverrides<int> store;
    store.RecordSet(L"app", L"A", 7, 1);
    store.RecordSet(L"app", L"B", 7, 2);
    store.RecordSet(L"app", L"B", 9, 0);  // client 9 now holds B
    store.RecordSet(L"app", L"C", 0, 3);  // one-shot CLI change
    Check(store.TakeOwned(0).empty(), "a one-shot owner never releases on its own");
    auto released = store.TakeOwned(7);
    Check(released.size() == 1 && released[0].key == L"A", "disconnect undoes only keys the client still holds");
    auto second = store.TakeOwned(9);
    Check(second.size() == 1 && second[0].original == 2, "the latest writer restores the first original");
    Check(store.Count() == 1 && store.Find(L"app", L"C"), "the one-shot change stays until reset");
}

void TestGlob()
{
    Check(DevToolsResourceKeyMatches(L"", L"Anything"), "empty pattern matches all");
    Check(DevToolsResourceKeyMatches(L"Accent*", L"AccentFillColorDefaultBrush"), "prefix glob");
    Check(DevToolsResourceKeyMatches(L"*fill*brush", L"AccentFillColorDefaultBrush"), "case-insensitive infix glob");
    Check(DevToolsResourceKeyMatches(L"Button?ackground", L"ButtonBackground"), "single-character wildcard");
    Check(!DevToolsResourceKeyMatches(L"Accent", L"AccentFill"), "no implicit trailing wildcard");
    Check(!DevToolsResourceKeyMatches(L"*Brush", L"BrushColor"), "suffix glob anchors at the end");
    Check(DevToolsResourceKeyMatches(L"*", L""), "star matches an empty key");
}

}  // namespace

int RunResourceOverrideTests()
{
    std::printf("resource overrides\n");
    TestFirstOriginalWins();
    TestResetByKeyAndAll();
    TestOwnerRelease();
    TestGlob();
    return g_resourceOverrideFailures;
}
