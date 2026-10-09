// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Trust tests cover fail-closed posture parsing, access order, pipe SDDL and post-read client
// identity/integrity. Known-bad controls ensure rejection checks are not vacuous.

#include "DevToolsTrust.h"
#include "DevToolsProtocolSchema.h"

#include <sddl.h>
#include <cstdio>
#include <string>
#include <vector>

int g_trustFailures = 0;

static void TCheck(bool cond, const char* what)
{
    if (!cond) { ++g_trustFailures; std::printf("  FAIL  %s\n", what); }
    else       {                    std::printf("  ok    %s\n", what); }
}

// ---- posture: DevToolsAccess ordering -------------------------------------------------------------------------

static void TestAccessOrderingAndTokens()
{
    std::printf("DevToolsAccess ordering and wire tokens\n");

    TCheck(DevToolsAccess_Satisfies(DevToolsAccess::Read, DevToolsAccess::Read), "  Read satisfies Read");
    TCheck(!DevToolsAccess_Satisfies(DevToolsAccess::Read, DevToolsAccess::Ui), "  Read does NOT satisfy Ui");
    TCheck(!DevToolsAccess_Satisfies(DevToolsAccess::Read, DevToolsAccess::Mutation), "  Read does NOT satisfy Mutation");
    TCheck(DevToolsAccess_Satisfies(DevToolsAccess::Ui, DevToolsAccess::Read), "  Ui satisfies Read");
    TCheck(DevToolsAccess_Satisfies(DevToolsAccess::Ui, DevToolsAccess::Ui), "  Ui satisfies Ui");
    TCheck(!DevToolsAccess_Satisfies(DevToolsAccess::Ui, DevToolsAccess::Mutation), "  Ui does NOT satisfy Mutation");
    TCheck(DevToolsAccess_Satisfies(DevToolsAccess::Mutation, DevToolsAccess::Read), "  Mutation satisfies Read");
    TCheck(DevToolsAccess_Satisfies(DevToolsAccess::Mutation, DevToolsAccess::Ui), "  Mutation satisfies Ui");
    TCheck(DevToolsAccess_Satisfies(DevToolsAccess::Mutation, DevToolsAccess::Mutation), "  Mutation satisfies Mutation");

    TCheck(std::wstring(DevToolsAccessToken(DevToolsAccess::Read)) == L"read", "  Read token is \"read\"");
    TCheck(std::wstring(DevToolsAccessToken(DevToolsAccess::Ui)) == L"ui", "  Ui token is \"ui\"");
    TCheck(std::wstring(DevToolsAccessToken(DevToolsAccess::Mutation)) == L"mutation", "  Mutation token is \"mutation\"");

    DevToolsAccess parsed;
    TCheck(DevToolsAccessFromToken(L"read", &parsed) && parsed == DevToolsAccess::Read, "  \"read\" parses back to Read");
    TCheck(DevToolsAccessFromToken(L"ui", &parsed) && parsed == DevToolsAccess::Ui, "  \"ui\" parses back to Ui");
    TCheck(DevToolsAccessFromToken(L"mutation", &parsed) && parsed == DevToolsAccess::Mutation,
           "  \"mutation\" parses back to Mutation");
    TCheck(!DevToolsAccessFromToken(L"Mutation", &parsed), "  token parsing is case-sensitive (\"Mutation\" rejected)");
    TCheck(!DevToolsAccessFromToken(L"", &parsed), "  an empty token is rejected");
    TCheck(!DevToolsAccessFromToken(L"write", &parsed), "  an unrecognized token is rejected");
}

// ---- posture: DevToolsTrust_ParseInit fail-closed parsing -----------------------------------------------------

static void TestParseInitFailsClosed()
{
    std::printf("DevToolsTrust_ParseInit fails closed on anything but a well-formed blob\n");

    // Positive control FIRST: prove the parser can reach every tier when the input is well-formed, so the
    // fail-closed assertions below are not vacuously true because parsing is simply broken end to end.
    DevToolsTrustInit ok = DevToolsTrust_ParseInit(L"{\"version\":1,\"posture\":\"mutation\",\"cliExe\":\"C:\\\\winapp.exe\"}", false);
    TCheck(ok.posture == DevToolsAccess::Mutation, "  control: a well-formed mutation blob parses to Mutation");
    TCheck(ok.cliExe == L"C:\\winapp.exe", "  control: cliExe round-trips");

    DevToolsTrustInit uiOk = DevToolsTrust_ParseInit(L"{\"version\":1,\"posture\":\"ui\"}", false);
    TCheck(uiOk.posture == DevToolsAccess::Ui, "  control: a well-formed ui blob parses to Ui");
    TCheck(uiOk.cliExe.empty(), "  control: an absent cliExe stays empty");

    // Now the fail-closed cases.
    TCheck(DevToolsTrust_ParseInit(L"", false).posture == DevToolsAccess::Read, "  empty init data fails closed to Read");
    TCheck(DevToolsTrust_ParseInit(L"not json", false).posture == DevToolsAccess::Read, "  malformed JSON fails closed to Read");
    TCheck(DevToolsTrust_ParseInit(L"[1,2,3]", false).posture == DevToolsAccess::Read, "  a JSON array (not object) fails closed to Read");
    TCheck(DevToolsTrust_ParseInit(L"{\"posture\":\"mutation\"}", false).posture == DevToolsAccess::Read,
           "  missing version fails closed to Read");
    TCheck(DevToolsTrust_ParseInit(L"{\"version\":2,\"posture\":\"mutation\"}", false).posture == DevToolsAccess::Read,
           "  an unrecognized version fails closed to Read");
    TCheck(DevToolsTrust_ParseInit(L"{\"version\":\"1\",\"posture\":\"mutation\"}", false).posture == DevToolsAccess::Read,
           "  a version carried as a string (not a number) fails closed to Read");
    TCheck(DevToolsTrust_ParseInit(L"{\"version\":1}", false).posture == DevToolsAccess::Read,
           "  a missing posture fails closed to Read");
    TCheck(DevToolsTrust_ParseInit(L"{\"version\":1,\"posture\":\"Mutation\"}", false).posture == DevToolsAccess::Read,
           "  a mis-cased posture token fails closed to Read");
    TCheck(DevToolsTrust_ParseInit(L"{\"version\":1,\"posture\":\"admin\"}", false).posture == DevToolsAccess::Read,
           "  a made-up posture token fails closed to Read");
    TCheck(DevToolsTrust_ParseInit(L"{\"version\":1,\"posture\":\"admin\",\"cliExe\":\"C:\\\\evil.exe\"}", false).cliExe.empty(),
           "  an invalid posture rejects the entire initialization payload");
    TCheck(DevToolsTrust_ParseInit(L"{\"version\":1,\"posture\":\"mutation\"}", false).cliExe.empty(),
           "  a malformed/absent cliExe never defaults to something non-empty");
}

// ---- posture: the WINAPP_DEVTOOLS_MUTATION=deny floor ------------------------------------------------------

static void TestMutationEnvFloorCapsMutationOnly()
{
    std::printf("The deny floor caps a requested Mutation posture to Ui, and nothing else\n");

    TCheck(DevToolsTrust_ParseInit(L"{\"version\":1,\"posture\":\"mutation\"}", /*mutationEnvDenied*/ true).posture == DevToolsAccess::Ui,
           "  Mutation is capped to Ui under the deny floor");
    TCheck(DevToolsTrust_ParseInit(L"{\"version\":1,\"posture\":\"ui\"}", /*mutationEnvDenied*/ true).posture == DevToolsAccess::Ui,
           "  Ui is unaffected by the deny floor");
    TCheck(DevToolsTrust_ParseInit(L"{\"version\":1,\"posture\":\"read\"}", /*mutationEnvDenied*/ true).posture == DevToolsAccess::Read,
           "  Read is unaffected by the deny floor");
    // Without the floor, the SAME blob reaches Mutation (this is the control that makes the case above mean
    // something: the floor is what capped it, not a parsing bug).
    TCheck(DevToolsTrust_ParseInit(L"{\"version\":1,\"posture\":\"mutation\"}", /*mutationEnvDenied*/ false).posture == DevToolsAccess::Mutation,
           "  control: the identical blob reaches Mutation when the floor is not set");
}

static void TestMutationEnvDeniedReadsRealEnvironment()
{
    std::printf("DevToolsTrust_MutationEnvDenied reads WINAPP_DEVTOOLS_MUTATION live (not cached)\n");

    SetEnvironmentVariableW(L"WINAPP_DEVTOOLS_MUTATION", nullptr);
    TCheck(!DevToolsTrust_MutationEnvDenied(), "  unset env is not denied");
    SetEnvironmentVariableW(L"WINAPP_DEVTOOLS_MUTATION", L"deny");
    TCheck(DevToolsTrust_MutationEnvDenied(), "  \"deny\" is denied");
    SetEnvironmentVariableW(L"WINAPP_DEVTOOLS_MUTATION", L"DENY");
    TCheck(DevToolsTrust_MutationEnvDenied(), "  \"DENY\" is denied (case-insensitive, matching the injector's floor)");
    SetEnvironmentVariableW(L"WINAPP_DEVTOOLS_MUTATION", L"allow");
    TCheck(!DevToolsTrust_MutationEnvDenied(), "  any other value is not denied");
    SetEnvironmentVariableW(L"WINAPP_DEVTOOLS_MUTATION", nullptr); // leave the process environment as found
}

// ---- posture: process-wide storage -------------------------------------------------------------------------

static void TestPostureInitializationIsImmutable()
{
    std::printf("DevToolsTrust posture is initialized once and cannot be raised later\n");

    TCheck(DevToolsTrust_CurrentPosture() == DevToolsAccess::Read, "  uninitialized posture fails closed to Read");
    TCheck(DevToolsTrust_InitializePosture(DevToolsAccess::Ui), "  first initialization succeeds");
    TCheck(DevToolsTrust_CurrentPosture() == DevToolsAccess::Ui, "  first posture is stored");
    TCheck(!DevToolsTrust_MutationEnabled(), "  MutationEnabled is false at Ui");
    TCheck(!DevToolsTrust_InitializePosture(DevToolsAccess::Mutation), "  later initialization is rejected");
    TCheck(DevToolsTrust_CurrentPosture() == DevToolsAccess::Ui, "  rejected initialization cannot raise posture");
}

// ---- pipe security: SDDL construction -----------------------------------------------------------------------

// GENERIC_ALL, mapped through the named-pipe generic mapping, is FILE_ALL_ACCESS -- which includes
// FILE_CREATE_PIPE_INSTANCE (0x4, the same bit value as FILE_APPEND_DATA). SDDL's bare "GA" token stores the
// raw generic bit (0x10000000) in the ACE, not the expanded specific bits, so this is the expansion step an
// access check performs -- reused here to prove today's baseline really was broader than kDevToolsPipeOwnerRights.
static DWORD ExpandNamedPipeGenericMask(DWORD mask)
{
    GENERIC_MAPPING pipeMapping{ FILE_GENERIC_READ, FILE_GENERIC_WRITE, FILE_GENERIC_EXECUTE, FILE_ALL_ACCESS };
    MapGenericMask(&mask, &pipeMapping);
    return mask;
}

static bool ExtractOwnerAce(const std::wstring& sddl, std::wstring* outSid, DWORD* outMask, bool* outProtected)
{
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &sd, nullptr)) return false;

    bool ok = false;
    BOOL daclPresent = FALSE, daclDefaulted = FALSE;
    PACL dacl = nullptr;
    if (GetSecurityDescriptorDacl(sd, &daclPresent, &dacl, &daclDefaulted) && daclPresent && dacl && dacl->AceCount == 1) {
        ACCESS_ALLOWED_ACE* ace = nullptr;
        if (GetAce(dacl, 0, reinterpret_cast<LPVOID*>(&ace))) {
            *outMask = ace->Mask;
            LPWSTR sidStr = nullptr;
            if (ConvertSidToStringSidW(reinterpret_cast<PSID>(&ace->SidStart), &sidStr)) {
                *outSid = sidStr;
                LocalFree(sidStr);
                ok = true;
            }
        }
    }
    SECURITY_DESCRIPTOR_CONTROL control{};
    DWORD revision = 0;
    if (GetSecurityDescriptorControl(sd, &control, &revision)) {
        *outProtected = (control & SE_DACL_PROTECTED) != 0;
    }
    LocalFree(sd);
    return ok;
}

static bool ExtractMandatoryLabel(const std::wstring& sddl, std::wstring* outSid, DWORD* outMask)
{
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &sd, nullptr)) return false;

    bool ok = false;
    BOOL saclPresent = FALSE, saclDefaulted = FALSE;
    PACL sacl = nullptr;
    if (GetSecurityDescriptorSacl(sd, &saclPresent, &sacl, &saclDefaulted) && saclPresent && sacl && sacl->AceCount >= 1) {
        SYSTEM_MANDATORY_LABEL_ACE* ace = nullptr;
        if (GetAce(sacl, 0, reinterpret_cast<LPVOID*>(&ace))) {
            *outMask = ace->Mask;
            LPWSTR sidStr = nullptr;
            if (ConvertSidToStringSidW(reinterpret_cast<PSID>(&ace->SidStart), &sidStr)) {
                *outSid = sidStr;
                LocalFree(sidStr);
                ok = true;
            }
        }
    }
    LocalFree(sd);
    return ok;
}

static void TestPipeSddlIsMinimalAndLabeled()
{
    std::printf("The pipe security descriptor is an owner-only minimal-rights DACL plus a mandatory label\n");

    const std::wstring ownerSid = L"S-1-5-21-111111111-222222222-333333333-1000";
    const std::wstring integritySid = L"S-1-16-8192"; // SECURITY_MANDATORY_MEDIUM_RID, an arbitrary but valid IL SID

    // Positive control: today's ORIGINAL baseline ("D:P(A;;GA;;;<sid>)", no mandatory label) really did grant
    // far more than kDevToolsPipeOwnerRights once its generic bit is expanded -- including WRITE_DAC, DELETE, and
    // the create-pipe-instance bit -- so the tightened assertions below are a real regression gate, not a
    // strawman. If this control ever stopped detecting the difference, the "is minimal" checks below would be
    // meaningless.
    {
        std::wstring legacySddl = L"D:P(A;;GA;;;"; legacySddl += ownerSid; legacySddl += L")";
        std::wstring legacySid; DWORD legacyMask = 0; bool legacyProtected = false;
        TCheck(ExtractOwnerAce(legacySddl, &legacySid, &legacyMask, &legacyProtected),
               "  control: the legacy GA-based SDDL parses");
        const DWORD legacyExpanded = ExpandNamedPipeGenericMask(legacyMask);
        TCheck((legacyExpanded & FILE_CREATE_PIPE_INSTANCE) != 0,
               "  control: the legacy GA grant, expanded, includes FILE_CREATE_PIPE_INSTANCE");
        TCheck((legacyExpanded & WRITE_DAC) != 0, "  control: the legacy GA grant, expanded, includes WRITE_DAC");
        TCheck((legacyExpanded & DELETE) != 0, "  control: the legacy GA grant, expanded, includes DELETE");
    }

    const std::wstring sddl = DevToolsTrust_BuildPipeSddl(ownerSid, integritySid, kDevToolsPipeOwnerRights);

    std::wstring aceSid; DWORD aceMask = 0; bool protectedDacl = false;
    TCheck(ExtractOwnerAce(sddl, &aceSid, &aceMask, &protectedDacl), "  the built SDDL parses and has exactly one DACL ACE");
    TCheck(aceSid == ownerSid, "  the ACE grants the owner's SID, not a broader group");
    TCheck(protectedDacl, "  the DACL is protected (SE_DACL_PROTECTED; no inherited ACEs can widen it)");
    TCheck(aceMask == kDevToolsPipeOwnerRights, "  the ACE mask is EXACTLY kDevToolsPipeOwnerRights, no more and no less");
    TCheck((aceMask & WRITE_DAC) == 0, "  the ACE does NOT grant WRITE_DAC");
    TCheck((aceMask & WRITE_OWNER) == 0, "  the ACE does NOT grant WRITE_OWNER");
    TCheck((aceMask & DELETE) == 0, "  the ACE does NOT grant DELETE");
    TCheck((aceMask & GENERIC_ALL) == 0, "  the ACE mask carries no leftover generic bit");
    // kDevToolsPipeClientRights (what the converted native client's CreateFileW actually requests) is a strict
    // subset of what the ACE grants, so a minimal-rights client connects without ever exercising
    // FILE_CREATE_PIPE_INSTANCE -- it never asks for it.
    TCheck((aceMask & kDevToolsPipeClientRights) == kDevToolsPipeClientRights,
           "  the ACE grants at least the exact rights the native minimal-rights client requests");
    TCheck((kDevToolsPipeClientRights & FILE_CREATE_PIPE_INSTANCE) == 0,
           "  the client's OWN requested mask never includes FILE_CREATE_PIPE_INSTANCE");

    std::wstring labelSid; DWORD labelMask = 0;
    TCheck(ExtractMandatoryLabel(sddl, &labelSid, &labelMask), "  the built SDDL carries a SACL mandatory-label ACE");
    TCheck(labelSid == integritySid, "  the mandatory label pins the SAME integrity SID passed in");
    TCheck((labelMask & SYSTEM_MANDATORY_LABEL_NO_WRITE_UP) != 0, "  the label policy is NO_WRITE_UP");
    TCheck((labelMask & SYSTEM_MANDATORY_LABEL_NO_READ_UP) != 0, "  the label policy is NO_READ_UP");
    TCheck((labelMask & SYSTEM_MANDATORY_LABEL_NO_EXECUTE_UP) != 0, "  the label policy is NO_EXECUTE_UP");
}

static void TestCurrentOwnerAndIntegritySidsResolve()
{
    std::printf("DevToolsTrust_CurrentOwnerSid / CurrentIntegritySid resolve this process's real token\n");

    std::wstring ownerSid;
    TCheck(DevToolsTrust_CurrentOwnerSid(&ownerSid), "  the current process owner SID resolves");
    TCheck(ownerSid.rfind(L"S-1-", 0) == 0, "  it looks like a SID (\"S-1-...\")");

    std::wstring integritySid;
    TCheck(DevToolsTrust_CurrentIntegritySid(&integritySid), "  the current process integrity SID resolves");
    TCheck(integritySid.rfind(L"S-1-16-", 0) == 0, "  it is a mandatory-label SID (\"S-1-16-...\")");
}

static void TestCreatePipeSecurityDescriptorMatchesCurrentProcess()
{
    std::printf("DevToolsTrust_CreatePipeSecurityDescriptor wires the real process SID/IL into the SDDL\n");

    std::wstring expectedOwner, expectedIntegrity;
    TCheck(DevToolsTrust_CurrentOwnerSid(&expectedOwner) && DevToolsTrust_CurrentIntegritySid(&expectedIntegrity),
           "  (setup) current process SID/IL resolve");

    PSECURITY_DESCRIPTOR sd = DevToolsTrust_CreatePipeSecurityDescriptor();
    TCheck(sd != nullptr, "  a security descriptor is produced (fail-closed path not taken)");
    if (!sd) return;

    LPWSTR sddlOut = nullptr;
    TCheck(ConvertSecurityDescriptorToStringSecurityDescriptorW(
               sd, SDDL_REVISION_1, DACL_SECURITY_INFORMATION | SACL_SECURITY_INFORMATION | LABEL_SECURITY_INFORMATION,
               &sddlOut, nullptr) == TRUE,
           "  it round-trips back to an SDDL string");
    if (sddlOut) {
        std::wstring sddl = sddlOut;
        LocalFree(sddlOut);

        std::wstring aceSid; DWORD aceMask = 0; bool protectedDacl = false;
        TCheck(ExtractOwnerAce(sddl, &aceSid, &aceMask, &protectedDacl) && aceSid == expectedOwner,
               "  the DACL grants THIS process's owner SID");
        TCheck(aceMask == kDevToolsPipeOwnerRights, "  at exactly kDevToolsPipeOwnerRights");

        std::wstring labelSid; DWORD labelMask = 0;
        TCheck(ExtractMandatoryLabel(sddl, &labelSid, &labelMask) && labelSid == expectedIntegrity,
               "  the SACL mandatory label matches THIS process's integrity level");
    }
    LocalFree(sd);
}

static void TestProtectedPipeAcceptsMinimalRightsClient()
{
    std::printf("the production descriptor accepts its documented minimal-rights client\n");
    PSECURITY_DESCRIPTOR sd = DevToolsTrust_CreatePipeSecurityDescriptor();
    TCheck(sd != nullptr, "  (setup) production security descriptor created");
    if (!sd) return;
    SECURITY_ATTRIBUTES sa{ sizeof(sa), sd, FALSE };

    wchar_t pipeName[128];
    _snwprintf_s(pipeName, _countof(pipeName), _TRUNCATE, L"\\\\.\\pipe\\devtoolstrust-test-protected-%lu-%lu",
                 GetCurrentProcessId(), GetTickCount());
    HANDLE server = CreateNamedPipeW(pipeName, PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, PIPE_UNLIMITED_INSTANCES, 4096, 4096, 0, &sa);
    TCheck(server != INVALID_HANDLE_VALUE, "  first protected server instance created");

    HANDLE nextServer = INVALID_HANDLE_VALUE;
    if (server != INVALID_HANDLE_VALUE) {
        nextServer = CreateNamedPipeW(pipeName, PIPE_ACCESS_DUPLEX,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, PIPE_UNLIMITED_INSTANCES, 4096, 4096, 0, &sa);
        if (nextServer == INVALID_HANDLE_VALUE) {
            std::printf("          second CreateNamedPipeW error: %lu\n", GetLastError());
        }
        TCheck(nextServer != INVALID_HANDLE_VALUE, "  owner can create the next production pipe instance");
    }

    HANDLE client = INVALID_HANDLE_VALUE;
    if (server != INVALID_HANDLE_VALUE) {
        client = CreateFileW(pipeName, kDevToolsPipeClientRights, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        if (client == INVALID_HANDLE_VALUE) {
            std::printf("          CreateFileW error: %lu\n", GetLastError());
        }
        TCheck(client != INVALID_HANDLE_VALUE, "  same-owner client opens with exactly kDevToolsPipeClientRights");
    }

    if (client != INVALID_HANDLE_VALUE) CloseHandle(client);
    if (nextServer != INVALID_HANDLE_VALUE) CloseHandle(nextServer);
    if (server != INVALID_HANDLE_VALUE) CloseHandle(server);
    LocalFree(sd);
}

// ---- pipe security: post-connect client identity/integrity check --------------------------------------------

static void CheckVerifierReverted()
{
    HANDLE token = nullptr;
    const BOOL opened = OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &token);
    const DWORD error = GetLastError();
    TCheck(!opened && error == ERROR_NO_TOKEN, "  verifier leaves no thread impersonation token");
    if (token) {
        CloseHandle(token);
        RevertToSelf();
    }
}

// A same-process, same-user, same-integrity-level named pipe (server and client both created here) is the
// positive control for DevToolsTrust_VerifyPipeClient: the identity/IL check must accept the ordinary case, not
// just reject bad ones.
static void TestVerifyPipeClientAcceptsSameUserSameLevel(bool identification)
{
    std::printf("DevToolsTrust_VerifyPipeClient accepts same-user, same-IL (%s)\n",
                identification ? "host Identification rights/SQOS" : "default flags");

    wchar_t pipeName[128];
    _snwprintf_s(pipeName, _countof(pipeName), _TRUNCATE, L"\\\\.\\pipe\\devtoolstrust-test-%lu-%lu",
                 GetCurrentProcessId(), GetTickCount());

    HANDLE server = CreateNamedPipeW(pipeName, PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr);
    TCheck(server != INVALID_HANDLE_VALUE, "  (setup) test pipe server instance created");
    if (server == INVALID_HANDLE_VALUE) return;

    const DWORD rights = identification ? kDevToolsPipeClientRights : GENERIC_READ | GENERIC_WRITE;
    const DWORD flags = identification ? SECURITY_SQOS_PRESENT | SECURITY_IDENTIFICATION : 0;
    HANDLE client = CreateFileW(pipeName, rights, 0, nullptr, OPEN_EXISTING, flags, nullptr);
    TCheck(client != INVALID_HANDLE_VALUE, "  (setup) test pipe client connected");
    if (client != INVALID_HANDLE_VALUE) {
        TCheck(ConnectNamedPipe(server, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED,
               "  (setup) server observes the connection");
        // ImpersonateNamedPipeClient impersonates "the last message read from the pipe", so it requires at
        // least one message to have actually been read first (ERROR_CANT_IMPERSONATE otherwise) -- exactly
        // the ordering DevToolsTap.cpp's PipeConnection now uses (verify after the first ReadFile, not at accept).
        const char probe[] = "x";
        DWORD written = 0;
        TCheck(WriteFile(client, probe, sizeof(probe), &written, nullptr) == TRUE,
               "  (setup) client wrote a probe byte");
        char readBuf[8]{}; DWORD read = 0;
        TCheck(ReadFile(server, readBuf, sizeof(readBuf), &read, nullptr) == TRUE && read > 0,
               "  (setup) server read the probe byte");
        TCheck(DevToolsTrust_VerifyPipeClient(server), identification
            ? "  same-user, same-IL Identification client is accepted"
            : "  same-user, same-IL default client is accepted");
        CheckVerifierReverted();
        CloseHandle(client);
    }
    CloseHandle(server);
}

static void TestVerifyPipeClientRejectsInvalidPipeWithoutLeaking()
{
    std::printf("DevToolsTrust_VerifyPipeClient rejects invalid pipes without retaining primary tokens\n");
    DWORD before = 0, after = 0;
    const bool countedBefore = GetProcessHandleCount(GetCurrentProcess(), &before) != FALSE;
    TCheck(countedBefore, "  (setup) initial process handle count read");
    bool refused = true;
    for (int i = 0; i < 100; ++i) {
        if (DevToolsTrust_VerifyPipeClient(INVALID_HANDLE_VALUE)) refused = false;
    }
    TCheck(refused, "  all 100 invalid-pipe attempts fail closed");
    CheckVerifierReverted();
    const bool countedAfter = GetProcessHandleCount(GetCurrentProcess(), &after) != FALSE;
    TCheck(countedAfter, "  (setup) final process handle count read");
    TCheck(countedBefore && countedAfter && before == after, "  invalid-pipe attempts do not leak token handles");
}

// Negative control: lower the CLIENT thread's integrity level (same user, same SID -- Windows always allows
// lowering your own token's IL, no privilege required) and connect+write under that impersonation. The
// server-side check must now refuse it: a lower-IL client is exactly the case the mandatory label and this
// check both exist to stop. Reuses the same helper/assertion path as the positive control above, so a
// "refused" result here is evidence the check itself works, not that the setup silently failed differently.
static void TestVerifyPipeClientRejectsLowerIntegrityLevel()
{
    std::printf("DevToolsTrust_VerifyPipeClient rejects a lower-integrity-level connection (negative control)\n");

    HANDLE processToken = nullptr;
    OpenProcessToken(GetCurrentProcess(), TOKEN_DUPLICATE | TOKEN_QUERY, &processToken);
    TCheck(processToken != nullptr, "  (setup) opened this process's token");
    if (!processToken) return;

    HANDLE lowToken = nullptr;
    BOOL duplicated = DuplicateTokenEx(processToken, TOKEN_ALL_ACCESS, nullptr, SecurityImpersonation,
                                       TokenImpersonation, &lowToken);
    TCheck(duplicated == TRUE && lowToken != nullptr, "  (setup) duplicated an impersonation token");
    CloseHandle(processToken);
    if (!duplicated || !lowToken) return;

    PSID lowSid = nullptr;
    TCheck(ConvertStringSidToSidW(L"S-1-16-4096", &lowSid) == TRUE, "  (setup) built the Low mandatory-label SID");
    if (lowSid) {
        TOKEN_MANDATORY_LABEL label{};
        label.Label.Attributes = SE_GROUP_INTEGRITY;
        label.Label.Sid = lowSid;
        const DWORD labelSize = sizeof(TOKEN_MANDATORY_LABEL) + GetLengthSid(lowSid);
        TCheck(SetTokenInformation(lowToken, TokenIntegrityLevel, &label, labelSize) == TRUE,
               "  (setup) lowered the duplicated token's integrity level to Low");
        LocalFree(lowSid);
    }

    // This test's OWN pipe (not the production DevToolsTrust_CreatePipeSecurityDescriptor) is deliberately given a
    // Low mandatory label with NO_WRITE_UP -- the OPPOSITE of production's Medium/High label -- purely so a
    // Low-IL client is mechanically ABLE to write a probe byte at all (Windows' own default mandatory policy
    // otherwise refuses a lower-IL WriteFile before our check ever runs, which would prove nothing about
    // DevToolsTrust_VerifyPipeClient specifically). The rejection this test asserts comes ENTIRELY from
    // DevToolsTrust_VerifyPipeClient comparing the client's real (Low) integrity level against THIS PROCESS's real
    // (higher) one -- the test pipe's own label plays no part in that comparison.
    PSECURITY_DESCRIPTOR sd = nullptr;
    TCheck(ConvertStringSecurityDescriptorToSecurityDescriptorW(
               L"D:(A;;GA;;;WD)S:(ML;;NW;;;S-1-16-4096)", SDDL_REVISION_1, &sd, nullptr) == TRUE,
           "  (setup) built a permissive, Low-labeled test pipe descriptor");
    SECURITY_ATTRIBUTES sa{ sizeof(sa), sd, FALSE };

    wchar_t pipeName[128];
    _snwprintf_s(pipeName, _countof(pipeName), _TRUNCATE, L"\\\\.\\pipe\\devtoolstrust-test-lowil-%lu-%lu",
                 GetCurrentProcessId(), GetTickCount());
    HANDLE server = CreateNamedPipeW(pipeName, PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, sd ? &sa : nullptr);
    TCheck(server != INVALID_HANDLE_VALUE, "  (setup) test pipe server instance created");

    HANDLE client = INVALID_HANDLE_VALUE;
    if (server != INVALID_HANDLE_VALUE) {
        const bool impersonating = ImpersonateLoggedOnUser(lowToken) == TRUE;
        TCheck(impersonating, "  (setup) this thread is impersonating the Low-IL token");
        if (impersonating) {
            client = CreateFileW(pipeName, GENERIC_READ | GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
        }
        TCheck(client != INVALID_HANDLE_VALUE, "  (setup) the Low-IL client connected");

        // The identity ImpersonateNamedPipeClient later captures is the writer's token AT THE TIME OF THE
        // WRITE (NPFS tags each message with its writer's context when written, not merely at connect) --
        // so the probe write below must happen WHILE STILL impersonating the Low-IL token. Only THEN is it
        // safe to revert; connecting alone is not enough to bind the low identity to what the server reads.
        if (client != INVALID_HANDLE_VALUE) {
            TCheck(ConnectNamedPipe(server, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED,
                   "  (setup) server observes the connection");
            const char probe[] = "x";
            DWORD written = 0;
            TCheck(WriteFile(client, probe, sizeof(probe), &written, nullptr) == TRUE,
                   "  (setup) the Low-IL client wrote a probe byte (the test pipe's own label permits it)");
        }
        if (impersonating) RevertToSelf();
    }

    if (server != INVALID_HANDLE_VALUE && client != INVALID_HANDLE_VALUE) {
        char readBuf[8]{}; DWORD read = 0;
        TCheck(ReadFile(server, readBuf, sizeof(readBuf), &read, nullptr) == TRUE && read > 0,
               "  (setup) server read the probe byte");
        TCheck(!DevToolsTrust_VerifyPipeClient(server), "  a lower-integrity-level client is REFUSED");
        CheckVerifierReverted();
    }
    if (client != INVALID_HANDLE_VALUE) CloseHandle(client);
    if (server != INVALID_HANDLE_VALUE) CloseHandle(server);
    if (sd) LocalFree(sd);
    CloseHandle(lowToken);
}

int RunTrustTests()
{
    std::printf("== DevToolsTrust posture + pipe security ==\n");
    TestAccessOrderingAndTokens();
    TestParseInitFailsClosed();
    TestMutationEnvFloorCapsMutationOnly();
    TestMutationEnvDeniedReadsRealEnvironment();
    TestPostureInitializationIsImmutable();
    TestPipeSddlIsMinimalAndLabeled();
    TestCurrentOwnerAndIntegritySidsResolve();
    TestCreatePipeSecurityDescriptorMatchesCurrentProcess();
    TestProtectedPipeAcceptsMinimalRightsClient();
    TestVerifyPipeClientAcceptsSameUserSameLevel(false);
    TestVerifyPipeClientAcceptsSameUserSameLevel(true);
    TestVerifyPipeClientRejectsInvalidPipeWithoutLeaking();
    TestVerifyPipeClientRejectsLowerIntegrityLevel();
    return g_trustFailures;
}
