// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>

enum class DevToolsPerfSite : int {
    TapTreeCallback = 0,
    TapTreeWatchTurn,
    TapRemovedTurn,
    OverlayTrackTick,
    OverlayAdorner,
    OverlayPins,
    OverlayLayoutAdorner,
    OverlayPick,
    OverlayHighlight,
    OverlaySetComments,
    OverlayBindDiag,
    WinTreeRefresh,
    WinSnapshot,
    WinPreview,
    WinTreeRows,
    WinClassify,
    WinPropPane,
    WinPropRead,
    WinPropWrite,
    WinPropCommit,
    WinXamlParse,
    WinTreeFilter,
    WinRowRealize,
    WinTreeVisible,
    WinTreeHover,
    WinHoverRevert,
    WinFocus,
    WinComments,
    WinCommentLive,
    TapPreviewBatch,
    Count
};

void DevToolsPerf_LatchUiThread();

bool DevToolsPerf_UiThreadLatched();

bool DevToolsPerf_SetEnabled(bool on);
bool DevToolsPerf_Enabled();

void DevToolsPerf_Reset();

bool DevToolsPerf_SamplerStart();
void DevToolsPerf_SamplerStop();
bool DevToolsPerf_SamplerRunning();

std::wstring DevToolsPerf_ReportJson();

class DevToolsPerfScope {
public:
    explicit DevToolsPerfScope(DevToolsPerfSite site);
    ~DevToolsPerfScope();
    DevToolsPerfScope(const DevToolsPerfScope&) = delete;
    DevToolsPerfScope& operator=(const DevToolsPerfScope&) = delete;
private:
    DevToolsPerfSite  m_site;
    bool         m_live;
    double       m_t0;
    double       m_childUs;
    double*      m_prevSink;
};

#define WINAPP_DEVTOOLS_PERF_CAT2(a, b) a##b
#define WINAPP_DEVTOOLS_PERF_CAT(a, b) WINAPP_DEVTOOLS_PERF_CAT2(a, b)
#define WINAPP_DEVTOOLS_PERF(site) DevToolsPerfScope WINAPP_DEVTOOLS_PERF_CAT(devToolsPerfScope_, __LINE__)(site)
