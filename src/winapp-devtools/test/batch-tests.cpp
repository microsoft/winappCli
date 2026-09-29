// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tests for DevToolsBatch.h -- the pure request/reply shaping behind VisualTree.getPreviews and
// DevTools.releaseOwnedState. These bodies are otherwise string assembly inside HandleCommand, where the only way
// to find out they were wrong is to attach a client to a live app and read the wire.
//
// Build/run: scripts/test-native-units.ps1 (also invoked by src/winapp-devtools/build-devtools.ps1).

#include "DevToolsBatch.h"

#include <cstdio>

namespace {

int g_batchFailures = 0;

void Check(bool condition, const char* message)
{
    if (condition) std::printf("  ok    %s\n", message);
    else { ++g_batchFailures; std::printf("  FAIL  %s\n", message); }
}

void CheckJson(const std::wstring& got, const wchar_t* want, const char* message)
{
    if (got == want) { std::printf("  ok    %s\n", message); return; }
    ++g_batchFailures;
    std::printf("  FAIL  %s\n", message);
    std::printf("        want %ls\n", want);
    std::printf("        got  %ls\n", got.c_str());
}

void TestHandleListParsing()
{
    std::printf("A preview request's handle list is parsed strictly\n");
    std::vector<unsigned long long> wires;
    size_t requested = 0;
    bool truncated = false;

    Check(DevToolsBatch::ParsePreviewHandles(L"12,34,56", 512, wires, requested, truncated) &&
          wires.size() == 3 && wires[0] == 12 && wires[2] == 56 && requested == 3 && !truncated,
          "a plain comma-separated list parses in request order");

    Check(DevToolsBatch::ParsePreviewHandles(L"12,,34,", 512, wires, requested, truncated) &&
          wires.size() == 2 && requested == 2,
          "empty tokens from a trailing or doubled comma are skipped, not rejected");

    Check(!DevToolsBatch::ParsePreviewHandles(L"12,abc", 512, wires, requested, truncated),
          "a non-numeric token refuses the whole batch rather than silently answering about fewer nodes");
    Check(!DevToolsBatch::ParsePreviewHandles(L"12,0", 512, wires, requested, truncated),
          "handle 0 is the wire's 'nothing' sentinel and is not a node to describe");
    Check(!DevToolsBatch::ParsePreviewHandles(L"12,007", 512, wires, requested, truncated),
          "a non-canonical decimal is refused, matching every other handle-taking verb");
    Check(!DevToolsBatch::ParsePreviewHandles(L"", 512, wires, requested, truncated),
          "an empty list is a bad request, not an empty answer");
    Check(!DevToolsBatch::ParsePreviewHandles(L",,,", 512, wires, requested, truncated),
          "a list of nothing but separators is a bad request too");
}

void TestBatchCapIsReportedRatherThanHidden()
{
    std::printf("An over-cap batch is truncated AND says so\n");
    std::wstring list;
    for (int i = 1; i <= 6; ++i) {
        if (i > 1) list += L',';
        list += std::to_wstring(i);
    }

    std::vector<unsigned long long> wires;
    size_t requested = 0;
    bool truncated = false;
    Check(DevToolsBatch::ParsePreviewHandles(list, 4, wires, requested, truncated), "the batch still parses");
    Check(wires.size() == 4, "the tail beyond the cap is dropped");
    Check(requested == 6, "the reply echoes what was ASKED for, not what survived the cap");
    Check(truncated, "truncation is reported, so a partial answer never reads as a complete one");
}

void TestPreviewJsonOmitsSilentNodes()
{
    std::printf("A node with nothing identifying to say is absent, not empty\n");
    const std::vector<unsigned long long> wires{ 10, 11, 12 };
    const std::vector<std::wstring> captions{ L"Sign in", L"", L"U+E713" };

    CheckJson(
        DevToolsBatch::BuildPreviewJson(wires, captions, /*requested*/ 3, /*truncated*/ false),
        L"{\"previews\":[{\"handle\":\"10\",\"preview\":\"Sign in\"},"
        L"{\"handle\":\"12\",\"preview\":\"U+E713\"}],"
        L"\"requested\":3,\"returned\":2,\"truncated\":false}",
        "the silent node is omitted and `returned` counts what was actually said");

    CheckJson(
        DevToolsBatch::BuildPreviewJson({}, {}, /*requested*/ 0, /*truncated*/ false),
        L"{\"previews\":[],\"requested\":0,\"returned\":0,\"truncated\":false}",
        "an all-silent batch is a well-formed empty array, not a malformed one");
}

void TestPreviewJsonEscapesCaptions()
{
    std::printf("A caption is escaped, because element text is app-controlled\n");
    const std::vector<unsigned long long> wires{ 7 };
    const std::vector<std::wstring> captions{ L"say \"hi\"\\now" };

    const std::wstring json = DevToolsBatch::BuildPreviewJson(wires, captions, 1, false);
    Check(json.find(L"\\\"hi\\\"") != std::wstring::npos, "quotes in element text are escaped");
    Check(json.find(L"\\\\now") != std::wstring::npos, "backslashes in element text are escaped");
}

void TestPreviewJsonToleratesAShortCaptionColumn()
{
    std::printf("A caption column shorter than the handle list cannot read out of bounds\n");
    const std::vector<unsigned long long> wires{ 1, 2, 3 };
    const std::vector<std::wstring> captions{ L"only one" };

    CheckJson(
        DevToolsBatch::BuildPreviewJson(wires, captions, 3, false),
        L"{\"previews\":[{\"handle\":\"1\",\"preview\":\"only one\"}],"
        L"\"requested\":3,\"returned\":1,\"truncated\":false}",
        "the pairing stops at the shorter column instead of walking past it");
}

void TestReleaseJsonNamesBothLists()
{
    std::printf("A release reply names what it restored and what it left alone\n");
    DevToolsOwnedReleaseOutcome outcome;
    outcome.revision = 14;
    outcome.released.push_back(DevToolsOwnedRestore{ DevToolsStateAxis::Toolbar, 0 });
    outcome.released.push_back(DevToolsOwnedRestore{ DevToolsStateAxis::Highlight, 0 });
    outcome.retained.push_back(DevToolsStateAxis::PickArm);

    CheckJson(
        DevToolsBatch::BuildReleaseJson(outcome),
        L"{\"revision\":14,\"released\":[\"overlayToolbar\",\"selection\"],\"retained\":[\"pickArm\"]}",
        "released and retained both appear, using the wire axis names");

    DevToolsOwnedReleaseOutcome nothing;
    nothing.revision = 2;
    CheckJson(
        DevToolsBatch::BuildReleaseJson(nothing),
        L"{\"revision\":2,\"released\":[],\"retained\":[]}",
        "a client that owned nothing gets an honest empty release, never a global reset");
}

void TestReleaseJsonMatchesTheTracker()
{
    std::printf("The reply shape is fed by the real tracker, not by a hand-built outcome\n");
    DevToolsOwnedState state;
    state.RecordSet(DevToolsStateAxis::Toolbar, 7, /*prior*/ 0, /*now*/ 1);
    state.RecordSet(DevToolsStateAxis::PickArm, 9, /*prior*/ 0, /*now*/ 1);

    const DevToolsOwnedReleaseOutcome outcome = state.Release(7);
    CheckJson(
        DevToolsBatch::BuildReleaseJson(outcome),
        (L"{\"revision\":" + std::to_wstring(outcome.revision) +
         L",\"released\":[\"overlayToolbar\"],\"retained\":[\"pickArm\"]}").c_str(),
        "the axis another connection owns is retained, and the caller's own is released");
}

} // namespace

int RunBatchTests()
{
    std::printf("DevTools batch request/reply shaping tests (DevToolsBatch)\n");
    TestHandleListParsing();
    TestBatchCapIsReportedRatherThanHidden();
    TestPreviewJsonOmitsSilentNodes();
    TestPreviewJsonEscapesCaptions();
    TestPreviewJsonToleratesAShortCaptionColumn();
    TestReleaseJsonNamesBothLists();
    TestReleaseJsonMatchesTheTracker();
    return g_batchFailures;
}
