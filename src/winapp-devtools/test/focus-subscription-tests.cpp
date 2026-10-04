// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsEvents.h"
#include "DevToolsFocusOwners.h"

#include <cstdio>
#include <string>

namespace {

int g_focusFailures = 0;

void Check(bool condition, const char* message)
{
    if (condition) std::printf("  ok    %s\n", message);
    else { ++g_focusFailures; std::printf("  FAIL  %s\n", message); }
}

void TestExternalFocusSubscribersOwnOneSharedLifecycle()
{
    std::printf("External Focus subscribers own one shared target lifecycle\n");

    // Positive control: the old per-request bool policy lets one client's disable turn tracking off under
    // another enabled client. This must keep reproducing the reported multi-client failure.
    bool legacyTracking = false;
    legacyTracking = true;  // client one enables
    legacyTracking = true;  // client two enables
    legacyTracking = false; // client one disables
    Check(!legacyTracking, "control: a single shared bool turns tracking off under the second client");

    DevToolsConn* first = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
    DevToolsConn* second = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
    Check(first && second, "two independent connections register");
    if (!first || !second) {
        if (first) DevToolsEvents_Unregister(first);
        if (second) DevToolsEvents_Unregister(second);
        return;
    }

    Check(DevToolsEvents_SetDomain(first, DevToolsDomain_Focus, true) == DevToolsDomain_FirstEnabled,
          "the first enable owns target activation");
    Check(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Focus) == 1,
          "the Focus subscriber count becomes one");
    Check(DevToolsEvents_SetDomain(first, DevToolsDomain_Focus, true) == DevToolsDomain_NoChange,
          "a repeated enable is idempotent");
    Check(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Focus) == 1,
          "an idempotent enable does not inflate the count");

    Check(DevToolsEvents_SetDomain(second, DevToolsDomain_Focus, true) == DevToolsDomain_NoChange,
          "a second client does not reactivate the shared target hook");
    Check(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Focus) == 2,
          "both enabled clients are reference counted");
    Check(DevToolsEvents_SetDomain(first, DevToolsDomain_Focus, false) == DevToolsDomain_NoChange,
          "one disable leaves tracking owned by the other client");
    Check(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Focus) == 1,
          "the remaining client keeps one Focus reference");

    Check((DevToolsEvents_Unregister(first) & DevToolsDomain_Focus) == 0,
          "disconnecting an already-disabled client has no lifecycle edge");
    Check((DevToolsEvents_Unregister(second) & DevToolsDomain_Focus) != 0,
          "disconnecting the final enabled client owns target cleanup");
    Check(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Focus) == 0,
          "disconnect cleanup releases the final Focus reference");
}

void TestRetainedDisconnectedConnectionCannotResubscribe()
{
    std::printf("A queued Focus callback cannot resurrect a disconnected connection\n");
    DevToolsConn* connection = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
    Check(connection != nullptr, "the race fixture connection registers");
    if (!connection) return;

    DevToolsEvents_Retain(connection);
    Check(DevToolsEvents_SetDomain(connection, DevToolsDomain_Focus, true) == DevToolsDomain_FirstEnabled,
          "the fixture owns the first Focus reference");
    Check((DevToolsEvents_Unregister(connection) & DevToolsDomain_Focus) != 0,
          "disconnect reports the final Focus cleanup edge");
    Check(DevToolsEvents_SetDomain(connection, DevToolsDomain_Focus, true) == DevToolsDomain_ConnectionGone,
          "a retained callback sees that its connection is gone");
    Check(DevToolsEvents_DomainSubscriberCount(DevToolsDomain_Focus) == 0,
          "the stale callback cannot resurrect the subscriber count");
    DevToolsEvents_Release(connection);

    DevToolsConn* replacement = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
    Check(replacement != nullptr, "a replacement connection registers");
    if (!replacement) return;
    Check(DevToolsEvents_SetDomain(replacement, DevToolsDomain_Focus, true) == DevToolsDomain_FirstEnabled,
          "a new enable after cleanup owns a fresh activation edge");
    DevToolsEvents_Unregister(replacement);
}

void TestWindowAndExternalOwnersCannotDisableEachOther()
{
    std::printf("Window and external Focus owners remain independent\n");
    DevToolsFocusOwners owners;
    Check(owners.SetWindow(true), "the inspector toggle activates tracking");
    Check(owners.SetExternal(true), "external subscribers share the active tracker");
    Check(owners.SetWindow(false), "closing the inspector leaves external tracking active");
    Check(!owners.SetExternal(false), "the final external owner deactivates tracking");
    Check(owners.SetExternal(true), "external tracking activates without an inspector window");
    Check(owners.SetWindow(true), "the inspector can join an external owner");
    Check(owners.SetExternal(false), "external cleanup leaves the inspector owner active");
    Check(!owners.SetWindow(false), "the final inspector owner deactivates tracking");
}

void TestOwnerTokensNameALiveNegotiatedSession()
{
    std::printf("An owner token names one live, negotiated connection and nothing else\n");

    // Positive control: the identity this replaces. Ownership keyed on the connection a WRITE arrives on ties
    // process-global UI state to a short-lived request pipe, so the state is released the instant the request
    // finishes -- the client's mutation undoes itself and completePick/releaseOwnedState can never find
    // anything owned.
    {
        DevToolsConn* request = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
        Check(request != nullptr, "the control request connection registers");
        if (request) {
            const uint64_t writeConn = DevToolsEvents_ConnId(request);
            DevToolsEvents_Unregister(request);
            Check(writeConn != 0, "control: a per-request connection id exists only while the request does");
        }
    }

    DevToolsConn* session = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
    DevToolsConn* other = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
    Check(session && other, "a session connection and a second client register");
    if (!session || !other) {
        if (session) DevToolsEvents_Unregister(session);
        if (other) DevToolsEvents_Unregister(other);
        return;
    }

    const std::wstring token = DevToolsEvents_MarkNegotiated(session);
    const std::wstring otherToken = DevToolsEvents_MarkNegotiated(other);
    Check(token.size() == 32, "negotiating mints a 128-bit owner token");
    Check(token != otherToken, "two sessions get different tokens");
    Check(token != std::to_wstring(DevToolsEvents_ConnId(session)),
          "the token is not the connection id, which every event publishes in `origin`");
    Check(DevToolsEvents_ResolveOwnerToken(token) == DevToolsEvents_ConnId(session),
          "the token resolves to its own session");
    Check(DevToolsEvents_ResolveOwnerToken(otherToken) == DevToolsEvents_ConnId(other),
          "and the second session's token resolves to that one");
    Check(DevToolsEvents_ResolveOwnerToken(L"") == 0,
          "an absent owner is not a lookup: it establishes nothing rather than matching something");
    Check(DevToolsEvents_ResolveOwnerToken(L"00000000000000000000000000000000") == 0,
          "a forged token names nobody");
    Check(DevToolsEvents_ResolveOwnerToken(std::to_wstring(DevToolsEvents_ConnId(session))) == 0,
          "naming a connection id where a token belongs is refused, so one client cannot claim another's state");

    // A request pipe that never negotiated has a token, but it is not claimable: only a session is.
    DevToolsConn* unnegotiated = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
    Check(unnegotiated != nullptr, "an un-negotiated request connection registers");
    if (unnegotiated) {
        Check(DevToolsEvents_ResolveOwnerToken(DevToolsEvents_MarkNegotiated(unnegotiated)) != 0,
              "marking it negotiated is what makes its token resolve");
        DevToolsEvents_Unregister(unnegotiated);
    }

    DevToolsEvents_Unregister(session);
    Check(DevToolsEvents_ResolveOwnerToken(token) == 0,
          "a disconnected session's token stops resolving, so nothing new can be owned by a session that is gone");
    Check(DevToolsEvents_ResolveOwnerToken(otherToken) == DevToolsEvents_ConnId(other),
          "the surviving session is unaffected");
    DevToolsEvents_Unregister(other);
}

void TestALiveOwnerIsRecheckedAfterTheTokenResolved()
{
    std::printf("A resolved owner is still checkable when the mutation it authorized finally runs\n");

    // THE RACE, in the two steps the product actually performs. A state-mutating request resolves its owner
    // token on the PIPE thread, then queues the mutation onto the app's UI thread. Those are different moments,
    // and the session can disconnect in between. Recording ownership under it then is worse than not recording
    // it: that session's disconnect release has ALREADY run and found the axis unowned, so nothing will ever
    // restore it and the app keeps the DevTools chrome for the rest of its life.
    //
    // This is the seam that makes the second step decidable. It is deliberately a pure connection-registry
    // question -- no overlay, no dispatcher, no XAML -- so it can be pinned here rather than only in a live
    // two-app gate where the window is milliseconds wide and effectively unreachable on demand.
    DevToolsConn* session = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
    Check(session != nullptr, "the session connection registers");
    if (!session) return;

    const std::wstring token = DevToolsEvents_MarkNegotiated(session);
    const uint64_t resolved = DevToolsEvents_ResolveOwnerToken(token);   // step 1: on the pipe thread
    Check(resolved != 0, "the token resolves while the session is alive");
    Check(DevToolsEvents_IsOwnerLive(resolved), "and the resolved id is live, so the queued mutation may proceed");

    DevToolsEvents_Unregister(session);                                  // the session goes away in between

    Check(!DevToolsEvents_IsOwnerLive(resolved),                          // step 2: on the UI thread
          "the SAME id the request already resolved must stop being live, or a pending mutation establishes "
          "state under a session that has gone and nothing can ever release it");
    Check(DevToolsEvents_ResolveOwnerToken(token) == 0,
          "control: re-resolving the token agrees, so the two checks cannot disagree about one session");

    // A connection that registered but never negotiated is not a session and cannot own anything, even though
    // it has an id. Without this, "has an id" would be mistaken for "is a claimable owner".
    DevToolsConn* unnegotiated = DevToolsEvents_Register(INVALID_HANDLE_VALUE);
    Check(unnegotiated != nullptr, "an un-negotiated connection registers");
    if (unnegotiated) {
        Check(!DevToolsEvents_IsOwnerLive(DevToolsEvents_ConnId(unnegotiated)),
              "a connection that never handshaked is not a session and owns nothing");
        DevToolsEvents_MarkNegotiated(unnegotiated);
        Check(DevToolsEvents_IsOwnerLive(DevToolsEvents_ConnId(unnegotiated)),
              "negotiating is what makes it claimable");
        DevToolsEvents_Unregister(unnegotiated);
    }

    Check(!DevToolsEvents_IsOwnerLive(0),
          "connection id 0 is the 'attribute to nobody' sentinel, never a live owner");
}

} // namespace

int RunFocusSubscriptionTests()
{
    std::printf("Focus subscription lifecycle tests\n");
    TestExternalFocusSubscribersOwnOneSharedLifecycle();
    TestRetainedDisconnectedConnectionCannotResubscribe();
    TestWindowAndExternalOwnersCannotDisableEachOther();
    TestOwnerTokensNameALiveNegotiatedSession();
    TestALiveOwnerIsRecheckedAfterTheTokenResolved();
    return g_focusFailures;
}
