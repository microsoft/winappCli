// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>
#include <vector>

enum class DevToolsWalkOutcome {
    Resolved,
    Null,
    Missing,
    Threw,
    NotReached,
};

struct DevToolsPathWalkStep {
    std::wstring   path;
    DevToolsWalkOutcome outcome = DevToolsWalkOutcome::Resolved;
    std::wstring   value;
    std::wstring   type;
    bool           stop  = false;
    bool           after = false;
};

struct DevToolsPathWalkView {
    bool show = false;

    std::wstring againstName;
    std::wstring againstNote;

    std::wstring qHead;
    std::wstring qEmphasis;
    std::wstring qTail;

    std::wstring answer;

    std::vector<DevToolsPathWalkStep> steps;

    bool revealException = false;

    bool namesException = false;
};

DevToolsPathWalkView DevToolsPathWalk_FromJson(const std::wstring& json, bool agentPresent);

std::wstring DevToolsPathWalk_PlainText(const DevToolsPathWalkView& v);

const wchar_t* DevToolsPathWalk_OutcomeText(DevToolsWalkOutcome outcome);

const wchar_t* DevToolsPathWalk_StopMark();
