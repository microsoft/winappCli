// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Runtime XAML is parsed by the target app; every overlay entry point is UI-thread only.

#pragma once

#include <unknwn.h>
#include <inspectable.h>
#include <xamlom.h>
#include <string>
#include <functional>
#include <vector>
#include <cstdlib>
#include <cwchar>
#include "DevToolsWriteGate.h"
#include "DevToolsSurface.h"
inline bool DevToolsIsBoolType(const std::wstring& writeOrValueType)
{
    if (writeOrValueType == L"Boolean") return true;
    return writeOrValueType.size() >= 8 &&
           writeOrValueType.compare(writeOrValueType.size() - 8, 8, L"Boolean") == 0;
}

inline bool DevToolsBoolValueIsOn(const std::wstring& value)
{
    return value == L"True" || value == L"true" || value == L"1";
}

inline const wchar_t* DevToolsBoolLiteral(bool on) { return on ? L"True" : L"False"; }

inline std::vector<std::wstring> DevToolsEnumMembersFor(const std::wstring& type)
{
    if (type == L"Visibility")          return { L"Visible", L"Collapsed" };
    if (type == L"HorizontalAlignment") return { L"Left", L"Center", L"Right", L"Stretch" };
    if (type == L"VerticalAlignment")   return { L"Top", L"Center", L"Bottom", L"Stretch" };
    if (type == L"Boolean")             return { L"False", L"True" };
    return {};
}

inline int DevToolsEnumIndexOf(const std::vector<std::wstring>& members, const std::wstring& value)
{
    if (!value.empty()) {
        wchar_t* end = nullptr;
        long n = wcstol(value.c_str(), &end, 10);
        if (end && *end == L'\0' && n >= 0 && n < (long)members.size()) return (int)n;
    }
    for (size_t i = 0; i < members.size(); ++i)
        if (_wcsicmp(members[i].c_str(), value.c_str()) == 0) return (int)i;
    return -1;
}

// Seed overlay handles before attach so DevToolsTap excludes our subtree from census/pick results.
typedef void (*DevToolsSeedFn)(InstanceHandle);

// Predicate used by picking and hit testing to skip DevTools-owned elements.
typedef bool (*DevToolsIsOverlayFn)(InstanceHandle);

void DevToolsOverlayLog(const wchar_t* fmt, ...);

// Build with the target app's XamlReader/Popup and attach on the app UI thread.
HRESULT DevToolsOverlay_BuildAndInsert(IXamlDiagnostics* diag, IVisualTreeService3* vts3,
                                  InstanceHandle appPanel, unsigned int index, DevToolsSeedFn seed,
                                  InstanceHandle rootHandle, DevToolsIsOverlayFn isOverlay,
                                  std::wstring* errorMessage = nullptr);

// Hit-test a screen point in the active XamlRoot, skip overlay hits, and return app-owned bounds in DIPs.
HRESULT DevToolsOverlay_Pick(IXamlDiagnostics* diag, InstanceHandle rootHandle, int screenX, int screenY,
                        DevToolsSeedFn seed, DevToolsIsOverlayFn isOverlay, InstanceHandle* outHandle, RECT* outBoundsDip);

// Pick-point classification reports whether coordinate conversion matched the active XamlRoot.
struct DevToolsSurfacePoint
{
    bool resolved = false;
    bool outside = false;
    long contentDipX = 0;
    long contentDipY = 0;
    long contentW = 0;
    long contentH = 0;
    std::wstring activeName;
    std::wstring pointName;
    std::wstring reason;
};
// UI-thread only: reads live XAML surface state through diagnostics.
void DevToolsOverlay_ClassifyPickPoint(IXamlDiagnostics* diag, InstanceHandle rootHandle,
                                  int screenX, int screenY, DevToolsSurfacePoint* out);

// The overlay owns its active anchored surface; the tap supplies candidate roots from its census.
using DevToolsSurfaceRootsFn = std::vector<InstanceHandle>(*)();
void DevToolsOverlay_SetSurfaceRootsReader(DevToolsSurfaceRootsFn fn);

// Refresh surfaces on the UI thread; anchor 0 keeps the current live surface when possible.
bool DevToolsOverlay_RefreshSurfaces(IXamlDiagnostics* diag, InstanceHandle anchor);

// Surface snapshots are read-only protocol state from the last refresh.
const DevToolsSurface& DevToolsOverlay_ActiveSurface();
const DevToolsSurface& DevToolsOverlay_ToolbarSurface();
const std::vector<DevToolsSurface>& DevToolsOverlay_AllSurfaces();

// Reparent across XamlRoots on the UI thread; selection bounds are root-local and must be dropped.
HRESULT DevToolsOverlay_MoveToPanel(IXamlDiagnostics* diag, InstanceHandle newPanel);

// Recovery searches stay within the canonical XamlRoot.
using DevToolsFindHostPanelFn = InstanceHandle(*)(unsigned long long xamlRootKey);
using DevToolsSelectionHostFn = HRESULT(*)(InstanceHandle anchor, bool selection);
void DevToolsOverlay_SetSelectionHostBridge(DevToolsSelectionHostFn fn);
HRESULT DevToolsOverlay_MoveSelectionToPanel(IXamlDiagnostics* diag, InstanceHandle panel, bool recovering = false);
HRESULT DevToolsOverlay_MoveHighlightToPanel(IXamlDiagnostics* diag, InstanceHandle panel);
HRESULT DevToolsOverlay_RecoverHost(IXamlDiagnostics* diag, DevToolsFindHostPanelFn findPanel,
                                  InstanceHandle* currentPanel);
bool DevToolsOverlay_IsToolbarRequested();

// Highlight a live app handle by probing the same hit-test path used by picking.
HRESULT DevToolsOverlay_HighlightHandle(IXamlDiagnostics* diag, InstanceHandle rootHandle, InstanceHandle target,
                                   DevToolsSeedFn seed, DevToolsIsOverlayFn isOverlay);

// Commit selection chrome on the UI thread; hover callers use highlight-only state.
HRESULT DevToolsOverlay_SelectHandle(IXamlDiagnostics* diag, InstanceHandle rootHandle, InstanceHandle target,
                                DevToolsSeedFn seed, DevToolsIsOverlayFn isOverlay);

// A true result means the detached DevTools window owns this pick completion.
typedef bool (*DevToolsPickedFn)(InstanceHandle);
void DevToolsOverlay_SetPickedSink(DevToolsPickedFn sink);

// Maps a raw hit to the element a pick selects. UI thread only.
typedef InstanceHandle (*DevToolsPickTargetFn)(InstanceHandle hit);
// True while picks select raw hits (Just my XAML off); hover re-picks when it changes.
typedef bool (*DevToolsPickRawFn)();
void DevToolsOverlay_SetPickTarget(DevToolsPickTargetFn target, DevToolsPickRawFn raw);

// Focus tracking has independent in-process and external owners.
typedef void (*DevToolsFocusedFn)(InstanceHandle);
void DevToolsOverlay_SetFocusedSink(DevToolsFocusedFn sink);
// Tracking stays active until both focus owners are disabled.
bool DevToolsOverlay_SetFocusTracking(bool enabled);
bool DevToolsOverlay_SetExternalFocusTracking(bool enabled);

// Payload-free state changes force clients to re-read getters and avoid stale old/new values.
typedef void (*DevToolsOverlayStateChangedFn)(void);
void DevToolsOverlay_SetStateChangedSink(DevToolsOverlayStateChangedFn sink);

// Query the shared sticky picker without arming it.
bool DevToolsOverlay_IsPickArmed();

// Toolbar visibility also requires a live root still hosting overlay content.
bool DevToolsOverlay_IsToolbarVisible();

// Show/hide only the rail; pick, highlight, comments, and layout state survive.
HRESULT DevToolsOverlay_SetToolbarVisible(bool visible);

// Returns the raw handle currently drawn, not merely the last requested target.
InstanceHandle DevToolsOverlay_GetHighlightHandle();

// Reads the layout-toggle latch without recomputing geometry.
bool DevToolsOverlay_IsLayoutAdornersOn();

// Captures the last layout rectangles actually drawn; callers must ignore rects when placed is 0.
struct DevToolsOverlayLayoutRects
{
    int on;
    int placed;
    unsigned long long handle;
    RECT box;
    RECT margin;
    RECT padding;
    int haveMargin;
    int havePadding;
    int marginBands;
    int padBands;
    wchar_t sizeText[32];
};
HRESULT DevToolsOverlay_GetLayoutRects(DevToolsOverlayLayoutRects* out);

// Uses the toolbar path, including persistence; UI-thread only.
HRESULT DevToolsOverlay_SetLayoutAdorners(bool on);

// Removed-handle batches are delivered later on the dispatcher so consumers avoid teardown reentry.
typedef void (*DevToolsNodeRemovedFn)(InstanceHandle);
bool DevToolsTap_SetNodeRemovedSink(DevToolsNodeRemovedFn fn);

// Diagnostic census count for DevTools-owned elements still excluded from app totals.
size_t DevToolsTap_ExcludedElementCount();

// Stable anchors use source-qualified authored ancestry, not transient handles or line numbers.
typedef bool (*DevToolsAnchorOfFn)(InstanceHandle h, std::wstring* outAnchor);
// Unresolved anchors mean “not back yet”; callers must not treat 0 as permanent deletion.
typedef InstanceHandle (*DevToolsResolveAnchorFn)(const wchar_t* anchor);
typedef unsigned long long (*DevToolsWireOfFn)(InstanceHandle raw);
void DevToolsOverlay_SetAnchorBridge(DevToolsAnchorOfFn anchorOf, DevToolsResolveAnchorFn resolve, DevToolsWireOfFn wireOf);

// The composer shells out to the CLI so the CLI remains the only comment-store writer.
void DevToolsOverlay_SetCliExe(const wchar_t* path);
void DevToolsOverlay_SetGuestComments(const std::wstring& start, const std::wstring& binding, const std::wstring& epoch);
std::wstring DevToolsOverlay_GuestCommentsJson();
std::wstring DevToolsOverlay_GuestCommentToken(const std::wstring& operation, const std::wstring& revision);
bool DevToolsOverlay_ResolveGuestComment(const std::wstring& id, const std::wstring& revision,
    std::function<void(int)> completed);

// Persisted comments are keyed by stable id and optional rebuild-stable anchor.
struct DevToolsOverlayComment
{
    const wchar_t* id;
    const wchar_t* text;
    const wchar_t* anchor;
    const wchar_t* revision = nullptr;
};

// Replace pushed comments. An unversioned push keeps markers authored in this session by a racing add; an
// authoritative (store-generation-ordered) push owns the whole set, and local writer completions defer to it.
HRESULT DevToolsOverlay_SetComments(IXamlDiagnostics* diag, InstanceHandle root,
                               const DevToolsOverlayComment* items, size_t count, size_t* outPlaced, bool authoritative);


// False is logged locally; this callback never redirects to another host.
typedef bool (*DevToolsInprocInspectFn)();
void DevToolsOverlay_SetInprocInspect(DevToolsInprocInspectFn fn);


// Precedence entries mirror DevToolsReadChainEntry without pulling wire-only fields into overlay ABI.
struct DevToolsCardChainEntry
{
    std::wstring source;
    std::wstring value;
    std::wstring file;
    unsigned int line = 0;
    bool winner = false;
};

// Card rows carry shared display/write metadata; empty write type means read-only.
struct DevToolsCardRow
{
    std::wstring name;
    std::wstring type;
    std::wstring value;
    std::wstring source;
    std::wstring valueType;
    std::wstring binding;
    std::wstring authored;
    std::wstring authoredKind;
    std::wstring authoredKey;
    std::vector<std::wstring> fields;
    std::wstring valueState;
    std::wstring editKind;
    std::vector<DevToolsCardChainEntry> chain;
    std::vector<DevToolsCardRow> children;
};

// Reads a live target IInspectable owned by the caller.
typedef bool (*DevToolsCardReadFn)(IInspectable* target, std::wstring* outType, std::wstring* outName,
                              std::vector<DevToolsCardRow>* outRows, std::wstring* outAuthoredState,
                              std::wstring* outAuthoredXaml);
// Reads TextBox text through diagnostics so the overlay needs no TextBox ABI.
typedef bool (*DevToolsCardReadInputFn)(IInspectable* input, std::wstring* outText);
// Write outcomes distinguish failure, success, and binding-replacement confirmation.
typedef DevToolsWriteOutcome (*DevToolsCardWriteFn)(IInspectable* target, const wchar_t* prop, const wchar_t* type,
                                          const wchar_t* value, bool confirmBindingReplace);
void DevToolsOverlay_SetCardBridge(DevToolsCardReadFn read, DevToolsCardReadInputFn readInput, DevToolsCardWriteFn write);
// Bracket card insertion so card elements self-exclude by live handle.
typedef void (*DevToolsCardScopeFn)(int on);
void DevToolsOverlay_SetCardScope(DevToolsCardScopeFn scope);
// Freeze the census while async TextBox template parts appear and disappear.
typedef void (*DevToolsCardFreezeFn)(int on);
void DevToolsOverlay_SetCardFreeze(DevToolsCardFreezeFn freeze);

// Reads the selected raw handle’s source URI/position when instrumentation exists.
typedef bool (*DevToolsSourceReadFn)(InstanceHandle raw, std::wstring* outFile, unsigned int* outLine, unsigned int* outCol);
void DevToolsOverlay_SetSourceReader(DevToolsSourceReadFn read);

// Invoke from a worker: the managed relay needs the UI thread and would deadlock if called there.
typedef bool (*DevToolsBindingAskFn)(unsigned long long rawHandle, const wchar_t* op, const wchar_t* prop,
                                std::wstring* out);
void DevToolsOverlay_SetBindingAsk(DevToolsBindingAskFn ask);

// Invoke from a worker because the implementation marshals to the app UI thread.
typedef bool (*DevToolsPathWalkAskFn)(unsigned long long rawHandle, const wchar_t* prop, std::wstring* out);
void DevToolsOverlay_SetPathWalkAsk(DevToolsPathWalkAskFn ask);

// `fromUserPick` is call-local state; protocol/window selections must not open the quick-edit panel.
void DevToolsOverlay_ShowBadgeForHandle(IXamlDiagnostics* diag, InstanceHandle target, bool fromUserPick = false);

// Arms the shared full-canvas picker on the UI thread; false means no catcher can deliver a pick.
bool DevToolsOverlay_ArmPick();

// Explicit cancel tears down the catcher and clears selection; idempotent on the UI thread.
void DevToolsOverlay_DisarmPick();

// Window-owned one-shot completion removes the catcher after PointerReleased without deselecting.
void DevToolsOverlay_CompleteOneShotPick();

// Pick-mode notifications come from the same UI-thread point that updates the overlay button.
using DevToolsPickModeFn = void (*)(bool armed);
void DevToolsOverlay_SetPickModeSink(DevToolsPickModeFn fn);
