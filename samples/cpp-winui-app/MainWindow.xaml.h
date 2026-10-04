#pragma once

#include "MainWindow.g.h"

namespace winrt::CppWinUIApp::implementation
{
    struct MainWindow : MainWindowT<MainWindow>
    {
        MainWindow()
        {
            // Xaml objects should not call InitializeComponent during construction.
            // See https://github.com/microsoft/cppwinrt/tree/master/nuget#initializecomponent
        }

        void IdentityButton_Click(winrt::Windows::Foundation::IInspectable const& sender, winrt::Microsoft::UI::Xaml::RoutedEventArgs const& args);
    };
}

namespace winrt::CppWinUIApp::factory_implementation
{
    struct MainWindow : MainWindowT<MainWindow, implementation::MainWindow>
    {
    };
}
