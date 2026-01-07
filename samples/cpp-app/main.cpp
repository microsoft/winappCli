#include <iostream>
#include <windows.h>
#include <appmodel.h>

#pragma comment(lib, "kernel32.lib")

int main() {
    UINT32 length = 0;
    LONG result = GetCurrentPackageFamilyName(&length, nullptr);
    
    if (result == ERROR_INSUFFICIENT_BUFFER) {
        // We have a package identity, get the family name
        wchar_t* familyName = new wchar_t[length];
        result = GetCurrentPackageFamilyName(&length, familyName);
        
        if (result == ERROR_SUCCESS) {
            std::wcout << L"Package Family Name: " << familyName << std::endl;
            delete[] familyName;
            return 0;
        }
        
        delete[] familyName;
    }
    
    // No package identity or error
    std::cout << "Not packaged" << std::endl;
    return 0;
}