// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <windows.h>
#include <roapi.h>
#include <roerrorapi.h>
#include <objidl.h>
#include "../native/WinApp.DevTools.Native/DevToolsProjected.h"
static HRESULT ObserveInputText(void*, HSTRING*);
static HRESULT ObserveContent(void*, IInspectable**);
static HRESULT ObservePlaceholder(void*, HSTRING*);
#define DevToolsGetTextBoxText ObserveInputText
#define DevToolsGetTextBlockText ObserveInputText
#define DevToolsGetContentControlContent ObserveContent
#define DevToolsGetTextBoxHeader ObserveContent
#define DevToolsGetPlaceholderText ObservePlaceholder
static HRESULT ObserveBindingAgile(AgileReferenceOptions, REFIID, IUnknown*, IAgileReference**);
#define RoGetAgileReference ObserveBindingAgile
#define WINAPP_DEVTOOLS_NATIVE_RUNTIME_TESTS
#include "../native/WinApp.DevTools.Native/DevToolsTap.cpp"
#undef RoGetAgileReference
#undef DevToolsGetTextBoxText
#undef DevToolsGetTextBlockText
#undef DevToolsGetContentControlContent
#undef DevToolsGetTextBoxHeader
#undef DevToolsGetPlaceholderText
#include <condition_variable>
#include <iomanip>
#include <sstream>

void CommentFixtureOpen(unsigned long long raw, bool composer);
std::wstring CommentFixtureLaunch(DWORD pid, const wchar_t* exe, const wchar_t* source,
    const std::wstring& token, const std::wstring& text, bool composer, bool local, unsigned* spawns);

struct InputTextBox : IInspectable {
    ULONG refs = 1;
    std::wstring text;
    std::wstring placeholder;
    HRESULT readResult = S_OK;
    bool canRead = true;
    bool textBlock = false;
    bool contentControl = false;
    winrt::Windows::Foundation::IInspectable content{nullptr};
    unsigned reads = 0;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        *value = nullptr;
        if (iid != IID_IUnknown && iid != __uuidof(IInspectable) &&
            !(contentControl && iid == DevToolsIid<DevToolsXC::IContentControl>()) &&
            (iid != (textBlock ? DevToolsIid<DevToolsXC::ITextBlock>() : DevToolsIid<DevToolsXC::ITextBox>()) || !canRead)) return E_NOINTERFACE;
        *value = static_cast<IInspectable*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    HRESULT STDMETHODCALLTYPE GetIids(ULONG* count, IID** ids) override { *count=0; *ids=nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRuntimeClassName(HSTRING* value) override { *value=nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetTrustLevel(TrustLevel* value) override { *value=BaseTrust; return S_OK; }
};
static HRESULT ObserveContent(void* input, IInspectable** value)
{
    auto* box = static_cast<InputTextBox*>(input);
    *value = nullptr;
    if (box->content) box->content.as<IInspectable>().copy_to(value);
    return S_OK;
}
static HRESULT ObserveInputText(void* input, HSTRING* value)
{
    auto* box = static_cast<InputTextBox*>(input);
    ++box->reads;
    *value = nullptr;
    if (FAILED(box->readResult)) return box->readResult;
    return WindowsCreateString(box->text.data(), static_cast<UINT32>(box->text.size()), value);
}
static HRESULT ObservePlaceholder(void* input, HSTRING* value)
{
    const auto* box = static_cast<InputTextBox*>(input);
    return WindowsCreateString(box->placeholder.data(), static_cast<UINT32>(box->placeholder.size()), value);
}

static bool g_observeAgileRelease = false;
static HRESULT g_releaseApartment = E_UNEXPECTED;
static APTTYPE g_releaseType = APTTYPE_CURRENT;
static DWORD g_releaseThread = 0;
static unsigned g_observedReleases = 0;

struct ObservedAgile : IAgileReference {
    IAgileReference* inner;
    std::atomic<ULONG> refs{1};
    explicit ObservedAgile(IAgileReference* value) : inner(value) {}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        if (iid != IID_IUnknown && iid != __uuidof(IAgileReference)) return inner->QueryInterface(iid,value);
        *value=this; AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override {
        const ULONG remaining=--refs;
        if (!remaining) {
            APTTYPEQUALIFIER qualifier;
            g_releaseApartment=CoGetApartmentType(&g_releaseType,&qualifier);
            g_releaseThread=GetCurrentThreadId();
            ++g_observedReleases;
            // Observe the bad context safely; do not release the real COM owner uninitialized in the red control.
            const bool repair=g_releaseApartment==CO_E_NOTINITIALIZED;
            const HRESULT initialized=repair?CoInitializeEx(nullptr,COINIT_MULTITHREADED):S_FALSE;
            inner->Release();
            if (repair && SUCCEEDED(initialized)) CoUninitialize();
            delete this;
        }
        return remaining;
    }
    HRESULT STDMETHODCALLTYPE Resolve(REFIID iid, void** value) override { return inner->Resolve(iid,value); }
};

static HRESULT ObserveBindingAgile(AgileReferenceOptions options, REFIID iid, IUnknown* object, IAgileReference** value)
{
    HRESULT hr=::RoGetAgileReference(options,iid,object,value);
    if (SUCCEEDED(hr) && g_observeAgileRelease) *value=new ObservedAgile(*value);
    return hr;
}

static std::mutex g_heldMutex;
static std::condition_variable g_heldReady;
static IUnknown* g_heldHandler = nullptr;
static InstanceHandle g_mutatedHandle = 0;
static std::wstring g_mutatedProperty, g_mutatedType, g_mutatedValue;
static bool g_wasClear = false;

static HRESULT HoldEnqueue(void*, IUnknown* handler, bool* enqueued)
{
    handler->AddRef();
    { std::lock_guard<std::mutex> lock(g_heldMutex); g_heldHandler = handler; }
    *enqueued = true;
    g_heldReady.notify_one();
    return S_OK;
}

static HRESULT ObserveMutation(InstanceHandle handle, const std::wstring& property,
    const std::wstring& type, const std::wstring& value, bool clear)
{
    g_mutatedHandle = handle;
    g_mutatedProperty = property;
    g_mutatedType = type;
    g_mutatedValue = value;
    g_wasClear = clear;
    return S_OK;
}

static std::wstring PushCommentSnapshot(long long generation, const std::wstring& commentId)
{
    const auto request = DevToolsRpcParse(L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"Internal.setComments\","
        L"\"params\":{\"generation\":" + std::to_wstring(generation) +
        L",\"comments\":[{\"id\":\"" + commentId + L"\",\"text\":\"t\"}]}}");
    std::wstring reply;
    std::thread caller([&] { reply = HandleRpc(nullptr, request); });
    IUnknown* handler = nullptr;
    {
        std::unique_lock<std::mutex> lock(g_heldMutex);
        if (g_heldReady.wait_for(lock, std::chrono::seconds(3), [] { return g_heldHandler != nullptr; })) {
            handler = g_heldHandler;
            g_heldHandler = nullptr;
        }
    }
    if (handler) {
        IDispatcherQueueHandler* callback = nullptr;
        if (SUCCEEDED(handler->QueryInterface(IID_PPV_ARGS(&callback)))) { callback->Invoke(); callback->Release(); }
        handler->Release();
    }
    caller.join();
    return reply;
}

static bool QueuedMutation(bool byName, bool clear, bool replace)
{
    g_slots.clear(); g_freeSlots.clear(); g_ihToSlot.clear(); g_byName.clear(); g_nameOf.clear();
    g_sessionNonce = 17;
    MintSlot_nolock(101);
    g_byName[L"Target"] = 101;
    g_nameOf[101] = L"Target";
    const auto wire = PackWire_nolock(101);
    g_mutatedHandle = 0;
    g_mutatedProperty.clear(); g_mutatedType.clear(); g_mutatedValue.clear();
    const std::wstring target = byName ? L"\"name\":\"Target\"" : L"\"handle\":\"" + std::to_wstring(wire) + L"\"";
    const auto request = DevToolsRpcParse(L"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\""
        + std::wstring(clear ? L"HotReload.clearProperty" : L"HotReload.setProperty")
        + L"\",\"params\":{" + target + L",\"prop\":\"Text\""
        + (clear ? L"" : L",\"type\":\"String\",\"value\":\"File | Edit\"") + L"}}");
    std::wstring reply;
    std::thread caller([&] { reply = HandleRpc(nullptr, request); });
    IUnknown* handler = nullptr;
    {
        std::unique_lock<std::mutex> lock(g_heldMutex);
        if (g_heldReady.wait_for(lock, std::chrono::seconds(3), [] { return g_heldHandler != nullptr; })) {
            handler = g_heldHandler;
            g_heldHandler = nullptr;
        }
    }
    if (!handler) {
        caller.join();
        std::wprintf(L"Mutation did not reach UI dispatch: %s\n", reply.c_str());
        return false;
    }
    if (replace) {
        AcquireSRWLockExclusive(&g_censusLock);
        FreeSlot_nolock(101);
        MintSlot_nolock(101);
        g_byName[L"Target"] = 101;
        ReleaseSRWLockExclusive(&g_censusLock);
    }
    IDispatcherQueueHandler* callback = nullptr;
    const HRESULT queried = handler->QueryInterface(IID_PPV_ARGS(&callback));
    if (SUCCEEDED(queried)) { callback->Invoke(); callback->Release(); }
    handler->Release();
    caller.join();
    const bool safe = replace
        ? g_mutatedHandle == 0 && reply.find(L"stale-handle") != std::wstring::npos
        : g_mutatedHandle == 101 && g_mutatedProperty == L"Text" && g_wasClear == clear
            && (clear || (g_mutatedType == L"String" && g_mutatedValue == L"File | Edit"))
            && reply.find(L"\"result\":null") != std::wstring::npos;
    std::printf("Queued mutation: name=%d clear=%d replacement=%d target=%llu passed=%d\n",
        byName, clear, replace, g_mutatedHandle, safe);
    return SUCCEEDED(queried) && safe;
}

struct BindingObserver : IInspectable {
    std::atomic<ULONG> refs{1};
    winrt::com_ptr<IUnknown> marshaler;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** out) override {
        *out = nullptr;
        if (iid == IID_IMarshal) return marshaler->QueryInterface(iid, out);
        if (iid != IID_IUnknown && iid != __uuidof(IInspectable) && iid != __uuidof(IAgileObject))
            return E_NOINTERFACE;
        *out = static_cast<IInspectable*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    HRESULT STDMETHODCALLTYPE GetIids(ULONG* n, IID** v) override { *n=0; *v=nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRuntimeClassName(HSTRING* v) override { *v=nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetTrustLevel(TrustLevel* v) override { *v=BaseTrust; return S_OK; }
};
struct BindingDiagnostics : IXamlDiagnostics {
    BindingObserver object;
    HANDLE entered = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    HANDLE finish = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    bool hold = false;
    bool failAcquire = false;
    unsigned calls = 0;
    ~BindingDiagnostics() { CloseHandle(entered); CloseHandle(finish); }
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void**) override { return E_NOINTERFACE; }
    ULONG STDMETHODCALLTYPE AddRef() override { return 1; }
    ULONG STDMETHODCALLTYPE Release() override { return 1; }
    HRESULT STDMETHODCALLTYPE GetDispatcher(IInspectable**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetUiLayer(IInspectable**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetApplication(IInspectable**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetIInspectableFromHandle(InstanceHandle, IInspectable** value) override {
        ++calls;
        if (failAcquire) { *value=nullptr; return E_FAIL; }
        if (hold) {
            SetEvent(entered);
            if (WaitForSingleObject(finish, 15000) != WAIT_OBJECT_0) return E_ABORT;
        }
        object.AddRef(); *value=&object; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetHandleFromIInspectable(IInspectable*, InstanceHandle*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE HitTest(RECT, unsigned int*, InstanceHandle**) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE RegisterInstance(IInspectable*, InstanceHandle*) override { return E_NOTIMPL; }
    HRESULT STDMETHODCALLTYPE GetInitializationData(BSTR*) override { return E_NOTIMPL; }
};

struct TextDiagnostics : BindingDiagnostics {
    InputTextBox text;
    HRESULT STDMETHODCALLTYPE GetIInspectableFromHandle(InstanceHandle handle, IInspectable** value) override {
        *value=nullptr;
        if(handle!=901) return E_INVALIDARG;
        text.AddRef();*value=&text;return S_OK;
    }
};

struct TextTreeService : IVisualTreeService3 {
    InputTextBox& text;
    unsigned writes=0;
    explicit TextTreeService(InputTextBox& value):text(value){}
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID,void** value) override {*value=nullptr;return E_NOINTERFACE;}
    ULONG STDMETHODCALLTYPE AddRef() override {return 1;}
    ULONG STDMETHODCALLTYPE Release() override {return 1;}
    HRESULT STDMETHODCALLTYPE AdviseVisualTreeChange(IVisualTreeServiceCallback*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE UnadviseVisualTreeChange(IVisualTreeServiceCallback*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetEnums(unsigned int* count,EnumType** values) override {*count=0;*values=nullptr;return S_OK;}
    HRESULT STDMETHODCALLTYPE CreateInstance(BSTR,BSTR,InstanceHandle*) override {++writes;return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetPropertyValuesChain(InstanceHandle handle,unsigned int* sc,PropertyChainSource** sources,
        unsigned int* pc,PropertyChainValue** values) override {
        *sc=0;*pc=0;*sources=nullptr;*values=nullptr;
        if(handle!=901) return E_INVALIDARG;
        auto* s=static_cast<PropertyChainSource*>(CoTaskMemAlloc(2*sizeof(PropertyChainSource)));
        auto* v=static_cast<PropertyChainValue*>(CoTaskMemAlloc(4*sizeof(PropertyChainValue)));
        if(!s || !v){CoTaskMemFree(s);CoTaskMemFree(v);return E_OUTOFMEMORY;}
        ZeroMemory(s,2*sizeof(PropertyChainSource));ZeroMemory(v,4*sizeof(PropertyChainValue));
        s[0].Source=BaseValueSourceLocal;s[1].Source=BaseValueSourceDefault;
        const wchar_t* names[]={L"Text",L"Text",L"Content",L"Tag"};
        for(unsigned i=0;i<4;++i){
            const bool nullValue=i==1 || i==2 || (i==0 && text.text.empty());
            v[i].Index=i;v[i].PropertyName=SysAllocString(names[i]);
            v[i].Type=SysAllocString(i==2 ? L"Windows.Foundation.Object" : L"Windows.Foundation.String");
            v[i].ValueType=SysAllocString(nullValue ? L"Windows.Foundation.Object" : L"Windows.Foundation.String");
            v[i].Value=SysAllocString(i==0 && !text.text.empty() ? text.text.c_str() : L"0");
            v[i].MetadataBits=nullValue ? IsValueNull : None;
            v[i].Overridden=i==1;v[i].PropertyChainIndex=i==1 ? 1 : 0;
        }
        *sc=2;*pc=4;*sources=s;*values=v;return S_OK;
    }
    HRESULT STDMETHODCALLTYPE SetProperty(InstanceHandle,InstanceHandle,unsigned int) override {++writes;return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE ClearProperty(InstanceHandle,unsigned int) override {++writes;return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetCollectionCount(InstanceHandle,unsigned int*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetCollectionElements(InstanceHandle,unsigned int,unsigned int*,CollectionElementValue**) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE AddChild(InstanceHandle,InstanceHandle,unsigned int) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE RemoveChild(InstanceHandle,unsigned int) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE ClearChildren(InstanceHandle) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetPropertyIndex(InstanceHandle,LPCWSTR,unsigned int*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetProperty(InstanceHandle,unsigned int,InstanceHandle*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE ReplaceResource(InstanceHandle,InstanceHandle,InstanceHandle) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE RenderTargetBitmap(InstanceHandle,RenderTargetBitmapOptions,unsigned int,unsigned int,IBitmapData**) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE ResolveResource(InstanceHandle,LPCWSTR,ResourceType,unsigned int) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE GetDictionaryItem(InstanceHandle,LPCWSTR,BOOL,InstanceHandle*) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE AddDictionaryItem(InstanceHandle,InstanceHandle,InstanceHandle) override {return E_NOTIMPL;}
    HRESULT STDMETHODCALLTYPE RemoveDictionaryItem(InstanceHandle,InstanceHandle) override {return E_NOTIMPL;}
};

static void CheckCuratedText(const std::function<void(bool,const char*)>& check)
{
    TextDiagnostics diagnostics;
    TextTreeService tree(diagnostics.text);
    g_diag=&diagnostics;g_vts3=&tree;
    for(bool block : {false,true}){
        diagnostics.text.textBlock=block;diagnostics.text.readResult=S_OK;
        for(const auto* text : {L"",L"0",L"000",L"<tag>&\"quoted\""}){
            diagnostics.text.text=text;
            std::vector<DevToolsReadProp> props;
            check(SUCCEEDED(ReadCuratedProps(901,&props,nullptr)) && props.size()==3,"actual curated Text read succeeds");
            if(props.size()!=3) continue;
            const auto& p=props[0];
            check(p.value==text && p.type==L"Windows.Foundation.String" && p.valueState.empty(),
                "curated effective Text preserves exact String value/type/state");
            check(p.chain.size()==2 && p.chain[0].value==(diagnostics.text.text.empty() ? L"0" : text) &&
                p.chain[1].value==L"0","curated raw Text chain remains unmodified");
            check(props[1].value==L"0" && props[1].valueState==L"null" && props[1].type==L"Windows.Foundation.Object",
                "object Content null is not an empty String");
            check(props[2].value==L"0" && props[2].valueState.empty(),"unrelated literal String zero is preserved");
            check(diagnostics.text.refs==1,"curated Text releases resolved element and typed interface");
        }
        diagnostics.text.text=L"";diagnostics.text.readResult=E_FAIL;
        std::vector<DevToolsReadProp> props;
        ReadCuratedProps(901,&props,nullptr);
        check(props.size()==3 && props[0].valueState==L"unresolved","failed live Text getter reports unresolved instead of fabricated value");
        check(tree.writes==0 && diagnostics.text.refs==1,"failed effective getter never mutates or leaks target");
    }
    diagnostics.text.canRead=false;
    std::vector<DevToolsReadProp> props;
    ReadCuratedProps(901,&props,nullptr);
    check(props.size()==3 && props[0].value==L"0" && props[0].valueState==L"null",
        "unsupported custom Text keeps existing diagnostic semantics");
    g_diag=nullptr;g_vts3=nullptr;
}

static void CheckSearchText(const std::function<void(bool,const char*)>& check)
{
    TextDiagnostics diagnostics;
    g_diag = &diagnostics;
    g_slots.clear(); g_freeSlots.clear(); g_ihToSlot.clear();
    g_type.clear(); g_nameOf.clear(); g_children.clear(); g_parent.clear();
    MintSlot_nolock(901);
    g_type[901] = L"TextBlock";
    g_nameOf[901] = L"Heading";
    SourceUri_SeedFromAdd_nolock(901, L"ms-appx:///MainPage.xaml");
    g_dispatcher = reinterpret_cast<IInspectable*>(1);
    g_testEnqueueUiOperation = [](void*, IUnknown* handler, bool* enqueued) {
        winrt::com_ptr<IDispatcherQueueHandler> callback;
        const HRESULT hr = handler->QueryInterface(IID_PPV_ARGS(callback.put()));
        *enqueued = SUCCEEDED(hr);
        return FAILED(hr) ? hr : callback->Invoke();
    };
    const auto count = [](const std::wstring& query, bool authored = false) {
        DevToolsJson result;
        if (!DevToolsJsonParse(CmdFind(query, authored), result)) return size_t(-1);
        const auto matches = result.Find(L"matches");
        return matches ? matches->arr.size() : size_t(-1);
    };
    for (bool block : {false, true}) {
        diagnostics.text.textBlock = block;
        diagnostics.text.text = std::wstring(60, L'x') + L" Today's intentions";
        check(count(L"INTENTIONS") == 1, "real TextBlock/TextBox text matches beyond the short row caption");
        check(count(L"MainPage.xaml") == 1 && count(L"Heading") == 1 && count(L"TextBlock") == 1,
            "text search preserves source, name and type matching");
        diagnostics.text.text.clear();
        check(count(L"intentions") == 0 && count(L"(bound)") == 0,
            "empty Text is not a synthetic binding caption");
        diagnostics.text.readResult = E_FAIL;
        check(count(L"intentions") == 0, "failed Text read does not fabricate a match");
        diagnostics.text.readResult = S_OK;
    }
    diagnostics.text.canRead = false;
    diagnostics.text.contentControl = true;
    diagnostics.text.content = winrt::box_value(12345);
    check(count(L"12345") == 1, "numeric Content uses the existing primitive reader");
    diagnostics.text.content = winrt::box_value(true);
    check(count(L"true") == 1, "boolean Content uses the existing primitive reader");
    diagnostics.text.content = winrt::box_value(L"Save changes");
    check(count(L"save changes") == 1, "string Content matches its actual value");
    diagnostics.text.content = nullptr;
    check(count(L"Save") == 0, "null Content is not text");
    InputTextBox objectContent;
    objectContent.text = L"not a primitive caption";
    winrt::copy_from_abi(diagnostics.text.content, static_cast<IInspectable*>(&objectContent));
    check(count(L"primitive caption") == 0 && objectContent.reads == 0,
        "object Content never executes an arbitrary getter or converter");
    diagnostics.text.content = nullptr;
    g_type.erase(901);
    check(CmdFind(L"Save", false) == L"ERR not-ready", "unrealized elements are not invented by search");
    check(diagnostics.text.refs == 1, "search releases every projected element reference");
    g_testEnqueueUiOperation = nullptr; g_dispatcher = nullptr; g_diag = nullptr;
    g_type.clear(); g_nameOf.clear(); SourceUri_Evict_nolock(901);
    g_slots.clear(); g_freeSlots.clear(); g_ihToSlot.clear();
}

struct EffectiveFont : winrt::implements<EffectiveFont, DevToolsXM::IFontFamily>
{
    winrt::hstring source;
    bool fail = false;
    winrt::hstring Source() const { if (fail) throw winrt::hresult_error(E_FAIL); return source; }
};
struct EffectiveBrush : winrt::implements<EffectiveBrush, DevToolsXM::ISolidColorBrush>
{
    bool fail = false;
    winrt::Windows::UI::Color Color() const {
        if (fail) throw winrt::hresult_error(E_FAIL);
        return {0x80, 0x12, 0x34, 0x56};
    }
    void Color(winrt::Windows::UI::Color const&) { throw winrt::hresult_error(E_ACCESSDENIED); }
};
struct EffectiveDiagnostics : BindingDiagnostics
{
    winrt::Windows::Foundation::IInspectable value{nullptr};
    HRESULT STDMETHODCALLTYPE GetIInspectableFromHandle(InstanceHandle handle, IInspectable** out) override {
        *out = nullptr;
        if (handle != 902 || !value) return E_INVALIDARG;
        value.as<IInspectable>().copy_to(out);
        return S_OK;
    }
};
struct EffectiveTree : TextTreeService
{
    std::wstring name = L"FontFamily", type = L"Microsoft.UI.Xaml.Media.FontFamily";
    bool bound = false;
    bool nullValue = false;
    bool reportHandle = true;
    BaseValueSource source = BaseValueSourceDefault;
    using TextTreeService::TextTreeService;
    HRESULT STDMETHODCALLTYPE GetProperty(InstanceHandle, unsigned int, InstanceHandle* out) override {
        *out = nullValue ? 0 : 902; return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetPropertyValuesChain(InstanceHandle h, unsigned int* sc, PropertyChainSource** sources,
        unsigned int* pc, PropertyChainValue** values) override {
        const HRESULT hr = TextTreeService::GetPropertyValuesChain(h, sc, sources, pc, values);
        if (FAILED(hr)) return hr;
        (*sources)[0].Source = source;
        for (unsigned i = 0; i < *pc; ++i) {
            auto& row = (*values)[i];
            SysFreeString(row.PropertyName); row.PropertyName = SysAllocString(name.c_str());
            SysFreeString(row.Type); row.Type = SysAllocString(type.c_str());
            SysFreeString(row.ValueType); row.ValueType = SysAllocString(type.c_str());
            SysFreeString(row.Value); row.Value = SysAllocString(reportHandle ? L"902" : L"");
            row.MetadataBits = static_cast<MetadataBit>((reportHandle ? IsValueHandle : 0) |
                (bound ? IsValueBindingExpression : 0));
        }
        return S_OK;
    }
};
static void CheckEffectiveValues(const std::function<void(bool,const char*)>& check)
{
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    CheckSearchText(check);
    {
        InputTextBox owner;
        EffectiveTree tree(owner);
        EffectiveDiagnostics diag;
        g_diag = &diag; g_vts3 = &tree;
        auto read = [&] {
            std::vector<DevToolsReadProp> props;
            check(SUCCEEDED(ReadCuratedProps(901, &props, nullptr)) && props.size() == 1, "effective property read succeeds");
            return props.empty() ? DevToolsReadProp{} : props[0];
        };
        auto font = winrt::make_self<EffectiveFont>();
        diag.value = font.as<winrt::Windows::Foundation::IInspectable>();
        for (const auto source : {BaseValueSourceDefault, static_cast<BaseValueSource>(5)}) {
            tree.source = source;
            for (const bool handleReported : {true, false}) {
                tree.reportHandle = handleReported;
                for (const auto* token : {L"XamlAutoFontFamily", L"ms-appx:///Assets/Fonts/Custom.ttf#Custom", L"Arial, Consolas", L""}) {
                    font->source = token;
                    const auto row = read();
                    check(row.value == token && row.valueState.empty() && row.writeType.empty(),
                        "default/inherited FontFamily reads exact Source without inventing a write capability");
                    check(ToCardRow(row).value == token && ToCardRow(row).valueState.empty(),
                        "wire and inspector share effective font token including empty");
                }
            }
        }
        tree.reportHandle = true;
        font->fail = true;
        check(read().valueState == L"unresolved", "failed FontFamily Source is unavailable, not empty success");
        tree.name = L"Foreground"; tree.type = L"Microsoft.UI.Xaml.Media.Brush";
        auto brush = winrt::make_self<EffectiveBrush>();
        diag.value = brush.as<winrt::Windows::Foundation::IInspectable>();
        auto row = read();
        check(row.value == L"#80123456" && row.editKind == L"color", "solid brush reads typed Color including alpha");
        brush->fail = true;
        row = read();
        check(row.valueState == L"unresolved" && row.editKind == L"none",
            "failed brush read never invents transparent black");
        tree.name = L"Content"; tree.type = L"Windows.Foundation.Object";
        for (const bool bound : {false, true}) {
            tree.bound = bound;
            for (const auto* value : {L"", L"(bound)", L"{SolidColorBrush}", L"0", L"Hello"}) {
                diag.value = winrt::box_value(winrt::hstring(value));
                row = read();
                check(row.value == value && row.type == L"Windows.Foundation.String" && row.editKind == L"text",
                    "boxed Content String including placeholder-like literals is an effective primitive");
                check(row.valueState == (bound ? L"binding" : L""), "Content retains binding provenance separately from its value");
            }
        }
        tree.bound = false;
        diag.value = font.as<winrt::Windows::Foundation::IInspectable>();
        row = read();
        check(row.value != L"(bound)" && row.valueState == L"unresolved" && row.editKind == L"none",
            "complex unbound Content is not mislabeled as a binding");
        tree.bound = true;
        row = read();
        check(row.source == L"Binding" && row.valueState == L"unresolved",
            "complex bound Content preserves binding provenance without an editable placeholder");
        tree.nullValue = true;
        row = read();
        check(row.valueState == L"null" && row.value.empty(), "null bound Content differs from failed or complex reads");
        check(tree.writes == 0, "effective value reads never mutate the app");
        g_diag = nullptr; g_vts3 = nullptr;
    }
    winrt::uninit_apartment();
}

static bool BindingCallerScope()
{
    if (FAILED(CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED))) return false;
    bool passed=true;
    for (bool failAcquire : {false,true}) {
        BindingDiagnostics diagnostics;
        diagnostics.failAcquire=failAcquire;
        g_diag=&diagnostics;
        g_slots.clear(); g_freeSlots.clear(); g_ihToSlot.clear();
        MintSlot_nolock(505);
        const auto wire=PackWire_nolock(505);
        g_observeAgileRelease=true; g_observedReleases=0;
        HRESULT before=E_UNEXPECTED, after=E_UNEXPECTED, uiResult=E_FAIL;
        DWORD callerId=0;
        std::wstring reply;
        std::thread ui([&] {
            uiResult=CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
            if (FAILED(uiResult)) return;
            CoCreateFreeThreadedMarshaler(&diagnostics.object,diagnostics.object.marshaler.put());
            IUnknown* handler=nullptr;
            {
                std::unique_lock<std::mutex> lock(g_heldMutex);
                if(g_heldReady.wait_for(lock,std::chrono::seconds(3),[]{return g_heldHandler!=nullptr;})) {
                    handler=g_heldHandler; g_heldHandler=nullptr;
                }
            }
            if(handler) {
                IDispatcherQueueHandler* callback=nullptr;
                if(SUCCEEDED(handler->QueryInterface(IID_PPV_ARGS(&callback)))) {callback->Invoke();callback->Release();}
                handler->Release();
            }
            CoUninitialize();
        });
        std::thread caller([&] {
            callerId=GetCurrentThreadId();
            APTTYPE type; APTTYPEQUALIFIER qualifier;
            before=CoGetApartmentType(&type,&qualifier);
            reply=CmdBindingOp(wire,L"diagnose",L"Text",L"supplied");
            after=CoGetApartmentType(&type,&qualifier);
        });
        caller.join(); ui.join();
        const bool context=failAcquire ? g_observedReleases==0 : (g_observedReleases==1 && g_releaseThread==callerId
            && SUCCEEDED(g_releaseApartment) && g_releaseType==APTTYPE_MTA);
        const ULONG referenceCount=diagnostics.object.refs.load();
        const bool result=SUCCEEDED(uiResult) && before==CO_E_NOTINITIALIZED && after==CO_E_NOTINITIALIZED
            && context && referenceCount==1
            && (failAcquire ? reply==L"ERR not-found" : reply.find(L"unavailable")!=std::wstring::npos);
        std::ostringstream diagnostic;
        diagnostic << "Binding caller scope failAcquire=" << failAcquire
            << std::hex << std::uppercase << std::setfill('0')
            << " before=" << std::setw(8) << static_cast<ULONG>(before)
            << " release=" << std::setw(8) << static_cast<ULONG>(g_releaseApartment)
            << std::dec << " releaseType=" << g_releaseType
            << std::hex << " after=" << std::setw(8) << static_cast<ULONG>(after)
            << std::dec << " refs=" << referenceCount << " releases=" << g_observedReleases
            << " sameThread=" << (g_releaseThread==callerId) << " pass=" << result << '\n';
        std::fputs(diagnostic.str().c_str(), stdout);
        passed=passed && result;
        g_observeAgileRelease=false;
        diagnostics.object.marshaler=nullptr;
        g_diag=nullptr;
    }
    CoUninitialize();
    return passed;
}

static bool BindingCallerRejectsSta()
{
    CoInitializeEx(nullptr,COINIT_APARTMENTTHREADED);
    const auto oldDispatcher=g_dispatcher;
    g_dispatcher=nullptr;
    const auto wire=PackWire_nolock(505);
    const auto response=CmdBindingOp(wire,L"diagnose",L"Text",L"supplied");
    APTTYPE type; APTTYPEQUALIFIER qualifier;
    const HRESULT apartment=CoGetApartmentType(&type,&qualifier);
    const bool passed=response==L"ERR "+HrStr(RPC_E_CHANGED_MODE) && SUCCEEDED(apartment)
        && (type==APTTYPE_STA || type==APTTYPE_MAINSTA);
    std::wprintf(L"Binding STA init refusal=%s preservedSTA=%d pass=%d\n",response.c_str(),SUCCEEDED(apartment),passed);
    g_dispatcher=oldDispatcher;
    CoUninitialize();
    return passed;
}

static bool InitialBindingTimeout(bool running)
{
    BindingDiagnostics diagnostics;
    if (FAILED(CoCreateFreeThreadedMarshaler(&diagnostics.object, diagnostics.object.marshaler.put()))) return false;
    diagnostics.hold = running;
    g_diag = &diagnostics;
    g_slots.clear(); g_freeSlots.clear(); g_ihToSlot.clear();
    MintSlot_nolock(303);
    const auto wire = PackWire_nolock(303);
    std::wstring reply;
    const auto start = GetTickCount64();
    std::thread caller([&] { reply = CmdBindingOp(wire, L"diagnose", L"Text", L"supplied"); });
    IUnknown* handler = nullptr;
    {
        std::unique_lock<std::mutex> lock(g_heldMutex);
        if (g_heldReady.wait_for(lock, std::chrono::seconds(3), [] { return g_heldHandler != nullptr; })) {
            handler = g_heldHandler; g_heldHandler = nullptr;
        }
    }
    if (!handler) { caller.join(); g_diag=nullptr; return false; }
    auto invoke = [&] {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        IDispatcherQueueHandler* callback = nullptr;
        if (SUCCEEDED(handler->QueryInterface(IID_PPV_ARGS(&callback)))) {
            callback->Invoke(); callback->Release();
        }
        handler->Release();
        CoUninitialize();
    };
    std::thread ui;
    if (running) {
        ui = std::thread(invoke);
        if (WaitForSingleObject(diagnostics.entered, 3000) != WAIT_OBJECT_0) {
            SetEvent(diagnostics.finish); caller.join(); ui.join(); g_diag=nullptr; return false;
        }
    }
    caller.join();
    const auto elapsed = GetTickCount64() - start;
    SetEvent(diagnostics.finish);
    if (running) ui.join(); else invoke();
    const ULONG refs = diagnostics.object.refs.load();
    const bool safe = refs == 1 && diagnostics.calls == (running ? 1u : 0u)
        && elapsed >= 9500 && elapsed < 12000 && reply == L"ERR not-found";
    std::printf("Initial binding running=%d elapsed=%llu refs=%lu calls=%u safe=%d\n",
        running, elapsed, refs, diagnostics.calls, safe);
    // Observer storage stays allocated until all callbacks are joined, including the red control.
    while (diagnostics.object.refs > 1) diagnostics.object.Release();
    g_diag = nullptr;
    return safe;
}

static bool CompleteBindingRelay()
{
    BindingDiagnostics diagnostics;
    if (FAILED(CoCreateFreeThreadedMarshaler(&diagnostics.object, diagnostics.object.marshaler.put()))) return false;
    g_diag = &diagnostics;
    g_slots.clear(); g_freeSlots.clear(); g_ihToSlot.clear();
    MintSlot_nolock(404);
    const auto wire = PackWire_nolock(404);
    bool passed = true;
    for (const wchar_t* op : {L"capture", L"restore"}) {
        const auto name = L"\\\\.\\pipe\\winapp-devtools-binding-" + std::to_wstring(GetCurrentProcessId());
        HANDLE pipe = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_WAIT, 1, 4096, 4096, 0, nullptr);
        if (pipe == INVALID_HANDLE_VALUE) { g_diag=nullptr; return false; }
        bool identity = false;
        std::thread peer([&] {
            CoInitializeEx(nullptr, COINIT_MULTITHREADED);
            if (ConnectNamedPipe(pipe, nullptr) || GetLastError() == ERROR_PIPE_CONNECTED) {
                auto exact = [&](void* value, DWORD bytes) {
                    auto target = static_cast<char*>(value);
                    while (bytes) {
                        DWORD got=0;
                        if (!ReadFile(pipe,target,bytes,&got,nullptr)||!got) return false;
                        target+=got; bytes-=got;
                    }
                    return true;
                };
                char header[9]; DWORD length=0;
                if (exact(header,9) && std::string(header,9)=="BINDING2\n" && exact(&length,4) && length <= 65536) {
                    std::vector<char> bytes(length);
                    if (exact(bytes.data(),length)) {
                        winrt::com_ptr<IStream> stream;
                        if (SUCCEEDED(CreateStreamOnHGlobal(nullptr,TRUE,stream.put()))) {
                            ULONG written=0; stream->Write(bytes.data(),length,&written);
                            LARGE_INTEGER zero{}; stream->Seek(zero,STREAM_SEEK_SET,nullptr);
                            winrt::com_ptr<IInspectable> object;
                            if (SUCCEEDED(CoUnmarshalInterface(stream.get(),__uuidof(IInspectable),object.put_void()))) {
                                auto original = static_cast<IInspectable*>(&diagnostics.object);
                                winrt::com_ptr<IUnknown> a, b;
                                original->QueryInterface(IID_PPV_ARGS(a.put()));
                                object->QueryInterface(IID_PPV_ARGS(b.put()));
                                identity = a.get() == b.get();
                            }
                        }
                        for (int i=0;i<3;++i) {
                            DWORD size=0;
                            if(!exact(&size,4)||size>65536) { identity=false;break; }
                            std::vector<char> field(size);
                            if(!exact(field.data(),size)) { identity=false;break; }
                        }
                    }
                }
                const char response[]="BINDING2 {\"state\":\"none\"}\n";
                DWORD sent=0; WriteFile(pipe,response,sizeof(response)-1,&sent,nullptr);
                FlushFileBuffers(pipe);
            }
            DisconnectNamedPipe(pipe);
            CoUninitialize();
        });
        std::wstring reply;
        std::thread caller([&] { reply=CmdBindingOp(wire,op,L"Text",L"supplied"); });
        IUnknown* handler=nullptr;
        {
            std::unique_lock<std::mutex> lock(g_heldMutex);
            if (g_heldReady.wait_for(lock,std::chrono::seconds(3),[]{return g_heldHandler!=nullptr;})) {
                handler=g_heldHandler; g_heldHandler=nullptr;
            }
        }
        if (handler) {
            IDispatcherQueueHandler* callback=nullptr;
            if(SUCCEEDED(handler->QueryInterface(IID_PPV_ARGS(&callback)))) {callback->Invoke();callback->Release();}
            handler->Release();
        }
        caller.join(); peer.join(); CloseHandle(pipe);
        const ULONG referenceCount=diagnostics.object.refs.load();
        passed = passed && handler && identity && reply == L"{\"state\":\"none\"}" && referenceCount == 1;
        std::wprintf(L"Actual binding op=%s acquired/resolved IUnknown same=%d baseline-refs=%lu\n",
            op,identity,referenceCount);
    }
    g_diag=nullptr;
    return passed && diagnostics.calls==2;
}

struct GuestNegotiationDiagnostics : BindingDiagnostics {
    std::wstring initialization;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID iid, void** value) override {
        *value = nullptr;
        if (iid != IID_IUnknown && iid != __uuidof(IXamlDiagnostics)) return E_NOINTERFACE;
        *value = static_cast<IXamlDiagnostics*>(this);
        AddRef();
        return S_OK;
    }
    HRESULT STDMETHODCALLTYPE GetInitializationData(BSTR* value) override {
        *value = SysAllocStringLen(initialization.data(), static_cast<UINT>(initialization.size()));
        return *value ? S_OK : E_OUTOFMEMORY;
    }
};

static int GuestNegotiation(int argc, wchar_t** argv)
{
    // Exercise the shipping SetSite/init parser/immutable authority/Hello producer without a window or injection.
    GuestNegotiationDiagnostics diagnostics;
    DevToolsTap tap;
    for (int index = 2; index < argc; ++index) {
        // Model the UDK's 260-wchar packet using its actual CRT copy operation.
        wchar_t packet[MAX_PATH]{};
        auto previous = _set_thread_local_invalid_parameter_handler(
            [](const wchar_t*, const wchar_t*, const wchar_t*, unsigned int, uintptr_t) {});
        const int copied = wcscpy_s(packet, _countof(packet), argv[index]);
        _set_thread_local_invalid_parameter_handler(previous);
        std::fprintf(stderr, "modeled-udk-copy units=%zu errno=%d returned=%zu\n",
            wcslen(argv[index]), copied, wcslen(packet));
        diagnostics.initialization = packet;
        if (FAILED(tap.SetSite(&diagnostics))) return 2;
    }
    tap.SetSite(nullptr);
    const auto sibling = GuestSiblingWriterPath();
    std::fprintf(stderr, "writer-sibling=%d\n", !sibling.empty() && g_cliExe == sibling);
    std::fprintf(stderr, "comment-unavailable=%d\n", DevToolsOverlay_GuestCommentToken(L"", L"") == L"unavailable");
    DevToolsRpcRequest request{};
    request.method = L"DevTools.negotiate";
    request.hasId = true;
    request.idRaw = L"1";
    request.params.type = DevToolsJsonType::Object;
    const auto response = HandleRpc(nullptr, request);
    const int count = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, response.data(),
        static_cast<int>(response.size()), nullptr, 0, nullptr, nullptr);
    if (!count) return 3;
    std::string bytes(count, '\0');
    if (!WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, response.data(),
        static_cast<int>(response.size()), bytes.data(), count, nullptr, nullptr)) return 3;
    return std::fwrite(bytes.data(), 1, bytes.size(), stdout) == bytes.size() ? 0 : 4;
}

#include "query-runtime-tests.inc"

int wmain(int argc, wchar_t** argv)
{
    if (argc == 9 && std::wstring(argv[1]) == L"--overlay-comment-command") {
        DevToolsTrust_InitializePosture(DevToolsAccess::Mutation);
        const InstanceHandle raw = 0x12345678;
        const bool composer = std::wstring(argv[4]) == L"composer";
        const std::wstring outcome = argv[5];
        g_sessionNonce = 17;
        MintSlot_nolock(101);
        g_type[101] = L"Grid";
        if (outcome != L"untracked") MintSlot_nolock(raw);
        g_type[raw] = L"TextBlock";
        g_nameOf[raw] = L"BoundTitle";
        const auto initialWire = PackWire_nolock(raw);
        DevToolsOverlay_SetAnchorBridge(
            [](InstanceHandle h, std::wstring* anchor) { return TapAnchorOf(h, anchor); },
            &TapResolveAnchor, &TapWireOf);
        CommentFixtureOpen(raw, composer);
        auto retire = [&] {
            FreeSlot_nolock(raw);
            if (outcome == L"retired") g_type.erase(raw);
            else MintSlot_nolock(raw);
        };
        if (outcome == L"retired" || outcome == L"reused") retire();
        if (outcome == L"new-session") ++g_sessionNonce;
        unsigned spawns = 0;
        const auto command = CommentFixtureLaunch(wcstoul(argv[3], nullptr, 10), argv[2], argv[8],
            argv[7], argv[6], composer, outcome == L"local", &spawns);
        if (outcome == L"retired-after-command" || outcome == L"reused-after-command") {
            FreeSlot_nolock(raw);
            if (outcome == L"retired-after-command") g_type.erase(raw);
            else MintSlot_nolock(raw);
        }
        if (outcome == L"new-session-after-command") ++g_sessionNonce;
        int count = 0;
        auto parsed = command.empty() ? nullptr : CommandLineToArgvW(command.c_str(), &count);
        std::wstring json = L"{\"spawns\":" + std::to_wstring(spawns) +
            L",\"raw\":\"" + std::to_wstring(raw) + L"\",\"initialWire\":\"" + std::to_wstring(initialWire) +
            L"\",\"currentWire\":\"" + std::to_wstring(PackWire_nolock(raw)) +
            L"\",\"tree\":" + CmdEnumerate({L"visualtree.enumerate"}) + L",\"args\":[";
        for (int i = 1; i < count; ++i) {
            if (i > 1) json += L",";
            json += L"\"" + DevToolsJsonEscape(parsed[i]) + L"\"";
        }
        if (parsed) LocalFree(parsed);
        json += L"]}";
        const int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, json.data(),
            static_cast<int>(json.size()), nullptr, 0, nullptr, nullptr);
        std::string bytes(length, '\0');
        if (!length || !WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, json.data(),
            static_cast<int>(json.size()), bytes.data(), length, nullptr, nullptr)) return 3;
        return std::fwrite(bytes.data(), 1, bytes.size(), stdout) == bytes.size() ? 0 : 4;
    }
    if (argc == 3 && std::wstring(argv[1]) == L"--writer-failure") {
        std::wprintf(L"%s", DevToolsCommentText::WriterFailureStatus(wcstoul(argv[2], nullptr, 10)).c_str());
        return 0;
    }
    if (argc == 9 && std::wstring(argv[1]) == L"--comment-command") {
        const auto args = DevToolsCommentText::AddArguments(wcstoul(argv[3], nullptr, 10), argv[5], argv[6], argv[4]);
        const auto command = DevToolsCommentText::WriterCommand(argv[2], args, argv[8], argv[7]);
        int count = 0;
        auto parsed = CommandLineToArgvW(command.c_str(), &count);
        if (!parsed) return 2;
        std::wstring json = L"[";
        for (int i = 1; i < count; ++i) {
            if (i > 1) json += L",";
            json += L"\"" + DevToolsJsonEscape(parsed[i]) + L"\"";
        }
        LocalFree(parsed);
        json += L"]";
        const int length = WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, json.data(),
            static_cast<int>(json.size()), nullptr, 0, nullptr, nullptr);
        std::string bytes(length, '\0');
        if (!length || !WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, json.data(),
            static_cast<int>(json.size()), bytes.data(), length, nullptr, nullptr)) return 3;
        return std::fwrite(bytes.data(), 1, bytes.size(), stdout) == bytes.size() ? 0 : 4;
    }
    if (argc == 5 && std::wstring(argv[1]) == L"--read-source") {
        wchar_t* end = nullptr;
        const auto built = _wcstoui64(argv[4], &end, 10);
        if (!built || !end || *end) return 2;
        DevToolsAuthored_Init(argv[2], built);
        std::wstring text;
        const auto state = DevToolsAuthored_ReadElement(argv[3], 1, &text);
        std::wprintf(L"%s", DevToolsAuthored_StateToken(state));
        return 0;
    }
    if (argc >= 3 && std::wstring(argv[1]) == L"--guest-negotiation") return GuestNegotiation(argc, argv);
    if (argc != 1) return 2;
    int failed = 0;
    int checks = 0;
    auto check = [&](bool condition, const char* name) {
        ++checks;
        if (!condition) { ++failed; std::printf("FAIL %s\n", name); }
    };
    InputTextBox input;
    for (const auto* value : { L"", L"0", L"000", L"<tag> & \"quoted\"" }) {
        input.text = value;
        std::wstring actual = L"unread";
        check(CardReadInputBridge(&input, &actual) && actual == value,
              "shared input bridge reads exact live text, including empty and literal zero");
        check(input.refs == 1, "input bridge releases queried TextBox");
    }
    input.readResult = E_FAIL;
    std::wstring unchanged = L"unchanged";
    check(!CardReadInputBridge(&input, &unchanged) && unchanged == L"unchanged",
          "failed live getter is not successful empty text");
    input.readResult = S_OK;
    input.canRead = false;
    check(!CardReadInputBridge(&input, &unchanged), "unsupported input is refused");
    CheckCuratedText(check);
    CheckEffectiveValues(check);
    CheckQueries(check);
    DevToolsTrust_InitializePosture(DevToolsAccess::Mutation);
    auto call = [&](const wchar_t* method) {
        DevToolsRpcRequest request{};
        request.method = method;
        request.hasId = true;
        request.idRaw = L"1";
        request.params.type = DevToolsJsonType::Object;
        return HandleRpc(nullptr, request);
    };
    for (const auto* method : { L"Comment.add", L"Comment.resolve", L"Comment.delete" }) {
        const auto response = call(method);
        check(response.find(L"\"error\"") != std::wstring::npos, "comment mutation refuses missing host");
        check(response.find(L"no-cli") != std::wstring::npos, "comment refusal identifies host prerequisite");
        check(response.find(std::to_wstring(DevToolsErr::CapabilityUnsupported)) != std::wstring::npos,
              "comment refusal has CapabilityUnsupported code");
    }
    check(call(L"Comment.list").find(L"\"total\":0") != std::wstring::npos, "empty comment snapshot is real data");
    const auto negotiated = call(L"DevTools.negotiate");
    check(negotiated.find(L"Comment.add") != std::wstring::npos, "real host-dependent methods remain implemented");
    check(negotiated.find(L"Internal.") == std::wstring::npos, "internal methods are not advertised");
    for (const auto* method : { L"Internal.pageInspectable", L"Internal.contentRoot", L"DevTools.cancel" }) {
        const auto response = call(method);
        check(response.find(std::to_wstring(DevToolsErr::MethodNotFound)) != std::wstring::npos,
              "excluded or unsupported method is refused");
    }
    check(DevToolsProtocolFindMethod(L"DevTools.ping") != nullptr, "native ping remains implemented");
    check(DevToolsProtocolFindMethod(L"HotReload.setProperty") != nullptr, "inspector property editing remains");
    // Only enqueue and terminal COM setters are replaced; RPC, census and dispatch operation are real.
    g_dispatcher = reinterpret_cast<IInspectable*>(1); // The enqueue adapter never dereferences this token.
    g_testEnqueueUiOperation = HoldEnqueue;
    g_testPropertyMutation = ObserveMutation;
    check(BindingCallerScope(), "actual binding state release owns MTA with initially uninitialized caller");
    check(BindingCallerRejectsSta(), "binding initialization failure preserves caller STA and reports HRESULT");
    for (bool byName : { false, true }) for (bool clear : { false, true }) {
        check(QueuedMutation(byName, clear, false), "unchanged selected element is mutated successfully");
        check(QueuedMutation(byName, clear, true), "retired selection cannot mutate a replacement element");
    }
    {
        const auto newer = PushCommentSnapshot(5, L"newer");
        const auto older = PushCommentSnapshot(3, L"older");
        check(newer.find(L"\"stale\":false") != std::wstring::npos && older.find(L"\"stale\":true") != std::wstring::npos,
              "an older store generation is reported stale");
        {
            std::lock_guard<std::mutex> lock(g_commentsMutex);
            check(g_comments.size() == 1 && g_comments[0].id == L"newer",
                  "an older store generation cannot replace a newer comment snapshot");
        }
        g_commentsGeneration = -1;
    }
    {
        // TextBox (app) -> Grid (template) -> PlaceholderTextContentPresenter (template): a pick on the placeholder
        // selects the TextBox the developer wrote.
        g_parent.clear();
        g_parent[3] = 2; g_parent[2] = 1; g_parent[1] = 0;
        AcquireSRWLockExclusive(&g_sourceUriLock);
        g_sourceInfo.clear();
        g_sourceInfo[1] = SourceInfoEntry{ L"ms-appx:///MainPage.xaml", 0 };
        g_sourceInfo[2] = SourceInfoEntry{ L"ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml", 0 };
        g_sourceInfo[3] = SourceInfoEntry{ L"ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml", 0 };
        g_sourceInfo[4] = SourceInfoEntry{ L"", 0 };
        g_sourceInfo[5] = SourceInfoEntry{ L"ms-appx:///Microsoft.UI.Xaml/Themes/generic.xaml", 0 };
        ReleaseSRWLockExclusive(&g_sourceUriLock);
        check(PickTarget(3) == 1, "a pick on a control template part selects its nearest app-authored ancestor");
        check(PickTarget(1) == 1, "a pick on app XAML keeps the hit");
        check(PickTarget(4) == 4, "a hit with unknown provenance is kept rather than guessed");
        check(PickTarget(5) == 5, "a framework hit with no app-authored ancestor is kept");
        // With a bound source root, authored means a file under it, whatever the package layout contains.
        wchar_t temp[MAX_PATH]{};
        GetTempPathW(MAX_PATH, temp);
        const std::wstring root = std::wstring(temp) + L"winapp-pick-root-" + std::to_wstring(GetCurrentProcessId());
        CreateDirectoryW(root.c_str(), nullptr);
        CreateDirectoryW((root + L"\\Pages").c_str(), nullptr);
        const std::wstring page = root + L"\\Pages\\Main.xaml";
        const HANDLE created = CreateFileW(page.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
        if (created != INVALID_HANDLE_VALUE) CloseHandle(created);
        DevToolsAuthored_Init(root, 0);
        AcquireSRWLockExclusive(&g_sourceUriLock);
        g_sourceInfo[1] = SourceInfoEntry{ L"ms-appx:///Pages/Main.xaml", 0 };
        ReleaseSRWLockExclusive(&g_sourceUriLock);
        check(PickTarget(3) == 1, "with a source root, a template part selects the ancestor declared in a project file");
        AcquireSRWLockExclusive(&g_sourceUriLock);
        g_sourceInfo[1] = SourceInfoEntry{ L"ms-appx:///Pages/NotInProject.xaml", 0 };
        ReleaseSRWLockExclusive(&g_sourceUriLock);
        check(PickTarget(3) == 3, "an ancestor whose source is not a project file is not treated as authored");
        DevToolsAuthored_Init(L"", 0);
        DeleteFileW(page.c_str());
        RemoveDirectoryW((root + L"\\Pages").c_str());
        RemoveDirectoryW(root.c_str());
        g_parent.clear();
        AcquireSRWLockExclusive(&g_sourceUriLock);
        g_sourceInfo.clear();
        ReleaseSRWLockExclusive(&g_sourceUriLock);
    }
    const HRESULT apartment = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    check(SUCCEEDED(apartment), "binding test COM initialization");
    if (SUCCEEDED(apartment)) {
        const HRESULT parseError = static_cast<HRESULT>(0x802B000A);
        const std::wstring detail = L"Cannot find Resource \"AcrylicBackgroundFillColorDefaultBrush\".\nC:\\owned\\fixture";
        check(RoOriginateErrorW(parseError, static_cast<UINT>(detail.size()), detail.c_str()) != FALSE,
              "originate an isolated XAML parse diagnostic");
        const auto captured = DevToolsXamlErrorMessage(parseError);
        check(captured.find(L"AcrylicBackgroundFillColorDefaultBrush") != std::wstring::npos,
              "capture the actual missing resource on the failing thread");
        const auto response = BridgeErrToRpc(L"1", L"ERR build-failed|" + BridgeEscape(captured));
        check(response.find(L"\"token\":\"build-failed\"") != std::wstring::npos,
              "overlay parse failure retains its stable protocol token");
        check(response.find(DevToolsJsonEscape(captured)) != std::wstring::npos,
              "overlay parse diagnostic survives bridge and JSON escaping");
        check(DevToolsXamlErrorMessage(S_OK).empty(), "successful XAML load has no diagnostic");
        const std::wstring longDetail = std::wstring(511, L'x') + L"\U0001F600" + std::wstring(4096, L'x');
        check(RoOriginateErrorW(parseError, static_cast<UINT>(longDetail.size()), longDetail.c_str()) != FALSE,
              "originate a long isolated XAML diagnostic");
        const auto bounded = DevToolsXamlErrorMessage(parseError);
        check(bounded.size() <= 2048 &&
              WideCharToMultiByte(CP_UTF8, WC_ERR_INVALID_CHARS, bounded.data(),
                  static_cast<int>(bounded.size()), nullptr, 0, nullptr, nullptr) > 0,
              "bounded XAML diagnostic preserves valid UTF-16");
        check(InitialBindingTimeout(false), "Pending binding timeout cancels diagnostics acquisition");
        check(InitialBindingTimeout(true), "Running binding timeout releases late diagnostics acquisition");
        check(CompleteBindingRelay(), "actual CmdBindingOp and relay preserve fake diagnostics identity across capture/restore transports");
        CoUninitialize();
    }
    g_testPropertyMutation = nullptr;
    g_testEnqueueUiOperation = nullptr;
    g_dispatcher = nullptr;
    std::printf("Native dispatcher: checks=%d passed=%d failed=%d skipped=0\n", checks, checks - failed, failed);
    return failed ? 1 : 0;
}
