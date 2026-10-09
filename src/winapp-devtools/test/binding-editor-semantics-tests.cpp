// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "../native/WinApp.DevTools.Native/DevToolsProjected.h"
static HRESULT FindEditorName(void*, HSTRING, IInspectable**);
static HRESULT PutEditorText(void*, HSTRING);
static HRESULT GetEditorSelection(void*, int*);
static HANDLE RefuseWorker(LPSECURITY_ATTRIBUTES, SIZE_T, LPTHREAD_START_ROUTINE, LPVOID, DWORD, LPDWORD);
#define DevToolsFindName FindEditorName
#define DevToolsPutText PutEditorText
#define DevToolsGetSelectedIndex GetEditorSelection
#define CreateThread RefuseWorker
#include "../native/WinApp.DevTools.Native/DevToolsWindow.cpp"
#undef CreateThread

struct EditorElement : IInspectable
{
    ULONG refs = 1;
    std::wstring text;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** out) override
    { *out = static_cast<IInspectable*>(this); AddRef(); return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    HRESULT STDMETHODCALLTYPE GetIids(ULONG* count, IID** ids) override
    { *count = 0; *ids = nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRuntimeClassName(HSTRING* out) override { *out = nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetTrustLevel(TrustLevel* out) override { *out = BaseTrust; return S_OK; }
};
static EditorElement status, pathBox, modeBox;
static unsigned submissions = 0;
static std::wstring submittedPath, submittedMode;
static int selectedMode = 0;
static HRESULT FindEditorName(void*, HSTRING name, IInspectable** out)
{
    const std::wstring key(WindowsGetStringRawBuffer(name, nullptr));
    *out = key == L"PPath" ? &pathBox : key == L"PMode" ? &modeBox : nullptr;
    if (*out) (*out)->AddRef();
    return S_OK;
}
static HRESULT PutEditorText(void* element, HSTRING value)
{
    static_cast<EditorElement*>(element)->text = WindowsGetStringRawBuffer(value, nullptr);
    return S_OK;
}
static HRESULT GetEditorSelection(void*, int* value) { *value = selectedMode; return S_OK; }
static HANDLE RefuseWorker(LPSECURITY_ATTRIBUTES, SIZE_T, LPTHREAD_START_ROUTINE, LPVOID param, DWORD, LPDWORD)
{
    const auto* ask = static_cast<BindOpAsk*>(param);
    ++submissions;
    submittedPath = ask->payload;
    submittedMode = ask->mode;
    SetLastError(ERROR_NOT_SUPPORTED);
    return nullptr; // Observe the write boundary without starting a worker, timer, app or UI.
}

int main()
{
    unsigned checks = 0, failed = 0;
    auto check = [&](bool ok, const char* name) {
        ++checks; if (!ok) ++failed;
        std::printf("%s %s\n", ok ? "PASS" : "FAIL", name);
    };
    g_statusTb = &status;
    g_selWire = 77;
    g_ctx.readInputFn = [](IInspectable* element, std::wstring& value) {
        value = static_cast<EditorElement*>(element)->text; return true;
    };
    DevToolsCardRow row;
    row.name = L"Text";
    row.authoredKind = L"binding";
    row.authored = L"{Binding Title, Converter={StaticResource Tag}, UpdateSourceTrigger=LostFocus}";
    row.source = L"Binding";
    KindCtx ctx{std::make_shared<const DevToolsCardRow>(row), L"Text", 0, nullptr, &pathBox};
    pathBox.text = L"OtherValue";
    CommitKind(ctx);
    check(submissions == 0, "Enter/unconfirmed classic edit never reaches write boundary");
    check(status.text.find(L"Not applied") != std::wstring::npos, "unconfirmed edit explicitly explains refusal");
    check(status.text.find(L"Replace entire binding") != std::wstring::npos, "refusal names separate confirmation action");
    for (size_t i = 0; i < BindingModes().size(); ++i) {
        selectedMode = static_cast<int>(i);
        KindApplySink apply;
        apply.Init(ctx);
        apply.Invoke(nullptr, nullptr);
        check(submissions == i + 1, "explicit Apply reaches one write boundary");
        check(submittedPath == L"OtherValue" && submittedMode == BindingModes()[i],
              "real editor forwards exact selected path and mode");
    }
    row.authoredKind = L"xBind";
    row.authored = L"{x:Bind Vm.Title}";
    row.source = L"Local";
    ctx.row = std::make_shared<const DevToolsCardRow>(row);
    PendingKindSet(L"Text", DevToolsKind::Binding);
    const auto before = submissions;
    CommitKind(ctx);
    check(submissions == before, "staged xBind replacement cannot bypass confirmation via Enter");
    check(PendingKindFind(L"Text") != nullptr, "refused compiled replacement remains staged for cancel");
    PendingKindErase(L"Text");
    ApplyValidateAnswer(L"{\"expressionSource\":\"proposed\",\"againstKind\":\"binding\",\"segments\":["
                        L"{\"path\":\"Title\",\"found\":true,\"hr\":\"0x0\",\"isNull\":false,\"value\":\"title\"}]}");
    check(g_validateVerdict.find(L"Proposed replacement") != std::wstring::npos &&
          g_validateVerdict.find(L"not the installed binding") != std::wstring::npos,
          "editor validation identifies new DataContext replacement rather than current explicit source");
    DevToolsWindowComment comment;
    comment.id = L"host-note";
    g_commentRows = {comment};
    std::function<void(int)> completion;
    g_commentResolveFn = [&](const wchar_t* id, std::function<void(int)> completed) {
        check(std::wstring(id) == L"host-note", "resolve callback retains exact comment identity");
        completion = std::move(completed);
        return true;
    };
    for (const int result : {0, 1, 0x57410001, 0x57410002}) {
        CommentResolveSink resolve;
        resolve.Init(0);
        resolve.Invoke(nullptr, nullptr);
        check(status.text.find(L"Resolving") != std::wstring::npos, "actual Resolve button does not claim persistence after spawn");
        completion(result);
        check(status.text.find(result == 0 ? L"Resolved on the host" :
            result == 1 ? L"not confirmed" : result == 0x57410001 ? L"markers could not refresh" : L"changed after") != std::wstring::npos,
            "actual Resolve button distinguishes host acknowledgement, failure, marker failure and later edits");
    }
    CommentResolveSink staleResolve;
    staleResolve.Init(0);
    staleResolve.Invoke(nullptr, nullptr);
    ++g_route1Gen;
    status.text = L"new inspector window";
    completion(0);
    check(status.text == L"new inspector window", "closed or replaced inspector cannot receive old resolve completion");
    g_commentResolveFn = nullptr;
    g_commentRows.clear();
    g_statusTb = nullptr;
    std::printf("binding editor semantics: %u checks, %u failures\n", checks, failed);
    return failed ? 1 : 0;
}
