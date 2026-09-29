// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

class DevToolsFocusOwners
{
public:
    bool SetWindow(bool enabled) { window_ = enabled; return Active(); }
    bool SetExternal(bool enabled) { external_ = enabled; return Active(); }
    bool Active() const { return window_ || external_; }

private:
    bool window_ = false;
    bool external_ = false;
};
