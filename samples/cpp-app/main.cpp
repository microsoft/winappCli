#include <iostream>
#include <windows.h>
#include <appmodel.h>
#include <winrt/Microsoft.Windows.ApplicationModel.WindowsAppRuntime.h>

#pragma comment(lib, "kernel32.lib")

int main() {
    // Initialize WinRT
    winrt::init_apartment();
    
    UINT32 length = 0;
    LONG result = GetCurrentPackageFamilyName(&length, nullptr);
    
    if (result == ERROR_INSUFFICIENT_BUFFER) {
        // We have a package identity
        wchar_t* familyName = new wchar_t[length];
        result = GetCurrentPackageFamilyName(&length, familyName);
        
        if (result == ERROR_SUCCESS) {
            std::wcout << L"Package Family Name: " << familyName << std::endl;
            
            // Get Windows App Runtime version using the API
            auto runtimeVersion = winrt::Microsoft::Windows::ApplicationModel::WindowsAppRuntime::RuntimeInfo::AsString();
            std::wcout << L"Windows App Runtime Version: " << runtimeVersion.c_str() << std::endl;
            
            delete[] familyName;
            return 0;
        }
        
        delete[] familyName;
    }
    
    std::cout << "Not packaged" << std::endl;
    return 0;
}