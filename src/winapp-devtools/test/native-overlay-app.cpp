// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <windows.h>
#include <shellapi.h>
#undef GetCurrentTime
#include <filesystem>
#include <fstream>
#include <string>
#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.h>
#include <winrt/Windows.UI.Xaml.Interop.h>
#include <winrt/Microsoft.UI.Windowing.h>
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Controls.Primitives.h>
#include <winrt/Microsoft.UI.Xaml.Markup.h>
#include <winrt/Microsoft.UI.Xaml.Media.h>
#include <winrt/Microsoft.UI.Xaml.XamlTypeInfo.h>

using namespace winrt;
using namespace Microsoft::UI::Xaml;
using namespace Microsoft::UI::Xaml::Controls;

static std::filesystem::path report;
static bool withFluentResources = false;

struct App : ApplicationT<App, Markup::IXamlMetadataProvider>
{
    Markup::IXamlType GetXamlType(Windows::UI::Xaml::Interop::TypeName const& type)
    {
        return withFluentResources ? Metadata().GetXamlType(type) : nullptr;
    }
    Markup::IXamlType GetXamlType(hstring const& type)
    {
        return withFluentResources ? Metadata().GetXamlType(type) : nullptr;
    }
    com_array<Markup::XmlnsDefinition> GetXmlnsDefinitions()
    {
        return withFluentResources ? Metadata().GetXmlnsDefinitions() : com_array<Markup::XmlnsDefinition>{};
    }
    XamlTypeInfo::XamlControlsXamlMetaDataProvider const& Metadata()
    {
        if (!metadata) metadata = XamlTypeInfo::XamlControlsXamlMetaDataProvider();
        return metadata;
    }

    void OnLaunched(LaunchActivatedEventArgs const&)
    {
        SetEnvironmentVariableW(L"WINAPP_DEVTOOLS_LOG", L"1");
        if (withFluentResources) Resources().MergedDictionaries().Append(XamlControlsResources());
        ProbeMarkup("literal-brush", LR"(<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="#FF262220"/>)");
        ProbeMarkup("theme-brush", LR"(<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="{ThemeResource AcrylicBackgroundFillColorDefaultBrush}"/>)");
        ProbeMarkup("text-style", LR"(<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Style="{StaticResource CaptionTextBlockStyle}"/>)");
        ProbeMarkup("local-controls-resources", LR"(<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" xmlns:controls="using:Microsoft.UI.Xaml.Controls"><Grid.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><controls:XamlControlsResources/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Grid.Resources><Border Background="{ThemeResource AcrylicBackgroundFillColorDefaultBrush}"/></Grid>)");
        ProbeMarkup("local-theme-source", LR"(<Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"><Grid.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries><ResourceDictionary Source="ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml"/></ResourceDictionary.MergedDictionaries></ResourceDictionary></Grid.Resources><Border Background="{ThemeResource AcrylicBackgroundFillColorDefaultBrush}"><TextBlock Style="{StaticResource BodyStrongTextBlockStyle}" Foreground="{ThemeResource TextFillColorPrimaryBrush}"/></Border></Grid>)");
        ProbeMarkup("theme-brush-after-local-source", LR"(<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="{ThemeResource AcrylicBackgroundFillColorDefaultBrush}"/>)");
        ProbeMarkup("core-theme-brush", LR"(<Border xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Background="{ThemeResource SystemControlBackgroundChromeMediumLowBrush}"/>)");
        ProbeMarkup("strong-text-style", LR"(<TextBlock xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" Style="{StaticResource BodyStrongTextBlockStyle}"/>)");
        ProbeMarkup("mica-type", LR"(<MicaBackdrop xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"/>)");
        window = Window();
        window.Title(L"WinApp owned native overlay " + std::to_wstring(GetCurrentProcessId()));
        auto panel = StackPanel();
        panel.Name(L"NativeRoot");
        panel.Padding(ThicknessHelper::FromUniformLength(24));
        panel.Spacing(16);
        auto text = TextBlock();
        text.Name(L"NativeLabel");
        text.Text(L"Owned C++/WinRT overlay fixture");
        text.FontSize(24);
        auto button = Button();
        button.Name(L"NativeButton");
        button.Content(box_value(L"Owned fixture button"));
        panel.Children().Append(text);
        panel.Children().Append(button);
        window.Content(panel);
        window.AppWindow().Resize({ 850, 650 });
        window.AppWindow().Show(false);

        HWND owned = nullptr;
        EnumWindows([](HWND candidate, LPARAM state) -> BOOL {
            DWORD pid = 0;
            GetWindowThreadProcessId(candidate, &pid);
            wchar_t title[256]{};
            GetWindowTextW(candidate, title, ARRAYSIZE(title));
            if (pid == GetCurrentProcessId() &&
                std::wstring(title).rfind(L"WinApp owned native overlay ", 0) == 0) {
                *reinterpret_cast<HWND*>(state) = candidate;
                return FALSE;
            }
            return TRUE;
        }, reinterpret_cast<LPARAM>(&owned));
        check_bool(owned != nullptr);
        std::ofstream(report) << "{\"pid\":" << GetCurrentProcessId()
            << ",\"window\":" << reinterpret_cast<uintptr_t>(owned) << "}";

        timer.Interval(std::chrono::milliseconds(100));
        timer.Tick([this](auto&&, auto&&) {
            if (std::filesystem::exists(report.wstring() + L".exit")) {
                timer.Stop();
                window.Close();
                Exit();
                return;
            }
            if (!std::filesystem::exists(report.wstring() + L".snapshot")) return;
            std::filesystem::remove(report.wstring() + L".snapshot");
            bool toolbar = false;
            auto root = window.Content().as<FrameworkElement>().XamlRoot();
            for (auto const& popup : Media::VisualTreeHelper::GetOpenPopupsForXamlRoot(root)) {
                toolbar = toolbar || HasToolbar(popup.Child());
            }
            std::ofstream(report.wstring() + L".snapshot.json")
                << "{\"toolbarInOpenPopup\":" << (toolbar ? "true" : "false") << "}";
        });
        timer.Start();
    }

    static bool HasToolbar(DependencyObject const& node, unsigned depth = 0)
    {
        if (!node || depth > 40) return false;
        if (auto element = node.try_as<FrameworkElement>();
            element && element.Name() == L"DevToolsRoot" && element.IsLoaded() &&
            element.Visibility() == Visibility::Visible) return true;
        for (int index = 0; index < Media::VisualTreeHelper::GetChildrenCount(node); ++index) {
            if (HasToolbar(Media::VisualTreeHelper::GetChild(node, index), depth + 1)) return true;
        }
        return false;
    }

    static void ProbeMarkup(const char* name, const wchar_t* markup)
    {
        std::ofstream log(report.wstring() + L".xaml-probes.txt", std::ios::app);
        try {
            Markup::XamlReader::Load(markup);
            log << name << ": success\n";
        }
        catch (hresult_error const& error) {
            log << name << ": 0x" << std::hex << static_cast<uint32_t>(error.code())
                << " " << to_string(error.message()) << "\n";
        }
    }

    Window window{ nullptr };
    XamlTypeInfo::XamlControlsXamlMetaDataProvider metadata{ nullptr };
    DispatcherTimer timer;
};

int WINAPI wWinMain(HINSTANCE, HINSTANCE, LPWSTR, int)
{
    int count = 0;
    auto arguments = CommandLineToArgvW(GetCommandLineW(), &count);
    if (!arguments) return ERROR_INVALID_PARAMETER;
    if (count == 2 || (count == 3 && std::wstring(arguments[2]) == L"--with-fluent-resources")) {
        report = arguments[1];
        withFluentResources = count == 3;
    }
    LocalFree(arguments);
    if (!report.is_absolute()) return ERROR_INVALID_PARAMETER;
    try {
        init_apartment(apartment_type::single_threaded);
        Application::Start([](auto&&) { make<App>(); });
        return 0;
    }
    catch (hresult_error const& error) {
        std::ofstream(report.wstring() + L".error.txt")
            << "0x" << std::hex << static_cast<uint32_t>(error.code()) << " " << to_string(error.message());
        return error.code();
    }
}
