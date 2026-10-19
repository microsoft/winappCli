// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

enum class DevToolsWriteOutcome : int
{
    Failed = 0,

    Ok = 1,

    ReplacedBinding = 2,

    NeedsConfirm = 3,
};

inline bool DevToolsWriteLanded(DevToolsWriteOutcome o)
{
    return o == DevToolsWriteOutcome::Ok || o == DevToolsWriteOutcome::ReplacedBinding;
}

inline bool DevToolsWriteRefused(DevToolsWriteOutcome o) { return o == DevToolsWriteOutcome::NeedsConfirm; }
