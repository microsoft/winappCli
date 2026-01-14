#include <iostream>
#include <windows.h>
#include <appmodel.h>
#include <winrt/Microsoft.Windows.ApplicationModel.WindowsAppRuntime.h>

int main() {
    // Initialize WinRT
    winrt::init_apartment();
    
    UINT32 length = 0;
    LONG result = GetCurrentPackageFamilyName(&length, nullptr);
    
    if (result == ERROR_INSUFFICIENT_BUFFER) {
        // We have a package identity
        std::wstring familyName;
        familyName.resize(length);
        
        result = GetCurrentPackageFamilyName(&length, familyName.data());
        
        if (result == ERROR_SUCCESS) {
            std::wcout << L"Package Family Name: " << familyName.c_str() << std::endl;
            
            // Get Windows App Runtime version using the API
            auto runtimeVersion = winrt::Microsoft::Windows::ApplicationModel::WindowsAppRuntime::RuntimeInfo::AsString();
            std::wcout << L"Windows App Runtime Version: " << runtimeVersion.c_str() << std::endl;
            
            return 0;
        }
    }
    
    std::cout << "Not packaged" << std::endl;
    return 0;
}