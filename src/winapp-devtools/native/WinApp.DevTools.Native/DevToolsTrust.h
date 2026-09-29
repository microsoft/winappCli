// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include "DevToolsProtocolSchema.h" // DevToolsAccess

#include <windows.h>
#include <string>


bool DevToolsTrust_InitializePosture(DevToolsAccess posture);
DevToolsAccess DevToolsTrust_CurrentPosture();

// True when `posture` reaches at least `required` (Read <= Ui <= Mutation).
bool DevToolsAccess_Satisfies(DevToolsAccess posture, DevToolsAccess required);

bool DevToolsTrust_MutationEnabled();

bool DevToolsTrust_MutationEnvDenied();

struct DevToolsTrustInit {
    DevToolsAccess posture = DevToolsAccess::Read;   // fail-closed default
    std::wstring cliExe;                    // empty when the injector supplied none
    bool cliSibling = false;
    const wchar_t* guestInitializationError = nullptr;
    bool guestComments = false;
    std::wstring guestCommentStart, guestCommentBinding, guestCommentEpoch;
};

DevToolsTrustInit DevToolsTrust_ParseInit(const std::wstring& initializationDataJson, bool mutationEnvDenied);
bool DevToolsTrust_ValidGuestContext(const std::wstring& start, const std::wstring& binding,
    const std::wstring& epoch, bool allowUnavailable = false);


constexpr DWORD kDevToolsPipeClientRights = FILE_READ_DATA | FILE_WRITE_DATA | FILE_READ_ATTRIBUTES | SYNCHRONIZE;
constexpr DWORD kDevToolsPipeOwnerRights  = FILE_GENERIC_READ | FILE_GENERIC_WRITE | FILE_CREATE_PIPE_INSTANCE;

std::wstring DevToolsTrust_BuildPipeSddl(const std::wstring& ownerSid, const std::wstring& integritySid, DWORD rights);

// Resolves this process token's user SID / integrity-level SID as SDDL strings ("S-1-5-21-...", "S-1-16-...").
bool DevToolsTrust_CurrentOwnerSid(std::wstring* outSid);
bool DevToolsTrust_CurrentIntegritySid(std::wstring* outSid);

// Pipe security is same-user and same-integrity; DevToolsTrust_VerifyPipeClient rechecks connected clients.
PSECURITY_DESCRIPTOR DevToolsTrust_CreatePipeSecurityDescriptor();


bool DevToolsTrust_VerifyPipeClient(HANDLE pipe);
