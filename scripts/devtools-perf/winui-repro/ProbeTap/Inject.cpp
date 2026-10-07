// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Loads ReproTap.dll into a running WinUI 3 process with InitializeXamlDiagnosticsEx.
// Usage: Inject.exe <pid> <full path to ReproTap.dll> <full path to the target's Microsoft.Internal.FrameworkUdk.dll> <advise=0|advise=1>
// Prints the HRESULT (0 on success).
#include <windows.h>
#include <objbase.h>
#include <cstdio>
#include <cwchar>

extern "C" const CLSID CLSID_ReproTap = { 0x5E1F7A20, 0x3C4B, 0x4D7E, { 0x9A, 0x61, 0x2B, 0x8C, 0x0D, 0x41, 0x7E, 0x13 } };
typedef HRESULT (WINAPI *InitializeXamlDiagnosticsExFn)(LPCWSTR, DWORD, LPCWSTR, LPCWSTR, CLSID, LPCWSTR);

int wmain(int argc, wchar_t** argv)
{
    if (argc != 5) { fwprintf(stderr, L"usage: Inject <pid> <tap dll> <FrameworkUdk dll> <init data>\n"); return 2; }
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    // Load the UDK with its own folder and System32 as the search path for its dependencies.
    HMODULE udk = LoadLibraryExW(argv[3], nullptr, LOAD_LIBRARY_SEARCH_SYSTEM32 | LOAD_LIBRARY_SEARCH_DLL_LOAD_DIR);
    if (!udk) { fwprintf(stderr, L"LoadLibraryEx failed: %lu\n", GetLastError()); return 1; }
    auto init = reinterpret_cast<InitializeXamlDiagnosticsExFn>(GetProcAddress(udk, "InitializeXamlDiagnosticsEx"));
    if (!init) { fwprintf(stderr, L"InitializeXamlDiagnosticsEx is not exported\n"); return 1; }
    HRESULT hr = init(L"WinUIVisualDiagConnection1", wcstoul(argv[1], nullptr, 10), argv[3], argv[2], CLSID_ReproTap, argv[4]);
    wprintf(L"%ld\n", (long)hr);
    return 0;
}
