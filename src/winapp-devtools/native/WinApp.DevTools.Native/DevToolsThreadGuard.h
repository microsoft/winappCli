#pragma once
#include <windows.h>

template <DWORD(WINAPI* Body)(LPVOID)>
DWORD WINAPI DevToolsThreadEntry(LPVOID param)
{
    // Thread entry points are hard C++ exception boundaries inside an injected DLL.
    try {
        return Body(param);
    } catch (...) {
        return 0;
    }
}
