// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <unknwn.h>
#include <inspectable.h>
#include <xamlom.h>
#include <string>
#include <vector>
#include <utility>
#include <functional>
#include "DevToolsOverlay.h"
#include "DevToolsRead.h"
#include "DevToolsWriteGate.h"

// Node handles are opaque generation-stamped wires; callbacks must re-resolve them before live reads.
struct DevToolsWindowNode
{
    InstanceHandle handle = 0;
    std::wstring   name;
    std::wstring   type;
    int            depth = 0;
    std::wstring   preview;
    std::wstring   searchText;
    bool           previewRead = false;
    std::wstring   sourceUri;
};

using DevToolsWindowRowsFn      = std::function<void(InstanceHandle wire, std::wstring& outType,
                                                std::wstring& outName, std::vector<DevToolsCardRow>& outRows,
                                                std::wstring& outAuthoredState, std::wstring& outAuthoredXaml)>;
using DevToolsWindowReadInputFn = std::function<bool(IInspectable* box, std::wstring& outText)>;
// Write outcome distinguishes failure, binding-replace refusal, and confirmed data loss.
using DevToolsWindowWriteFn     = std::function<DevToolsWriteOutcome(InstanceHandle wire, const std::wstring& prop,
                                                           const std::wstring& type, const std::wstring& value,
                                                           bool confirmBindingReplace)>;
// clearFn - reset one curated property to its inherited/default value on the node (IVisualTreeService::
// ClearProperty). Returns false on a stale handle or if the property index cannot be resolved.
using DevToolsWindowClearFn     = std::function<bool(InstanceHandle wire, const std::wstring& prop)>;

// ResolveResource assigns a named resource value; it cannot recover or rebind the original resource identity.
using DevToolsWindowResolveFn   = std::function<bool(InstanceHandle wire, const std::wstring& prop,
                                                const std::wstring& resourceName, bool asTheme)>;

using DevToolsWindowBindFn      = std::function<bool(InstanceHandle wire, const std::wstring& op,
                                                const std::wstring& prop, const std::wstring& payload,
                                                const std::wstring& mode, std::wstring* outJson)>;

using DevToolsWindowSourceFn    = std::function<bool(InstanceHandle wire, std::wstring& outFile,
                                                unsigned int& outLine, unsigned int& outCol)>;

using DevToolsWindowLayout = DevToolsReadLayout;
using DevToolsWindowLayoutFn    = std::function<bool(InstanceHandle wire, DevToolsWindowLayout& out)>;

using DevToolsWindowHighlightFn = std::function<void(InstanceHandle /*wire; 0 = clear*/, bool /*select*/)>;

using DevToolsWindowWireLiveFn  = std::function<bool(InstanceHandle /*wire*/)>;

using DevToolsWindowCensusCountFn = std::function<size_t()>;

using DevToolsWindowSnapshotFn = std::function<void(std::vector<DevToolsWindowNode>& outNodes, size_t& outCensusTotal)>;

using DevToolsWindowPreviewFn = std::function<void(std::vector<DevToolsWindowNode>& nodes,
                                             const std::vector<size_t>& indices)>;

// Open/close and every live XAML read must run on the app UI thread.
HRESULT DevToolsWindow_Open(IXamlDiagnostics* diag,
                       const std::vector<DevToolsWindowNode>& nodes,
                       size_t censusTotal,
                       DevToolsWindowRowsFn rowsFn,
                       DevToolsWindowReadInputFn readInputFn,
                       DevToolsWindowWriteFn writeFn,
                       DevToolsWindowClearFn clearFn,
                       DevToolsWindowResolveFn resolveFn,
                       DevToolsWindowBindFn bindFn,
                       DevToolsWindowSourceFn sourceFn,
                       DevToolsWindowLayoutFn layoutFn,
                       DevToolsWindowHighlightFn highlightFn,
                       DevToolsWindowWireLiveFn wireLiveFn,
                       DevToolsWindowCensusCountFn censusCountFn,
                       DevToolsWindowSnapshotFn snapshotFn,
                       DevToolsWindowPreviewFn previewFn,
                       DevToolsSeedFn seed,
                       InstanceHandle preselectWire = 0,
                       std::wstring* errorMessage = nullptr);

HRESULT DevToolsWindow_Close();

bool DevToolsWindow_IsOpen();

// True when Just my XAML has been turned off in the inspector (it stays off while the window is hidden). UI thread only.
bool DevToolsWindow_ShowsFrameworkRows();


struct DevToolsWindowComment
{
    std::wstring id;
    std::wstring text;
    std::wstring status;
    std::wstring element;
    std::wstring file;
    std::wstring uri;
    std::wstring anchor;
    unsigned int line = 0;
    std::wstring revision;
};

// Snapshot the pushed comment set. Returns false when no set has been pushed (nothing to show yet).
using DevToolsWindowCommentsFn = std::function<bool(std::vector<DevToolsWindowComment>* /*out*/)>;

// Resolve a comment's live anchor to a WIRE handle for selection, or 0 when its element isn't realized right
// now — which is a normal answer (another page, a virtualized item), not a failure.
using DevToolsWindowAnchorWireFn = std::function<InstanceHandle(const wchar_t* /*anchor*/)>;

using DevToolsWindowCommentResolveFn = std::function<bool(const wchar_t* /*id*/, std::function<void(int)> /*completed*/)>;

// Register the comment bridge. Safe to call before or after DevToolsWindow_Open; the view is empty (and says so)
// until it is registered. UI thread only.
void DevToolsWindow_SetCommentBridge(DevToolsWindowCommentsFn commentsFn,
                                DevToolsWindowAnchorWireFn anchorWireFn,
                                DevToolsWindowCommentResolveFn resolveFn);

// Worker-only native walk callback; implementation marshals to the app UI thread.
// Empty tryPath diagnoses the installed path, nonempty validates a proposed path.
using DevToolsWindowWalkFn = std::function<bool(InstanceHandle wire, const std::wstring& prop,
                                           const std::wstring& tryPath, std::wstring* outJson)>;
void DevToolsWindow_SetPathWalk(DevToolsWindowWalkFn walkFn);

void DevToolsWindow_RefreshComments();

bool DevToolsWindow_OnPicked(InstanceHandle wire, bool releaseCommit);
// Re-reads the properties pane when `wire` is the element it shows and no edit would be lost. UI thread only.
void DevToolsWindow_RereadSelection(InstanceHandle wire);

void DevToolsWindow_OnPickModeChanged(bool armed);

void DevToolsWindow_OnFocusChanged(InstanceHandle wire);

bool DevToolsWindow_OwnsElement(IInspectable* element);
