// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

class DevToolsPickRoute
{
public:
    void ArmWindowPick()    { m_windowPickArmed = true; }
    void CancelWindowPick() { m_windowPickArmed = false; }
    bool IsWindowPickArmed() const { return m_windowPickArmed; }
    bool CommitReleasedPick()
    {
        const bool fromWindow = m_windowPickArmed;
        m_windowPickArmed = false;
        return fromWindow;
    }

private:
    bool m_windowPickArmed = false;
};
