#include "pch.h"
#include "MainWindow.xaml.h"
#if __has_include("MainWindow.g.cpp")
#include "MainWindow.g.cpp"
#endif

#include <winrt/Windows.ApplicationModel.h>

using namespace winrt;
using namespace Microsoft::UI::Xaml;

namespace winrt::CppWinUIApp::implementation
{
    void MainWindow::IdentityButton_Click(Windows::Foundation::IInspectable const&, RoutedEventArgs const&)
    {
        try
        {
            IdentityText().Text(L"Package family name: " + Windows::ApplicationModel::Package::Current().Id().FamilyName());
        }
        catch (hresult_error const&)
        {
            IdentityText().Text(L"Not running with package identity");
        }
    }
}
