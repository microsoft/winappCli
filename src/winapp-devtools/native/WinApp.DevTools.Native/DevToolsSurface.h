// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <windows.h>
#include <inspectable.h>
#include <xamlom.h>   // InstanceHandle, IXamlDiagnostics
#include <string>
#include <vector>

struct DevToolsSurface
{
    InstanceHandle rootHandle = 0;

    unsigned long long xamlRootKey = 0;

    unsigned long long islandId = 0;

    HWND hostHwnd = nullptr;

    float contentW = 0.0f;   // XamlRoot.Size, DIPs
    float contentH = 0.0f;

    RECT contentRect{};

    bool hasConverter = false;   // IXamlRoot3 present: screen<->local is asked of the root
    bool hostVisible = false;    // XamlRoot.IsHostVisible

    // What the user is told. Never a bare handle (naming rule 4).
    std::wstring displayName;

    bool Valid() const { return rootHandle != 0 && xamlRootKey != 0; }
};

bool DevToolsSurface_Resolve(IXamlDiagnostics* diag, InstanceHandle element, DevToolsSurface* out);

std::vector<DevToolsSurface> DevToolsSurface_ResolveAll(IXamlDiagnostics* diag,
                                              const std::vector<InstanceHandle>& candidates);

// Screen point -> this surface's local DIP point, the coordinate contract HitTestForXamlRoot expects.
bool DevToolsSurface_ScreenToLocal(const DevToolsSurface& s, int screenX, int screenY, float* localX, float* localY);
bool DevToolsSurface_LocalToScreen(const DevToolsSurface& s, float localX, float localY, POINT* screen);

bool DevToolsSurface_ContainsScreenPoint(const DevToolsSurface& s, int screenX, int screenY);

unsigned int DevToolsSurface_SessionOrdinal(unsigned long long xamlRootKey);

void DevToolsSurface_ForgetDeadOrdinals(const std::vector<unsigned long long>& liveKeys);
