#include <windows.h>
#include <winrt/Windows.Foundation.h>
int WINAPI wWinMain(HINSTANCE, HINSTANCE, PWSTR, int) {
    winrt::init_apartment();
    return 0;
}
