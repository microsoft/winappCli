// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <windows.h>
#include <unknwn.h>

#include <functional>

using DevToolsUiEnqueueFn = HRESULT (*)(void* context, IUnknown* handler, bool* enqueued);

HRESULT DevToolsUiDispatch_Run(void* context, DevToolsUiEnqueueFn enqueue,
                          std::function<HRESULT()> operation, DWORD timeoutMs = 10000,
                          bool* completed = nullptr);
