// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

typedef void (*DevToolsTreeChangedFn)(void);

bool DevToolsTreeWatch_Subscribe(DevToolsTreeChangedFn fn);

void DevToolsTreeWatch_Drain();
