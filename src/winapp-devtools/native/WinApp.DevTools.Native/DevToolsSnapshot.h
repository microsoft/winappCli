// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <windows.h>
#include <inspectable.h>
#include <memory>
#include <string>

// The screenshot a UI comment carries: the element in its nearby context, rendered from the app's own XAML so the
// DevTools chrome is never in it. Begin runs on the UI thread and returns at once; rendering completes on later
// frames. Finish waits for it on any other thread, then crops, masks, outlines and encodes a PNG.
struct DevToolsSnapshotJob;

std::shared_ptr<DevToolsSnapshotJob> DevToolsSnapshot_Begin(IInspectable* element, const wchar_t** error);

// On success `json` is the Internal.elementSnapshot result object; on failure `error` is a stable token.
bool DevToolsSnapshot_Finish(const std::shared_ptr<DevToolsSnapshotJob>& job, DWORD timeoutMs,
                             std::wstring* json, const wchar_t** error);
