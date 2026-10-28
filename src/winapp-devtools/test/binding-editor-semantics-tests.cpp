// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "../native/WinApp.DevTools.Native/DevToolsProjected.h"
static HRESULT FindEditorName(void*, HSTRING, IInspectable**);
static HRESULT PutEditorText(void*, HSTRING);
static HRESULT GetEditorSelection(void*, int*);
static HRESULT GetEditorKey(void* args, int* key) { *key = *static_cast<int*>(args); return S_OK; }
static bool keyHandled = false;
static HRESULT PutEditorHandled(void*, bool handled) { keyHandled = handled; return S_OK; }
static HANDLE RefuseWorker(LPSECURITY_ATTRIBUTES, SIZE_T, LPTHREAD_START_ROUTINE, LPVOID, DWORD, LPDWORD);
#define DevToolsFindName FindEditorName
#define DevToolsPutText PutEditorText
#define DevToolsGetSelectedIndex GetEditorSelection
#define DevToolsGetKey GetEditorKey
#define DevToolsPutHandled PutEditorHandled
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
struct ResourceBox : winrt::implements<ResourceBox, DevToolsXC::IAutoSuggestBox>
{
    winrt::hstring text;
    bool isOpen = false;
    double MaxSuggestionListHeight() { return 200; }
    void MaxSuggestionListHeight(double) {}
    bool IsSuggestionListOpen() { return isOpen; }
    void IsSuggestionListOpen(bool value) { isOpen = value; }
    winrt::hstring TextMemberPath() { return {}; }
    void TextMemberPath(winrt::hstring const&) {}
    winrt::hstring Text() { return text; }
    void Text(winrt::hstring const& value) { text = value; }
    bool UpdateTextOnSelect() { return false; }
    void UpdateTextOnSelect(bool) {}
    winrt::hstring PlaceholderText() { return {}; }
    void PlaceholderText(winrt::hstring const&) {}
    winrt::Windows::Foundation::IInspectable Header() { return nullptr; }
    void Header(winrt::Windows::Foundation::IInspectable const&) {}
    bool AutoMaximizeSuggestionArea() { return false; }
    void AutoMaximizeSuggestionArea(bool) {}
    DevToolsX::Style TextBoxStyle() { return nullptr; }
    void TextBoxStyle(DevToolsX::Style const&) {}
    DevToolsXC::IconElement QueryIcon() { return nullptr; }
    void QueryIcon(DevToolsXC::IconElement const&) {}
    DevToolsXC::LightDismissOverlayMode LightDismissOverlayMode() { return DevToolsXC::LightDismissOverlayMode::Auto; }
    void LightDismissOverlayMode(DevToolsXC::LightDismissOverlayMode) {}
    winrt::Windows::Foundation::IInspectable Description() { return nullptr; }
    void Description(winrt::Windows::Foundation::IInspectable const&) {}
    using ChosenHandler = winrt::Windows::Foundation::TypedEventHandler<DevToolsXC::AutoSuggestBox,
        DevToolsXC::AutoSuggestBoxSuggestionChosenEventArgs>;
    using ChangedHandler = winrt::Windows::Foundation::TypedEventHandler<DevToolsXC::AutoSuggestBox,
        DevToolsXC::AutoSuggestBoxTextChangedEventArgs>;
    using QueryHandler = winrt::Windows::Foundation::TypedEventHandler<DevToolsXC::AutoSuggestBox,
        DevToolsXC::AutoSuggestBoxQuerySubmittedEventArgs>;
    winrt::event_token SuggestionChosen(ChosenHandler const&) { return {}; }
    void SuggestionChosen(winrt::event_token const&) {}
    winrt::event_token TextChanged(ChangedHandler const&) { return {}; }
    void TextChanged(winrt::event_token const&) {}
    winrt::event_token QuerySubmitted(QueryHandler const&) { return {}; }
    void QuerySubmitted(winrt::event_token const&) {}
};
struct ResourceChoice : winrt::implements<ResourceChoice, DevToolsXC::IAutoSuggestBoxSuggestionChosenEventArgs,
    DevToolsXC::IAutoSuggestBoxQuerySubmittedEventArgs>
{
    winrt::Windows::Foundation::IInspectable item{nullptr};
    winrt::Windows::Foundation::IInspectable SelectedItem() { return item; }
    winrt::Windows::Foundation::IInspectable ChosenSuggestion() { return item; }
    winrt::hstring QueryText() { return L"ChosenResource"; }
};
struct ResourceTextChange : winrt::implements<ResourceTextChange, DevToolsXC::IAutoSuggestBoxTextChangedEventArgs>
{
    DevToolsXC::AutoSuggestionBoxTextChangeReason reason = DevToolsXC::AutoSuggestionBoxTextChangeReason::ProgrammaticChange;
    DevToolsXC::AutoSuggestionBoxTextChangeReason Reason() { return reason; }
    void Reason(DevToolsXC::AutoSuggestionBoxTextChangeReason value) { reason = value; }
    bool CheckCurrent() { return true; }
};
struct ViewItem : winrt::implements<ViewItem, DevToolsXC::ISelectorBarItem>
{
    winrt::hstring Text() { return {}; }
    void Text(winrt::hstring const&) {}
    DevToolsXC::IconElement Icon() { return nullptr; }
    void Icon(DevToolsXC::IconElement const&) {}
};
struct ViewSelector : winrt::implements<ViewSelector, DevToolsXC::ISelectorBar>
{
    winrt::Windows::Foundation::Collections::IVector<DevToolsXC::SelectorBarItem> items =
        winrt::single_threaded_vector<DevToolsXC::SelectorBarItem>();
    DevToolsXC::SelectorBarItem selected{nullptr};
    auto Items() { return items; }
    auto SelectedItem() { return selected; }
    void SelectedItem(DevToolsXC::SelectorBarItem const& value) { selected = value; }
    using Handler = winrt::Windows::Foundation::TypedEventHandler<DevToolsXC::SelectorBar,
        DevToolsXC::SelectorBarSelectionChangedEventArgs>;
    winrt::event_token SelectionChanged(Handler const&) { return {}; }
    void SelectionChanged(winrt::event_token const&) {}
};
static DevToolsXC::IAutoSuggestBox resourceBox{nullptr};
static unsigned submissions = 0;
static std::wstring submittedPath, submittedMode;
static int selectedMode = 0;
static HRESULT FindEditorName(void*, HSTRING name, IInspectable** out)
{
    const std::wstring key(WindowsGetStringRawBuffer(name, nullptr));
    *out = key == L"PResKey" ? reinterpret_cast<IInspectable*>(winrt::get_abi(resourceBox)) :
        key == L"PPath" ? &pathBox : key == L"PMode" ? &modeBox : nullptr;
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
    {
        auto selector = winrt::make_self<ViewSelector>();
        selector->items.Append(winrt::make<ViewItem>().as<DevToolsXC::SelectorBarItem>());
        selector->items.Append(winrt::make<ViewItem>().as<DevToolsXC::SelectorBarItem>());
        auto abi = winrt::get_abi(selector.as<DevToolsXC::ISelectorBar>());
        OnlySetToggleSink views;
        views.Init();
        selector->selected = selector->items.GetAt(1);
        views.Invoke(abi, nullptr);
        check(ShowingSetView(), "the property selector chooses Set");
        views.Invoke(abi, nullptr);
        check(ShowingSetView(), "reselecting Set does not toggle the active view off");
        selector->selected = nullptr;
        views.Invoke(abi, nullptr);
        check(selector->selected == selector->items.GetAt(1), "an empty property selection restores the active view");
        selector->selected = selector->items.GetAt(0);
        views.Invoke(abi, nullptr);
        check(!ShowingSetView(), "the property selector returns to Computed");
        CommentsViewSink modes;
        modes.Init();
        g_inspectorViewSelector = abi;
        selector->selected = selector->items.GetAt(1);
        modes.Invoke(abi, nullptr);
        check(g_commentsView, "the inspector selector opens Comments");
        modes.Invoke(abi, nullptr);
        check(g_commentsView, "reselecting Comments does not toggle the active view off");
        selector->selected = nullptr;
        modes.Invoke(abi, nullptr);
        check(selector->selected == selector->items.GetAt(1), "an empty inspector selection restores Comments");
        selector->selected = selector->items.GetAt(0);
        modes.Invoke(abi, nullptr);
        check(!g_commentsView, "the inspector selector returns to the visual tree");
        g_inspectorViewSelector = nullptr;
    }
    {
        const std::vector<std::wstring> keys{L"AccentBrush", L"ControlFillColorDefaultBrush", L"BodyText"};
        check(FilterResourceKeys(keys, L"BRUSH") == std::vector<std::wstring>{L"AccentBrush", L"ControlFillColorDefaultBrush"},
            "resource typeahead filters case-insensitively without changing candidate order");
        check(FilterResourceKeys(keys, L"").size() == 3 && FilterResourceKeys(keys, L"missing").empty(),
            "resource typeahead supports an empty query and an empty result without a fake key");
        resourceBox = winrt::make<ResourceBox>();
        resourceBox.Text(L"OriginalResource");
        bool found = false;
        check(ReadBox(&pathBox, L"PResKey", &found) == L"OriginalResource" && found,
            "resource commit reads AutoSuggestBox.Text without the TextBox read callback");
        DevToolsCardRow resourceRow;
        resourceRow.name = L"Foreground";
        resourceRow.type = L"Brush";
        resourceRow.authoredKind = L"themeResource";
        resourceRow.authoredKey = L"OriginalResource";
        check(ResourceEditorSeed(resourceRow, L"Foreground", DevToolsKind::Theme) == L"OriginalResource",
            "opening an existing resource editor preserves its authored key");
        NoteSessionAssign(L"Foreground", DevToolsKind::Theme, L"{ThemeResource SessionResource}");
        check(ResourceEditorSeed(resourceRow, L"Foreground", DevToolsKind::Theme) == L"SessionResource",
            "reopening a resource editor seeds the current session assignment, not stale source XAML");
        check(ResourceEditorSeed(resourceRow, L"Foreground", DevToolsKind::Static).empty(),
            "staging a different resource kind starts with an empty key rather than applying a suggestion");
        SessionAssignErase(L"Foreground");
        KindCtx resourceCtx{std::make_shared<const DevToolsCardRow>(resourceRow), L"Foreground", 0, nullptr, &pathBox};
        unsigned resolves = 0;
        g_ctx.resolveFn = [&](auto, const auto&, const auto& key, bool theme) {
            ++resolves;
            check(key == L"ChosenResource" && theme, "resource assignment forwards the exact chosen key and kind");
            return true;
        };
        KindKeySink preview;
        preview.Init(resourceCtx, false, true);
        int enter = 13;
        keyHandled = false;
        preview.Invoke(nullptr, &enter);
        check(!keyHandled && resolves == 0, "resource PreviewKeyDown leaves Enter for AutoSuggestBox to interpret");
        auto choice = winrt::make_self<ResourceChoice>();
        choice->item = winrt::box_value(L"ChosenResource");
        ResSuggestSink choose;
        choose.Init();
        choose.Invoke(winrt::get_abi(resourceBox),
            winrt::get_abi(choice.as<DevToolsXC::IAutoSuggestBoxSuggestionChosenEventArgs>()));
        check(resourceBox.Text() == L"ChosenResource" && resolves == 0,
            "pointer or keyboard suggestion choice only fills the field");
        ResQuerySink query;
        query.Init(resourceCtx);
        query.Invoke(nullptr, winrt::get_abi(choice.as<DevToolsXC::IAutoSuggestBoxQuerySubmittedEventArgs>()));
        check(resolves == 0, "Enter choosing a suggestion does not apply the resource");
        choice->item = nullptr;
        query.Invoke(nullptr, winrt::get_abi(choice.as<DevToolsXC::IAutoSuggestBoxQuerySubmittedEventArgs>()));
        check(resolves == 1, "a separate Enter on typed text applies the resource once");
        KindApplySink apply;
        apply.Init(resourceCtx);
        apply.Invoke(nullptr, nullptr);
        check(resolves == 2, "the explicit Apply action still applies the resource");
        ResTextChangedSink changed;
        changed.Init(keys);
        auto change = winrt::make_self<ResourceTextChange>();
        for (auto reason : {DevToolsXC::AutoSuggestionBoxTextChangeReason::ProgrammaticChange,
                DevToolsXC::AutoSuggestionBoxTextChangeReason::SuggestionChosen}) {
            change->reason = reason;
            check(changed.Invoke(winrt::get_abi(resourceBox),
                winrt::get_abi(change.as<DevToolsXC::IAutoSuggestBoxTextChangedEventArgs>())) == S_OK,
                "non-user resource text changes do not refilter suggestions");
        }
        ++g_route1Gen;
        query.Invoke(nullptr, winrt::get_abi(choice.as<DevToolsXC::IAutoSuggestBoxQuerySubmittedEventArgs>()));
        check(resolves == 2, "stale resource callbacks cannot write after a pane replacement");
        g_ctx.resolveFn = nullptr;
        resourceBox = nullptr;
        g_sessionAssign.clear();
        g_dirtySeed.clear();
    }
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
    g_validateFromXBind = true;
    ApplyValidateAnswer(L"{\"expressionSource\":\"proposed\",\"againstKind\":\"binding\",\"segments\":["
                        L"{\"path\":\"ViewModel\",\"found\":false,\"hr\":\"0x80070490\",\"isNull\":false}]}");
    check(g_validateBad && g_validateVerdict.find(L"starts at the DataContext") != std::wstring::npos,
          "a failing path copied from x:Bind explains that {Binding} starts at the DataContext");
    g_validateFromXBind = false;
    ApplyValidateAnswer(L"{\"expressionSource\":\"proposed\",\"againstKind\":\"binding\",\"segments\":["
                        L"{\"path\":\"ViewModel\",\"found\":false,\"hr\":\"0x80070490\",\"isNull\":false}]}");
    check(g_validateVerdict.find(L"starts at the DataContext") == std::wstring::npos,
          "a typed {Binding} path gets no x:Bind hint");
    check(RestoreLanded(L"{\"state\":\"restored\",\"bound\":\"True\"}"), "a reattached {Binding} counts as restored");
    check(RestoreLanded(L"{\"state\":\"unavailable\",\"ownerUpdated\":\"True\",\"restoreVerification\":\"unchecked\","
                        L"\"before\":\"OVERRIDE\",\"after\":\"Hello\"}"),
          "a compiled refresh that changed the target counts as restored");
    check(!RestoreLanded(L"{\"state\":\"unavailable\",\"ownerUpdated\":\"True\",\"restoreVerification\":\"unchecked\","
                         L"\"before\":\"OVERRIDE\",\"after\":\"OVERRIDE\"}"),
          "a compiled refresh that left the override in place is not restored");
    check(!RestoreLanded(L"{\"state\":\"unavailable\",\"ownerUpdated\":\"True\",\"before\":\"OVERRIDE\","
                         L"\"targetUnavailable\":\"gone\"}"),
          "a compiled refresh whose target could not be reread is not restored");
    check(!RestoreLanded(L"{\"state\":\"unavailable\",\"reason\":\"nothing was captured\"}"),
          "an unavailable restore is not restored");
    check(!RestoreLanded(L"{\"state\":\"restored\",\"bound\":\"False\"}"),
          "a reattached binding the app dropped again is not restored");
    check(!DiagMayCapture(DevToolsBindingTone::Works, L"", L"", /*afterLiveWrite*/ true),
          "a literal written over the binding keeps the saved binding for Restore");
    check(DiagMayCapture(DevToolsBindingTone::Works, L"", L"", false), "a live binding is captured");
    check(DiagMayCapture(DevToolsBindingTone::Works, L"restore", L"{\"state\":\"restored\",\"bound\":\"True\"}", true),
          "a restored binding is captured again");
    check(!DiagMayCapture(DevToolsBindingTone::Works, L"restore", L"{\"state\":\"restored\",\"bound\":\"False\"}", true),
          "a failed restore keeps the saved binding");
    row.name = L"Text";
    row.authoredKind = L"xBind";
    g_propRows = {row};
    NoteRestoredBinding(L"Text", L"{\"state\":\"restored\",\"restoreKind\":\"binding\",\"path\":\"Title\","
                                 L"\"mode\":\"TwoWay\",\"bound\":\"True\"}");
    const SessionAssign* restoredAssign = SessionAssignFind(L"Text");
    check(restoredAssign && restoredAssign->kind == DevToolsKind::Binding &&
          restoredAssign->detail == L"{Binding Title, Mode=TwoWay}",
          "restoring a panel-applied {Binding} over an x:Bind shows the {Binding}, not the XAML");
    SessionAssignErase(L"Text");
    g_propRows[0].authoredKind = L"binding";
    g_propRows[0].authoredKey = L"Name";
    NoteRestoredBinding(L"Text", L"{\"state\":\"restored\",\"restoreKind\":\"binding\",\"path\":\"Title\","
                                 L"\"mode\":\"OneWay\",\"bound\":\"True\"}");
    restoredAssign = SessionAssignFind(L"Text");
    check(restoredAssign && restoredAssign->detail == L"{Binding Title, Mode=OneWay}",
          "restoring a panel-applied {Binding Title} over XAML {Binding Name} shows Title, not the XAML path");
    SessionAssignErase(L"Text");
    g_propRows[0].authoredKey = L"Title";
    NoteRestoredBinding(L"Text", L"{\"state\":\"restored\",\"restoreKind\":\"binding\",\"path\":\"Title\","
                                 L"\"mode\":\"OneWay\",\"bound\":\"True\"}");
    check(SessionAssignFind(L"Text") == nullptr, "restoring the XAML's own {Binding} leaves the XAML as the source");
    g_propRows.clear();
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
