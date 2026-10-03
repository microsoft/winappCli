// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <windows.h>
#include <winstring.h>   // HSTRING
#include <inspectable.h> // IInspectable
#include <roapi.h>       // RoGetActivationFactory (DevToolsActivate)
#include <algorithm>
#include <utility>
#include <vector>
#undef GetCurrentTime

#include <winrt/Windows.Foundation.Collections.h>
#include <winrt/Windows.Graphics.h>   // PointInt32 / RectInt32, the screen side of ContentCoordinateConverter
#include <winrt/Microsoft.UI.Content.h>
#include <winrt/Microsoft.UI.Dispatching.h>
#include <winrt/Microsoft.UI.Input.h>
#include <winrt/Microsoft.UI.Windowing.h>
#include <winrt/Microsoft.UI.Xaml.h>
#include <winrt/Microsoft.UI.Xaml.Automation.h>
#include <winrt/Microsoft.UI.Xaml.Controls.h>
#include <winrt/Microsoft.UI.Xaml.Controls.Primitives.h>
#include <winrt/Microsoft.UI.Xaml.Data.h>
#include <winrt/Microsoft.UI.Xaml.Documents.h>
#include <winrt/Microsoft.UI.Xaml.Input.h>
#include <winrt/Microsoft.UI.Xaml.Markup.h>
#include <winrt/Microsoft.UI.Xaml.Media.h>
#include <winrt/Microsoft.UI.Xaml.Media.Animation.h>

namespace DevToolsX   = winrt::Microsoft::UI::Xaml;
namespace DevToolsCnt = winrt::Microsoft::UI::Content;
namespace DevToolsXA  = winrt::Microsoft::UI::Xaml::Automation;
namespace DevToolsD   = winrt::Microsoft::UI::Dispatching;
namespace DevToolsIn  = winrt::Microsoft::UI::Input;
namespace DevToolsW   = winrt::Microsoft::UI::Windowing;
namespace DevToolsXC  = winrt::Microsoft::UI::Xaml::Controls;
namespace DevToolsXCP = winrt::Microsoft::UI::Xaml::Controls::Primitives;
namespace DevToolsXD  = winrt::Microsoft::UI::Xaml::Data;
namespace DevToolsXDoc = winrt::Microsoft::UI::Xaml::Documents;
namespace DevToolsXI  = winrt::Microsoft::UI::Xaml::Input;
namespace DevToolsXMk = winrt::Microsoft::UI::Xaml::Markup;
namespace DevToolsXM  = winrt::Microsoft::UI::Xaml::Media;
namespace DevToolsXMA = winrt::Microsoft::UI::Xaml::Media::Animation;

using DevToolsUIElementVector = winrt::Windows::Foundation::Collections::IVector<DevToolsX::UIElement>;
using DevToolsColumnDefinitionVector = winrt::Windows::Foundation::Collections::IVector<DevToolsXC::ColumnDefinition>;
using DevToolsInlineVector = winrt::Windows::Foundation::Collections::IVector<DevToolsXDoc::Inline>;

template <typename I>
inline winrt::impl::abi_t<I>* DevToolsAbi(void* itf) noexcept
{
    return static_cast<winrt::impl::abi_t<I>*>(itf);
}

inline void* DevToolsAbiStr(HSTRING h) noexcept { return reinterpret_cast<void*>(h); }
inline winrt::event_token* DevToolsAbiTok(__int64* t) noexcept { return reinterpret_cast<winrt::event_token*>(t); }
static_assert(sizeof(winrt::event_token) == sizeof(__int64), "event_token must be ABI-identical to the __int64 the tap passes");

template <typename I>
inline REFIID DevToolsIid() noexcept { return winrt::guid_of<I>(); }
static_assert(sizeof(winrt::guid) == sizeof(GUID), "winrt::guid must be ABI-identical to GUID");


inline HRESULT DevToolsNumberBoxGetValue(void* numberBox, double* value) noexcept
{ return DevToolsAbi<DevToolsXC::INumberBox>(numberBox)->get_Value(value); }
inline HRESULT DevToolsNumberBoxAddValueChanged(void* numberBox, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsXC::INumberBox>(numberBox)->add_ValueChanged(handler, DevToolsAbiTok(token)); }

inline HRESULT DevToolsRangeGetValue(void* range, double* value) noexcept
{ return DevToolsAbi<DevToolsXCP::IRangeBase>(range)->get_Value(value); }
inline HRESULT DevToolsRangeAddValueChanged(void* range, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsXCP::IRangeBase>(range)->add_ValueChanged(handler, DevToolsAbiTok(token)); }

inline HRESULT DevToolsWinPutContent(void* win, void* uiElement) noexcept
{ return DevToolsAbi<DevToolsX::IWindow>(win)->put_Content(uiElement); }
inline HRESULT DevToolsWinPutTitle(void* win, HSTRING title) noexcept
{ return DevToolsAbi<DevToolsX::IWindow>(win)->put_Title(DevToolsAbiStr(title)); }
inline HRESULT DevToolsWinActivate(void* win) noexcept
{ return DevToolsAbi<DevToolsX::IWindow>(win)->Activate(); }
inline HRESULT DevToolsWinClose(void* win) noexcept
{ return DevToolsAbi<DevToolsX::IWindow>(win)->Close(); }
inline HRESULT DevToolsWinGetAppWindow(void* win2, ::IInspectable** appWindow) noexcept
{ return DevToolsAbi<DevToolsX::IWindow2>(win2)->get_AppWindow(reinterpret_cast<void**>(appWindow)); }
inline HRESULT DevToolsWinPutSystemBackdrop(void* win2, void* backdrop) noexcept
{ return DevToolsAbi<DevToolsX::IWindow2>(win2)->put_SystemBackdrop(backdrop); }

inline HRESULT DevToolsAppWindowGetFromWindowId(void* statics, unsigned __int64 windowId, ::IInspectable** appWindow) noexcept
{
    winrt::impl::abi_t<winrt::Microsoft::UI::WindowId> id{ windowId };
    return DevToolsAbi<DevToolsW::IAppWindowStatics>(statics)->GetFromWindowId(id, reinterpret_cast<void**>(appWindow));
}
inline HRESULT DevToolsAppWindowGetId(void* appWindow, unsigned __int64* value) noexcept
{
    winrt::impl::abi_t<winrt::Microsoft::UI::WindowId> id{};
    HRESULT hr = DevToolsAbi<DevToolsW::IAppWindow>(appWindow)->get_Id(&id);
    *value = id.Value;
    return hr;
}
inline HRESULT DevToolsAppWindowGetTitleBar(void* appWindow, ::IInspectable** titleBar) noexcept
{ return DevToolsAbi<DevToolsW::IAppWindow>(appWindow)->get_TitleBar(reinterpret_cast<void**>(titleBar)); }
inline HRESULT DevToolsTitleBarGetExtendsContentIntoTitleBar(void* titleBar, bool* value) noexcept
{ return DevToolsAbi<DevToolsW::IAppWindowTitleBar>(titleBar)->get_ExtendsContentIntoTitleBar(value); }
inline HRESULT DevToolsTitleBarGetHeight(void* titleBar, int* physicalPx) noexcept
{ return DevToolsAbi<DevToolsW::IAppWindowTitleBar>(titleBar)->get_Height(reinterpret_cast<int32_t*>(physicalPx)); }
inline HRESULT DevToolsTitleBarGetLeftInset(void* titleBar, int* physicalPx) noexcept
{ return DevToolsAbi<DevToolsW::IAppWindowTitleBar>(titleBar)->get_LeftInset(reinterpret_cast<int32_t*>(physicalPx)); }
inline HRESULT DevToolsTitleBarGetRightInset(void* titleBar, int* physicalPx) noexcept
{ return DevToolsAbi<DevToolsW::IAppWindowTitleBar>(titleBar)->get_RightInset(reinterpret_cast<int32_t*>(physicalPx)); }
inline HRESULT DevToolsTitleBarPutPreferredTheme(void* titleBar3, DevToolsW::TitleBarTheme theme) noexcept
{ return DevToolsAbi<DevToolsW::IAppWindowTitleBar3>(titleBar3)->put_PreferredTheme(static_cast<int32_t>(theme)); }

inline HRESULT DevToolsFindName(void* fe, HSTRING name, ::IInspectable** found) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->FindName(DevToolsAbiStr(name), reinterpret_cast<void**>(found)); }
inline HRESULT DevToolsPutMargin(void* fe, double left, double top, double right, double bottom) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->put_Margin({ left, top, right, bottom }); }
inline HRESULT DevToolsGetMargin(void* fe, double* l, double* t, double* r, double* b) noexcept
{
    winrt::impl::abi_t<DevToolsX::Thickness> th{};
    HRESULT hr = DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->get_Margin(&th);
    if (SUCCEEDED(hr)) { *l = th.Left; *t = th.Top; *r = th.Right; *b = th.Bottom; }
    return hr;
}
template <typename I>
inline HRESULT DevToolsGetPaddingAs(void* p, double* l, double* t, double* r, double* b) noexcept
{
    winrt::impl::abi_t<DevToolsX::Thickness> th{};
    HRESULT hr = DevToolsAbi<I>(p)->get_Padding(&th);
    if (SUCCEEDED(hr)) { *l = th.Left; *t = th.Top; *r = th.Right; *b = th.Bottom; }
    return hr;
}
// ICustomPropertyProvider permits native lookup of named CLR properties, not enumeration.
inline HRESULT DevToolsCppGetCustomProperty(void* provider, HSTRING name, ::IInspectable** prop) noexcept
{ return DevToolsAbi<DevToolsXD::ICustomPropertyProvider>(provider)->GetCustomProperty(DevToolsAbiStr(name), reinterpret_cast<void**>(prop)); }

inline HRESULT DevToolsCppGetTypeName(void* provider, HSTRING* name) noexcept
{
    winrt::impl::abi_t<winrt::Windows::UI::Xaml::Interop::TypeName> tn{};
    const HRESULT hr = DevToolsAbi<DevToolsXD::ICustomPropertyProvider>(provider)->get_Type(&tn);
    if (name) *name = reinterpret_cast<HSTRING>(tn.Name);
    return hr;
}

inline HRESULT DevToolsCpGetValue(void* customProperty, ::IInspectable* target, ::IInspectable** value) noexcept
{ return DevToolsAbi<DevToolsXD::ICustomProperty>(customProperty)->GetValue(target, reinterpret_cast<void**>(value)); }
inline HRESULT DevToolsCpGetCanRead(void* customProperty, bool* canRead) noexcept
{ return DevToolsAbi<DevToolsXD::ICustomProperty>(customProperty)->get_CanRead(canRead); }

inline HRESULT DevToolsGetDataContext(void* fe, ::IInspectable** value) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->get_DataContext(reinterpret_cast<void**>(value)); }

inline HRESULT DevToolsAddSizeChanged(void* fe, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->add_SizeChanged(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsRemoveSizeChanged(void* fe, __int64 token) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->remove_SizeChanged(*DevToolsAbiTok(&token)); }
inline HRESULT DevToolsGetActualWidth(void* fe, double* value) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->get_ActualWidth(value); }
inline HRESULT DevToolsGetActualHeight(void* fe, double* value) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->get_ActualHeight(value); }
inline HRESULT DevToolsGetIsLoaded(void* fe, bool* value) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->get_IsLoaded(value); }
inline HRESULT DevToolsGetOpacity(void* ui, double* value) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->get_Opacity(value); }
inline HRESULT DevToolsGetVisualParent(::IInspectable* element, ::IInspectable** parent) noexcept
{
    *parent = nullptr;
    winrt::com_ptr<::IInspectable> dependency;
    const HRESULT hr = element->QueryInterface(DevToolsIid<DevToolsX::IDependencyObject>(), dependency.put_void());
    if (FAILED(hr)) return hr;
    if (!dependency) return E_NOINTERFACE;
    try {
        auto statics = winrt::get_activation_factory<DevToolsXM::VisualTreeHelper, DevToolsXM::IVisualTreeHelperStatics>();
        return DevToolsAbi<DevToolsXM::IVisualTreeHelperStatics>(winrt::get_abi(statics))->GetParent(
            dependency.get(), reinterpret_cast<void**>(parent));
    } catch (const winrt::hresult_error& error) {
        return error.code();
    }
}
inline HRESULT DevToolsPutWidth(void* fe, double value) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->put_Width(value); }
inline HRESULT DevToolsPutHeight(void* fe, double value) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->put_Height(value); }

inline HRESULT DevToolsGetRequestedTheme(void* fe, int* theme) noexcept
{
    int32_t value = 0;
    HRESULT hr = DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->get_RequestedTheme(&value);
    if (SUCCEEDED(hr)) *theme = value;
    return hr;
}
inline HRESULT DevToolsPutRequestedTheme(void* fe, int theme) noexcept
{
    return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->put_RequestedTheme(theme);
}
inline HRESULT DevToolsGetActualTheme(void* fe, int* theme) noexcept
{
    int32_t value = 0;
    HRESULT hr = DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->get_ActualTheme(&value);
    if (SUCCEEDED(hr)) *theme = value;
    return hr;
}
inline HRESULT DevToolsAddActualThemeChanged(void* fe, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->add_ActualThemeChanged(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsRemoveActualThemeChanged(void* fe, __int64 token) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->remove_ActualThemeChanged(*DevToolsAbiTok(&token)); }

inline HRESULT DevToolsPutVisibility(void* ui, int visibility) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->put_Visibility(visibility); }
inline HRESULT DevToolsGetVisibility(void* ui, int* visibility) noexcept
{
    int32_t v = 1;
    HRESULT hr = DevToolsAbi<DevToolsX::IUIElement>(ui)->get_Visibility(&v);
    if (SUCCEEDED(hr)) *visibility = v;
    return hr;
}
inline HRESULT DevToolsGetXamlRoot(void* ui, ::IInspectable** xamlRoot) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->get_XamlRoot(reinterpret_cast<void**>(xamlRoot)); }

inline HRESULT DevToolsXamlRootGetSize(void* xamlRoot, float* widthDip, float* heightDip) noexcept
{
    winrt::Windows::Foundation::Size sz{};
    HRESULT hr = DevToolsAbi<DevToolsX::IXamlRoot>(xamlRoot)->get_Size(&sz);
    if (SUCCEEDED(hr)) { *widthDip = sz.Width; *heightDip = sz.Height; }
    return hr;
}
inline HRESULT DevToolsXamlRootGetIsHostVisible(void* xamlRoot, bool* visible) noexcept
{ return DevToolsAbi<DevToolsX::IXamlRoot>(xamlRoot)->get_IsHostVisible(visible); }
inline HRESULT DevToolsXamlRootGetContentIslandEnvironment(void* xamlRoot2, ::IInspectable** env) noexcept
{ return DevToolsAbi<DevToolsX::IXamlRoot2>(xamlRoot2)->get_ContentIslandEnvironment(reinterpret_cast<void**>(env)); }
inline HRESULT DevToolsContentIslandEnvironmentGetAppWindowId(void* env, unsigned __int64* windowId) noexcept
{
    winrt::impl::abi_t<winrt::Microsoft::UI::WindowId> id{};
    HRESULT hr = DevToolsAbi<DevToolsCnt::IContentIslandEnvironment>(env)->get_AppWindowId(&id);
    if (SUCCEEDED(hr)) *windowId = id.Value;
    return hr;
}
inline HRESULT DevToolsXamlRootGetCoordinateConverter(void* xamlRoot3, ::IInspectable** converter) noexcept
{ return DevToolsAbi<DevToolsX::IXamlRoot3>(xamlRoot3)->get_CoordinateConverter(reinterpret_cast<void**>(converter)); }
inline HRESULT DevToolsXamlRootGetContentIsland(void* xamlRoot4, ::IInspectable** island) noexcept
{ return DevToolsAbi<DevToolsX::IXamlRoot4>(xamlRoot4)->get_ContentIsland(reinterpret_cast<void**>(island)); }
inline HRESULT DevToolsContentIslandGetId(void* island, unsigned __int64* value) noexcept
{ return DevToolsAbi<DevToolsCnt::IContentIsland>(island)->get_Id(value); }
inline HRESULT DevToolsConverterScreenToLocal(void* converter, int screenX, int screenY, float* localX, float* localY) noexcept
{
    winrt::impl::abi_t<winrt::Windows::Graphics::PointInt32> p{ screenX, screenY };
    winrt::Windows::Foundation::Point out{};
    HRESULT hr = DevToolsAbi<DevToolsCnt::IContentCoordinateConverter>(converter)->ConvertScreenToLocalWithPoint(p, &out);
    if (SUCCEEDED(hr)) { *localX = out.X; *localY = out.Y; }
    return hr;
}
inline HRESULT DevToolsConverterLocalToScreen(void* converter, float x, float y, float w, float h,
                                         int* sx, int* sy, int* sw, int* sh) noexcept
{
    winrt::Windows::Foundation::Rect r{ x, y, w, h };
    winrt::impl::abi_t<winrt::Windows::Graphics::RectInt32> out{};
    HRESULT hr = DevToolsAbi<DevToolsCnt::IContentCoordinateConverter>(converter)->ConvertLocalToScreenWithRect(r, &out);
    if (SUCCEEDED(hr)) { *sx = out.X; *sy = out.Y; *sw = out.Width; *sh = out.Height; }
    return hr;
}
inline HRESULT DevToolsUpdateLayout(void* ui) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->UpdateLayout(); }
inline HRESULT DevToolsStartBringIntoView(void* ui) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->StartBringIntoView(); }
inline HRESULT DevToolsFocus(void* ui, int focusState, bool* succeeded) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->Focus(focusState, succeeded); }
inline HRESULT DevToolsTransformToVisual(void* ui, void* visual, void** generalTransform) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->TransformToVisual(visual, generalTransform); }
inline HRESULT DevToolsGetDesiredSize(void* ui, float* outW, float* outH) noexcept
{
    winrt::Windows::Foundation::Size s{};
    HRESULT hr = DevToolsAbi<DevToolsX::IUIElement>(ui)->get_DesiredSize(&s);
    if (SUCCEEDED(hr)) { *outW = s.Width; *outH = s.Height; }
    return hr;
}
inline HRESULT DevToolsGetRenderSize(void* ui, float* outW, float* outH) noexcept
{
    winrt::Windows::Foundation::Size s{};
    HRESULT hr = DevToolsAbi<DevToolsX::IUIElement>(ui)->get_RenderSize(&s);
    if (SUCCEEDED(hr)) { *outW = s.Width; *outH = s.Height; }
    return hr;
}
inline HRESULT DevToolsAddGotFocus(void* ui, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->add_GotFocus(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsAddLostFocus(void* ui, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->add_LostFocus(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsGetOriginalSource(void* routedEventArgs, ::IInspectable** source) noexcept
{ return DevToolsAbi<DevToolsX::IRoutedEventArgs>(routedEventArgs)->get_OriginalSource(reinterpret_cast<void**>(source)); }
inline HRESULT DevToolsAddPointerEntered(void* ui, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->add_PointerEntered(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsAddPointerExited(void* ui, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->add_PointerExited(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsAddPointerMoved(void* ui, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->add_PointerMoved(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsAddPointerPressed(void* ui, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->add_PointerPressed(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsAddPointerReleased(void* ui, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->add_PointerReleased(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsAddPreviewKeyDown(void* ui, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->add_PreviewKeyDown(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsRemovePreviewKeyDown(void* ui, __int64 token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->remove_PreviewKeyDown(*DevToolsAbiTok(&token)); }
inline HRESULT DevToolsRemoveGotFocus(void* ui, __int64 token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->remove_GotFocus(*DevToolsAbiTok(&token)); }
inline HRESULT DevToolsAddPointerWheelChanged(void* ui, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->add_PointerWheelChanged(handler, DevToolsAbiTok(token)); }

inline HRESULT DevToolsArgsGetCurrentPoint(void* args, void* relativeTo, ::IInspectable** point) noexcept
{ return DevToolsAbi<DevToolsXI::IPointerRoutedEventArgs>(args)->GetCurrentPoint(relativeTo, reinterpret_cast<void**>(point)); }
inline HRESULT DevToolsAddPointerCanceled(void* ui, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->add_PointerCanceled(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsAddPointerCaptureLost(void* ui, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsX::IUIElement>(ui)->add_PointerCaptureLost(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsArgsPutHandled(void* args, bool handled) noexcept
{ return DevToolsAbi<DevToolsXI::IPointerRoutedEventArgs>(args)->put_Handled(handled); }
inline HRESULT DevToolsPointGetProperties(void* point, ::IInspectable** props) noexcept
{ return DevToolsAbi<DevToolsIn::IPointerPoint>(point)->get_Properties(reinterpret_cast<void**>(props)); }
inline HRESULT DevToolsPointPropsGetWheelDelta(void* props, int* delta) noexcept
{ return DevToolsAbi<DevToolsIn::IPointerPointProperties>(props)->get_MouseWheelDelta(delta); }
inline HRESULT DevToolsPointPropsGetIsHorizontalWheel(void* props, bool* horizontal) noexcept
{ return DevToolsAbi<DevToolsIn::IPointerPointProperties>(props)->get_IsHorizontalMouseWheel(horizontal); }

inline HRESULT DevToolsPropertyValueCreateDouble(void* statics, double value, ::IInspectable** boxed) noexcept
{ return DevToolsAbi<winrt::Windows::Foundation::IPropertyValueStatics>(statics)->CreateDouble(value, reinterpret_cast<void**>(boxed)); }
inline HRESULT DevToolsPropertyValueCreateBoolean(void* statics, bool value, ::IInspectable** boxed) noexcept
{ return DevToolsAbi<winrt::Windows::Foundation::IPropertyValueStatics>(statics)->CreateBoolean(value, reinterpret_cast<void**>(boxed)); }
inline HRESULT DevToolsPropertyValueCreateString(void* statics, HSTRING value, ::IInspectable** boxed) noexcept
{ return DevToolsAbi<winrt::Windows::Foundation::IPropertyValueStatics>(statics)->CreateString(DevToolsAbiStr(value), reinterpret_cast<void**>(boxed)); }

inline HRESULT DevToolsScrollGetVerticalOffset(void* sv, double* value) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewer>(sv)->get_VerticalOffset(value); }
inline HRESULT DevToolsScrollGetHorizontalOffset(void* sv, double* value) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewer>(sv)->get_HorizontalOffset(value); }
inline HRESULT DevToolsScrollGetScrollableHeight(void* sv, double* value) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewer>(sv)->get_ScrollableHeight(value); }
inline HRESULT DevToolsScrollGetScrollableWidth(void* sv, double* value) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewer>(sv)->get_ScrollableWidth(value); }
inline HRESULT DevToolsScrollChangeView(void* sv, void* horizontal, void* vertical, void* zoom,
                                   bool disableAnimation, bool* changed) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewer>(sv)->ChangeViewWithOptionalAnimation(horizontal, vertical, zoom, disableAnimation, changed); }
inline HRESULT DevToolsScrollAddViewChanging(void* sv, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewer>(sv)->add_ViewChanging(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsScrollRemoveViewChanging(void* sv, __int64 token) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewer>(sv)->remove_ViewChanging(*DevToolsAbiTok(&token)); }
inline HRESULT DevToolsScrollAddViewChanged(void* sv, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewer>(sv)->add_ViewChanged(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsScrollRemoveViewChanged(void* sv, __int64 token) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewer>(sv)->remove_ViewChanged(*DevToolsAbiTok(&token)); }
inline HRESULT DevToolsViewChangedIsIntermediate(void* args, bool* value) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewerViewChangedEventArgs>(args)->get_IsIntermediate(value); }
inline HRESULT DevToolsViewChangingGetNextView(void* args, ::IInspectable** view) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewerViewChangingEventArgs>(args)->get_NextView(reinterpret_cast<void**>(view)); }
inline HRESULT DevToolsScrollViewGetHorizontalOffset(void* view, double* value) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewerView>(view)->get_HorizontalOffset(value); }
inline HRESULT DevToolsScrollViewGetVerticalOffset(void* view, double* value) noexcept
{ return DevToolsAbi<DevToolsXC::IScrollViewerView>(view)->get_VerticalOffset(value); }
using DevToolsViewChangingHandler = winrt::Windows::Foundation::EventHandler<DevToolsXC::ScrollViewerViewChangingEventArgs>;
using DevToolsViewChangedHandler  = winrt::Windows::Foundation::EventHandler<DevToolsXC::ScrollViewerViewChangedEventArgs>;

// Subscribe pointer/focus events by name, never by a caller-provided ABI slot.
enum class DevToolsUiEvent { PointerEntered, PointerExited, PointerMoved, PointerPressed, PointerReleased, GotFocus, LostFocus };

inline HRESULT DevToolsAddUiEvent(void* ui, DevToolsUiEvent which, void* handler, __int64* token) noexcept
{
    switch (which) {
    case DevToolsUiEvent::PointerEntered: return DevToolsAddPointerEntered(ui, handler, token);
    case DevToolsUiEvent::PointerExited:  return DevToolsAddPointerExited(ui, handler, token);
    case DevToolsUiEvent::PointerMoved:   return DevToolsAddPointerMoved(ui, handler, token);
    case DevToolsUiEvent::PointerPressed: return DevToolsAddPointerPressed(ui, handler, token);
    case DevToolsUiEvent::PointerReleased:return DevToolsAddPointerReleased(ui, handler, token);
    case DevToolsUiEvent::GotFocus:       return DevToolsAddGotFocus(ui, handler, token);
    case DevToolsUiEvent::LostFocus:      return DevToolsAddLostFocus(ui, handler, token);
    }
    return E_INVALIDARG;
}

inline const wchar_t* DevToolsUiEventName(DevToolsUiEvent which) noexcept
{
    switch (which) {
    case DevToolsUiEvent::PointerEntered: return L"PointerEntered";
    case DevToolsUiEvent::PointerExited:  return L"PointerExited";
    case DevToolsUiEvent::PointerMoved:   return L"PointerMoved";
    case DevToolsUiEvent::PointerPressed: return L"PointerPressed";
    case DevToolsUiEvent::PointerReleased:return L"PointerReleased";
    case DevToolsUiEvent::GotFocus:       return L"GotFocus";
    case DevToolsUiEvent::LostFocus:      return L"LostFocus";
    }
    return L"?";
}

inline HRESULT DevToolsPutText(void* textBlock, HSTRING text) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->put_Text(DevToolsAbiStr(text)); }
inline HRESULT DevToolsPutBackground(void* control, void* brush) noexcept
{ return DevToolsAbi<DevToolsXC::IControl>(control)->put_Background(brush); }
inline HRESULT DevToolsAddTextChanged(void* textBox, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBox>(textBox)->add_TextChanged(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsGetTextBoxText(void* textBox, HSTRING* text) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBox>(textBox)->get_Text(reinterpret_cast<void**>(text)); }
inline HRESULT DevToolsGetPlaceholderText(void* textBox, HSTRING* text) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBox>(textBox)->get_PlaceholderText(reinterpret_cast<void**>(text)); }
inline HRESULT DevToolsGetTextBoxHeader(void* textBox, IInspectable** value) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBox>(textBox)->get_Header(reinterpret_cast<void**>(value)); }
inline HRESULT DevToolsGetSuggestText(void* box, HSTRING* value) noexcept
{ return DevToolsAbi<DevToolsXC::IAutoSuggestBox>(box)->get_Text(reinterpret_cast<void**>(value)); }
inline HRESULT DevToolsGetSuggestPlaceholder(void* box, HSTRING* value) noexcept
{ return DevToolsAbi<DevToolsXC::IAutoSuggestBox>(box)->get_PlaceholderText(reinterpret_cast<void**>(value)); }
inline HRESULT DevToolsGetSuggestHeader(void* box, IInspectable** value) noexcept
{ return DevToolsAbi<DevToolsXC::IAutoSuggestBox>(box)->get_Header(reinterpret_cast<void**>(value)); }
inline HRESULT DevToolsGetExpanderHeader(void* expander, IInspectable** value) noexcept
{ return DevToolsAbi<DevToolsXC::IExpander>(expander)->get_Header(reinterpret_cast<void**>(value)); }
inline HRESULT DevToolsGetIsExpanded(void* expander, bool* value) noexcept
{ return DevToolsAbi<DevToolsXC::IExpander>(expander)->get_IsExpanded(value); }
inline HRESULT DevToolsGetIsSelected(void* item, bool* value) noexcept
{ return DevToolsAbi<DevToolsXCP::ISelectorItem>(item)->get_IsSelected(value); }
inline HRESULT DevToolsGetIsChecked(void* button, IInspectable** value) noexcept
{ return DevToolsAbi<DevToolsXCP::IToggleButton>(button)->get_IsChecked(reinterpret_cast<void**>(value)); }
inline HRESULT DevToolsPutTextBoxText(void* textBox, HSTRING text) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBox>(textBox)->put_Text(DevToolsAbiStr(text)); }
// The element with focus in `element`'s XamlRoot (caller releases), and whether it is `element` or inside it.
inline HRESULT DevToolsFocusedWithin(void* element, IInspectable** focused, bool* inside) noexcept
{
    *focused = nullptr; *inside = false;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, element);
        const auto root = object.as<DevToolsX::UIElement>();
        const auto xamlRoot = root.XamlRoot();
        if (!xamlRoot) return S_FALSE;
        auto node = DevToolsXI::FocusManager::GetFocusedElement(xamlRoot).try_as<DevToolsX::DependencyObject>();
        for (auto current = node; current; current = DevToolsXM::VisualTreeHelper::GetParent(current))
            if (current == root) { *inside = true; break; }
        if (node) *focused = static_cast<IInspectable*>(winrt::detach_abi(node));
        return S_OK;
    } catch (...) { return winrt::to_hresult(); }
}
// Focus `target` with pointer state, so the app shows no focus rectangle or accelerator tips. With no usable target,
// the first focusable element of `anyElement`'s window content takes it.
inline HRESULT DevToolsFocusWithPointer(void* target, void* anyElement, bool* moved) noexcept
{
    *moved = false;
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        DevToolsX::UIElement element{ nullptr };
        if (target) { winrt::copy_from_abi(object, target); element = object.try_as<DevToolsX::UIElement>(); }
        if (!element || !element.XamlRoot()) {
            winrt::copy_from_abi(object, anyElement);
            const auto xamlRoot = object.as<DevToolsX::UIElement>().XamlRoot();
            const auto content = xamlRoot ? xamlRoot.Content() : nullptr;
            element = content ? DevToolsXI::FocusManager::FindFirstFocusableElement(content).try_as<DevToolsX::UIElement>() : nullptr;
        }
        *moved = element && element.Focus(DevToolsX::FocusState::Pointer);
        return S_OK;
    } catch (...) { return winrt::to_hresult(); }
}
// Enter inserts a line break and long lines wrap; otherwise a single-line box.
inline HRESULT DevToolsPutTextBoxMultiline(void* textBox, bool multiline) noexcept
{
    const HRESULT hr = DevToolsAbi<DevToolsXC::ITextBox>(textBox)->put_AcceptsReturn(multiline);
    return FAILED(hr) ? hr : DevToolsAbi<DevToolsXC::ITextBox>(textBox)->put_TextWrapping(
        static_cast<int32_t>(multiline ? winrt::Microsoft::UI::Xaml::TextWrapping::Wrap : winrt::Microsoft::UI::Xaml::TextWrapping::NoWrap));
}
inline HRESULT DevToolsPutPlaceholderText(void* textBox, HSTRING text) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBox>(textBox)->put_PlaceholderText(DevToolsAbiStr(text)); }
inline HRESULT DevToolsPutTextFontWeight(void* textBlock, unsigned short weight) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->put_FontWeight({ weight }); }

inline HRESULT DevToolsGetTextBlockText(void* textBlock, HSTRING* text) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->get_Text(reinterpret_cast<void**>(text)); }
inline HRESULT DevToolsGetContentControlContent(void* contentControl, IInspectable** content) noexcept
{ return DevToolsAbi<DevToolsXC::IContentControl>(contentControl)->get_Content(reinterpret_cast<void**>(content)); }
inline HRESULT DevToolsGetContentPresenterContent(void* presenter, IInspectable** content) noexcept
{ return DevToolsAbi<DevToolsXC::IContentPresenter>(presenter)->get_Content(reinterpret_cast<void**>(content)); }
inline HRESULT DevToolsGetFontIconGlyph(void* fontIcon, HSTRING* glyph) noexcept
{ return DevToolsAbi<DevToolsXC::IFontIcon>(fontIcon)->get_Glyph(reinterpret_cast<void**>(glyph)); }
inline HRESULT DevToolsGetAutomationId(void* statics, void* dependencyObject, HSTRING* id) noexcept
{ return DevToolsAbi<DevToolsXA::IAutomationPropertiesStatics>(statics)->GetAutomationId(dependencyObject,
                                                                               reinterpret_cast<void**>(id)); }

// Activate directly through a cached runtime-class factory; avoid per-element markup parsing.
inline ::IInspectable* DevToolsActivate(const wchar_t* runtimeClass) noexcept
{
    static std::vector<std::pair<const wchar_t*, ::IActivationFactory*>> cache;
    ::IActivationFactory* fac = nullptr;
    for (const auto& e : cache) if (e.first == runtimeClass) { fac = e.second; break; }
    if (!fac) {
        HSTRING h = nullptr;
        if (FAILED(WindowsCreateString(runtimeClass, (UINT32)wcslen(runtimeClass), &h))) return nullptr;
        const HRESULT hr = RoGetActivationFactory(h, __uuidof(::IActivationFactory),
                                                  reinterpret_cast<void**>(&fac));
        WindowsDeleteString(h);
        if (FAILED(hr) || !fac) return nullptr;
        cache.push_back({ runtimeClass, fac });   // held for the process lifetime, like the class name
    }
    ::IInspectable* o = nullptr;
    if (FAILED(fac->ActivateInstance(&o))) return nullptr;
    return o;
}

inline HRESULT DevToolsPutStyle(void* fe, void* style) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->put_Style(style); }
inline HRESULT DevToolsGetStyle(void* fe, ::IInspectable** style) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->get_Style(reinterpret_cast<void**>(style)); }
inline HRESULT DevToolsPutHorizontalAlignment(void* fe, int alignment) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->put_HorizontalAlignment(alignment); }
inline HRESULT DevToolsPutVerticalAlignment(void* fe, int alignment) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->put_VerticalAlignment(alignment); }
inline HRESULT DevToolsPutMinWidth(void* fe, double value) noexcept
{ return DevToolsAbi<DevToolsX::IFrameworkElement>(fe)->put_MinWidth(value); }

inline HRESULT DevToolsPutBorderThickness(void* control, double l, double t, double r, double b) noexcept
{ return DevToolsAbi<DevToolsXC::IControl>(control)->put_BorderThickness({ l, t, r, b }); }
inline HRESULT DevToolsPutPadding(void* control, double l, double t, double r, double b) noexcept
{ return DevToolsAbi<DevToolsXC::IControl>(control)->put_Padding({ l, t, r, b }); }
inline HRESULT DevToolsPutHorizontalContentAlignment(void* control, int alignment) noexcept
{ return DevToolsAbi<DevToolsXC::IControl>(control)->put_HorizontalContentAlignment(alignment); }
inline HRESULT DevToolsPutContent(void* contentControl, ::IInspectable* content) noexcept
{ return DevToolsAbi<DevToolsXC::IContentControl>(contentControl)->put_Content(content); }
inline HRESULT DevToolsPutControlFontSize(void* control, double size) noexcept
{ return DevToolsAbi<DevToolsXC::IControl>(control)->put_FontSize(size); }

inline HRESULT DevToolsPutFontSize(void* textBlock, double size) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->put_FontSize(size); }
inline HRESULT DevToolsPutFontFamily(void* textBlock, void* fontFamily) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->put_FontFamily(fontFamily); }
inline HRESULT DevToolsGetFontFamily(void* textBlock, ::IInspectable** fontFamily) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->get_FontFamily(reinterpret_cast<void**>(fontFamily)); }
inline HRESULT DevToolsGetFontFamilySource(void* fontFamily, HSTRING* source) noexcept
{ return DevToolsAbi<DevToolsXM::IFontFamily>(fontFamily)->get_Source(reinterpret_cast<void**>(source)); }
inline HRESULT DevToolsPutTextForeground(void* textBlock, void* brush) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->put_Foreground(brush); }
inline HRESULT DevToolsGetTextForeground(void* textBlock, ::IInspectable** brush) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->get_Foreground(reinterpret_cast<void**>(brush)); }
inline HRESULT DevToolsGetFontSize(void* textBlock, double* size) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->get_FontSize(size); }
inline HRESULT DevToolsGetTextFontWeight(void* textBlock, unsigned short* weight) noexcept
{
    winrt::impl::abi_t<winrt::Windows::UI::Text::FontWeight> fw{};
    HRESULT hr = DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->get_FontWeight(&fw);
    if (SUCCEEDED(hr)) *weight = fw.Weight;
    return hr;
}
inline HRESULT DevToolsGetSolidColorBrushColor(void* brush, unsigned char* a, unsigned char* r,
                                          unsigned char* g, unsigned char* b) noexcept
{
    winrt::impl::abi_t<winrt::Windows::UI::Color> c{};
    HRESULT hr = DevToolsAbi<DevToolsXM::ISolidColorBrush>(brush)->get_Color(&c);
    if (SUCCEEDED(hr)) { *a = c.A; *r = c.R; *g = c.G; *b = c.B; }
    return hr;
}
inline HRESULT DevToolsPutTextTrimming(void* textBlock, int trimming) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->put_TextTrimming(trimming); }
inline HRESULT DevToolsPutTextWrapping(void* textBlock, int wrapping) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->put_TextWrapping(wrapping); }
inline HRESULT DevToolsPutFontStyle(void* textBlock, int style) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->put_FontStyle(style); }
inline HRESULT DevToolsGetInlines(void* textBlock, ::IInspectable** inlines) noexcept
{ return DevToolsAbi<DevToolsXC::ITextBlock>(textBlock)->get_Inlines(reinterpret_cast<void**>(inlines)); }

inline HRESULT DevToolsPutRunText(void* run, HSTRING text) noexcept
{ return DevToolsAbi<DevToolsXDoc::IRun>(run)->put_Text(DevToolsAbiStr(text)); }
inline HRESULT DevToolsPutTextElementForeground(void* textElement, void* brush) noexcept
{ return DevToolsAbi<DevToolsXDoc::ITextElement>(textElement)->put_Foreground(brush); }
inline HRESULT DevToolsPutTextElementFontStyle(void* textElement, int style) noexcept
{ return DevToolsAbi<DevToolsXDoc::ITextElement>(textElement)->put_FontStyle(style); }

inline HRESULT DevToolsGetBorderBackground(void* border, ::IInspectable** brush) noexcept
{ return DevToolsAbi<DevToolsXC::IBorder>(border)->get_Background(reinterpret_cast<void**>(brush)); }
// Border.Background has its own interface slot; the swatch fill represents the property value.
inline HRESULT DevToolsPutBorderBackground(void* border, void* brush) noexcept
{ return DevToolsAbi<DevToolsXC::IBorder>(border)->put_Background(brush); }

inline HRESULT DevToolsGridGetColumnDefinitions(void* grid, ::IInspectable** defs) noexcept
{ return DevToolsAbi<DevToolsXC::IGrid>(grid)->get_ColumnDefinitions(reinterpret_cast<void**>(defs)); }
inline HRESULT DevToolsColumnDefsAppend(void* defs, ::IInspectable* def) noexcept
{ return DevToolsAbi<DevToolsColumnDefinitionVector>(defs)->Append(def); }
inline HRESULT DevToolsInlinesAppend(void* inlines, ::IInspectable* run) noexcept
{ return DevToolsAbi<DevToolsInlineVector>(inlines)->Append(run); }
inline HRESULT DevToolsColumnDefPutWidth(void* columnDefinition, double value, int unitType) noexcept
{
    return DevToolsAbi<DevToolsXC::IColumnDefinition>(columnDefinition)->put_Width({ value, unitType });
}
inline HRESULT DevToolsGridSetColumn(void* gridStatics, ::IInspectable* element, int column) noexcept
{ return DevToolsAbi<DevToolsXC::IGridStatics>(gridStatics)->SetColumn(element, column); }

inline HRESULT DevToolsSetAutomationName(void* statics, void* dependencyObject, HSTRING name) noexcept
{ return DevToolsAbi<DevToolsXA::IAutomationPropertiesStatics>(statics)->SetName(dependencyObject, DevToolsAbiStr(name)); }
inline HRESULT DevToolsGetAutomationName(void* statics, void* dependencyObject, HSTRING* name) noexcept
{ return DevToolsAbi<DevToolsXA::IAutomationPropertiesStatics>(statics)->GetName(dependencyObject, reinterpret_cast<void**>(name)); }
inline HRESULT DevToolsSetAutomationId(void* statics, void* dependencyObject, HSTRING id) noexcept
{ return DevToolsAbi<DevToolsXA::IAutomationPropertiesStatics>(statics)->SetAutomationId(dependencyObject,
                                                                               DevToolsAbiStr(id)); }
inline HRESULT DevToolsSetLocalizedControlType(void* statics, void* dependencyObject, HSTRING value) noexcept
{ return DevToolsAbi<DevToolsXA::IAutomationPropertiesStatics>(statics)->SetLocalizedControlType(dependencyObject,
                                                                                       DevToolsAbiStr(value)); }
inline HRESULT DevToolsSetAutomationLevel(void* statics, void* dependencyObject, int level) noexcept
{ return DevToolsAbi<DevToolsXA::IAutomationPropertiesStatics>(statics)->SetLevel(dependencyObject, level); }
inline HRESULT DevToolsSetPositionInSet(void* statics, void* dependencyObject, int position) noexcept
{ return DevToolsAbi<DevToolsXA::IAutomationPropertiesStatics>(statics)->SetPositionInSet(dependencyObject, position); }
inline HRESULT DevToolsSetSizeOfSet(void* statics, void* dependencyObject, int size) noexcept
{ return DevToolsAbi<DevToolsXA::IAutomationPropertiesStatics>(statics)->SetSizeOfSet(dependencyObject, size); }

inline HRESULT DevToolsRepeaterPutItemsSource(void* repeater, ::IInspectable* source) noexcept
{ return DevToolsAbi<DevToolsXC::IItemsRepeater>(repeater)->put_ItemsSource(source); }
inline HRESULT DevToolsRepeaterPutItemTemplate(void* repeater, void* elementFactory) noexcept
{ return DevToolsAbi<DevToolsXC::IItemsRepeater>(repeater)->put_ItemTemplate(elementFactory); }
inline HRESULT DevToolsRepeaterTryGetElement(void* repeater, int index, ::IInspectable** element) noexcept
{ return DevToolsAbi<DevToolsXC::IItemsRepeater>(repeater)->TryGetElement(index, reinterpret_cast<void**>(element)); }
inline HRESULT DevToolsRepeaterGetElementIndex(void* repeater, ::IInspectable* element, int* index) noexcept
{ return DevToolsAbi<DevToolsXC::IItemsRepeater>(repeater)->GetElementIndex(element, index); }

inline HRESULT DevToolsGetArgsData(void* getArgs, ::IInspectable** data) noexcept
{ return DevToolsAbi<DevToolsX::IElementFactoryGetArgs>(getArgs)->get_Data(reinterpret_cast<void**>(data)); }
inline HRESULT DevToolsGetRecycleElement(void* recycleArgs, ::IInspectable** element) noexcept
{ return DevToolsAbi<DevToolsX::IElementFactoryRecycleArgs>(recycleArgs)->get_Element(reinterpret_cast<void**>(element)); }

inline HRESULT DevToolsGetContentTemplate(void* contentControl, ::IInspectable** tmpl) noexcept
{ return DevToolsAbi<DevToolsXC::IContentControl>(contentControl)->get_ContentTemplate(reinterpret_cast<void**>(tmpl)); }
inline HRESULT DevToolsTemplateLoadContent(void* dataTemplate, ::IInspectable** content) noexcept
{ return DevToolsAbi<DevToolsX::IDataTemplate>(dataTemplate)->LoadContent(reinterpret_cast<void**>(content)); }

inline HRESULT DevToolsUnboxInt32(::IInspectable* boxed, int* value) noexcept
{
    if (!boxed) return E_POINTER;
    void* ref = nullptr;
    if (FAILED(boxed->QueryInterface(DevToolsIid<winrt::Windows::Foundation::IReference<int32_t>>(), &ref)) || !ref)
        return E_NOINTERFACE;
    const HRESULT hr = DevToolsAbi<winrt::Windows::Foundation::IReference<int32_t>>(ref)->get_Value(value);
    reinterpret_cast<IUnknown*>(ref)->Release();
    return hr;
}

struct DevToolsElementFactoryBase : ::IInspectable
{
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID r, void** p) noexcept override
    {
        if (!p) return E_POINTER;
        if (r == __uuidof(::IUnknown) || r == __uuidof(::IInspectable) ||
            r == DevToolsIid<DevToolsX::IElementFactory>() || r == __uuidof(::IAgileObject)) { *p = this; return S_OK; }
        *p = nullptr; return E_NOINTERFACE;
    }
    ULONG STDMETHODCALLTYPE AddRef() noexcept override { return 2; }
    ULONG STDMETHODCALLTYPE Release() noexcept override { return 1; }
    HRESULT STDMETHODCALLTYPE GetIids(ULONG* c, IID** i) noexcept override { if (c) *c = 0; if (i) *i = nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRuntimeClassName(HSTRING* n) noexcept override { if (n) *n = nullptr; return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetTrustLevel(TrustLevel* t) noexcept override { if (t) *t = BaseTrust; return S_OK; }
    virtual HRESULT STDMETHODCALLTYPE GetElement(void* args, void** element) noexcept = 0;
    virtual HRESULT STDMETHODCALLTYPE RecycleElement(void* args) noexcept = 0;
};

// -- Microsoft.UI.Xaml.Controls.IControl -- (enable/disable a toolbar toggle whose action is unavailable)
inline HRESULT DevToolsPutIsEnabled(void* control, bool enabled) noexcept
{ return DevToolsAbi<DevToolsXC::IControl>(control)->put_IsEnabled(enabled); }

inline HRESULT DevToolsToggleGetIsOn(void* toggleSwitch, bool* on) noexcept
{ return DevToolsAbi<DevToolsXC::IToggleSwitch>(toggleSwitch)->get_IsOn(on); }
inline HRESULT DevToolsTogglePutIsOn(void* toggleSwitch, bool on) noexcept
{ return DevToolsAbi<DevToolsXC::IToggleSwitch>(toggleSwitch)->put_IsOn(on); }
inline HRESULT DevToolsAddToggled(void* toggleSwitch, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsXC::IToggleSwitch>(toggleSwitch)->add_Toggled(handler, DevToolsAbiTok(token)); }

inline HRESULT DevToolsAddClick(void* buttonBase, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsXCP::IButtonBase>(buttonBase)->add_Click(handler, DevToolsAbiTok(token)); }
inline HRESULT DevToolsToggleButtonPutIsChecked(void* toggleButton, void* boxedBool) noexcept
{ return DevToolsAbi<DevToolsXCP::IToggleButton>(toggleButton)->put_IsChecked(boxedBool); }
inline HRESULT DevToolsGetSelectedIndex(void* selector, int* index) noexcept
{ return DevToolsAbi<DevToolsXCP::ISelector>(selector)->get_SelectedIndex(index); }
inline HRESULT DevToolsAddSelectionChanged(void* selector, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsXCP::ISelector>(selector)->add_SelectionChanged(handler, DevToolsAbiTok(token)); }

inline HRESULT DevToolsGetKey(void* keyArgs, int* virtualKey) noexcept
{ return DevToolsAbi<DevToolsXI::IKeyRoutedEventArgs>(keyArgs)->get_Key(virtualKey); }
inline HRESULT DevToolsPutHandled(void* keyArgs, bool handled) noexcept
{ return DevToolsAbi<DevToolsXI::IKeyRoutedEventArgs>(keyArgs)->put_Handled(handled); }
inline HRESULT DevToolsTransformPoint(void* generalTransform, float x, float y, float* outX, float* outY) noexcept
{
    winrt::Windows::Foundation::Point in{ x, y }, out{};
    HRESULT hr = DevToolsAbi<DevToolsXM::IGeneralTransform>(generalTransform)->TransformPoint(in, &out);
    if (SUCCEEDED(hr)) { *outX = out.X; *outY = out.Y; }
    return hr;
}
inline HRESULT DevToolsStoryboardBegin(void* storyboard) noexcept
{ return DevToolsAbi<DevToolsXMA::IStoryboard>(storyboard)->Begin(); }

inline HRESULT DevToolsXamlReaderLoad(void* readerStatics, HSTRING xaml, ::IInspectable** result) noexcept
{ return DevToolsAbi<DevToolsXMk::IXamlReaderStatics>(readerStatics)->Load(DevToolsAbiStr(xaml), reinterpret_cast<void**>(result)); }

// Capture on the failing UI thread, before another WinRT call can replace its restricted error.
inline std::wstring DevToolsXamlErrorMessage(HRESULT result)
{
    if (SUCCEEDED(result)) return {};
    const auto message = winrt::hresult_error(result, winrt::take_ownership_from_abi).message();
    auto length = (std::min)(message.size(), winrt::hstring::size_type{2048});
    if (length && message[length - 1] >= 0xD800 && message[length - 1] <= 0xDBFF)
        --length;
    return std::wstring(message.c_str(), length);
}

// -- Microsoft.UI.Xaml.Controls.IPanel -- (the app Panel's OWN Children collection is how we attach)
inline HRESULT DevToolsPanelGetChildren(void* panel, ::IInspectable** children) noexcept
{ return DevToolsAbi<DevToolsXC::IPanel>(panel)->get_Children(reinterpret_cast<void**>(children)); }

// -- Windows.Foundation.Collections.IVector<Microsoft.UI.Xaml.UIElement> -- (Panel.Children)
inline HRESULT DevToolsVecGetSize(void* vec, unsigned int* size) noexcept
{ return DevToolsAbi<DevToolsUIElementVector>(vec)->get_Size(size); }
inline HRESULT DevToolsVecGetAt(void* vec, unsigned int index, ::IInspectable** item) noexcept
{ return DevToolsAbi<DevToolsUIElementVector>(vec)->GetAt(index, reinterpret_cast<void**>(item)); }
inline HRESULT DevToolsVecInsertAt(void* vec, unsigned int index, ::IInspectable* item) noexcept
{ return DevToolsAbi<DevToolsUIElementVector>(vec)->InsertAt(index, item); }
inline HRESULT DevToolsVecAppend(void* vec, ::IInspectable* item) noexcept
{ return DevToolsAbi<DevToolsUIElementVector>(vec)->Append(item); }
inline HRESULT DevToolsVecRemoveAt(void* vec, unsigned int index) noexcept
{ return DevToolsAbi<DevToolsUIElementVector>(vec)->RemoveAt(index); }
inline HRESULT DevToolsVecClear(void* vec) noexcept
{ return DevToolsAbi<DevToolsUIElementVector>(vec)->Clear(); }
inline HRESULT DevToolsVecIndexOf(void* vec, ::IInspectable* item, unsigned int* index, unsigned char* found) noexcept
{
    bool f = false;
    HRESULT hr = DevToolsAbi<DevToolsUIElementVector>(vec)->IndexOf(item, index, &f);
    *found = f ? 1u : 0u;
    return hr;
}

inline HRESULT DevToolsPopupPutChild(void* popup, ::IInspectable* child) noexcept
{ return DevToolsAbi<DevToolsXCP::IPopup>(popup)->put_Child(child); }
inline HRESULT DevToolsPopupPutIsOpen(void* popup, bool isOpen) noexcept
{ return DevToolsAbi<DevToolsXCP::IPopup>(popup)->put_IsOpen(isOpen); }
inline HRESULT DevToolsPopupGetIsOpen(void* popup, bool* isOpen) noexcept
{ return DevToolsAbi<DevToolsXCP::IPopup>(popup)->get_IsOpen(isOpen); }
inline HRESULT DevToolsPopupPutHorizontalOffset(void* popup, double offset) noexcept
{ return DevToolsAbi<DevToolsXCP::IPopup>(popup)->put_HorizontalOffset(offset); }
inline HRESULT DevToolsPopupPutVerticalOffset(void* popup, double offset) noexcept
{ return DevToolsAbi<DevToolsXCP::IPopup>(popup)->put_VerticalOffset(offset); }
inline HRESULT DevToolsPopupPutShouldConstrainToRootBounds(void* popup, bool constrain) noexcept
{ return DevToolsAbi<DevToolsXCP::IPopup>(popup)->put_ShouldConstrainToRootBounds(constrain); }
inline HRESULT DevToolsPopupGetIsConstrainedToRootBounds(void* popup, bool* constrained) noexcept
{ return DevToolsAbi<DevToolsXCP::IPopup>(popup)->get_IsConstrainedToRootBounds(constrained); }

// -- Microsoft.UI.Xaml.Controls.Primitives.IThumb -- (drag-to-resize; the DevTools window's pane splitter)
inline HRESULT DevToolsAddDragDelta(void* thumb, void* handler, __int64* token) noexcept
{ return DevToolsAbi<DevToolsXCP::IThumb>(thumb)->add_DragDelta(handler, DevToolsAbiTok(token)); }

// -- Microsoft.UI.Xaml.Controls.Primitives.IDragDeltaEventArgs -- (grip drag increments)
inline HRESULT DevToolsDragDeltaGetChange(void* args, double* horizontal, double* vertical) noexcept
{
    auto* a = DevToolsAbi<DevToolsXCP::IDragDeltaEventArgs>(args);
    HRESULT hr = a->get_HorizontalChange(horizontal);
    if (SUCCEEDED(hr)) hr = a->get_VerticalChange(vertical);
    return hr;
}

inline HRESULT DevToolsCanvasGetLeft(void* canvasStatics, ::IInspectable* element, double* value) noexcept
{ return DevToolsAbi<DevToolsXC::ICanvasStatics>(canvasStatics)->GetLeft(element, value); }
inline HRESULT DevToolsCanvasSetLeft(void* canvasStatics, ::IInspectable* element, double value) noexcept
{ return DevToolsAbi<DevToolsXC::ICanvasStatics>(canvasStatics)->SetLeft(element, value); }
inline HRESULT DevToolsCanvasGetTop(void* canvasStatics, ::IInspectable* element, double* value) noexcept
{ return DevToolsAbi<DevToolsXC::ICanvasStatics>(canvasStatics)->GetTop(element, value); }
inline HRESULT DevToolsCanvasSetTop(void* canvasStatics, ::IInspectable* element, double value) noexcept
{ return DevToolsAbi<DevToolsXC::ICanvasStatics>(canvasStatics)->SetTop(element, value); }

// -- Microsoft.UI.Xaml.Controls.IBorder -- (reach the toolbar's StackPanel through its Border)
inline HRESULT DevToolsBorderGetChild(void* border, ::IInspectable** child) noexcept
{ return DevToolsAbi<DevToolsXC::IBorder>(border)->get_Child(reinterpret_cast<void**>(child)); }

// Projected DispatcherQueue access for live XAML reads/mutations on the app UI thread.
inline HRESULT DevToolsDispatcherTryEnqueue(void* dispatcher, ::IUnknown* handler, bool* enqueued) noexcept
{ return DevToolsAbi<DevToolsD::IDispatcherQueue>(dispatcher)->TryEnqueue(handler, enqueued); }
