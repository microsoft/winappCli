// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsTrust.h"
#include "DevToolsProtocol.h" // DevToolsJson / DevToolsJsonParse -- reused to read the injector's init blob

#include <sddl.h>
#include <atomic>
#include <algorithm>
#include <vector>

namespace {
constexpr int kUninitializedPosture = -1;
std::atomic<int> g_posture{ kUninitializedPosture };
} // namespace

bool DevToolsTrust_InitializePosture(DevToolsAccess posture)
{
    int expected = kUninitializedPosture;
    return g_posture.compare_exchange_strong(
        expected, static_cast<int>(posture), std::memory_order_acq_rel);
}

DevToolsAccess DevToolsTrust_CurrentPosture()
{
    const int posture = g_posture.load(std::memory_order_acquire);
    return posture == kUninitializedPosture ? DevToolsAccess::Read : static_cast<DevToolsAccess>(posture);
}

bool DevToolsAccess_Satisfies(DevToolsAccess posture, DevToolsAccess required)
{
    return static_cast<int>(posture) >= static_cast<int>(required);
}

bool DevToolsTrust_MutationEnabled()
{
    return DevToolsTrust_CurrentPosture() == DevToolsAccess::Mutation;
}

bool DevToolsTrust_MutationEnvDenied()
{
    wchar_t buf[16];
    DWORD n = GetEnvironmentVariableW(L"WINAPP_DEVTOOLS_MUTATION", buf, _countof(buf));
    return n > 0 && n < _countof(buf) && _wcsicmp(buf, L"deny") == 0;
}

bool DevToolsTrust_ValidGuestContext(const std::wstring& start, const std::wstring& binding,
    const std::wstring& epoch, bool allowUnavailable)
{
    return !start.empty() && start.front() != L'0' && start.size() <= 19 &&
        (start.size() < 19 || start <= L"9223372036854775807") &&
        std::all_of(start.begin(), start.end(), [](wchar_t c) { return c >= L'0' && c <= L'9'; }) &&
        ((allowUnavailable && binding.empty()) ||
            (binding.size() == 32 && std::all_of(binding.begin(), binding.end(), [](wchar_t c) {
                return (c >= L'0' && c <= L'9') || (c >= L'a' && c <= L'f') || (c >= L'A' && c <= L'F');
            }))) && !epoch.empty();
}

DevToolsTrustInit DevToolsTrust_ParseInit(const std::wstring& initializationDataJson, bool mutationEnvDenied)
{
    DevToolsTrustInit result; // fail-closed default: Read, no cliExe

    DevToolsJson root;
    if (!initializationDataJson.empty() && DevToolsJsonParse(initializationDataJson, root) && root.IsObject()) {
        const DevToolsJson* version = root.Find(L"version");
        const bool versionOk = version && version->type == DevToolsJsonType::Number && version->num == 1.0;
        if (versionOk) {
            DevToolsAccess posture;
            if (DevToolsAccessFromToken(root.GetString(L"posture"), &posture)) {
                result.posture = posture;
                result.cliExe = root.GetString(L"cliExe");
                result.guestComments = root.Find(L"guestCommentStart") != nullptr;
                result.guestCommentStart = root.GetString(L"guestCommentStart");
                result.guestCommentBinding = root.GetString(L"guestCommentBinding");
                result.guestCommentEpoch = root.GetString(L"guestCommentEpoch");
                if (const auto sibling = root.Find(L"cliSibling")) {
                    const auto start = root.Find(L"guestCommentStart");
                    const auto binding = root.Find(L"guestCommentBinding");
                    const auto epoch = root.Find(L"guestCommentEpoch");
                    if (sibling->type != DevToolsJsonType::Bool || !sibling->b ||
                        root.Find(L"cliExe") || !start || start->type != DevToolsJsonType::String ||
                        !binding || binding->type != DevToolsJsonType::String ||
                        !epoch || epoch->type != DevToolsJsonType::String ||
                        !DevToolsTrust_ValidGuestContext(result.guestCommentStart, result.guestCommentBinding,
                            result.guestCommentEpoch, true)) {
                        result = {};
                        result.guestInitializationError = L"invalid-sibling-writer";
                    } else {
                        result.cliSibling = true;
                    }
                }
            }
        }
    }

    if (mutationEnvDenied && result.posture == DevToolsAccess::Mutation) {
        result.posture = DevToolsAccess::Ui; // the deny floor caps mutation to ui; the read-only UI still renders
    }
    return result;
}

std::wstring DevToolsTrust_BuildPipeSddl(const std::wstring& ownerSid, const std::wstring& integritySid, DWORD rights)
{
    wchar_t rightsHex[16];
    swprintf_s(rightsHex, L"0x%x", rights);
    std::wstring sddl = L"D:P(A;;";
    sddl += rightsHex;
    sddl += L";;;";
    sddl += ownerSid;
    sddl += L")S:(ML;;NWNRNX;;;";
    sddl += integritySid;
    sddl += L")";
    return sddl;
}

namespace {

bool QueryTokenSid(HANDLE token, TOKEN_INFORMATION_CLASS infoClass, std::vector<BYTE>* buf, PSID* outSid)
{
    DWORD len = 0;
    GetTokenInformation(token, infoClass, nullptr, 0, &len);
    if (!len) return false;
    buf->resize(len);
    if (!GetTokenInformation(token, infoClass, buf->data(), len, &len)) return false;
    *outSid = (infoClass == TokenUser)
        ? reinterpret_cast<TOKEN_USER*>(buf->data())->User.Sid
        : reinterpret_cast<TOKEN_MANDATORY_LABEL*>(buf->data())->Label.Sid;
    return true;
}

bool SidToString(PSID sid, std::wstring* outSid)
{
    LPWSTR sidStr = nullptr;
    if (!ConvertSidToStringSidW(sid, &sidStr)) return false;
    *outSid = sidStr;
    LocalFree(sidStr);
    return true;
}

bool IntegrityRid(HANDLE token, DWORD* outLevel)
{
    std::vector<BYTE> buf;
    PSID sid = nullptr;
    if (!QueryTokenSid(token, TokenIntegrityLevel, &buf, &sid)) return false;
    UCHAR count = *GetSidSubAuthorityCount(sid);
    if (count == 0) return false;
    *outLevel = *GetSidSubAuthority(sid, count - 1);
    return true;
}

} // namespace

bool DevToolsTrust_CurrentOwnerSid(std::wstring* outSid)
{
    if (!outSid) return false;
    HANDLE tok = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok)) return false;
    std::vector<BYTE> buf;
    PSID sid = nullptr;
    bool ok = QueryTokenSid(tok, TokenUser, &buf, &sid) && SidToString(sid, outSid);
    CloseHandle(tok);
    return ok;
}

bool DevToolsTrust_CurrentIntegritySid(std::wstring* outSid)
{
    if (!outSid) return false;
    HANDLE tok = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok)) return false;
    std::vector<BYTE> buf;
    PSID sid = nullptr;
    bool ok = QueryTokenSid(tok, TokenIntegrityLevel, &buf, &sid) && SidToString(sid, outSid);
    CloseHandle(tok);
    return ok;
}

PSECURITY_DESCRIPTOR DevToolsTrust_CreatePipeSecurityDescriptor()
{
    std::wstring ownerSid, integritySid;
    if (!DevToolsTrust_CurrentOwnerSid(&ownerSid) || !DevToolsTrust_CurrentIntegritySid(&integritySid)) return nullptr;

    std::wstring sddl = DevToolsTrust_BuildPipeSddl(ownerSid, integritySid, kDevToolsPipeOwnerRights);
    PSECURITY_DESCRIPTOR sd = nullptr;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl.c_str(), SDDL_REVISION_1, &sd, nullptr)) {
        return nullptr;
    }
    return sd;
}

bool DevToolsTrust_VerifyPipeClient(HANDLE pipe)
{
    // Identification-level impersonation cannot open the server's primary token.
    // Identification-level impersonation cannot open the server primary token; capture self before impersonating.
    HANDLE selfToken = nullptr;
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &selfToken)) return false;
    std::vector<BYTE> selfUserBuf;
    PSID selfSid = nullptr;
    DWORD selfIL = 0;
    const bool selfOk = QueryTokenSid(selfToken, TokenUser, &selfUserBuf, &selfSid) &&
                        IntegrityRid(selfToken, &selfIL);
    CloseHandle(selfToken);
    if (!selfOk || !ImpersonateNamedPipeClient(pipe)) return false;

    bool ok = false;
    HANDLE clientToken = nullptr;
    if (OpenThreadToken(GetCurrentThread(), TOKEN_QUERY, TRUE, &clientToken)) {
        std::vector<BYTE> clientUserBuf;
        PSID clientSid = nullptr;
        DWORD clientIL = 0;
        if (QueryTokenSid(clientToken, TokenUser, &clientUserBuf, &clientSid) &&
            EqualSid(clientSid, selfSid) &&
            IntegrityRid(clientToken, &clientIL) &&
            clientIL >= selfIL) {
            ok = true;
        }
        CloseHandle(clientToken);
    }

    RevertToSelf();
    return ok;
}
