// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Runtime XAML is parsed by the target app so the overlay follows the app's WinUI version.

#include "DevToolsOverlay.h"
#include "DevToolsInspectorAcceptance.h"
#include "DevToolsFocusOwners.h"
#include "DevToolsThreadGuard.h"
#include "DevToolsProjected.h"
#include "DevToolsSelectionTracking.h"
#include "DevToolsSelectionPlacement.h"
#include "DevToolsToolbarCorner.h"
#include "DevToolsTheme.h"
#include "DevToolsSink.h"
#include "DevToolsBindingRow.h"
#include "DevToolsCommentText.h"
#include "DevToolsEditText.h"
#include "DevToolsSettings.h"
#include "DevToolsText.h"
#include "DevToolsPathWalk.h"
#include "DevToolsBindingRelay.h"
#include "DevToolsProtocol.h"
#include "DevToolsTrust.h"
#include "DevToolsPerf.h"
#include "DevToolsSurface.h"
#include <windows.h>
#include <objidl.h>
#include <roapi.h>
#include <winstring.h>
#include <string>
#include <cstdio>
#include <vector>
#include <deque>
#include <atomic>
#include <functional>
#include <algorithm>
#include <cmath>
#include <sstream>
#include "DevToolsRead.h"
#include <iomanip>
#include <locale>
#include <limits>
#include <cstdlib>
#include <utility>
#include <initializer_list>

// Overlay UI markup lives as reviewable.xaml under native/WinApp.DevTools.Native/xaml/*.xaml and is embedded as
// DevToolsXaml::* wide-string literals by this generated header (produced by gen-xaml-resources.ps1, run from
// build-devtools.ps1). The header is build output (.gitignored) — edit the.xaml, not the header.
#include "DevToolsOverlayXaml.g.h"

// PW_RENDERFULLCONTENT (WinUI3/DirectComposition-aware PrintWindow capture) may be absent from older SDK
// winuser.h; define it defensively so the screenshot button captures composited content correctly.
#ifndef PW_RENDERFULLCONTENT
#define PW_RENDERFULLCONTENT 0x00000002
#endif

// Generated WinRT ABI slots and IIDs come from DevToolsProjected. Classic COM interfaces without winmd
// metadata, including IXamlDiagnostics2 and IWindowNative, use the published SDK declarations.
struct __declspec(uuid("523A35EE-EB38-4AE6-A3E1-5B7D0D547BD0")) IXamlDiagnostics2_Abi : IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE GetUiLayerForXamlRoot(InstanceHandle instanceHandle, IInspectable** ppLayer) = 0;
    virtual HRESULT STDMETHODCALLTYPE HitTestForXamlRoot(InstanceHandle instanceHandle, RECT rect,
                                                         unsigned int* pCount, InstanceHandle** ppInstanceHandles) = 0;
};

#include "DevToolsOverlay.State.inc"
#include "DevToolsOverlay.Host.inc"
#include "DevToolsOverlay.Pick.inc"
#include "DevToolsOverlay.Toolbar.inc"
#include "DevToolsOverlay.Build.inc"
#include "DevToolsOverlay.SelectionPanel.inc"
#include "DevToolsOverlay.SelectionRows.inc"
#include "DevToolsOverlay.BindingDiagnosis.inc"
#include "DevToolsOverlay.Composer.inc"
#include "DevToolsOverlay.CommentMode.inc"
#include "DevToolsOverlay.Tracking.inc"
#include "DevToolsOverlay.Anchoring.inc"
#include "DevToolsOverlay.Exports.inc"
