// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <windows.h>
#include <string>

static DWORD writerPid;
static unsigned writerSpawns;
static std::wstring writerCommand;
static DWORD WINAPI WriterPid() { return writerPid ? writerPid : GetCurrentProcessId(); }
static BOOL WINAPI ObserveWriter(LPCWSTR, LPWSTR command, LPSECURITY_ATTRIBUTES, LPSECURITY_ATTRIBUTES,
    BOOL, DWORD, LPVOID, LPCWSTR, LPSTARTUPINFOW, LPPROCESS_INFORMATION)
{
    ++writerSpawns;
    writerCommand = command;
    SetLastError(ERROR_CANCELLED);
    return FALSE; // Observe the actual spawn boundary without starting a process or timer.
}
#define CreateProcessW ObserveWriter
#define GetCurrentProcessId WriterPid
#include "../native/WinApp.DevTools.Native/DevToolsOverlay.cpp"
#undef GetCurrentProcessId
#undef CreateProcessW

void OverlayFixtureSeedHighlight(InstanceHandle handle)
{
    g_lastHighlightHandle = handle;
}

// The overlay host falls back to Popups whenever any step of the UI-layer lookup does not answer, or when forced.
bool OverlayFixtureHostFallsBack()
{
    const bool layer = HostUseLayer(false, true, S_OK, S_OK, true);
    const bool fallbacks = !HostUseLayer(true, true, S_OK, S_OK, true) &&
        !HostUseLayer(false, false, S_OK, S_OK, true) &&
        !HostUseLayer(false, true, E_NOINTERFACE, S_OK, true) &&
        !HostUseLayer(false, true, S_OK, E_NOINTERFACE, true) &&
        !HostUseLayer(false, true, S_OK, S_OK, false);
    DevToolsOverlay_ForcePopupHost(true);
    const bool forced = DevToolsOverlay_IsPopupHostForced() && !LayerRootFor(reinterpret_cast<IXamlDiagnostics*>(1),
        reinterpret_cast<IInspectable*>(1));
    DevToolsOverlay_ForcePopupHost(false);
    return layer && fallbacks && forced && std::wstring(DevToolsOverlay_HostPath()) == L"none";
}

// Ctrl+Shift+F12 whose modifier key-downs reach the app after the F12 (the text-field case) still counts; an F12
// followed by anything else, or by the modifiers too late, does not.
bool OverlayFixtureLateShortcut()
{
    const bool bare = !IsToolbarShortcut(VK_F12);          // no modifier is down in the test process
    const bool late = !IsLateToolbarShortcut(VK_SHIFT) && IsLateToolbarShortcut(VK_CONTROL);
    const bool once = !IsLateToolbarShortcut(VK_SHIFT) && !IsLateToolbarShortcut(VK_CONTROL);
    IsToolbarShortcut(VK_F12);
    const bool interrupted = !IsLateToolbarShortcut(L'A') && !IsLateToolbarShortcut(VK_CONTROL) && !IsLateToolbarShortcut(VK_SHIFT);
    IsToolbarShortcut(VK_F12);
    g_bareF12At -= 1000;
    const bool expired = !IsLateToolbarShortcut(VK_CONTROL) && !IsLateToolbarShortcut(VK_SHIFT);
    // Ctrl reached the app before the F12 and Shift after it.
    BYTE keys[256]{};
    GetKeyboardState(keys);
    keys[VK_CONTROL] = 0x80;
    SetKeyboardState(keys);
    const bool ctrlFirst = !IsToolbarShortcut(VK_F12);
    keys[VK_CONTROL] = 0;
    SetKeyboardState(keys);
    const bool split = ctrlFirst && IsLateToolbarShortcut(VK_SHIFT);
    return bare && late && once && interrupted && expired && split;
}

std::vector<InstanceHandle> OverlayFixtureSurfaceCandidates()
{
    return g_surfaceRoots ? g_surfaceRoots() : std::vector<InstanceHandle>{};
}

bool OverlayFixtureDetachedDraftGuards()
{
    if (g_pickCatcher || g_selUi || g_selRootLost) return false;
    g_selRootLost = true;
    g_pickCatcher = reinterpret_cast<IInspectable*>(1); // Arm must refuse before accepting an existing catcher.
    const bool refused = !DevToolsOverlay_ArmPick();
    g_pickCatcher = nullptr;
    const bool retained = !DismissSelectionPanel() && g_selRootLost;
    OnSelCloseClick(nullptr, nullptr);
    return refused && retained && !g_selRootLost;
}

void CommentFixtureOpen(unsigned long long raw, bool composer)
{
    SetCommentTarget(raw, composer);
}

std::wstring CommentFixtureLaunch(DWORD pid, const wchar_t* exe, const wchar_t* source,
    const std::wstring& token, const std::wstring& text, bool composer, bool local, unsigned* spawns)
{
    writerPid = pid;
    writerSpawns = 0;
    writerCommand.clear();
    DevToolsOverlay_SetCliExe(exe);
    SetEnvironmentVariableW(L"WINAPP_DEVTOOLS_SOURCE_ROOT", source);
    if (!local) {
        size_t begin = token.find(L'.') + 1;
        const auto end = token.find(L'.', begin);
        const auto start = token.substr(begin, end - begin);
        begin = token.find(L'.', token.find(L'.', end + 1) + 1) + 1;
        const auto bindingEnd = token.find(L'.', begin);
        DevToolsOverlay_SetGuestComments(start, token.substr(begin, bindingEnd - begin), token.substr(bindingEnd + 1));
    }
    LaunchCommentAdd(text, L"note", composer);
    *spawns = writerSpawns;
    return writerCommand;
}
