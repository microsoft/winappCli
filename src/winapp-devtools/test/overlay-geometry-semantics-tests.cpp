// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "../native/WinApp.DevTools.Native/DevToolsProjected.h"
#include "../native/WinApp.DevTools.Native/DevToolsTrust.h"
#include "../native/WinApp.DevTools.Native/DevToolsSurface.h"
#include "../native/WinApp.DevTools.Native/DevToolsOwnedState.h"
static HRESULT SwitchChildren(void*, IInspectable**);
static HRESULT SwitchSize(void*, unsigned*);
static HRESULT SwitchIndex(void*, IInspectable*, unsigned*, unsigned char*);
static HRESULT SwitchInsert(void*, unsigned, IInspectable*);
static HRESULT SwitchRemove(void*, unsigned);
static HRESULT SwitchPopup(void*, bool);
static HRESULT SwitchPopupOpen(void*, bool*);
static HRESULT SwitchLeft(void*, IInspectable*, double);
static HRESULT SwitchTop(void*, IInspectable*, double);
static HRESULT SwitchSizeEvent(void*, void*, __int64*);
static HRESULT SwitchKeyEvent(void*, void*, __int64*);
static HRESULT SwitchFocusEvent(void*, void*, __int64*);
static HRESULT SwitchRemoveSize(void*, __int64);
static HRESULT SwitchRemoveKey(void*, __int64);
static HRESULT SwitchRemoveFocus(void*, __int64);
static bool SwitchSurface(IXamlDiagnostics*, InstanceHandle, DevToolsSurface*);
static std::vector<DevToolsSurface> SwitchSurfaces(IXamlDiagnostics*, const std::vector<InstanceHandle>&);
static HRESULT WINAPI GeometryActivationFactory(HSTRING, REFIID, void**);
static HRESULT GeometryLoadMarkup(void*, HSTRING, IInspectable**);
static bool GeometryScreenToLocal(const DevToolsSurface&, int, int, float*, float*);
static unsigned switchWindowSearches = 0;
static BOOL WINAPI SwitchEnumWindows(WNDENUMPROC, LPARAM) { ++switchWindowSearches; return FALSE; }
static UINT_PTR WINAPI SwitchTimer(HWND, UINT_PTR, UINT, TIMERPROC) { return 1; }
static BOOL WINAPI SwitchKillTimer(HWND, UINT_PTR) { return TRUE; }
static BOOL WINAPI GeometryCursor(POINT* point) { *point = {0, 0}; return TRUE; }
static bool CommentMutationEnabled() { return true; }
static unsigned commentLaunches = 0;
static std::wstring commentCommand;
static bool guestWriterTest = false;
static HANDLE commentProcess = nullptr;
static DWORD commentExitCode = 0;
static BOOL WINAPI CommentExitCode(HANDLE process, LPDWORD code)
{
    if (guestWriterTest && process == commentProcess) { *code = commentExitCode; return TRUE; }
    return GetExitCodeProcess(process, code);
}
static BOOL WINAPI CommentCreateProcess(LPCWSTR, LPWSTR command, LPSECURITY_ATTRIBUTES, LPSECURITY_ATTRIBUTES,
    BOOL, DWORD, LPVOID, LPCWSTR, LPSTARTUPINFOW, LPPROCESS_INFORMATION process)
{
    ++commentLaunches;
    commentCommand = command;
    *process = {};
    if (guestWriterTest) {
        commentProcess = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        process->hProcess = commentProcess;
    }
    return TRUE;
}
static HRESULT GeometryXamlRoot(void*, IInspectable**);
static HRESULT GeometryRootSize(void*, float* width, float* height)
{ *width = 800; *height = 700; return S_OK; }
static HRESULT GeometryPutVisibility(void*, int);
static HRESULT GeometryLoaded(void*, bool*);
static HRESULT GeometryVisibility(void*, int*);
static HRESULT GeometryOpacity(void*, double*);
static HRESULT GeometryParent(IInspectable*, IInspectable**);
static HRESULT GeometryRenderSize(void*, float*, float*);
static HRESULT GeometryActualWidth(void*, double*);
static HRESULT GeometryActualHeight(void*, double*);
static HRESULT GeometryTransform(void*, void*, void**);
static HRESULT GeometryPoint(void*, float, float, float*, float*);
static HRESULT GeometryMargin(void*, double*, double*, double*, double*);
static double quickNumber = 0;
static LONGLONG dismissClock = 0;
static LONGLONG dismissWriteCost = 0;
static LONGLONG dismissClosedAt = -1;
static BOOL WINAPI DismissCounter(LARGE_INTEGER* value) { value->QuadPart=dismissClock;return TRUE; }
static BOOL WINAPI DismissFrequency(LARGE_INTEGER* value) { value->QuadPart=1'000'000;return TRUE; }
static int dismissFocusState = -1;
static HRESULT DismissFocus(void*, int state, bool* value) { dismissFocusState=state;*value=true;return S_OK; }
static IInspectable* focusedOutsidePanel = nullptr;
static bool focusInPanel = false;
static unsigned pointerFocusCalls = 0;
static HRESULT TestFocusedWithin(void*, IInspectable** focused, bool* inside)
{
    *focused = focusedOutsidePanel; if (*focused) (*focused)->AddRef();
    *inside = focusInPanel; return S_OK;
}
static HRESULT TestFocusWithPointer(void*, void*, bool* moved) { ++pointerFocusCalls; *moved = true; return S_OK; }
static bool escapeHandled = false;
static int commentKey = 27;
static bool commentControl = false;
static HRESULT EscapeKey(void*, int* value) { *value = commentKey; return S_OK; }
static HRESULT EscapeHandled(void*, bool value) { escapeHandled = value; return S_OK; }
static SHORT WINAPI NoModifierKey(int key) { return key == VK_CONTROL && commentControl ? SHORT(0x8000) : 0; }
static HRESULT quickNumberResult = S_OK;
static HRESULT ReadQuickNumber(void*, double* value) { *value=quickNumber; return quickNumberResult; }
template <typename I> static HRESULT GeometryPadding(void*, double*, double*, double*, double*);
// Replace only runtime reads; both shipping geometry functions, their QIs, root Content ABI,
// identity checks, normalization and box-model calculations execute unchanged.
#define DevToolsGetXamlRoot GeometryXamlRoot
#define DevToolsXamlRootGetSize GeometryRootSize
#define DevToolsPutVisibility GeometryPutVisibility
#define DevToolsGetIsLoaded GeometryLoaded
#define DevToolsGetVisibility GeometryVisibility
#define DevToolsGetOpacity GeometryOpacity
#define DevToolsGetVisualParent GeometryParent
#define DevToolsGetRenderSize GeometryRenderSize
#define DevToolsGetActualWidth GeometryActualWidth
#define DevToolsGetActualHeight GeometryActualHeight
#define DevToolsTransformToVisual GeometryTransform
#define DevToolsTransformPoint GeometryPoint
#define DevToolsGetMargin GeometryMargin
#define DevToolsNumberBoxGetValue ReadQuickNumber
#define DevToolsRangeGetValue ReadQuickNumber
#define DevToolsFocus DismissFocus
#define DevToolsFocusedWithin TestFocusedWithin
#define DevToolsFocusWithPointer TestFocusWithPointer
#define DevToolsGetKey EscapeKey
#define DevToolsPutHandled EscapeHandled
#define GetKeyState NoModifierKey
#define QueryPerformanceCounter DismissCounter
#define QueryPerformanceFrequency DismissFrequency
#define DevToolsGetPaddingAs GeometryPadding
#define DevToolsTrust_MutationEnabled CommentMutationEnabled
#define CreateProcessW CommentCreateProcess
#define GetExitCodeProcess CommentExitCode
#define DevToolsPanelGetChildren SwitchChildren
#define DevToolsVecGetSize SwitchSize
#define DevToolsVecIndexOf SwitchIndex
#define DevToolsVecInsertAt SwitchInsert
#define DevToolsVecRemoveAt SwitchRemove
#define DevToolsPopupPutIsOpen SwitchPopup
#define DevToolsPopupGetIsOpen SwitchPopupOpen
#define DevToolsCanvasSetLeft SwitchLeft
#define DevToolsCanvasSetTop SwitchTop
#define DevToolsAddSizeChanged SwitchSizeEvent
#define DevToolsAddPreviewKeyDown SwitchKeyEvent
#define DevToolsAddGotFocus SwitchFocusEvent
#define DevToolsRemoveSizeChanged SwitchRemoveSize
#define DevToolsRemovePreviewKeyDown SwitchRemoveKey
#define DevToolsRemoveGotFocus SwitchRemoveFocus
#define DevToolsSurface_Resolve SwitchSurface
#define DevToolsSurface_ResolveAll SwitchSurfaces
#define DevToolsSurface_ScreenToLocal GeometryScreenToLocal
#define RoGetActivationFactory GeometryActivationFactory
#define DevToolsXamlReaderLoad GeometryLoadMarkup
#define EnumWindows SwitchEnumWindows
#define SetTimer SwitchTimer
#define KillTimer SwitchKillTimer
#define GetCursorPos GeometryCursor
#include "../native/WinApp.DevTools.Native/DevToolsOverlay.cpp"
#undef DevToolsGetKey
#undef DevToolsPutHandled
#undef GetKeyState
#undef DevToolsPanelGetChildren
#undef DevToolsVecGetSize
#undef DevToolsVecIndexOf
#undef DevToolsVecInsertAt
#undef DevToolsVecRemoveAt
#undef DevToolsPopupPutIsOpen
#undef DevToolsPopupGetIsOpen
#undef DevToolsCanvasSetLeft
#undef DevToolsCanvasSetTop
#undef DevToolsAddSizeChanged
#undef DevToolsAddPreviewKeyDown
#undef DevToolsAddGotFocus
#undef DevToolsRemoveSizeChanged
#undef DevToolsRemovePreviewKeyDown
#undef DevToolsRemoveGotFocus
#undef DevToolsSurface_Resolve
#undef DevToolsSurface_ResolveAll
#undef DevToolsSurface_ScreenToLocal
#undef RoGetActivationFactory
#undef DevToolsXamlReaderLoad
#undef EnumWindows
#undef SetTimer
#undef KillTimer
#undef GetCursorPos
#undef CreateProcessW
#undef DevToolsTrust_MutationEnabled
#undef DevToolsGetXamlRoot
#undef DevToolsXamlRootGetSize
#undef DevToolsPutVisibility
#undef DevToolsGetIsLoaded
#undef DevToolsGetVisibility
#undef DevToolsGetOpacity
#undef DevToolsGetVisualParent
#undef DevToolsGetRenderSize
#undef DevToolsGetActualWidth
#undef DevToolsGetActualHeight
#undef DevToolsTransformToVisual
#undef DevToolsTransformPoint
#undef DevToolsGetMargin
#undef DevToolsFocus
#undef QueryPerformanceCounter
#undef QueryPerformanceFrequency
#undef DevToolsGetPaddingAs
#include <limits>

struct GeometryObject : IInspectable
{
    ULONG refs = 1;
    IInspectable* root = nullptr;
    HRESULT rootHr = S_OK;
    HRESULT identityHr = S_OK;
    bool ui = true, fe = true, keyArgs = false;
    float width = 100, height = 80;
    double actualWidth = 100, actualHeight = 80;
    HRESULT sizeHr = S_OK, transformHr = S_OK, pointHr = S_OK;
    float scale = 1, x = 24, y = 24;
    std::wstring runtimeClass;
    bool loaded = true;
    int visibility = 0;
    double opacity = 1;
    IInspectable* parent = nullptr;
    HRESULT loadedHr = S_OK, visibilityHr = S_OK, opacityHr = S_OK, parentHr = S_OK;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        *out = nullptr;
        if (iid == IID_IUnknown && FAILED(identityHr)) return identityHr;
        if (iid != IID_IUnknown && iid != __uuidof(IInspectable) &&
            !(ui && iid == DevToolsIid<DevToolsX::IUIElement>()) &&
            !(fe && iid == DevToolsIid<DevToolsX::IFrameworkElement>()) &&
            !(keyArgs && iid == DevToolsIid<DevToolsXI::IKeyRoutedEventArgs>()) &&
            iid != DevToolsIid<DevToolsXC::IControl>()) return E_NOINTERFACE;
        *out = static_cast<IInspectable*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    HRESULT STDMETHODCALLTYPE GetIids(ULONG* count, IID** ids) override { *count = 0; *ids = nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRuntimeClassName(HSTRING* out) override {
        return WindowsCreateString(runtimeClass.c_str(), static_cast<UINT32>(runtimeClass.size()), out);
    }
    HRESULT STDMETHODCALLTYPE GetTrustLevel(TrustLevel* out) override { *out = BaseTrust; return S_OK; }
};

// Actual generated IXamlRoot ABI, with independent interface addresses sharing a canonical IUnknown.
struct GeometryRoot : winrt::impl::abi_t<DevToolsX::IXamlRoot>
{
    uint32_t refs = 1;
    GeometryObject* identity;
    GeometryObject* content;
    HRESULT identityHr = S_OK, contentHr = S_OK;
    bool nullIdentity = false;
    unsigned contentReads = 0;
    GeometryRoot(GeometryObject* id, GeometryObject* value) : identity(id), content(value) {}
    int32_t __stdcall QueryInterface(winrt::guid const& iid, void** out) noexcept override {
        *out = nullptr;
        if (iid == winrt::guid_of<winrt::Windows::Foundation::IUnknown>()) {
            if (FAILED(identityHr) || nullIdentity) return identityHr;
            *out = static_cast<IInspectable*>(identity); identity->AddRef(); return S_OK;
        }
        if (iid != winrt::guid_of<DevToolsX::IXamlRoot>() &&
            iid != winrt::guid_of<winrt::Windows::Foundation::IInspectable>()) return E_NOINTERFACE;
        *out = this; AddRef(); return S_OK;
    }
    uint32_t __stdcall AddRef() noexcept override { return ++refs; }
    uint32_t __stdcall Release() noexcept override { return --refs; }
    int32_t __stdcall GetIids(uint32_t* count, winrt::guid** ids) noexcept override
    { *count = 0; *ids = nullptr; return S_OK; }
    int32_t __stdcall GetRuntimeClassName(void** out) noexcept override { *out = nullptr; return S_OK; }
    int32_t __stdcall GetTrustLevel(winrt::Windows::Foundation::TrustLevel* out) noexcept override
    { *out = winrt::Windows::Foundation::TrustLevel::BaseTrust; return S_OK; }
    int32_t __stdcall get_Content(void** out) noexcept override {
        ++contentReads; *out = content;
        if (content) content->AddRef();
        return contentHr;
    }
    int32_t __stdcall get_Size(winrt::Windows::Foundation::Size*) noexcept override { return E_NOTIMPL; }
    int32_t __stdcall get_RasterizationScale(double*) noexcept override { return E_NOTIMPL; }
    int32_t __stdcall get_IsHostVisible(bool*) noexcept override { return E_NOTIMPL; }
    int32_t __stdcall add_Changed(void*, winrt::event_token*) noexcept override { return E_NOTIMPL; }
    int32_t __stdcall remove_Changed(winrt::event_token) noexcept override { return E_NOTIMPL; }
    IInspectable* Inspectable() { return reinterpret_cast<IInspectable*>(this); }
};

struct GeometryDiagnostics : IXamlDiagnostics
{
    GeometryObject supplied, target, content, foreignContent, identity, foreignIdentity;
    GeometryRoot active{&identity, &content}, alias{&identity, &content}, foreign{&foreignIdentity, &foreignContent};
    InstanceHandle dead = 0;
    GeometryDiagnostics() { supplied.root = active.Inspectable(); target.root = alias.Inspectable(); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** out) override { *out = nullptr; return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE GetDispatcher(IInspectable**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetUiLayer(IInspectable**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetApplication(IInspectable**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetIInspectableFromHandle(InstanceHandle handle, IInspectable** out) override {
        *out = nullptr;
        if (handle == dead) return E_FAIL;
        *out = handle == 1 ? &supplied : handle == 2 ? &target : nullptr;
        if (!*out) return E_INVALIDARG;
        (*out)->AddRef(); return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetHandleFromIInspectable(IInspectable*, InstanceHandle*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE HitTest(RECT, unsigned int*, InstanceHandle**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE RegisterInstance(IInspectable*, InstanceHandle*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetInitializationData(BSTR*) override { return E_NOTIMPL; }
    bool Balanced() const {
        return supplied.refs == 1 && target.refs == 1 && content.refs == 1 && foreignContent.refs == 1 &&
            identity.refs == 1 && foreignIdentity.refs == 1 && active.refs == 1 && alias.refs == 1 && foreign.refs == 1;
    }
};

static unsigned geometryTransforms = 0;
static void* geometryDestination = nullptr;
static HRESULT GeometryXamlRoot(void* ui, IInspectable** out) {
    auto* e = static_cast<GeometryObject*>(ui);
    *out = e->root; if (*out) (*out)->AddRef(); return e->rootHr;
}
static HRESULT GeometryLoaded(void* fe, bool* out)
{ auto e = static_cast<GeometryObject*>(fe); *out = e->loaded; return e->loadedHr; }
static HRESULT GeometryVisibility(void* ui, int* out)
{ auto e = static_cast<GeometryObject*>(ui); *out = e->visibility; return e->visibilityHr; }
static HRESULT GeometryPutVisibility(void* ui, int value)
{ static_cast<GeometryObject*>(ui)->visibility = value; return S_OK; }
static HRESULT GeometryOpacity(void* ui, double* out)
{ auto e = static_cast<GeometryObject*>(ui); *out = e->opacity; return e->opacityHr; }
static HRESULT GeometryParent(IInspectable* element, IInspectable** out)
{
    auto e = static_cast<GeometryObject*>(element);
    *out = e->parent;
    if (*out) (*out)->AddRef();
    return e->parentHr;
}
static HRESULT GeometryRenderSize(void* ui, float* w, float* h) {
    auto* e = static_cast<GeometryObject*>(ui); *w = e->width; *h = e->height; return e->sizeHr;
}
static HRESULT GeometryActualWidth(void* fe, double* value)
{ *value = static_cast<GeometryObject*>(fe)->actualWidth; return S_OK; }
static HRESULT GeometryActualHeight(void* fe, double* value)
{ *value = static_cast<GeometryObject*>(fe)->actualHeight; return S_OK; }
static HRESULT GeometryTransform(void* ui, void* destination, void** out) {
    ++geometryTransforms; geometryDestination = destination;
    auto* e = static_cast<GeometryObject*>(ui); *out = nullptr;
    if (FAILED(e->transformHr)) return e->transformHr;
    // Intentionally succeeds for foreign trees: production must refuse before reaching this seam.
    *out = e; e->AddRef(); return S_OK;
}
static HRESULT GeometryPoint(void* gt, float x, float y, float* ox, float* oy) {
    auto* e = static_cast<GeometryObject*>(gt);
    *ox = e->x + x * e->scale; *oy = e->y + y * e->scale; return e->pointHr;
}
static HRESULT GeometryMargin(void*, double* l, double* t, double* r, double* b)
{ *l = 2; *t = 3; *r = 4; *b = 5; return S_OK; }
template <typename I> static HRESULT GeometryPadding(void*, double* l, double* t, double* r, double* b)
{ *l = *t = *r = *b = 24; return S_OK; }

static bool GeometryRect(const RECT& r, LONG l, LONG t, LONG right, LONG bottom)
{ return r.left == l && r.top == t && r.right == right && r.bottom == bottom; }

static std::wstring commentInput;
static bool CommentReadInput(IInspectable*, std::wstring* out) { *out = commentInput; return true; }

struct SwitchObject : GeometryObject
{
    IInspectable* child = nullptr;
    bool popupOpen = true;
    HRESULT popupReadHr = S_OK, popupOpenHr = S_OK, popupCloseHr = S_OK, removeHr = S_OK;
    bool failInsert = false;
    double left = 0, top = 0;
    unsigned sizeAdds = 0, sizeRemoves = 0, keyAdds = 0, keyRemoves = 0;
    unsigned focusAdds = 0, focusRemoves = 0;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        if (iid == DevToolsIid<DevToolsXC::IPanel>() || iid == DevToolsIid<DevToolsUIElementVector>() ||
            iid == DevToolsIid<DevToolsXCP::IPopup>()) {
            *out = static_cast<IInspectable*>(this); AddRef(); return S_OK;
        }
        return GeometryObject::QueryInterface(iid, out);
    }
};
struct SwitchDiagnostics : GeometryDiagnostics
{
    unsigned hits = 0;
    InstanceHandle hitHandle = 0;
    HRESULT hitResult = S_OK;
    std::vector<InstanceHandle> hitStack;
    HRESULT STDMETHODCALLTYPE HitTest(RECT, unsigned int* count, InstanceHandle** values) override {
        ++hits; *count = 0; *values = nullptr;
        const auto stack = hitStack.empty() ? std::vector<InstanceHandle>{hitHandle} : hitStack;
        if (stack.front() && SUCCEEDED(hitResult)) {
            *values = static_cast<InstanceHandle*>(CoTaskMemAlloc(stack.size() * sizeof(InstanceHandle)));
            if (!*values) return E_OUTOFMEMORY;
            std::copy(stack.begin(), stack.end(), *values);
            *count = static_cast<unsigned>(stack.size());
        }
        return hitResult;
    }
    SwitchObject mainPanel, secondPanel, replacementPanel, mainTarget, secondTarget, mainPin, secondPin, popup, canvas;
    SwitchDiagnostics() {
        mainPanel.root = mainTarget.root = active.Inspectable();
        replacementPanel.root = active.Inspectable();
        secondPanel.root = secondTarget.root = foreign.Inspectable();
        mainPanel.child = &popup;
        mainTarget.x = 32; mainTarget.y = 161;
        secondTarget.x = 8; secondTarget.y = 49;
    }
    HRESULT STDMETHODCALLTYPE GetIInspectableFromHandle(InstanceHandle h, IInspectable** out) override {
        *out = h == 10 ? &mainPanel : h == 20 ? &secondPanel : h == 30 ? &replacementPanel :
               h == 11 ? &mainTarget : h == 21 ? &secondTarget : nullptr;
        if (!*out) return E_INVALIDARG;
        (*out)->AddRef(); return S_OK;
    }
};
static bool switchOpenedWithForeignPin = false;
static unsigned switchOpens = 0;
static InstanceHandle switchAttachedRoot = 10;
static bool switchTrackedDuringMove = false;
static bool hideDuringInsert = false;
static InstanceHandle unavailableSurface = 0;
static HRESULT SwitchChildren(void* p, IInspectable** out)
{ *out = static_cast<SwitchObject*>(p); (*out)->AddRef(); return S_OK; }
static HRESULT SwitchSize(void* p, unsigned* n)
{ *n = static_cast<SwitchObject*>(p)->child ? 1 : 0; return S_OK; }
static HRESULT SwitchIndex(void* p, IInspectable* child, unsigned* i, unsigned char* found)
{ *i = 0; *found = static_cast<SwitchObject*>(p)->child == child; return S_OK; }
static HRESULT SwitchInsert(void* p, unsigned, IInspectable* child) {
    auto panel = static_cast<SwitchObject*>(p);
    if (panel->failInsert) return E_FAIL;
    panel->child = child;
    auto diag = static_cast<SwitchDiagnostics*>(g_pickDiag);
    if (diag && panel == &diag->canvas) return S_OK;
    switchAttachedRoot = panel == &diag->mainPanel ? 10 : panel == &diag->replacementPanel ? 30 : 20;
    if (hideDuringInsert) DevToolsOverlay_SetToolbarVisible(false);
    const auto before = geometryTransforms;
    TrackingTick();
    if (geometryTransforms != before) switchTrackedDuringMove = true;
    return S_OK;
}
static HRESULT SwitchRemove(void* p, unsigned)
{
    auto panel = static_cast<SwitchObject*>(p);
    if (FAILED(panel->removeHr)) return panel->removeHr;
    panel->child = nullptr; return S_OK;
}
static HRESULT SwitchPopup(void* popup, bool open) {
    auto value = static_cast<SwitchObject*>(popup);
    if (open && FAILED(value->popupOpenHr)) return value->popupOpenHr;
    if (!open && FAILED(value->popupCloseHr)) return value->popupCloseHr;
    value->popupOpen = open;
    if (!open) dismissClosedAt=dismissClock;
    if (open) {
        ++switchOpens;
        for (const auto& pin : g_pins) {
            if (popup != g_selectionPopup.get()) continue;
            auto ui = static_cast<SwitchObject*>(pin.ui);
            const bool foreign = (pin.handle == 11 && switchAttachedRoot != 10 && switchAttachedRoot != 30) ||
                                 (pin.handle == 21 && switchAttachedRoot != 20);
            if (ui && foreign && ui->visibility == 0) switchOpenedWithForeignPin = true;
        }
    }
    return S_OK;
}
static HRESULT SwitchPopupOpen(void* popup, bool* open)
{ auto value = static_cast<SwitchObject*>(popup); *open = value->popupOpen; return value->popupReadHr; }
static HRESULT SwitchLeft(void*, IInspectable* ui, double v)
{ static_cast<SwitchObject*>(ui)->left = v; return S_OK; }
static HRESULT SwitchTop(void*, IInspectable* ui, double v)
{ static_cast<SwitchObject*>(ui)->top = v; return S_OK; }
static HRESULT SwitchSizeEvent(void* p, void*, __int64* token)
{ *token = ++static_cast<SwitchObject*>(p)->sizeAdds; return S_OK; }
static HRESULT SwitchKeyEvent(void* p, void*, __int64* token)
{ *token = ++static_cast<SwitchObject*>(p)->keyAdds; return S_OK; }
static HRESULT SwitchFocusEvent(void* p, void*, __int64* token)
{ *token = ++static_cast<SwitchObject*>(p)->focusAdds; return S_OK; }
static HRESULT SwitchRemoveSize(void* p, __int64)
{ ++static_cast<SwitchObject*>(p)->sizeRemoves; return S_OK; }
static HRESULT SwitchRemoveKey(void* p, __int64)
{ ++static_cast<SwitchObject*>(p)->keyRemoves; return S_OK; }
static HRESULT SwitchRemoveFocus(void* p, __int64)
{ ++static_cast<SwitchObject*>(p)->focusRemoves; return S_OK; }
static bool SwitchSurface(IXamlDiagnostics* diag, InstanceHandle h, DevToolsSurface* out) {
    if (h == unavailableSurface) return false;
    IInspectable* obj = nullptr;
    if (FAILED(diag->GetIInspectableFromHandle(h, &obj))) return false;
    auto e = static_cast<GeometryObject*>(obj);
    *out = {};
    out->rootHandle = h;
    winrt::com_ptr<IUnknown> identity;
    if (!e->root || FAILED(e->root->QueryInterface(IID_IUnknown, identity.put_void()))) {
        obj->Release(); return false;
    }
    out->xamlRootKey = reinterpret_cast<unsigned long long>(identity.get());
    out->contentW = 800; out->contentH = 700;
    out->displayName = h == 20 ? L"SECOND" : L"MAIN";
    obj->Release();
    return true;
}
static std::vector<DevToolsSurface> SwitchSurfaces(IXamlDiagnostics* diag, const std::vector<InstanceHandle>& roots) {
    std::vector<DevToolsSurface> all;
    for (auto h : roots) {
        DevToolsSurface s;
        if (SwitchSurface(diag, h, &s)) all.push_back(s);
    }
    return all;
}

static bool geometryAdornerReader = false;
static GeometryObject geometryReader;
static SwitchObject geometryAdorner;
static HRESULT WINAPI GeometryActivationFactory(HSTRING name, REFIID iid, void** out)
{
    if (geometryAdornerReader && iid == DevToolsIid<DevToolsXMk::IXamlReaderStatics>()) {
        *out = &geometryReader;
        geometryReader.AddRef();
        return S_OK;
    }
    return ::RoGetActivationFactory(name, iid, out);
}
static HRESULT GeometryLoadMarkup(void*, HSTRING, IInspectable** out)
{
    *out = &geometryAdorner;
    geometryAdorner.AddRef();
    return S_OK;
}
static bool GeometryScreenToLocal(const DevToolsSurface& surface, int x, int y, float* localX, float* localY)
{
    if (!surface.Valid()) return false;
    *localX = static_cast<float>(x);
    *localY = static_cast<float>(y);
    return true;
}
struct PickerHosts
{
    explicit PickerHosts(SwitchDiagnostics& diag)
    {
        g_selectionPanel.copy_from(&diag.mainPanel);
        ReadHostRootIdentity(&diag.mainPanel, g_selectionRootIdentity);
        g_highlightChildren.copy_from(&diag.canvas);
        SwitchSurface(&diag, 10, &g_activeSurface);
        geometryAdornerReader = true;
    }
    ~PickerHosts()
    {
        ClearAdorner();
        g_selectionPanel = nullptr;
        g_selectionRootIdentity = nullptr;
        g_highlightChildren = nullptr;
        g_activeSurface = {};
        geometryAdornerReader = false;
    }
};

static unsigned quickWrites = 0;
static bool quickReentrant = false;
static std::wstring quickWritten;
static DevToolsWriteOutcome quickOutcome = DevToolsWriteOutcome::Ok;
static unsigned quickReads = 0;
static bool quickReadSucceeded = true;
static bool ReadQuickRows(IInspectable*, std::wstring*, std::wstring*, std::vector<DevToolsCardRow>* rows,
    std::wstring*, std::wstring*)
{
    ++quickReads;
    DevToolsCardRow row;row.name=L"Opacity";row.type=L"Double";row.value=L"0.4";
    *rows={row};
    return quickReadSucceeded;
}
static DevToolsWriteOutcome WriteQuickValue(IInspectable*, const wchar_t*, const wchar_t*, const wchar_t* value, bool confirmed)
{
    ++quickWrites;quickWritten=value;
    dismissClock+=dismissWriteCost;
    if (quickReentrant) ++g_selGen;
    return confirmed ? DevToolsWriteOutcome::ReplacedBinding : quickOutcome;
}

static unsigned pickSourceReads = 0;
static bool ReadPickedSource(InstanceHandle handle, std::wstring* file, unsigned* line, unsigned* column)
{
    ++pickSourceReads;
    *file = handle == 11 ? L"" : L"ms-appx:///MainPage.xaml";
    *line = file->empty() ? 0 : 7;
    *column = 0;
    return !file->empty();
}
static bool IsPickChrome(InstanceHandle handle) { return handle == 99; }
static InstanceHandle recoveryCandidate = 0;
static unsigned recoveryFinds = 0;
static unsigned long long recoveryRootKey = 0;
static bool hideDuringFind = false;
static InstanceHandle FindRecoveryPanel(unsigned long long rootKey)
{
    ++recoveryFinds;
    recoveryRootKey = rootKey;
    if (hideDuringFind) DevToolsOverlay_SetToolbarVisible(false);
    return recoveryCandidate;
}

int main()
{
    unsigned checks = 0, failures = 0;
    auto check = [&](bool ok, const char* entry, const char* name) {
        ++checks; if (!ok) ++failures;
        std::printf("%s %s: %s\n", ok ? "PASS" : "FAIL", entry, name);
    };
    {
        SwitchObject canvas, bar, pill, snaps[kCornerCount];
        g_canvasStatics = &canvas; g_aRow = &bar; g_railL = &pill;
        for (int i = 0; i < kCornerCount; ++i) g_snapUi[i] = &snaps[i];
        g_protoW = 800; g_protoH = 700; g_toolbarVisible = true;
        ShowARow(false);
        check(bar.visibility == 1 && pill.visibility == 0 &&
            bar.left >= 0 && bar.top >= 0, "toolbar visibility", "collapsed bar leaves layout without negative parking");
        ShowARow(true);
        check(bar.visibility == 0 && pill.visibility == 1, "toolbar visibility", "expanded bar replaces the pill");
        OnToolbarGotFocus(nullptr, nullptr);
        OnACollapse(nullptr, nullptr);
        check(g_aRowOpen, "toolbar visibility", "pointer exit preserves keyboard-focused actions");
        OnToolbarLostFocus(nullptr, nullptr);
        OnACollapse(nullptr, nullptr);
        check(!g_aRowOpen && pill.visibility == 0, "toolbar visibility", "unfocused unpinned bar can collapse");
        ProtoSetPinned(true); ShowARow(true);
        g_dragging = true; ShowSnapTargets(true);
        check(std::all_of(std::begin(snaps), std::end(snaps), [](const auto& snap) { return snap.visibility == 0; }),
            "toolbar visibility", "visible drag exposes snap targets");
        DevToolsOverlay_SetToolbarVisible(false);
        check(bar.visibility == 1 && pill.visibility == 1 &&
            std::all_of(std::begin(snaps), std::end(snaps), [](const auto& snap) { return snap.visibility == 1; }),
            "toolbar visibility", "explicit hide collapses both halves and drag targets");
        ShowARow(true);
        check(bar.visibility == 1 && pill.visibility == 1, "toolbar visibility", "reposition cannot undo explicit hide");
        g_dragging = false;
        DevToolsOverlay_SetToolbarVisible(true);
        check(bar.visibility == 0 && pill.visibility == 1 &&
            std::all_of(std::begin(snaps), std::end(snaps), [](const auto& snap) { return snap.visibility == 1; }),
            "toolbar visibility", "show restores the selected half without stale drag targets");
        ProtoSetPinned(false); ShowARow(false);
        check(pill.visibility == 0, "toolbar visibility", "unpinned bar collapses to the pill without parking");
        g_canvasStatics = g_aRow = g_railL = nullptr;
        for (auto& snap : g_snapUi) snap = nullptr;
        g_toolbarVisible = false;
    }
    {
        DevToolsCardRow row;
        row.name = L"Content"; row.type = L"String"; row.editKind = L"text";
        for (const auto* text : {L"", L"(bound)", L"{SolidColorBrush}"}) {
            row.value = text;
            check(SelRowCanEdit(row) && BuildSelRowsMarkup({row}, false).find(L"DevToolsSelEdit0") != std::wstring::npos,
                "quick values", "real boxed String remains an editor value");
        }
        for (const auto* state : {L"unresolved", L"null", L"unset"}) {
            row.valueState = state;
            check(!SelRowCanEdit(row) && BuildSelRowsMarkup({row}, false).find(L"DevToolsSelEdit0") == std::wstring::npos,
                "quick values", "unavailable value cannot become an editor seed");
        }
        row.valueState.clear(); row.editKind = L"none";
        check(!SelRowCanEdit(row), "quick values", "complex value has no quick editor");
        std::vector<DevToolsCardRow> all;
        for (const auto* name : {L"Content",L"Text",L"Foreground",L"Opacity",L"IsEnabled",L"Visibility",L"Margin",L"Padding",L"IsChecked"}) {
            DevToolsCardRow item;item.name=name;item.value=L"sample";all.push_back(item);
        }
        const auto curated=DevToolsSelCurateRows(all);
        check(curated.size()==5, "quick peek", "at most five meaningful rows");
        check(std::any_of(curated.begin(),curated.end(),[](const auto& item){return item.name==L"IsChecked";}),
            "quick peek", "contextual checked value precedes generic layout defaults");
        row.name=L"Text";row.type=L"String";row.editKind=L"text";row.value=L"effective";
        row.source=L"Binding";row.binding=L"{Binding Title}";
        const auto markup=BuildSelRowsMarkup({row},false);
        check(markup.find(L"<Expander ")!=std::wstring::npos && markup.find(L"IsExpanded=\"False\"")!=std::wstring::npos,
            "quick peek", "editor is opt-in rather than always visible");
        check(markup.find(L"Text=\"effective\"")!=std::wstring::npos &&
            markup.find(L"Header=\"Binding details\"")!=std::wstring::npos,
            "quick peek", "effective value and binding details are separate");
        row.name=L"Opacity";row.type=L"Double";row.editKind=L"number";row.value=L"0.75";
        row.source.clear();row.binding.clear();
        const auto opacityMarkup=BuildSelRowsMarkup({row},false);
        check(opacityMarkup.find(L"<Slider ")!=std::wstring::npos &&
            opacityMarkup.find(L"Minimum=\"0\" Maximum=\"1\" StepFrequency=\"0.01\"")!=std::wstring::npos &&
            opacityMarkup.find(L"Text=\"0.75\"")!=std::wstring::npos,
            "quick peek", "opacity has a ranged keyboard slider and precise visible value");
        row.name=L"Width";
        check(BuildSelRowsMarkup({row},false).find(L"<NumberBox ")!=std::wstring::npos,
            "quick peek", "arbitrary numeric properties keep their unbounded numeric editor");
        row.name=L"Opacity";row.value=L"2";
        check(!SelRowUsesSlider(row),"quick peek","an out-of-range seed is not silently clamped");
        row.value=L"0.75";
        const auto panel=BuildSelectionPanelMarkup(L"TextBlock",L"TextBlock",0,0,500,0,0,L"",L"",
            L"",0,L"noFile",L"",0,0,false);
        check(panel.find(L"Source unavailable")!=std::wstring::npos &&
            panel.find(L"AutomationProperties.Name=\"XAML source:")!=std::wstring::npos &&
            panel.find(L"DevToolsSelOperationStatus")!=std::wstring::npos,
            "quick peek", "ordinary source absence is quiet and operation status has its own surface");
        check(panel.find(L"x:Name=\"DevToolsSelComment\"") < panel.find(L"x:Name=\"DevToolsSelRows\"") &&
            panel.find(L"Text=\"Comment\"")!=std::wstring::npos,
            "quick peek", "visible comment label and editor precede the property rows in keyboard order");
        check(panel.find(L"AutomationProperties.Name=\"Save comment\"") != std::wstring::npos &&
            panel.find(L"<Grid ColumnSpacing=\"8\" Visibility=\"Visible\">") != std::wstring::npos &&
            panel.find(L"Ctrl+Enter or leave this field to save.") != std::wstring::npos &&
            panel.find(L"$CMTSAVEVIS$") == std::wstring::npos,
            "comment save", "inline Save and keyboard/blur hint are resolved in production markup");
        const auto readOnlyPanel = BuildSelectionPanelMarkup(L"TextBlock", L"TextBlock", 0, 0, 500, 0, 0, L"", L"",
            L"", 0, L"noFile", L"", 0, 0, true);
        check(readOnlyPanel.find(L"<Grid ColumnSpacing=\"8\" Visibility=\"Collapsed\">") != std::wstring::npos,
            "comment save", "read-only inspector hides write affordance and hint");
        const auto likelyPanel = BuildSelectionPanelMarkup(L"TextBlock",L"TextBlock",0,0,500,0,0,L"",L"",
            L"MainPage.xaml",48,L"likely",L"<TextBlock/>",0,0,false);
        check(likelyPanel.find(L"Likely source:") != std::wstring::npos &&
            likelyPanel.find(L"DevToolsConfirmLikelySource") != std::wstring::npos &&
            likelyPanel.find(L"IsChecked=\"False\"") != std::wstring::npos,
            "source attribution", "likely quick-panel source requires an unchecked explicit confirmation");
        const auto likelyComposer = BuildComposerMarkup(L"TextBlock",L"",0,0,L"",L"MainPage.xaml",48,L"likely",L"<TextBlock/>");
        check(likelyComposer.find(L"Review likely XAML") != std::wstring::npos &&
            likelyComposer.find(L"DevToolsConfirmLikelySource") != std::wstring::npos &&
            likelyComposer.find(L"&lt;TextBlock/&gt;") != std::wstring::npos,
            "source attribution", "composer displays the inferred declaration before confirmation");
        check(panel.find(L"DevToolsConfirmLikelySource") == std::wstring::npos && !LikelySourceConfirmed(nullptr),
            "source attribution", "non-likely panel has no confirmation and a missing checkbox never confirms");
        row.authoredKind=L"literal";row.authored=L"0.75";
        check(!SelRowHasExpression(row) && BuildSelRowsMarkup({row},false).find(L"Binding details")==std::wstring::npos,
            "quick peek", "authored literal is not mislabeled as binding provenance");
        row.type=L"";row.editKind=L"none";row.authored.clear();row.value=L"Georgia";row.name=L"FontFamily";
        check(BuildSelRowsMarkup({row},false).find(L"<Expander ")==std::wstring::npos &&
            BuildSelRowsMarkup({row},false).find(L"Georgia")!=std::wstring::npos,
            "quick peek", "read-only font has a real value and no empty disclosure");
        row.name=L"Foreground";row.type=L"Brush";row.editKind=L"color";row.value=L"#80123456";
        check(BuildSelRowsMarkup({row},false).find(L"Color (#AARRGGBB)")!=std::wstring::npos &&
            BuildSelRowsMarkup({row},false).find(L"Text=\"#80123456\"")!=std::wstring::npos &&
            BuildSelRowsMarkup({row},false).find(L"Background=\"#80123456\"")!=std::wstring::npos,
            "quick peek", "color editor shows real alpha-inclusive hex with explicit format");
        row.value=L"#nothex";
        check(!SelectionHasColorSwatch(row),"quick peek","invalid color cannot inject markup or a made-up swatch");
        row.value=L"#80123456";row.editKind=L"text";row.valueType=L"Windows.Foundation.String";
        check(!SelectionHasColorSwatch(row),"quick peek","hex-looking text is not mislabeled as a color");
        check(BuildSelRowsMarkup({row},true).find(L"DevToolsSelEdit")==std::wstring::npos,
            "quick peek", "read-only posture never exposes mutation editors");
        all[0].valueState=L"unresolved";
        const auto available=DevToolsSelCurateRows(all);
        check(available.size()==5 && std::none_of(available.begin(),available.end(),[](const auto& item){return item.name==L"Content";}),
            "quick peek","unresolved complex Content does not displace meaningful values");
        row.name=L"Width";row.type=L"Double";row.editKind=L"number";row.authored.clear();
        for (const auto* symbolic : {L"Auto",L"NaN",L"inf",L"Infinity",L"",L"not-a-number"}) {
            row.value=symbolic;row.valueState.clear();
            check(!SelRowCanEdit(row) && BuildSelRowsMarkup({row},false).find(L"<NumberBox ")==std::wstring::npos,
                "quick numeric seed","nonfinite symbolic or invalid numeric seed never creates a zero-initialized editor");
        }
        row.value=L"NaN";row.valueState=L"unset";
        check(SelectionPreviewValue(row)==L"Auto" && !SelRowCanEdit(row),
            "quick numeric seed","legitimate auto-sized Width remains Auto and read-only");
        row.name=L"MaxWidth";row.value=L"inf";
        check(SelectionPreviewValue(row)==L"Unbounded" && !SelRowCanEdit(row),
            "quick numeric seed","unconstrained maximum is distinct from Auto or zero");
    }
    {
        GeometryObject target;
        g_selTarget=&target;g_cardWrite=WriteQuickValue;
        SelRowSink slider;
        quickNumber=0.75;
        slider.InitSlider(L"Opacity",L"Double",L"0.753",&target,quickNumber);
        slider.Invoke(nullptr,nullptr);
        check(quickWrites==0 && slider.original==L"0.753",
            "quick slider","realization and dismiss never write a rounded initial value");
        quickNumber=0.5;slider.Invoke(nullptr,nullptr);
        check(quickWrites==1 && quickWritten==L"0.5",
            "quick slider","user range change uses the existing write and readback path");
        slider.Invoke(nullptr,nullptr);
        check(quickWrites==1,"quick slider","unchanged slider value does not echo");
        quickNumber=0.6;quickOutcome=DevToolsWriteOutcome::NeedsConfirm;slider.Invoke(nullptr,nullptr);
        check(quickWrites==2 && g_selPending.size()==1 && slider.originalNumber==0.5,
            "quick slider","bound slider refuses replacement before consent");
        g_selPending.clear();quickOutcome=DevToolsWriteOutcome::Ok;quickWrites=0;
        SelRowSink numeric;
        quickNumber=0.75;
        numeric.InitNumber(L"Opacity",L"Double",L"0.750000",&target);
        numeric.Invoke(nullptr,nullptr);
        check(quickWrites==0,"quick editor","numeric realization does not write or reformat seed");
        quickNumber=0.5;numeric.Invoke(nullptr,nullptr);
        check(quickWrites==1 && quickWritten==L"0.5" && numeric.originalNumber==0.5,
            "quick editor","typed numeric change commits invariant-culture value");
        numeric.Invoke(nullptr,nullptr);
        check(quickWrites==1,"quick editor","unchanged numeric value does not echo");
        quickNumberResult=E_FAIL;numeric.Invoke(nullptr,nullptr);
        check(quickWrites==1 && !g_selOperationError.empty() && g_selDismissCommitFailed,
            "quick editor","failed getter is visible and keeps panel open");
        quickNumberResult=S_OK;quickNumber=std::nan("");numeric.Invoke(nullptr,nullptr);
        check(quickWrites==1,"quick editor","empty numeric input cannot write NaN");
        quickNumber=0.6;quickOutcome=DevToolsWriteOutcome::Failed;numeric.Invoke(nullptr,nullptr);
        check(quickWrites==2 && numeric.originalNumber==0.5 && g_selOperationError.find(L"Couldn't set")!=std::wstring::npos,
            "quick editor","failed write retains original seed and actionable status");
        quickOutcome=DevToolsWriteOutcome::NeedsConfirm;numeric.Invoke(nullptr,nullptr);
        check(quickWrites==3 && g_selPending.size()==1 && numeric.originalNumber==0.5,
            "quick editor","binding replacement is refused before consent");
        g_selRowSinks={&numeric};
        check(!SelectionSourceIsTextEditor(&target,false),
            "quick editor","numeric Enter remains owned by NumberBox rather than panel text commit");
        SelConfirmSink confirm;confirm.Init(L"Opacity");confirm.Invoke(nullptr,nullptr);
        check(quickWrites==4 && g_selPending.empty() && numeric.originalNumber==0.6 && g_selOperationError.empty(),
            "quick editor","confirmed replacement advances seed and clears its error");
        numeric.Invoke(nullptr,nullptr);
        check(quickWrites==4,"quick editor","dismiss after confirmation cannot repeat the mutation");
        quickOutcome=DevToolsWriteOutcome::Ok;quickReentrant=true;quickNumber=0.8;
        numeric.Invoke(nullptr,nullptr);
        check(quickWrites==5 && numeric.originalNumber==0.6,
            "quick editor","reentrant selection change cannot update new panel or old seed");
        numeric.Invoke(nullptr,nullptr);
        check(quickWrites==5,"quick editor","retired numeric callback is inert");
        SelRowSink text;
        text.InitText(L"Text",L"String",L"before",&target);
        g_selRowSinks={&text};
        GeometryObject header;
        check(SelectionSourceIsTextEditor(&target,false) && !SelectionSourceIsTextEditor(&header,false) &&
            !SelectionSourceIsTextEditor(nullptr,false),"quick editor","only current text editor intercepts Enter");
        {
            // A TextBox cannot hold every string: a single-line box keeps the first line, a multi-line box stores
            // line breaks as '\r'. Closing it unchanged must write nothing, and an edit keeps the value's own breaks.
            const std::wstring multi=L"Make room for\nwhat matters.";
            const unsigned writesBefore=quickWrites;
            const auto saveOutcome=quickOutcome;quickOutcome=DevToolsWriteOutcome::Ok;quickReentrant=false;
            g_cardReadInput=CommentReadInput;
            for (const auto* shown : {L"Make room for",L"Make room for\rwhat matters."}) {
                commentInput=shown;
                SelRowSink row;row.InitText(L"Text",L"String",multi,&target);
                row.Invoke(nullptr,nullptr);
                check(quickWrites==writesBefore && row.original==multi,"quick text","closing an unedited multi-line editor writes nothing");
            }
            commentInput=L"Make room for\rwhat matters.";
            SelRowSink row;row.InitText(L"Text",L"String",multi,&target);
            g_selRowSinks={&row};
            check(!SelectionSourceIsTextEditor(&target,false) && SelectionSourceIsTextEditor(&target,true),
                "quick text","Enter is a line break in a multi-line editor and Ctrl+Enter applies");
            commentInput=L"Make room for\ryou.";
            row.Invoke(nullptr,nullptr);
            check(quickWrites==writesBefore+1 && quickWritten==L"Make room for\nyou.","quick text","an edit keeps the value's \\n line breaks");
            SelRowSink crlf;commentInput=L"a\rb";crlf.InitText(L"Text",L"String",L"a\r\nb",&target);
            commentInput=L"a\rc";crlf.Invoke(nullptr,nullptr);
            check(quickWrites==writesBefore+2 && quickWritten==L"a\r\nc","quick text","an edit keeps the value's \\r\\n line breaks");
            check(XmlEscapeLines(L"a\r\nb")==L"a&#xD;&#xA;b","quick text","markup keeps both line-break characters");
            DevToolsCardRow multiRow;multiRow.name=L"Text";multiRow.type=L"String";multiRow.editKind=L"text";multiRow.value=multi;
            const std::wstring rowMarkup=BuildSelRowsMarkup({multiRow},false);
            const size_t editor=rowMarkup.find(L"<TextBox x:Name=\"DevToolsSelEdit0\"");
            const size_t accepts=rowMarkup.find(L"AcceptsReturn=\"True\"",editor), seeded=rowMarkup.find(L" Text=\"Make room for&#xA;",editor);
            check(editor!=std::wstring::npos && accepts!=std::wstring::npos && seeded!=std::wstring::npos && accepts<seeded,
                "quick text","a multi-line value accepts returns before its text is set, so the editor keeps every line");
            g_cardReadInput=nullptr;g_selRowSinks={&text};quickOutcome=saveOutcome;
        }
        ++g_selGen;
        check(!SelectionSourceIsTextEditor(&target,false),"quick editor","retired text editor cannot intercept disclosure Enter");
        quickReentrant=false;g_selPreviewProperties={L"Opacity"};g_cardRead=ReadQuickRows;
        UpdateSelectionPreview(L"Opacity");
        check(quickReads==1 && g_selOperationError.empty(),
            "quick preview","successful write rereads actual effective value instead of echoing requested input");
        quickReadSucceeded=false;UpdateSelectionPreview(L"Opacity");
        check(quickReads==2 && g_selOperationError.find(L"preview couldn't refresh")!=std::wstring::npos,
            "quick preview","readback failure remains explicit after successful mutation");
        DevToolsCardRow coerced;coerced.type=L"Double";coerced.value=L"0.4";
        check(SelectionPreviewValue(coerced)==L"0.4",
            "quick preview","fresh effective scalar is the preview display authority");
        g_cardRead=nullptr;g_selPreviewProperties.clear();
        for (const auto* invalid : {L"Auto",L"NaN",L"inf",L"",L"1junk"}) {
            SelRowSink unsupported;
            unsupported.InitNumber(L"Width",L"Double",invalid,&target);
            quickNumber=0;g_selRowSinks={&unsupported};
            const auto before=quickWrites;
            DevToolsSelFlushCommits();
            check(quickWrites==before && !std::isfinite(unsupported.originalNumber),
                "quick numeric seed","untouched symbolic or invalid dimension cannot become zero on panel close");
        }
        g_selTarget=nullptr;g_cardWrite=nullptr;g_selRowSinks.clear();
        g_selDismissCommitFailed=false;g_selOperationError.clear();g_selOperationProperty.clear();
        quickReentrant=false;
    }
    {
        SwitchObject popup;
        GeometryObject panel, icon, target;
        auto setup = [&] {
            g_selPanel=&panel;panel.AddRef();g_selIcon=&icon;icon.AddRef();
            g_selPopup=&popup;popup.AddRef();g_selTarget=&target;target.AddRef();
            popup.popupOpen=true;g_cardWrite=WriteQuickValue;
            g_selDismissCommitFailed=false;g_selDismissVisualClosed=false;
            dismissClock=0;dismissClosedAt=-1;dismissWriteCost=750'000;
        };
        setup();
        SelRowSink numeric;
        numeric.InitNumber(L"Width",L"Double",L"10",&target);
        quickNumber=20;quickOutcome=DevToolsWriteOutcome::Ok;g_selRowSinks={&numeric};
        check(DismissSelectionPanel() && dismissClosedAt==750'000 && !popup.popupOpen && g_selPanel==&panel,
            "dismiss phases","visual close follows controlled slow commit but does not wait for timer teardown");
        dismissClock+=5'000'000;
        check(!popup.popupOpen && g_selTarget==&target,
            "dismiss phases","starved teardown retains editor state without keeping popup visible");
        DevToolsSelDismissTimerProc(nullptr,0,0,0);
        check(g_selPanel==nullptr && panel.refs==1 && icon.refs==1 && target.refs==1 && popup.refs==1,
            "dismiss phases","later teardown releases the retained subtree exactly once");
        setup();
        numeric.InitNumber(L"Width",L"Double",L"10",&target);g_selRowSinks={&numeric};
        quickOutcome=DevToolsWriteOutcome::Failed;
        check(!DismissSelectionPanel() && popup.popupOpen && dismissClosedAt==-1 && !g_selDismissTimer,
            "dismiss phases","failed synchronous write keeps the draft and visible panel");
        quickOutcome=DevToolsWriteOutcome::Ok;
        check(DismissSelectionPanel() && !popup.popupOpen,"dismiss phases","explicit retry can close after successful write");
        SetSelectionOperationError(L"Width",L"Late blur failed");
        DevToolsSelDismissTimerProc(nullptr,0,0,0);
        check(popup.popupOpen && g_selPanel==&panel && !g_selDismissVisualClosed,
            "dismiss phases","late failure restores the same panel without replaying a write");
        ClearSelectionAnchor();
        g_cardWrite=nullptr;dismissWriteCost=0;
    }
    {
        // Closing the quick peek hands focus back to the app with pointer state, and only when focus is in the peek.
        SwitchObject popup;
        GeometryObject panel, icon, appElement, keyArgs, catcher;
        keyArgs.keyArgs = true;
        auto open = [&] {
            g_selPanel=&panel;panel.AddRef();g_selIcon=&icon;icon.AddRef();
            g_selPopup=&popup;popup.AddRef();popup.popupOpen=true;
            g_selDismissCommitFailed=false;g_selDismissVisualClosed=false;g_selRowSinks.clear();
        };
        auto teardown = [&] { if (g_selDismissTimer) DevToolsSelDismissTimerProc(nullptr,0,0,0); ClearSelectionAnchor(); };
        open();
        focusInPanel=true;focusedOutsidePanel=&appElement;pointerFocusCalls=0;dismissFocusState=-1;
        check(DismissSelectionPanel() && pointerFocusCalls==1,
            "panel focus","closing the peek with focus in it returns focus to the app with pointer state");
        check(dismissFocusState==3,"panel focus","the commit step focuses Close programmatically, not with keyboard state");
        teardown();
        open();
        focusInPanel=false;pointerFocusCalls=0;
        check(DismissSelectionPanel() && pointerFocusCalls==0,"panel focus","focus already in the app is left alone");
        teardown();
        focusedOutsidePanel=nullptr;focusInPanel=false;

        // Esc closes one layer at a time: the open panel, not pick mode.
        SwitchObject canvas;
        open();
        g_canvasChildren=&canvas;g_pickCatcher=&catcher;catcher.AddRef();g_selectedHandle=11;
        commentKey=VK_ESCAPE;escapeHandled=false;
        OnAppEscape(nullptr,&keyArgs);
        check(escapeHandled && g_pickCatcher==&catcher && !popup.popupOpen,
            "esc layers","Esc with a panel open closes only the panel and keeps pick mode on");
        teardown();
        if (g_pickCatcher) { g_pickCatcher->Release(); g_pickCatcher=nullptr; }
        g_canvasChildren=nullptr;g_selectedHandle=0;
    }
    {
        // A click on the element that is already selected keeps it; a click elsewhere dismisses the panel.
        SwitchDiagnostics diag;
        PickerHosts hosts(diag);
        SwitchObject popup;
        GeometryObject panel, icon, catcher;
        g_pickDiag=&diag;g_pickRoot=10;g_pickCatcher=&catcher;
        for (const InstanceHandle under : {InstanceHandle(11), InstanceHandle(12)}) {
            g_selPanel=&panel;panel.AddRef();g_selIcon=&icon;icon.AddRef();
            g_selPopup=&popup;popup.AddRef();popup.popupOpen=true;g_selRowSinks.clear();
            g_selectedHandle=11;diag.hitHandle=under;dismissFocusState=-1;
            OnCatcherClick(&catcher,nullptr);
            check(under==11 ? (dismissFocusState==-1 && popup.popupOpen && g_selectedHandle==11) : dismissFocusState==3,
                "selected click", under==11 ? "clicking the selected element keeps it and its panel"
                                            : "clicking another element still dismisses the panel");
            if (g_selDismissTimer) DevToolsSelDismissTimerProc(nullptr,0,0,0);
            ClearSelectionAnchor();
        }
        g_pickDiag=nullptr;g_pickRoot=g_selectedHandle=0;g_pickCatcher=nullptr;diag.hitHandle=0;
    }
    {
        // An explicit Save that is saved and linked to source closes the panel; saving on blur keeps it open.
        SwitchDiagnostics diagnostics;
        g_pickDiag=&diagnostics;
        g_srcRead=[](InstanceHandle, std::wstring* file, unsigned* line, unsigned*) { *file=L"MainPage.xaml"; *line=48; return true; };
        g_cardRead=[](IInspectable*, std::wstring*, std::wstring*, std::vector<DevToolsCardRow>*,
            std::wstring* state, std::wstring*) { *state=L"available"; return true; };
        SwitchObject popup;
        GeometryObject input, panel, icon;
        g_selComment=&input;g_cardReadInput=CommentReadInput;
        wchar_t fakeCli[]=L"never-executed.exe";
        g_cliExe.store(fakeCli);guestWriterTest=true;
        g_wireOf=[](InstanceHandle raw) { return raw + 1000ull; };
        for (const bool explicitSave : {false, true}) {
            g_selPanel=&panel;panel.AddRef();g_selIcon=&icon;icon.AddRef();
            g_selPopup=&popup;popup.AddRef();popup.popupOpen=true;g_selRowSinks.clear();
            g_selComment=&input;input.AddRef();
            g_selDismissCommitFailed=false;g_selDismissVisualClosed=false;
            SetCommentTarget(11,false);
            g_selCommentId=L"save-closes";g_selCommentSaved.clear();g_guestCommentWrite={};
            commentInput=L"Warmer color.";
            if (explicitSave) OnSelCommentSaveClick(nullptr,nullptr); else DevToolsSelCommitComment();
            commentExitCode=0;SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr,0,0,0);
            check(g_selCommentSaved==L"Warmer color." && popup.popupOpen!=explicitSave,"comment save",
                explicitSave ? "an explicit save linked to source closes the panel" : "saving on blur keeps the panel open");
            if (g_selDismissTimer) DevToolsSelDismissTimerProc(nullptr,0,0,0);
            ClearSelectionAnchor();
        }
        g_cliExe.store(nullptr);guestWriterTest=false;g_wireOf=nullptr;g_guestCommentWrite={};
        g_selComment=nullptr;g_cardReadInput=nullptr;g_selCommentSaved.clear();g_selCommentId.clear();
        g_pickDiag=nullptr;g_srcRead=nullptr;g_cardRead=nullptr;g_selHandle=g_selCommentWire=0;g_pins.clear();
    }
    {
        SwitchDiagnostics diag;
        GeometryObject catcher, editor, stale;
        PickerHosts hosts(diag);
        g_pickDiag = &diag; g_pickRoot = 10; g_pickCatcher = &catcher;
        g_selectedHandle = g_lastHighlightHandle = 11;
        for (bool composer : {false, true}) {
            g_selPanel = composer ? nullptr : &editor;
            g_composerUi = composer ? &editor : nullptr;
            g_hoverLastHandle = 11; g_hoverLastTick = 0; g_pressValid = false;
            const auto hits = diag.hits;
            OnCatcherPointerMoved(&catcher, nullptr);
            OnCatcherPointerPressed(&catcher, nullptr);
            OnCatcherPointerWheel(&catcher, nullptr);
            check(diag.hits == hits && g_selectedHandle == 11 && g_lastHighlightHandle == 11,
                "picker editor pause", composer ? "composer prevents hit-testing and outline changes" : "quick panel prevents hit-testing and outline changes");
            check(!g_pressValid, "picker editor pause", "editor does not latch an underlying press");
            g_selPanel = g_composerUi = nullptr;
            g_hoverLastTick = 0;
            OnCatcherPointerMoved(&catcher, nullptr);
            check(diag.hits == hits + 1 && g_pickCatcher == &catcher,
                "picker editor pause", "closing editor resumes existing picker intent");
            g_lastHighlightHandle = 11;
        }
        g_pressValid = false;
        OnCatcherPointerPressed(&stale, nullptr);
        check(!g_pressValid, "picker generation", "retired catcher callback cannot latch a press");
        OnCatcherPointerPressed(&catcher, nullptr);
        check(g_pressValid, "picker cancellation", "current catcher latches a press");
        OnCatcherPointerCanceled(&stale, nullptr);
        check(g_pressValid, "picker cancellation", "retired catcher cannot cancel the current press");
        OnCatcherPointerCanceled(&catcher, nullptr);
        const auto canceledHits = diag.hits;
        OnCatcherClick(&catcher, nullptr);
        check(!g_pressValid && diag.hits == canceledHits && g_selectedHandle == 11,
            "picker cancellation", "cancel clears press and trailing release does not commit");
        g_composerUi = &editor;
        OnCatcherPointerPressed(&catcher, nullptr);
        OnCatcherClick(&catcher, nullptr);
        check(!g_pressValid && diag.hits == canceledHits && g_composerUi == &editor,
            "picker editor pause", "composer blocks commit without discarding editor");
        g_composerUi = nullptr;
        diag.hitResult = E_FAIL;
        g_hoverLastTick = 0;
        OnCatcherPointerMoved(&catcher, nullptr);
        check(g_lastHighlightHandle == 11 && g_selectedHandle == 11,
            "picker failure", "hover getter failure preserves the selected outline");
        InstanceHandle picked = 99; RECT bounds{};
        check(DevToolsOverlay_Pick(&diag, 10, 0, 0, nullptr, nullptr, &picked, &bounds) == E_FAIL && !picked,
            "picker failure", "unexpected hit-test failure is not a normal empty hit");
        diag.hitResult = S_OK;
        check(DevToolsOverlay_Pick(&diag, 10, 0, 0, nullptr, nullptr, &picked, &bounds) == S_FALSE && !picked,
            "picker failure", "empty hit remains S_FALSE");
        diag.hitHandle = 11;
        OnCatcherPointerPressed(&catcher, nullptr);
        OnCatcherClick(&catcher, nullptr);
        check(!g_pressValid && g_selectedHandle == 11 && g_lastHighlightHandle == 11,
            "picker cancellation", "next press and successful pick recover without toggling");
        g_pickCatcher = nullptr;
        const auto hits = diag.hits;
        OnCatcherPointerMoved(&catcher, nullptr);
        check(diag.hits == hits && !g_pickCatcher, "picker editor pause", "disabled picker cannot be rearmed by a late callback");
        g_pickDiag = nullptr; g_pickRoot = g_selectedHandle = g_lastHighlightHandle = 0;
        g_pressValid = false;
    }
    {
        SwitchDiagnostics diag;
        GeometryObject catcher;
        PickerHosts hosts(diag);
        diag.mainTarget.runtimeClass = L"Microsoft.UI.Xaml.Shapes.Rectangle";
        g_pickDiag = &diag; g_pickRoot = 10; g_pickCatcher = &catcher;
        g_pickIsOverlay = IsPickChrome;
        g_srcRead = ReadPickedSource;
        for (const auto& stack : {std::vector<InstanceHandle>{99, 11, 10},
                                 std::vector<InstanceHandle>{99, 11, 21, 10},
                                 std::vector<InstanceHandle>{11}}) {
            diag.hitStack = stack;
            pickSourceReads = 0;
            const auto hits = diag.hits;
            InstanceHandle picked = 0; RECT bounds{};
            const HRESULT hr = DevToolsOverlay_Pick(&diag, 10, 0, 0, nullptr, IsPickChrome, &picked, &bounds);
            check(hr == S_OK && picked == 11 && g_lastHighlightHandle == 11,
                "exact picker", "source-less Shape stays selected ahead of authored parent/sibling and chrome");
            check(GeometryRect(bounds, 32, 161, 132, 241),
                "exact picker", "highlight geometry belongs to the actual hit, not its source-mapped neighbour");
            check(diag.hits == hits + 1 && pickSourceReads == 0,
                "exact picker", "selection does not read source or re-hit to find a replacement");
            g_hoverLastHandle = g_hoverLastTick = 0;
            OnCatcherPointerMoved(&catcher, nullptr);
            check(g_lastHighlightHandle == 11, "exact picker", "hover previews the exact Shape");
            OnCatcherPointerPressed(&catcher, nullptr);
            OnCatcherClick(&catcher, nullptr);
            check(g_selectedHandle == 11 && g_lastHighlightHandle == 11,
                "exact picker", "release commits the same Shape that hover previewed");
        }
        diag.hitStack.clear(); diag.hitHandle = 0;
        OnCatcherPointerPressed(&catcher, nullptr);
        OnCatcherClick(&catcher, nullptr);
        check(g_selectedHandle == 0 && g_lastHighlightHandle == 0 && g_pickCatcher == &catcher,
            "exact picker", "empty hit clears selection without disarming");
        diag.hitHandle = 11;
        OnCatcherPointerPressed(&catcher, nullptr);
        OnCatcherClick(&catcher, nullptr);
        check(g_selectedHandle == 11 && g_lastHighlightHandle == 11,
            "exact picker", "next valid hit recovers after empty space");
        g_srcRead = nullptr; g_pickIsOverlay = nullptr;
        g_pickCatcher = nullptr; g_pickDiag = nullptr;
        g_pickRoot = g_selectedHandle = g_lastHighlightHandle = 0;
        g_pressValid = false;
    }
    {
        SwitchDiagnostics diagnostics;
        g_pickDiag = &diagnostics;
        g_srcRead = [](InstanceHandle, std::wstring* file, unsigned* line, unsigned* column) {
            *file = L"MainPage.xaml"; *line = 48; *column = 128; return true;
        };
        g_cardRead = [](IInspectable*, std::wstring*, std::wstring*, std::vector<DevToolsCardRow>*,
            std::wstring* state, std::wstring* xaml) { *state = L"likely"; *xaml = L"<TextBlock/>"; return true; };
        for (bool composer : {false, true}) {
            SetCommentTarget(11, composer);
            const auto before = commentLaunches;
            check(!LaunchCommentAdd(L"retained draft", L"likely-note", composer) && before == commentLaunches &&
                g_commentSourceError.find(L"Nothing was saved") != std::wstring::npos,
                "source attribution", "each editor refuses likely capture without explicit confirmation before launching writer");
        }
        g_pickDiag = nullptr; g_srcRead = nullptr; g_cardRead = nullptr;
        ClearSelectionAnchor(); g_composerHandle = g_composerCommentWire = 0;
    }
    {
        GeometryObject input;
        g_selComment = &input;
        g_cardReadInput = CommentReadInput;
        g_selCommentId = L"composer-exact";
        wchar_t fakeCli[] = L"never-executed.exe";
        g_cliExe.store(fakeCli);
        guestWriterTest = true;
        g_wireOf = [](InstanceHandle raw) { return raw + 1000ull; };
        SetCommentTarget(11, false);
        g_guestCommentWrite = {};
        for (const auto& text : {std::wstring(L"  first\nsecond\n"), std::wstring(L"\tfirst\r\nsecond\r\n")}) {
            commentInput = text;
            g_selCommentSaved = L"old text";
            commentLaunches = 0;
            DevToolsSelCommitComment();
            check(commentLaunches == 1 && g_selCommentSaved == L"old text" && g_guestCommentWrite.process,
                "comment commit", "local spawn does not claim persistence");
            int argc = 0;
            auto args = CommandLineToArgvW(commentCommand.c_str(), &argc);
            bool exact = false;
            for (int i = 0; args && i + 1 < argc; ++i)
                if (std::wstring(args[i]) == L"-t") exact = args[i + 1] == text;
            check(exact, "comment commit", "actual LaunchCommentAdd command carries exact text to CLI");
            if (args) LocalFree(args);
            OnSelCommentSaveClick(nullptr, nullptr);
            check(commentLaunches == 1, "comment commit", "Save click after blur does not start a second writer");
            commentExitCode = 0; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            check(g_selCommentSaved == text && g_guestCommentWrite.status == L"Saved. Not linked to source.",
                "comment commit", "acknowledgement caches exact text and reports local persistence of a sourceless element");
            DevToolsSelCommitComment();
            check(commentLaunches == 1 && g_selCommentSaved == text, "comment commit",
                "actual no-op commit launches no writer and preserves saved cache");
            commentInput = DevToolsCommentText::NormalizeLineEndings(text);
            DevToolsSelFlushCommits();
            check(commentLaunches == 1 && g_selCommentSaved == text, "comment commit",
                "actual dismiss flush preserves CRLF cache despite normalized TextBox input");
        }
        check(input.refs == 1, "comment commit", "input references balanced");
        g_cliExe.store(nullptr);
        commentInput=L"retained unsaved draft";g_selCommentSaved=L"saved text";
        g_selDismissCommitFailed=false;
        DevToolsSelCommitComment();
        check(g_selDismissCommitFailed && g_selCommentSaved==L"saved text" &&
            commentInput==L"retained unsaved draft" && g_guestCommentWrite.failed &&
            !g_guestCommentWrite.status.empty(),
            "comment commit","unavailable local writer retains draft with visible dismiss failure");
        commentInput.clear();g_selDismissCommitFailed=false;
        DevToolsSelCommitComment();
        check(g_selDismissCommitFailed && g_selCommentSaved==L"saved text",
            "comment commit","failed clear-to-delete also refuses dismissal");
        g_selComment = nullptr;
        g_cardReadInput = nullptr;
        g_selCommentSaved.clear();
        g_selCommentId.clear();
        g_selDismissCommitFailed=false;g_selOperationError.clear();g_selOperationProperty.clear();
        g_guestCommentWrite = {}; guestWriterTest = false; g_wireOf = nullptr;
        g_selHandle = g_selCommentWire = 0; g_pins.clear();
    }
    {
        GeometryObject input;
        g_selComment = &input; g_cardReadInput = CommentReadInput;
        wchar_t fakeCli[] = L"never-executed.exe";
        g_cliExe.store(fakeCli); guestWriterTest = true;
        g_wireOf = [](InstanceHandle raw) { return raw + 1000ull; };
        SetCommentTarget(11, false);
        g_selCommentId = L"local-draft"; g_selCommentSaved = L"old";
        g_guestCommentWrite = {};
        commentInput = L"submitted";
        OnSelCommentSaveClick(nullptr, nullptr);
        commentInput = L"newer exact draft\r\n ";
        commentExitCode = 0; SetEvent(commentProcess);
        GuestCommentTimerProc(nullptr, 0, 0, 0);
        check(g_selCommentSaved == L"submitted" && commentInput == L"newer exact draft\r\n " &&
            g_guestCommentWrite.draft == commentInput && g_guestCommentWrite.failed &&
            g_guestCommentWrite.status.find(L"newer draft is not saved") != std::wstring::npos,
            "local writer", "completion preserves a newer draft and distinguishes it from saved text");
        OnSelCommentSaveClick(nullptr, nullptr);
        const auto submitted = g_guestCommentWrite.submitted;
        g_wireOf = [](InstanceHandle raw) { return raw + 2000ull; };
        commentExitCode = 0; SetEvent(commentProcess);
        GuestCommentTimerProc(nullptr, 0, 0, 0);
        check(!GuestCommentMatchesEditor(false) && g_selCommentSaved == L"submitted" &&
            commentInput == submitted, "local writer", "reused element lifetime rejects stale completion");
        g_guestCommentWrite = {};
        SetCommentTarget(11, false);
        for (const DWORD outcome : {DWORD(1), DWORD(0)}) {
            g_selCommentId = L"local-delete"; g_selCommentSaved = commentInput = L"saved";
            g_guestCommentWrite = {};
            SelDeleteComment();
            check(g_guestCommentWrite.process && g_selCommentSaved == L"saved",
                "local writer", "delete does not clear saved text before acknowledgement");
            commentExitCode = outcome; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            check(outcome ? g_selCommentSaved == L"saved" && g_guestCommentWrite.failed :
                g_selCommentSaved.empty() && !g_guestCommentWrite.failed,
                "local writer", "failed delete retains saved text and only successful delete clears it");
        }
        for (const DWORD saveOutcome : {DWORD(1), DWORD(0)}) {
            g_selCommentId = L"queued-delete"; g_selCommentSaved = L"old";
            commentInput = L"edited before Delete";
            g_guestCommentWrite = {}; commentLaunches = 0;
            DevToolsSelCommitComment();
            SelDeleteComment();
            check(commentLaunches == 1 && g_guestCommentWrite.deleteRequested,
                "local writer", "Delete after blur is retained behind the in-flight save");
            commentInput = L"typed after Delete";
            commentExitCode = saveOutcome; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            check(commentLaunches == 2 && g_guestCommentWrite.process &&
                g_guestCommentWrite.editor == GuestCommentEditor::Delete,
                "local writer", "queued deletion starts after either successful or failed save");
            commentExitCode = 0; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            check(g_selCommentSaved.empty() && commentInput == L"typed after Delete" &&
                g_guestCommentWrite.status.find(L"newer draft") != std::wstring::npos,
                "local writer", "delete acknowledgement preserves text entered after the delete request");
        }
        for (const bool pending : {false, true}) {
            g_anchorOf = [](InstanceHandle, std::wstring* anchor) { *anchor = L"local:discard"; return true; };
            g_selCommentId = L"discarded-draft"; g_selCommentSaved = L"old";
            commentInput = L"discard me";
            g_guestCommentWrite = {};
            DevToolsSelCommitComment();
            if (!pending) {
                commentExitCode = 1; SetEvent(commentProcess);
                GuestCommentTimerProc(nullptr, 0, 0, 0);
            }
            ++g_selGen; g_selCommentId = L"reopened-editor";
            GuestCommentRestoreDraft(false);
            check(g_selCommentId == L"discarded-draft" && g_guestCommentWrite.generation != g_selGen,
                "local writer", "recovered draft retains original completion generation");
            DiscardSelectionCommentDraft();
            if (pending) {
                GuestCommentRememberDraft(false);
                check(g_guestCommentWrite.discarded && g_guestCommentWrite.draft.empty(),
                    "local writer", "explicit discard cannot recapture the discarded draft during teardown");
                ++g_selGen;
                commentExitCode = 1; SetEvent(commentProcess);
                GuestCommentTimerProc(nullptr, 0, 0, 0);
            }
            check(!g_guestCommentWrite.process && !g_guestCommentWrite.failed && GuestCommentCanEdit(999),
                "local writer", "discard releases failed-write lockout even when completion arrives later");
        }
        for (const DWORD outcome : {DWORD(0), DWORD(1)}) {
            g_selCommentId = L"restored-text"; g_selCommentSaved = L"old";
            g_guestCommentWrite = {}; commentLaunches = 0;
            commentInput = L"submitted";
            DevToolsSelCommitComment();
            commentInput = L"old"; g_selDismissCommitFailed = false;
            DevToolsSelCommitComment();
            check(g_guestCommentWrite.process && g_selDismissCommitFailed && commentLaunches == 1,
                "local writer", "restoring earlier saved text cannot bypass an in-flight write");
            commentExitCode = outcome; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            ++g_selGen; g_selCommentId = L"reopened-noop";
            GuestCommentRestoreDraft(false);
            check(g_selCommentId == L"restored-text" && g_guestCommentWrite.generation != g_selGen,
                "local writer", "no-op retirement uses the recovered draft rather than its old completion generation");
            commentInput = g_selCommentSaved;
            DevToolsSelCommitComment();
            if (outcome) {
                check(g_guestCommentWrite.process && commentLaunches == 2,
                    "local writer", "unconfirmed exit cannot establish a no-op even when text matches the old cache");
                commentExitCode = 0; SetEvent(commentProcess);
                GuestCommentTimerProc(nullptr, 0, 0, 0);
            }
            check(!g_guestCommentWrite.failed && GuestCommentCanEdit(999) &&
                commentLaunches == (outcome ? 2u : 1u),
                "local writer", "known persistence releases the retained draft without a success-shaped fallback");
        }
        g_selCommentId = L"explicit-failed-close"; g_selCommentSaved = L"old";
        g_guestCommentWrite = {}; commentInput = L"unconfirmed";
        GeometryObject escapePanel, escapeArgs;
        escapeArgs.keyArgs = true;
        g_selPanel = &escapePanel; escapePanel.AddRef(); input.AddRef();
        const auto beforeEscape = commentLaunches;
        DevToolsSelCommitComment();
        OnSelKeyDown(nullptr, &escapeArgs);
        check(escapeHandled && g_selPanel == &escapePanel && g_guestCommentWrite.process,
            "local writer", "Escape cannot discard an in-flight save");
        commentExitCode = 1; SetEvent(commentProcess);
        GuestCommentTimerProc(nullptr, 0, 0, 0);
        g_selRootLost = true;
        OnSelKeyDown(nullptr, &escapeArgs);
        check(g_selPanel == &escapePanel && g_guestCommentWrite.failed,
            "local writer", "Escape still preserves a closed-root draft until explicit Close");
        g_selRootLost = false;
        OnSelKeyDown(nullptr, &escapeArgs);
        check(!g_selPanel && !g_guestCommentWrite.failed && GuestCommentCanEdit(999) &&
            commentLaunches == beforeEscape + 1 && escapePanel.refs == 1 && escapeArgs.refs == 1,
            "local writer", "Escape after a completed local failure discards without retrying persistence");
        g_selComment = &input; SetCommentTarget(11, false);
        g_selCommentId = L"reopened-pending"; g_selCommentSaved = L"old";
        g_guestCommentWrite = {}; commentLaunches = 0; commentInput = L"submitted";
        DevToolsSelCommitComment();
        ++g_selGen; g_selCommentId = L"new-editor";
        GuestCommentRestoreDraft(false);
        commentInput = L"old";
        OnSelCommentTextChanged(nullptr, nullptr);
        commentExitCode = 0; SetEvent(commentProcess);
        GuestCommentTimerProc(nullptr, 0, 0, 0);
        check(g_selCommentSaved == L"old" && commentInput == L"old" && g_guestCommentWrite.persisted &&
            g_guestCommentWrite.draft == L"old",
            "local writer", "reopened draft survives while completion remains generation-strict");
        DevToolsSelCommitComment();
        check(commentLaunches == 2 && g_guestCommentWrite.process && g_guestCommentWrite.submitted == L"old",
            "local writer", "explicit retry reconciles the earlier acknowledgement instead of skipping a necessary write");
        commentExitCode = 0; SetEvent(commentProcess);
        GuestCommentTimerProc(nullptr, 0, 0, 0);
        g_selCommentId = L"reopened-delete"; g_selCommentSaved = commentInput = L"saved";
        g_guestCommentWrite = {}; commentLaunches = 0;
        SelDeleteComment();
        ++g_selGen; g_selCommentId = L"new-delete-editor";
        GuestCommentRestoreDraft(false);
        commentExitCode = 0; SetEvent(commentProcess);
        GuestCommentTimerProc(nullptr, 0, 0, 0);
        check(g_selCommentSaved == L"saved" && commentInput == L"saved" && g_guestCommentWrite.persisted,
            "local writer", "successful old delete does not clear the reopened editor");
        DevToolsSelCommitComment();
        check(commentLaunches == 2 && g_guestCommentWrite.process && g_guestCommentWrite.submitted == L"saved",
            "local writer", "explicit Save after acknowledged deletion really restores retained text");
        commentExitCode = 0; SetEvent(commentProcess);
        GuestCommentTimerProc(nullptr, 0, 0, 0);
        for (const bool deleting : {false, true}) {
            g_pins.clear();
            SetCommentTarget(11, false);
            g_selCommentId = L"snapshot-race"; g_selCommentSaved = L"old";
            g_guestCommentWrite = {}; commentLaunches = 0; commentInput = L"submitted";
            if (deleting) SelDeleteComment(); else DevToolsSelCommitComment();
            DevToolsOverlayComment submitted{ L"snapshot-race", L"submitted", L"", L"saved-revision" };
            DevToolsOverlayComment newer{ L"snapshot-race", L"newer edit", L"", L"newer-revision" };
            DevToolsOverlay_SetComments(nullptr, 0, deleting ? nullptr : &submitted, deleting ? 0 : 1, nullptr, true);
            DevToolsOverlay_SetComments(nullptr, 0, deleting ? &newer : nullptr, deleting ? 1 : 0, nullptr, true);
            commentExitCode = 0; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            check(deleting ? g_pins.size() == 1 && g_pins[0].text == L"newer edit" : g_pins.empty(),
                "local snapshot", "delayed writer completion cannot replace a newer authoritative snapshot");
            check(commentInput == L"submitted" && g_guestCommentWrite.failed && g_guestCommentWrite.persisted,
                "local snapshot", "superseded completion retains draft and distinguishes persistence from current state");
            OnSelCommentLostFocus(nullptr, nullptr);
            DevToolsSelFlushCommits();
            check(commentLaunches == 1 && !g_guestCommentWrite.process && g_selDismissCommitFailed,
                "local snapshot", "blur and dismissal flush cannot retry a superseded save");
            DevToolsOverlay_SetComments(nullptr, 0, nullptr, 0, nullptr, true);
            check(g_pins.empty(), "local snapshot", "another empty snapshot leaves no resurrected session marker");
            if (deleting) {
                GeometryObject keyArgs; keyArgs.keyArgs = true;
                commentKey = 13; commentControl = g_selFocusedIsComment = true;
                OnSelKeyDown(nullptr, &keyArgs);
                check(escapeHandled && keyArgs.refs == 1, "local snapshot", "Ctrl+Enter explicitly retries without adding a newline");
                commentKey = 27; commentControl = g_selFocusedIsComment = false;
            } else {
                OnSelCommentSaveClick(nullptr, nullptr);
            }
            check(commentLaunches == 2 && g_guestCommentWrite.process,
                "local snapshot", "explicit retry after superseded completion is not mistaken for a saved no-op");
            DevToolsOverlay_SetComments(nullptr, 0, &submitted, 1, nullptr, true);
            SetCommentsShown(false);
            commentExitCode = 0; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            check(g_pins.size() == 1 && g_pins[0].seeded && g_pins[0].text == L"submitted" &&
                !g_guestCommentWrite.failed && g_guestCommentWrite.status == L"Saved. Not linked to source.",
                "local snapshot", "matching retry snapshot remains authoritative and completes normally");
            check(g_commentsShown, "local snapshot", "matching local save reveals markers without replacing the snapshot");
        }
        for (const bool escape : {false, true}) {
            GeometryObject panel, keyArgs;
            keyArgs.keyArgs = true;
            g_selPanel = &panel; panel.AddRef(); input.AddRef(); g_selComment = &input;
            SetCommentTarget(11, false);
            g_selCommentId = L"discard-superseded"; g_selCommentSaved = L"old";
            g_guestCommentWrite = {}; commentLaunches = 0; commentInput = L"submitted";
            OnSelCommentSaveClick(nullptr, nullptr);
            DevToolsOverlay_SetComments(nullptr, 0, nullptr, 0, nullptr, true);
            commentExitCode = 0; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            if (escape) OnSelKeyDown(nullptr, &keyArgs); else OnSelCloseClick(nullptr, nullptr);
            check(commentLaunches == 1 && !g_guestCommentWrite.process && !g_guestCommentWrite.failed &&
                !g_selPanel && g_pins.empty() && panel.refs == 1 && input.refs == 1 && keyArgs.refs == 1,
                "local snapshot", "explicit Close and Escape discard superseded draft without overwriting the store");
        }
        g_pins.clear();
        UpsertPin(11, L"unbound-session", L"persisted", RECT{});
        DevToolsOverlay_SetComments(nullptr, 0, nullptr, 0, nullptr, false);
        check(g_pins.size() == 1, "local snapshot", "unbound push retains session-authored marker behavior");
        DevToolsOverlay_SetComments(nullptr, 0, nullptr, 0, nullptr, true);
        check(g_pins.empty(), "local snapshot", "first authoritative snapshot also retires earlier session-only markers");
        DevToolsOverlay_SetComments(nullptr, 0, nullptr, 0, nullptr, false);
        g_guestCommentWrite = {}; guestWriterTest = false; g_wireOf = nullptr; g_cliExe.store(nullptr);
        g_anchorOf = nullptr;
        g_selComment = nullptr; g_cardReadInput = nullptr;
        g_selHandle = g_selCommentWire = 0; g_selCommentSaved.clear(); g_selCommentId.clear(); g_pins.clear();
        g_selDismissCommitFailed = false;
        check(input.refs == 1, "local writer", "completion and delete references remain balanced");
    }
    {
        GuestCommentAuthority authority{L"638936747284321987", L"0123456789abcdef0123456789abcdef", L"guest-epoch"};
        g_guestCommentAuthority.store(&authority);
        wchar_t fakeCli[] = L"never-executed.exe";
        g_cliExe.store(fakeCli);
        g_cardReadInput = CommentReadInput;
        g_anchorOf = [](InstanceHandle, std::wstring* value) { *value = L"source:Main.xaml|TextBlock#Title"; return true; };
        g_wireOf = [](InstanceHandle raw) { return raw + 1000ull; };
        guestWriterTest = true;
        for (const bool host : {false, true}) {
          g_guestCommentAuthority.store(host ? &authority : nullptr);
          for (const bool composer : {false, true}) {
            for (const DWORD outcome : {DWORD(1), DWORD(0), DWORD(0x57410001), DWORD(0x57410002), DWORD(STILL_ACTIVE)}) {
                if (!host && (outcome == 0x57410001 || outcome == 0x57410002)) continue;
                GeometryObject input;
                input.AddRef();
                g_pickRoot = 10;
                SetCommentTarget(11, false);
                SetCommentTarget(11, true);
                g_selCommentId = g_composerId = L"guest-note";
                g_selCommentSaved = g_composerInitText = L"old text";
                g_selCommentRevision = g_composerRevision = std::wstring(64, L'A');
                g_guestCommentWrite = {};
                if (composer) g_composerBox = &input;
                else g_selComment = &input;
                const std::wstring exact = L" \tfirst\r\n\r\nlast \n";
                commentInput = exact;
                commentLaunches = 0;
                if (composer) OnComposerSaveClick(nullptr, nullptr);
                else DevToolsSelCommitComment();
                check(commentLaunches == 1 && g_guestCommentWrite.process && g_selCommentSaved == L"old text" &&
                    (!composer || g_composerBox), "guest comment", "successful spawn is not persistence acknowledgement");
                check((host
                        ? commentCommand.find(L"--guest-comments") != std::wstring::npos &&
                            commentCommand.find(authority.binding + L"." + authority.epoch) != std::wstring::npos
                        : commentCommand.find(L"--guest-comments") == std::wstring::npos) &&
                    commentCommand.find(L"--from-element") != std::wstring::npos,
                    "comment writer", "exact element is pinned and host credentials are guest-only");
                const auto operation = g_guestCommentWrite.operation;
                if (outcome == STILL_ACTIVE) g_guestCommentWrite.began = GetTickCount64() - 90'001;
                else { commentExitCode = outcome; SetEvent(commentProcess); }
                GuestCommentTimerProc(nullptr, 0, 0, 0);
                check(!g_guestCommentWrite.process && !g_guestCommentTimer, "guest comment", "completion releases owned writer and timer");
                if (outcome == 1 || outcome == STILL_ACTIVE || outcome == 0x57410002) {
                    check(g_guestCommentWrite.failed && g_guestCommentWrite.draft == exact &&
                        (composer ? g_composerBox != nullptr : g_selCommentSaved == L"old text"),
                        "guest comment", "refusal, disconnect, timeout or later host edit retains exact draft without success state");
                    check(!g_guestCommentWrite.status.empty(), "guest comment", "save failure has explicit retained-draft status");
                    if (outcome == 1) {
                        commentInput = DevToolsCommentText::NormalizeLineEndings(exact);
                        if (composer) OnComposerSaveClick(nullptr, nullptr);
                        else DevToolsSelCommitComment();
                        check(g_guestCommentWrite.operation == operation && g_guestCommentWrite.submitted == exact,
                            "guest comment", "retry retains operation and original exact text despite TextBox newline normalization");
                        commentExitCode = 1; SetEvent(commentProcess);
                        GuestCommentTimerProc(nullptr, 0, 0, 0);
                    }
                } else {
                    check(!g_guestCommentWrite.failed && (composer ? g_composerInitText == exact : g_selCommentSaved == exact),
                        "guest comment", "only confirmed host persistence advances saved editor state");
                    if (outcome == 0x57410001)
                        check(g_guestCommentWrite.status.find(L"markers") != std::wstring::npos &&
                            (!composer || g_composerBox), "guest comment", "persisted-but-refresh-failed remains distinct and visible");
                }
                if (composer && g_composerBox) ClearComposer();
                if (!composer) ClearSelectionAnchor();
                check(input.refs == 1, "guest comment", "composer and selection references balance after async completion");
                g_pins.clear();
            }
          }
        }
        {
            GeometryObject input;
            input.AddRef(); g_selComment = &input;
            SetCommentTarget(11, false); g_pickRoot = 10;
            g_selCommentId = L"generation-note"; g_selCommentSaved = L"old";
            g_guestCommentWrite = {};
            commentInput = L"submitted old editor";
            DevToolsSelCommitComment();
            check(g_guestCommentWrite.process != nullptr, "guest comment", "generation control starts a real observed writer");
            ++g_selGen;
            g_selCommentId = L"new-editor"; g_selCommentSaved = L"new editor saved";
            commentInput = L"new editor draft";
            commentExitCode = 0; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            check(g_selCommentSaved == L"new editor saved" && commentInput == L"new editor draft",
                "guest comment", "completion cannot apply to a reused editor generation or comment identity");
            ClearSelectionAnchor();
            check(input.refs == 1, "guest comment", "reused editor cleanup balances references");
        }
        for (const bool composer : {false, true}) {
            g_wireOf = [](InstanceHandle raw) { return raw + 1000ull; };
            SetCommentTarget(11, composer);
            g_selCommentId = g_composerId = L"reused-raw";
            g_selCommentSaved = g_composerInitText = L"original saved";
            g_guestCommentWrite = {};
            LaunchCommentAdd(L"retained draft", L"reused-raw", composer);
            check(g_guestCommentWrite.process != nullptr,
                "guest comment", "lifetime control starts observed writer");
            const auto operation = g_guestCommentWrite.operation;
            g_wireOf = [](InstanceHandle raw) { return raw + 2000ull; };
            commentExitCode = 1; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            check(!GuestCommentMatchesEditor(composer), "guest comment", "raw reuse rejects old completion despite matching anchor");
            const auto failedWrite = g_guestCommentWrite;
            const auto launches = commentLaunches;
            check(!LaunchCommentAdd(L"retained draft", L"reused-raw", composer) && commentLaunches == launches,
                "guest comment", "retry never repacks an expired editor into the reused raw handle");
            check(g_guestCommentWrite.failed && g_guestCommentWrite.draft == L"retained draft",
                "guest comment", "pre-spawn refusal retains draft");
            g_guestCommentWrite = failedWrite;
            SetCommentTarget(11, composer);
            g_selCommentSaved = g_composerInitText = L"new object's saved text";
            GuestCommentRestoreDraft(composer);
            check((composer ? g_composerInitText : g_selCommentSaved) == L"new object's saved text" &&
                !GuestCommentMatchesEditor(composer), "guest comment", "reopened new generation cannot recover old object's draft");
            LaunchCommentAdd(L"retained draft", L"reused-raw", composer);
            check(g_guestCommentWrite.process &&
                g_guestCommentWrite.operation != operation,
                "guest comment", "new witness never reuses the old object's acknowledgement operation");
            commentExitCode = 1; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            g_wireOf = nullptr;
            const auto beforeMissingBridge = commentLaunches;
            check(!LaunchCommentAdd(L"retained draft", L"reused-raw", composer) &&
                commentLaunches == beforeMissingBridge && !g_guestCommentWrite.process,
                "guest comment", "missing census bridge refuses before spawning");
            SetCommentTarget(11, composer);
            LaunchCommentAdd(L"untracked draft", L"reused-raw", composer);
            g_wireOf = [](InstanceHandle raw) { return raw + 3000ull; };
            SetCommentTarget(11, composer);
            g_selCommentSaved = g_composerInitText = L"tracked object's saved text";
            GuestCommentRestoreDraft(composer);
            check((composer ? g_composerInitText : g_selCommentSaved) == L"tracked object's saved text" &&
                !GuestCommentMatchesEditor(composer), "guest comment", "untracked editor cannot restore its draft onto a newly tracked object");
        }
        for (const DWORD outcome : {DWORD(0), DWORD(1), DWORD(0x57410001), DWORD(0x57410002)}) {
            g_guestCommentWrite = {};
            g_pickRoot = 10;
            int completed = -1;
            const auto revision = std::wstring(64, L'A');
            check(DevToolsOverlay_ResolveGuestComment(L"resolve-note", revision,
                [&](int result) { completed = result; }), "guest resolve", "actual asynchronous resolve starts owned writer");
            check(completed == -1 && commentCommand.find(L"comments update --status resolved") != std::wstring::npos &&
                commentCommand.find(revision) != std::wstring::npos, "guest resolve", "spawn retains original revision without reporting persistence");
            commentExitCode = outcome; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            check(completed == static_cast<int>(outcome), "guest resolve", "completion reports acknowledgement or failure exactly");
            check(GuestCommentCanEdit(99), "guest resolve", "failed resolution without a prose draft does not lock unrelated editing");
        }
        {
            g_guestCommentWrite = {};
            int completed = -1;
            DevToolsOverlay_ResolveGuestComment(L"old-window-note", std::wstring(64, L'A'),
                [&](int result) { completed = result; });
            ++g_selGen;
            commentExitCode = 0; SetEvent(commentProcess);
            GuestCommentTimerProc(nullptr, 0, 0, 0);
            check(completed == -1, "guest resolve", "selection or surface generation change suppresses stale completion");
        }
        g_guestCommentWrite = {};
        g_guestCommentAuthority.store(nullptr);
        guestWriterTest = false;
        g_cliExe.store(nullptr); g_cardReadInput = nullptr; g_anchorOf = nullptr; g_wireOf = nullptr;
        g_selCommentSaved.clear(); g_selCommentId.clear();
        g_pickRoot = g_selHandle = g_composerHandle = 0;
    }
    {
        SwitchDiagnostics d;
        g_popup = &d.popup; g_popupUi = &d.popup;
        g_appPanel = &d.mainPanel; g_appPanel->AddRef();
        g_canvasStatics = &d.canvas;
        g_pickDiag = &d; g_pickRoot = 10;
        g_selectionPanel.copy_from(&d.mainPanel);
        SwitchSurface(&d, 10, &g_toolbarSurface);
        g_protoW = 800; g_protoH = 700;
        g_commentsShown = true;
        g_removedSinkState = -1; // no live census subscription in this console harness
        g_pins.push_back({11, L"main", L"main note", {}, &d.mainPin, nullptr, L"", true});
        g_pins.push_back({21, L"second", L"second note", {}, &d.secondPin, nullptr, L"", true});
        DevToolsOverlay_RefreshSurfaces(&d, 10);
        WireResizeTracking(&d.mainPanel);
        WireAppEscapeOnce();
        check(DevToolsOverlay_SetFocusTracking(true), "surface move", "actual focus owner wiring succeeds");
        TrackingTick();
        check(d.mainPin.left == 136 && d.mainPin.top == 161 && d.mainPin.visibility == 0 && d.secondPin.visibility == 1,
            "surface move", "initial tracking uses main content DIPs");
        {
            GeometryObject ancestor;
            d.mainTarget.parent = &ancestor;
            for (auto element : {static_cast<GeometryObject*>(&d.mainTarget), &ancestor}) {
                for (int state = 0; state < 8; ++state) {
                    if (state == 0) element->loaded = false;
                    if (state == 1) element->visibility = 1;
                    if (state == 2) element->opacity = 0;
                    if (state == 3) element->loadedHr = E_FAIL;
                    if (state == 4) element->visibilityHr = E_FAIL;
                    if (state == 5) element->opacityHr = E_FAIL;
                    if (state == 6) element->parentHr = E_FAIL;
                    if (state == 7) element->opacity = std::numeric_limits<double>::quiet_NaN();
                    RECT box{};
                    geometryTransforms = 0;
                    check(!TryElementRootBounds(&d, 10, 11, &box) && geometryTransforms == 0,
                        "pin presentation", "hidden or unreadable target/ancestor cannot supply display bounds");
                    TrackingTick();
                    check(d.mainPin.visibility == 1 &&
                        g_pins.size() == 2 && g_pins[0].handle == 11,
                        "pin presentation", "marker collapses while saved project count and anchor stay intact");
                    element->loaded = true; element->visibility = 0; element->opacity = 1;
                    element->loadedHr = element->visibilityHr = element->opacityHr = element->parentHr = S_OK;
                    TrackingTick();
                    check(d.mainPin.left == 136 && d.mainPin.top == 161 && d.mainPin.visibility == 0,
                        "pin presentation", "marker returns after state changes without moving its geometry");
                }
            }
            ancestor.opacity = 0.25;
            TrackingTick();
            check(d.mainPin.left == 136, "pin presentation", "partially transparent content remains present");
            ancestor.opacity = 1;
            ancestor.parent = &d.mainTarget;
            RECT box{};
            check(!TryElementRootBounds(&d, 10, 11, &box), "pin presentation", "cyclic ancestry is bounded and refused");
            ancestor.parent = nullptr;
            d.mainTarget.parent = nullptr;
            check(ancestor.refs == 1 && d.mainTarget.refs == 1,
                "pin presentation", "ancestor read failures and cycle guard release every reference");
            g_pins[0].ui = nullptr;
            g_pins[0].bounds = {};
            TrackingTick();
            check(GeometryRect(g_pins[0].bounds, 32, 161, 132, 241) && g_pins[0].handle == 11 &&
                g_pins[0].name.empty(),
                "pin presentation", "undrawn source-less marker can recover through its own live handle");
            g_pins[0].ui = &d.mainPin;
        }
        for (int cycle = 0; cycle != 3; ++cycle) {
            g_selectedHandle = 11;
            g_lastHighlightHandle = 11;
            g_hoverLastHandle = 11;
            g_lastPickBoundsDip = {32,161,132,241};
            const auto move = DevToolsOverlay_MoveToPanel(&d, 20);
            check(SUCCEEDED(move) && g_toolbarSurface.rootHandle == 20 && g_appPanel == &d.secondPanel &&
                g_pickRoot == 10, "surface move", "toolbar moves independently of the pick/tracking root");
            check(g_selectedHandle == 11 && g_lastHighlightHandle == 11 && g_hoverLastHandle == 11 &&
                g_lastPickBoundsDip.right == 132, "surface move", "toolbar move preserves selection and hover geometry");
            check(g_focusTracking && g_focusSource == &d.secondPanel &&
                d.mainPanel.focusAdds == d.mainPanel.focusRemoves &&
                d.secondPanel.focusAdds - d.secondPanel.focusRemoves == 1,
                "surface move", "active focus owner retained and subscription follows second root");
            TrackingTick();
            check(d.mainPin.left == 136 && d.mainPin.top == 161 && d.mainPin.visibility == 0 && d.secondPin.visibility == 1,
                "surface move", "toolbar movement does not retarget selection-root markers");
            RECT box{};
            geometryTransforms = 0;
            check(!TryElementRootBounds(&d, 20, 11, &box) && geometryTransforms == 0,
                "surface move", "explicit second root rejects first-root target before transform");
            check(TryElementRootBounds(&d, 20, 21, &box) && geometryDestination == &d.foreignContent,
                "surface move", "explicit second root measures its own Content independently of toolbar and selection");
            d.secondTarget.x = d.secondTarget.y = 24;
            d.secondTarget.scale = 2;
            DevToolsLayoutBox layout{};
            check(TryElementLayoutBox(&d, 20, 21, &layout) &&
                GeometryRect(layout.box, 24, 24, 224, 184) &&
                GeometryRect(layout.padding, 72, 72, 176, 136),
                "surface move", "moved root preserves positive padding24 and scaled layout geometry");
            d.secondTarget.x = 8; d.secondTarget.y = 49; d.secondTarget.scale = 1;
            check(SUCCEEDED(DevToolsOverlay_MoveToPanel(&d, 10)) && g_pickRoot == 10,
                "surface move", "actual move back publishes main root");
            TrackingTick();
            check(d.mainPin.left == 136 && d.mainPin.top == 161 && d.mainPin.visibility == 0 && d.secondPin.visibility == 1,
                "surface move", "move-back tracking restores main and collapses second");
        }
        check(!switchOpenedWithForeignPin, "surface move", "foreign marker is collapsed before any popup reopen");
        check(!switchTrackedDuringMove, "surface move", "reentrant tracking cannot read half-moved surface");
        switchWindowSearches = 0;
        HWND host = nullptr, island = nullptr;
        ResolveAppWindowCached(&host, &island);
        check(!host && !island && switchWindowSearches == 0,
            "surface move", "active root without host metadata never searches another window");
        check(d.mainPanel.sizeAdds - d.mainPanel.sizeRemoves == 1 &&
              d.secondPanel.sizeAdds == d.secondPanel.sizeRemoves &&
              d.mainPanel.keyAdds - d.mainPanel.keyRemoves == 1 &&
              d.secondPanel.keyAdds == d.secondPanel.keyRemoves,
              "surface move", "old live panels retain no duplicate resize/Escape subscriptions");
        d.secondPanel.failInsert = true;
        g_selectedHandle = 11;
        const auto mainSizeAdds = d.mainPanel.sizeAdds;
        const auto secondSizeAdds = d.secondPanel.sizeAdds;
        check(FAILED(DevToolsOverlay_MoveToPanel(&d, 20)) && g_pickRoot == 10 &&
            g_appPanel == &d.mainPanel && d.mainPanel.child == &d.popup && g_selectedHandle == 11,
            "surface move", "failed attach retains old root and restores old parent");
        check(d.mainPanel.sizeAdds == mainSizeAdds && d.secondPanel.sizeAdds == secondSizeAdds,
            "surface move", "failed move does not rewire subscriptions");
        g_selectedHandle = 0;
        TrackingTick();
        check(d.mainPin.left == 136 && d.mainPin.visibility == 0 && d.secondPin.visibility == 1,
            "surface move", "failed-move tracking stays in main coordinates");
        d.secondPanel.failInsert = false;
        {
            GuestCommentAuthority authority{L"638936747284321987", L"0123456789abcdef0123456789abcdef", L"surface-epoch"};
            g_guestCommentAuthority.store(&authority);
            wchar_t fakeCli[] = L"never-executed.exe";
            g_cliExe.store(fakeCli);
            g_cardReadInput = CommentReadInput;
            g_wireOf = [](InstanceHandle raw) { return raw + 1000ull; };
            guestWriterTest = true;
            for (const bool moveBackBeforeAck : {false, true}) {
                GeometryObject input;
                input.AddRef(); g_selComment = &input;
                SetCommentTarget(11, false); g_selCommentId = L"surface-note"; g_selCommentSaved = L"old";
                g_guestCommentWrite = {};
                commentInput = L"surface draft\r\n exact ";
                DevToolsSelCommitComment();
                check(g_guestCommentWrite.process != nullptr, "guest comment", "surface control starts observed writer");
                check(SUCCEEDED(DevToolsOverlay_MoveToPanel(&d, 20)), "guest comment", "real surface switch while host save is pending");
                if (moveBackBeforeAck)
                    check(SUCCEEDED(DevToolsOverlay_MoveToPanel(&d, 10)), "guest comment", "real move-back precedes old save completion");
                check(g_selComment == &input && input.refs == 2,
                    "guest comment", "toolbar movement preserves the pending editor and its lifetime");
                ClearSelectionAnchor();
                g_selCommentId = L"new-surface-editor"; g_selCommentSaved = L"new surface saved";
                commentExitCode = 0; SetEvent(commentProcess);
                GuestCommentTimerProc(nullptr, 0, 0, 0);
                check(g_selCommentSaved == L"new surface saved" && input.refs == 1,
                    "guest comment", "old completion cannot write new-root or move-back editor and source references are released");
                if (!moveBackBeforeAck)
                    check(SUCCEEDED(DevToolsOverlay_MoveToPanel(&d, 10)), "guest comment", "return to main after old completion");
            }
            g_guestCommentWrite = {};
            g_guestCommentAuthority.store(nullptr);
            guestWriterTest = false;
            g_cliExe.store(nullptr); g_cardReadInput = nullptr; g_wireOf = nullptr;
            g_selCommentSaved.clear(); g_selCommentId.clear();
        }
        g_pins.clear();
        UnwireResizeTracking();
        UnwireAppEscape();
        UnwireFocusTracking();
        DevToolsOverlay_SetFocusTracking(false);
        g_appPanel->Release(); g_appPanel = nullptr;
        g_selectionPanel = nullptr;
        g_hostRootIdentity = nullptr;
        check(d.Balanced() && d.mainPanel.refs == 1 && d.secondPanel.refs == 1 &&
            d.mainTarget.refs == 1 && d.secondTarget.refs == 1 && d.mainPin.refs == 1 && d.secondPin.refs == 1,
            "surface move", "move/rollback/teardown references balanced");
        check(d.mainPanel.sizeAdds == d.mainPanel.sizeRemoves && d.secondPanel.sizeAdds == d.secondPanel.sizeRemoves &&
              d.mainPanel.keyAdds == d.mainPanel.keyRemoves && d.secondPanel.keyAdds == d.secondPanel.keyRemoves,
              "surface move", "all panel subscriptions balanced on teardown");
        check(d.mainPanel.focusAdds == d.mainPanel.focusRemoves && d.secondPanel.focusAdds == d.secondPanel.focusRemoves,
              "surface move", "focus subscriptions balanced on teardown");
        g_popup = g_popupUi = g_canvasStatics = nullptr;
        g_pickDiag = nullptr; g_pickRoot = 0;
        g_selectedHandle = g_lastHighlightHandle = g_hoverLastHandle = 0;
        g_trackTimer = 0; g_escWired = false;
        g_activeSurface = {}; g_toolbarSurface = {}; g_allSurfaces.clear();
        g_commentsShown = false;
    }
    {
        SwitchDiagnostics d;
        GeometryObject catcher, draft;
        g_pickDiag = &d; g_pickRoot = 10;
        SwitchSurface(&d, 10, &g_toolbarSurface);
        g_popup = g_popupUi = &d.popup;
        g_appPanel = &d.mainPanel; g_appPanel->AddRef();
        g_canvasStatics = &d.canvas; g_aRow = &d.canvas;
        g_protoW = 800; g_protoH = 700;
        g_toolbarVisible = true;
        g_failedRecoveryPanel = 0;
        g_removedSinkState = -1;
        ReadHostRootIdentity(g_appPanel, g_hostRootIdentity);
        DevToolsOverlay_RefreshSurfaces(&d, 10);
        const auto originalKey = g_activeSurface.xamlRootKey;
        InstanceHandle moved = 99;
        recoveryFinds = 0; recoveryCandidate = 30;
        check(DevToolsOverlay_IsToolbarVisible(), "navigation host", "loaded attached open host is visible");
        check(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved) == S_FALSE &&
            !moved && recoveryFinds == 0, "navigation host", "healthy host causes no search or reparent");
        d.mainPanel.loaded = false;
        check(!DevToolsOverlay_IsToolbarVisible() && DevToolsOverlay_IsToolbarRequested(),
            "navigation host", "unloaded host is physically hidden but retains requested ownership state");
        d.popup.popupOpen = false;
        check(d.mainPanel.child == &d.popup &&
            SUCCEEDED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            moved == 30 && !d.mainPanel.child && d.replacementPanel.child == &d.popup &&
            g_activeSurface.xamlRootKey == originalKey && DevToolsOverlay_IsToolbarVisible(),
            "navigation host", "unloaded host retaining closed Popup membership recovers on the same canonical root");
        d.mainPanel.loaded = true;
        check(SUCCEEDED(DevToolsOverlay_MoveToPanel(&d, 10)),
            "navigation host", "explicit surface switch can restore the test's original live host");
        recoveryFinds = 0;
        d.mainPanel.loaded = true; d.mainPanel.child = nullptr;
        check(!DevToolsOverlay_IsToolbarVisible(), "navigation host", "orphaned Popup cannot claim visible");
        d.mainPanel.child = &d.popup; d.popup.popupOpen = false;
        check(!DevToolsOverlay_IsToolbarVisible(), "navigation host", "closed Popup cannot claim visible");
        d.popup.popupReadHr = E_FAIL;
        check(FAILED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            !moved && recoveryFinds == 0, "navigation host", "unreadable state does not guess a recovery");
        d.popup.popupReadHr = S_OK;
        recoveryCandidate = 0;
        check(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved) == S_FALSE && !moved,
            "navigation host", "transient no-host leaves the old Popup intact and truthfully hidden");
        DevToolsOwnedState owner;
        owner.RecordSet(DevToolsStateAxis::Toolbar, 7, 0, DevToolsOverlay_IsToolbarRequested());
        check(!owner.Reconcile(DevToolsStateAxis::Toolbar, DevToolsOverlay_IsToolbarRequested()) &&
            owner.Owner(DevToolsStateAxis::Toolbar) == 7 && !DevToolsOverlay_IsToolbarVisible(),
            "navigation ownership", "physical host loss does not disown the original visibility request");
        const auto released = owner.Release(7);
        for (const auto& restore : released.released)
            if (restore.axis == DevToolsStateAxis::Toolbar &&
                DevToolsOverlay_IsToolbarRequested() == (restore.expectedValue != 0))
                DevToolsOverlay_SetToolbarVisible(restore.restoreTo != 0);
        recoveryCandidate = 30;
        check(released.released.size() == 1 && !DevToolsOverlay_IsToolbarRequested() &&
            DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved) == S_FALSE &&
            !moved && !d.popup.popupOpen && g_appPanel == &d.mainPanel,
            "navigation ownership", "disconnect restores hidden intent before replacement and never resurrects UI");
        DevToolsOverlay_SetToolbarVisible(true);
        d.active.identity = &d.foreignIdentity;
        check(d.identity.refs == 2 && FAILED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            g_appPanel == &d.mainPanel,
            "navigation host", "pinned canonical identity rejects a changed root behind the same interface address");
        d.active.identity = &d.identity;
        DevToolsOverlay_SetToolbarVisible(true);
        recoveryCandidate = 20;
        check(FAILED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            g_appPanel == &d.mainPanel && d.mainPanel.child == &d.popup,
            "navigation host", "foreign XamlRoot is refused even if a finder supplies it");
        recoveryCandidate = 30;
        hideDuringFind = true;
        check(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved) == S_FALSE &&
            !DevToolsOverlay_IsToolbarRequested() && g_appPanel == &d.mainPanel,
            "navigation host", "hide during discovery invalidates the pending recovery");
        hideDuringFind = false;
        const auto finds = recoveryFinds;
        check(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved) == S_FALSE &&
            recoveryFinds == finds && !d.popup.popupOpen,
            "navigation host", "later tree changes never reopen explicitly hidden UI");
        DevToolsOverlay_SetToolbarVisible(true);
        d.popup.popupCloseHr = E_FAIL;
        check(FAILED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            d.mainPanel.child == &d.popup && !d.replacementPanel.child,
            "navigation host", "failed Popup close cannot start reparenting");
        d.popup.popupCloseHr = S_OK;
        DevToolsOverlay_SetToolbarVisible(true);
        d.mainPanel.removeHr = E_FAIL;
        check(FAILED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            d.mainPanel.child == &d.popup && !d.replacementPanel.child,
            "navigation host", "failed detach cannot attach a still-parented Popup elsewhere");
        d.mainPanel.removeHr = S_OK; d.popup.popupOpen = false;
        DevToolsOverlay_SetToolbarVisible(true);
        d.mainPanel.failInsert = d.replacementPanel.failInsert = true;
        check(FAILED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            !d.mainPanel.child && !d.replacementPanel.child && !DevToolsOverlay_IsToolbarVisible(),
            "navigation host", "failed rollback leaves visibility unavailable rather than claiming restoration");
        d.mainPanel.failInsert = false; d.mainPanel.child = &d.popup;
        DevToolsOverlay_SetToolbarVisible(true);
        d.replacementPanel.failInsert = true;
        check(FAILED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            d.mainPanel.child == &d.popup && g_appPanel == &d.mainPanel,
            "navigation host", "failed attachment keeps the same Popup and restores prior membership");
        d.mainPanel.loaded = false;
        const auto opens = switchOpens;
        check(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved) == S_FALSE &&
            switchOpens == opens, "navigation host", "self-generated tree changes do not retry a failed candidate forever");
        d.replacementPanel.failInsert = false;
        DevToolsOverlay_SetToolbarVisible(true);
        hideDuringInsert = true;
        check(FAILED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            !d.popup.popupOpen && !g_toolbarVisible && g_appPanel == &d.replacementPanel,
            "navigation host", "hide during attach prevents reopening after reentrant callbacks");
        hideDuringInsert = false;
        DevToolsOverlay_SetToolbarVisible(true);
        d.popup.popupOpenHr = E_FAIL;
        check(FAILED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            !DevToolsOverlay_IsToolbarVisible(), "navigation host", "reopen failure is not reported as recovered");
        d.popup.popupOpenHr = S_OK;
        DevToolsOverlay_SetToolbarVisible(true);
        unavailableSurface = 30;
        const auto windowSearches = switchWindowSearches;
        check(FAILED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            g_activeSurface.xamlRootKey == originalKey && !d.popup.popupOpen &&
            switchWindowSearches == windowSearches,
            "navigation host", "unavailable surface never falls back to a different window or claims recovery");
        unavailableSurface = 0;
        DevToolsOverlay_SetToolbarVisible(true);
        g_pickCatcher = &catcher;
        g_selectedHandle = 11; d.mainTarget.loaded = false;
        g_selPanel = &draft; g_selComment = &draft; g_selUi = nullptr;
        g_cardReadInput = CommentReadInput;
        commentInput = L"  exact unsaved\n draft ";
        g_selCommentSaved = L"previous saved";
        g_selCommentId = L"original-comment";
        g_selDetached = false;
        const auto editGeneration = g_selGen;
        g_pins.push_back({11, L"original-comment", L"previous saved", {}, &d.mainPin, nullptr, L"original-anchor", true});
        check(SUCCEEDED(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved)) &&
            moved == 30 && g_toolbarSurface.rootHandle == 30 && g_pickRoot == 10 && g_appPanel == &d.replacementPanel &&
            d.replacementPanel.child == &d.popup && DevToolsOverlay_IsToolbarVisible(),
            "navigation host", "same-root recovery reuses and reopens the original Popup");
        check(g_activeSurface.xamlRootKey == originalKey && recoveryRootKey == originalKey &&
            g_pickCatcher == &catcher && g_selGen == editGeneration && g_selPanel == &draft &&
            g_selComment == &draft && g_selDetached && g_selCommentId == L"original-comment" &&
            commentInput == L"  exact unsaved\n draft " && g_selCommentSaved == L"previous saved",
            "navigation host", "active surface, picker intent and detached unsaved editor identity survive");
        check(g_pins.size() == 1 && g_pins[0].id == L"original-comment" &&
            g_pins[0].name == L"original-anchor",
            "navigation host", "saved comment identity is never redirected by host recovery");
        g_pins.clear(); g_pickCatcher = nullptr;
        g_selPanel = g_selComment = nullptr; g_cardReadInput = nullptr;
        g_selDetached = false; g_selCommentSaved.clear(); g_selCommentId.clear();
        UnwireResizeTracking(); UnwireAppEscape(); UnwireFocusTracking();
        g_appPanel->Release(); g_appPanel = nullptr; g_hostRootIdentity = nullptr;
        g_popup = g_popupUi = g_aRow = g_canvasStatics = nullptr;
        check(DevToolsOverlay_RecoverHost(&d, FindRecoveryPanel, &moved) == S_FALSE && !moved,
            "navigation host", "late tree notification after host disposal is inert");
        check(d.Balanced() && d.mainPanel.refs == 1 && d.replacementPanel.refs == 1,
            "navigation host", "success, failure and disposal balance root and panel references");
        check(d.mainPanel.sizeAdds == d.mainPanel.sizeRemoves &&
            d.replacementPanel.sizeAdds == d.replacementPanel.sizeRemoves &&
            d.mainPanel.keyAdds == d.mainPanel.keyRemoves &&
            d.replacementPanel.keyAdds == d.replacementPanel.keyRemoves,
            "navigation host", "recovery and teardown remove every installed host event subscription");
        g_pickDiag = nullptr; g_pickRoot = g_selectedHandle = g_lastHighlightHandle = 0;
        g_activeSurface = {}; g_toolbarSurface = {}; g_allSurfaces.clear(); g_toolbarVisible = false;
        g_failedRecoveryPanel = 0; g_trackTimer = 0;
    }
    for (bool layout : {false, true}) {
        const char* entry = layout ? "TryElementLayoutBox" : "TryElementRootBounds";
        auto run = [&](const char* name, auto configure, bool expected, bool expectTransform) {
            GeometryDiagnostics d;
            configure(d);
            geometryTransforms = 0; geometryDestination = nullptr;
            RECT bounds{}; DevToolsLayoutBox box{}; bool noVisible = true;
            const bool ok = layout ? TryElementLayoutBox(&d, 1, 2, &box)
                                   : TryElementRootBounds(&d, 1, 2, &bounds, &noVisible);
            check(ok == expected, entry, name);
            check((geometryTransforms != 0) == expectTransform, entry, "transform boundary");
            if (expected) {
                check(geometryDestination == &d.content && d.active.contentReads == 1 && d.alias.contentReads == 0,
                      entry, "destination is supplied root Content, not surface element or target root read");
                check(GeometryRect(layout ? box.box : bounds, 24, 24, 124, 104),
                      entry, "positive content-DIP bounds preserve 24-DIP origin");
                if (layout) {
                    check(box.havePadding && GeometryRect(box.padding, 48, 48, 100, 80),
                          entry, "24-DIP padding remains aligned");
                    check(box.haveMargin && GeometryRect(box.margin, 22, 21, 128, 109),
                          entry, "margin remains aligned");
                }
            }
            check(d.Balanced(), entry, "all fake COM references balanced");
        };
        run("distinct interfaces / same COM identity accepted", [](auto&) {}, true, true);
        run("same XamlRoot interface accepted", [](auto& d) { d.target.root = d.active.Inspectable(); }, true, true);
        run("foreign-window target rejected even if transform would succeed",
            [](auto& d) { d.target.root = d.foreign.Inspectable(); }, false, false);
        run("supplied root detached", [](auto& d) { d.supplied.root = nullptr; }, false, false);
        run("target detached", [](auto& d) { d.target.root = nullptr; }, false, false);
        run("supplied root getter failed with nonnull result", [](auto& d) { d.supplied.rootHr = E_FAIL; }, false, false);
        run("target root getter failed with nonnull result", [](auto& d) { d.target.rootHr = E_FAIL; }, false, false);
        run("supplied identity query fails", [](auto& d) { d.active.identityHr = E_NOINTERFACE; }, false, false);
        run("target identity query fails", [](auto& d) { d.alias.identityHr = E_NOINTERFACE; }, false, false);
        run("supplied identity is null", [](auto& d) { d.active.nullIdentity = true; }, false, false);
        run("target identity is null", [](auto& d) { d.alias.nullIdentity = true; }, false, false);
        run("supplied root Content is null", [](auto& d) { d.active.content = nullptr; }, false, false);
        run("supplied Content getter fails with nonnull result", [](auto& d) { d.active.contentHr = E_FAIL; }, false, false);
        run("supplied handle is dead", [](auto& d) { d.dead = 1; }, false, false);
        run("target handle is dead", [](auto& d) { d.dead = 2; }, false, false);
        run("supplied element has no UI interface", [](auto& d) { d.supplied.ui = false; }, false, false);
        run("target has no UI interface", [](auto& d) { d.target.ui = false; }, false, false);
        run("transform fails", [](auto& d) { d.target.transformHr = E_FAIL; }, false, true);
        run("point transform fails", [](auto& d) { d.target.pointHr = E_FAIL; }, false, true);
        run("non-finite bounds rejected", [](auto& d) { d.target.x = std::numeric_limits<float>::quiet_NaN(); }, false, true);
        run("degenerate transformed bounds rejected", [](auto& d) { d.target.scale = 0; }, false, true);
        run("zero-size target rejected", [](auto& d) {
            d.target.width = d.target.height = 0;
            d.target.actualWidth = d.target.actualHeight = 0;
        }, false, false);

        GeometryDiagnostics d;
        RECT bounds{}; DevToolsLayoutBox box{};
        check(!(layout ? TryElementLayoutBox(nullptr, 1, 2, &box) : TryElementRootBounds(nullptr, 1, 2, &bounds)),
              entry, "null diagnostics rejected");
        check(!(layout ? TryElementLayoutBox(&d, 0, 2, &box) : TryElementRootBounds(&d, 0, 2, &bounds)),
              entry, "null supplied handle rejected");
        check(!(layout ? TryElementLayoutBox(&d, 1, 0, &box) : TryElementRootBounds(&d, 1, 0, &bounds)),
              entry, "null target handle rejected");
        check(!(layout ? TryElementLayoutBox(&d, 1, 2, nullptr) : TryElementRootBounds(&d, 1, 2, nullptr)),
              entry, "null output rejected");
        d.target.scale = 2;
        check(layout ? TryElementLayoutBox(&d, 1, 2, &box) : TryElementRootBounds(&d, 1, 2, &bounds),
              entry, "scaled same-surface geometry accepted");
        check(GeometryRect(layout ? box.box : bounds, 24, 24, 224, 184), entry, "scale preserved in content DIPs");
        if (layout) check(box.havePadding && GeometryRect(box.padding, 72, 72, 176, 136),
                          entry, "padding corners transformed with ancestor scale");
        check(d.Balanced(), entry, "final references balanced");
    }
    std::printf("overlay geometry semantics: %u checks, %u failures\n", checks, failures);
    return failures ? 1 : 0;
}
