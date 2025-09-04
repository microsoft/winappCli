#include <napi.h>
#include <shobjidl_core.h>
#include <windows.h>

#include <winrt/Windows.Foundation.h>
#include <winrt/Windows.UI.Notifications.h>
#include <winrt/Windows.Data.Xml.Dom.h>

using namespace winrt;
using namespace Windows::UI::Notifications;
using namespace Windows::Data::Xml::Dom;

// Function to display a Windows notification
void ShowNotification(const Napi::CallbackInfo& info) {
    Napi::Env env = info.Env();

    try {
        // Get arguments from JavaScript (title and message)
        std::string title = info[0].As<Napi::String>();
        std::string message = info[1].As<Napi::String>();

        // Define notification XML
        std::wstring xml = L"<toast><visual><binding template='ToastGeneric'><text>";
        xml += std::wstring(title.begin(), title.end());
        xml += L"</text><text>";
        xml += std::wstring(message.begin(), message.end());
        xml += L"</text></binding></visual></toast>";

        // Create a ToastNotificationManager
        ToastNotifier notifier = ToastNotificationManager::CreateToastNotifier();

        // Parse the XML
        XmlDocument toastXml;
        toastXml.LoadXml(xml);
        
        // Create a toast notification
        ToastNotification toast{ toastXml };
        notifier.Show(toast);
    } catch (const winrt::hresult_error& ex) {
        Napi::Error::New(env, winrt::to_string(ex.message())).ThrowAsJavaScriptException();
    } catch (const std::exception& ex) {
        // Handle exceptions and throw back to JavaScript
        Napi::Error::New(env, ex.what()).ThrowAsJavaScriptException();
    } catch (...) {
        Napi::Error::New(env, "Unknown error occurred").ThrowAsJavaScriptException();
    }
}

// Initialize the module
Napi::Object Init(Napi::Env env, Napi::Object exports) {
    exports.Set(Napi::String::New(env, "showNotification"), Napi::Function::New(env, ShowNotification));
    return exports;
}

NODE_API_MODULE(addon, Init)