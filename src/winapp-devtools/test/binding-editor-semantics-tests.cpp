// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "../native/WinApp.DevTools.Native/DevToolsProjected.h"
static HRESULT FindEditorName(void*, HSTRING, IInspectable**);
static HRESULT PutEditorText(void*, HSTRING);
static HRESULT GetEditorSelection(void*, int*);
static HRESULT GetEditorKey(void* args, int* key) { *key = *static_cast<int*>(args); return S_OK; }
static bool keyHandled = false;
static HRESULT PutEditorHandled(void*, bool handled) { keyHandled = handled; return S_OK; }
static HRESULT PutEditorVisibility(void*, int);
static HANDLE RefuseWorker(LPSECURITY_ATTRIBUTES, SIZE_T, LPTHREAD_START_ROUTINE, LPVOID, DWORD, LPDWORD);
#define DevToolsFindName FindEditorName
#define DevToolsPutText PutEditorText
#define DevToolsGetSelectedIndex GetEditorSelection
#define DevToolsGetKey GetEditorKey
#define DevToolsPutHandled PutEditorHandled
#define DevToolsPutVisibility PutEditorVisibility
#define CreateThread RefuseWorker
#include "../native/WinApp.DevTools.Native/DevToolsWindow.cpp"
#undef CreateThread

struct EditorElement : IInspectable
{
    ULONG refs = 1;
    std::wstring text;
    int visibility = 1;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** out) override
    { *out = static_cast<IInspectable*>(this); AddRef(); return S_OK; }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    HRESULT STDMETHODCALLTYPE GetIids(ULONG* count, IID** ids) override
    { *count = 0; *ids = nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRuntimeClassName(HSTRING* out) override { *out = nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetTrustLevel(TrustLevel* out) override { *out = BaseTrust; return S_OK; }
};
static EditorElement status, pathBox, modeBox, resourceError, resourceNote;
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
static unsigned restoreRequests = 0;
static std::wstring restoreOperation;
static std::map<__int64, KindEditorSink*> restoreEvents;
static __int64 nextRestoreEvent = 0;
static HRESULT AddRestoreEvent(void*, void* handler, __int64* token)
{
    auto* sink = static_cast<KindEditorSink*>(handler);
    sink->AddRef();
    restoreEvents[*token = ++nextRestoreEvent] = sink;
    return S_OK;
}
static HRESULT RemoveRestoreEvent(void*, __int64 token)
{
    const auto found = restoreEvents.find(token);
    if (found == restoreEvents.end()) return E_INVALIDARG;
    auto* sink = found->second;
    restoreEvents.erase(found);
    sink->Release();
    return S_OK;
}
static int selectedMode = 0;
static HRESULT FindEditorName(void*, HSTRING name, IInspectable** out)
{
    const std::wstring key(WindowsGetStringRawBuffer(name, nullptr));
    *out = key == L"PResKey" ? reinterpret_cast<IInspectable*>(winrt::get_abi(resourceBox))
        : key == L"PResError" ? &resourceError : key == L"PResNote" ? &resourceNote
        : key == L"PPath" ? &pathBox : key == L"PMode" ? &modeBox : nullptr;
    if (*out) (*out)->AddRef();
    return S_OK;
}
static HRESULT PutEditorText(void* element, HSTRING value)
{
    static_cast<EditorElement*>(element)->text = WindowsGetStringRawBuffer(value, nullptr);
    return S_OK;
}
static HRESULT GetEditorSelection(void*, int* value) { *value = selectedMode; return S_OK; }
static HRESULT PutEditorVisibility(void* element, int value)
{ static_cast<EditorElement*>(element)->visibility = value; return S_OK; }

using Object = winrt::Windows::Foundation::IInspectable;
using ObjectPair = winrt::Windows::Foundation::Collections::IKeyValuePair<Object, Object>;
struct ResourceDictionaryFixture : winrt::implements<ResourceDictionaryFixture,
    DevToolsX::IResourceDictionary,
    winrt::Windows::Foundation::Collections::IMap<Object, Object>,
    winrt::Windows::Foundation::Collections::IIterable<ObjectPair>>
{
    auto Source() { return winrt::Windows::Foundation::Uri{nullptr}; }
    void Source(const winrt::Windows::Foundation::Uri&) {}
    auto MergedDictionaries() { return merged; }
    auto ThemeDictionaries() { return themes; }
    Object Lookup(const Object& key) { return values.Lookup(winrt::unbox_value<winrt::hstring>(key)); }
    uint32_t Size() { return values.Size(); }
    bool HasKey(const Object& key) { return values.HasKey(winrt::unbox_value<winrt::hstring>(key)); }
    bool Insert(const Object& key, const Object& value)
    { return values.Insert(winrt::unbox_value<winrt::hstring>(key), value); }
    void Remove(const Object& key) { values.Remove(winrt::unbox_value<winrt::hstring>(key)); }
    void Clear() { values.Clear(); }
    auto GetView()
    {
        auto map = winrt::single_threaded_map<Object, Object>();
        for (const auto& entry : values) map.Insert(winrt::box_value(entry.Key()), entry.Value());
        return map.GetView();
    }
    auto First()
    {
        auto map = winrt::single_threaded_map<Object, Object>();
        for (const auto& entry : values) map.Insert(winrt::box_value(entry.Key()), entry.Value());
        return map.First();
    }
    winrt::Windows::Foundation::Collections::IMap<winrt::hstring, Object> values =
        winrt::single_threaded_map<winrt::hstring, Object>();
    winrt::Windows::Foundation::Collections::IVector<DevToolsX::ResourceDictionary> merged =
        winrt::single_threaded_vector<DevToolsX::ResourceDictionary>();
    winrt::Windows::Foundation::Collections::IMap<Object, Object> themes =
        winrt::single_threaded_map<Object, Object>();
};
static HANDLE RefuseWorker(LPSECURITY_ATTRIBUTES, SIZE_T, LPTHREAD_START_ROUTINE entry, LPVOID param, DWORD, LPDWORD)
{
    if (entry == DevToolsThreadEntry<DiagWorker>) {
        ++restoreRequests;
        restoreOperation = static_cast<DiagAsk*>(param)->op;
        SetLastError(ERROR_NOT_SUPPORTED);
        return nullptr;
    }
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
        const auto bar = selector.as<DevToolsXC::ISelectorBar>();
        selector->selected = selector->items.GetAt(1);
        ApplyPropViewSelection(bar);
        check(ShowingSetView(), "the property selector chooses Set");
        ApplyPropViewSelection(bar);
        check(ShowingSetView(), "reselecting Set does not toggle the active view off");
        selector->selected = nullptr;
        ApplyPropViewSelection(bar);
        check(selector->selected == selector->items.GetAt(1), "an empty property selection restores the active view");
        selector->selected = selector->items.GetAt(0);
        ApplyPropViewSelection(bar);
        check(!ShowingSetView(), "the property selector returns to Computed");
        OnInspectorRailSelection(true);
        check(g_commentsView, "the rail opens Comments");
        OnInspectorRailSelection(true);
        check(g_commentsView, "reselecting Comments keeps Comments open");
        g_navSyncing = true;
        OnInspectorRailSelection(false);
        check(g_commentsView, "a rail change that follows the view does not switch the view");
        g_navSyncing = false;
        OnInspectorRailSelection(false);
        check(!g_commentsView, "the rail returns to the visual tree");
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
        ResourceKeys resourceKeys;
        resourceKeys.keys = keys;
        const auto changed = ResourceKeyChangedHandler(&pathBox, L"Foreground", resourceKeys);
        auto change = winrt::make_self<ResourceTextChange>();
        resourceNote.text = L"unchanged";
        for (auto reason : {DevToolsXC::AutoSuggestionBoxTextChangeReason::ProgrammaticChange,
                DevToolsXC::AutoSuggestionBoxTextChangeReason::SuggestionChosen}) {
            change->reason = reason;
            changed(resourceBox.as<DevToolsXC::AutoSuggestBox>(), change.as<DevToolsXC::AutoSuggestBoxTextChangedEventArgs>());
            check(resourceNote.text == L"unchanged" && !resourceBox.IsSuggestionListOpen(),
                "non-user resource text changes do not refilter suggestions");
        }
        ++g_route1Gen;
        query.Invoke(nullptr, winrt::get_abi(choice.as<DevToolsXC::IAutoSuggestBoxQuerySubmittedEventArgs>()));
        check(resolves == 2, "stale resource callbacks cannot write after a pane replacement");
        g_ctx.resolveFn = nullptr;
        resourceBox = nullptr;
        g_sessionAssign.clear();
        g_editRecords.clear();
        g_appChanged.clear();
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
    KindCtx ctx{std::make_shared<const DevToolsCardRow>(row), L"Text", 0, nullptr, &pathBox, 77};
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
    {
        KindCtx stale = ctx;
        stale.wire = 76;
        KindApplySink late;
        late.Init(stale);
        const auto beforeLate = submissions;
        late.Invoke(nullptr, nullptr);
        check(submissions == beforeLate, "Apply from an editor built for another element does not write");
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
    {
        row = {};
        row.name = L"Text";
        row.authoredKind = L"binding";
        row.authoredKey = L"Person.Name";
        row.authored = L"{Binding Person.Name}";
        row.binding = L"{Binding Title, Mode=OneWay}";
        row.source = L"Binding";
        // Selection changes clear the session witnesses, but the next property read observes the installed binding.
        g_sessionAssign.clear();
        g_pendingKind.clear();
        check(BindingPathOf(row, L"Text") == L"Title", "reselection seeds the installed path rather than authored Person.Name");
        NoteSessionAssign(L"Text", DevToolsKind::Binding, L"{Binding Stale, Mode=TwoWay}");
        check(BindingPathOf(row, L"Text") == L"Title" && BindingModeOf(row, L"Text") == L"OneWay",
              "observed path and mode also win over a stale session witness");
        g_sessionAssign.clear();
        pathBox.text = BindingPathOf(row, L"Text");
        selectedMode = 2;
        ctx.row = std::make_shared<const DevToolsCardRow>(row);
        const auto count = submissions;
        KindApplySink apply;
        apply.Init(ctx);
        apply.Invoke(nullptr, nullptr);
        check(submissions == count + 1 && submittedPath == L"Title" && submittedMode == L"OneTime",
              "mode-only Apply after reselection preserves the installed Title path");
        row.authoredKind = L"xBind";
        row.authored = L"{x:Bind Vm.Title}";
        check(CurrentKindOf(row, L"Text") == DevToolsKind::Binding,
              "a regular runtime binding replacing x:Bind stays Binding after reselection");
        row.binding = L"{Binding Mode=OneTime}";
        check(BindingPathOf(row, L"Text").empty(), "a pathless installed binding does not seed Mode as a path");
        row.binding = L"{Binding Items[key=value], Mode=OneWay}";
        check(BindingPathOf(row, L"Text") == L"Items[key=value]",
              "an equals sign inside an indexer stays part of the installed path");
        pathBox.text = BindingPathOf(row, L"Text");
        ctx.row = std::make_shared<const DevToolsCardRow>(row);
        const auto indexedCount = submissions;
        KindApplySink indexedApply;
        indexedApply.Init(ctx);
        indexedApply.Invoke(nullptr, nullptr);
        check(submissions == indexedCount + 1 && submittedPath == L"Items[key=value]" && submittedMode == L"OneTime",
              "mode-only Apply preserves an installed indexer key containing an equals sign");
        row = {};
        row.name = L"Content"; row.authoredKind = L"literal"; row.authored = L"Submit";
        PendingKindSet(L"Content", DevToolsKind::Binding);
        check(StagedOrCurrentKind(row, L"Content") == DevToolsKind::Binding &&
              CurrentKindOf(row, L"Content") == DevToolsKind::Literal && !BindingLost(L"Content"),
              "staging Make Content a Binding changes only the staged choice, not installed or recovery state");
        g_pendingKind.clear();
    }
    {
        BindRestoreSink request, confirm, cancel;
        request.Init(L"Text");
        confirm.Init(L"Text", BindRestoreSink::Choice::Confirm);
        cancel.Init(L"Text", BindRestoreSink::Choice::Cancel);
        request.Invoke(nullptr, nullptr);
        check(restoreRequests == 1 && restoreOperation == L"restore",
              "first Restore requests scope without granting owner-wide consent");
        const std::wstring preview = LR"({"state":"confirmation-required","restoreScope":"owner","restoreOwner":"Page","warning":"other live edits"})";
        g_diagAnswers = {{g_route1Gen, g_selWire, L"Text", L"", preview, true}};
        InterlockedExchange(&g_diagRestoreBusy, 1);
        confirm.Invoke(nullptr, nullptr);
        check(restoreRequests == 1 && DiagFind(L"Text")->action == preview &&
              status.text.find(L"Try confirming again") != std::wstring::npos,
              "busy Restore preserves the confirmation and explains that it can be retried");
        cancel.Invoke(nullptr, nullptr);
        check(restoreRequests == 1 && DiagFind(L"Text")->action.empty() && g_diagRestoreBusy == 1,
              "Cancel consumes the prompt while busy without submitting or releasing another Restore");
        InterlockedExchange(&g_diagRestoreBusy, 0);
        confirm.Invoke(nullptr, nullptr);
        check(restoreRequests == 1, "an old confirmation cannot run after Cancel");
        g_diagAnswers[0].action = preview;
        InterlockedExchange(&g_diagRestoreBusy, 1);
        confirm.Invoke(nullptr, nullptr);
        check(restoreRequests == 1 && DiagFind(L"Text")->action == preview,
              "busy confirmation leaves the pending preview available");
        InterlockedExchange(&g_diagRestoreBusy, 0);
        confirm.Invoke(nullptr, nullptr);
        check(restoreRequests == 2 && restoreOperation == L"restoreconfirmed",
              "explicit confirmation alone submits owner-wide Restore");
        confirm.Invoke(nullptr, nullptr);
        check(restoreRequests == 2, "one confirmation cannot submit twice");
        g_diagAnswers[0].action = preview;
        ++g_selWire;
        confirm.Invoke(nullptr, nullptr);
        check(restoreRequests == 2, "confirmation from a previous selection cannot restore another element");
        --g_selWire;
        g_diagAnswers.clear();
    }
    {
        const std::wstring preview = LR"({"state":"confirmation-required","restoreScope":"owner","restoreOwner":"Page"})";
        const auto count = restoreRequests;
        std::vector<winrt::com_ptr<BindRestoreSink>> oldButtons;
        for (auto action : {BindRestoreSink::Choice::Request, BindRestoreSink::Choice::Confirm,
                            BindRestoreSink::Choice::Cancel}) {
            auto* sink = new BindRestoreSink();
            sink->Init(L"Text", action, &pathBox);
            winrt::com_ptr<BindRestoreSink> held;
            held.copy_from(sink);
            oldButtons.push_back(std::move(held));
            check(WireKindEditor(sink, &status, AddRestoreEvent, RemoveRestoreEvent),
                  "Restore buttons use the scoped editor subscription");
        }
        RetireKindEditors(&pathBox);
        check(restoreEvents.empty(), "row retirement unsubscribes all three Restore buttons");
        g_diagAnswers = {{g_route1Gen, g_selWire, L"Text", L"", preview, true}};
        for (const auto& sink : oldButtons) sink->Invoke(nullptr, nullptr);
        check(restoreRequests == count && DiagFind(L"Text")->action == preview,
              "retired same-selection Restore buttons cannot consume a new expansion's consent");
        auto* current = new BindRestoreSink();
        current->Init(L"Text", BindRestoreSink::Choice::Confirm, &pathBox);
        WireKindEditor(current, &status, AddRestoreEvent, RemoveRestoreEvent);
        current->Invoke(nullptr, nullptr);
        check(restoreRequests == count + 1 && restoreOperation == L"restoreconfirmed" &&
              DiagFind(L"Text")->action.empty(),
              "newly wired Confirm can consume preserved consent after row replacement");
        RetireKindEditors();
        check(restoreEvents.empty(), "pane retirement unsubscribes the new confirmation");
        g_diagAnswers.clear();
    }
    g_propRows.clear();
    {
        const auto dictionary = winrt::make_self<ResourceDictionaryFixture>();
        dictionary->values.Insert(L"AppSize", winrt::box_value(12.0));
        dictionary->values.Insert(L"NotANumber", winrt::box_value(L"text"));
        const auto merged = winrt::make_self<ResourceDictionaryFixture>();
        merged->values.Insert(L"MergedSize", winrt::box_value(24.0));
        merged->values.Insert(L"NotANumber", winrt::box_value(42.0));
        dictionary->merged.Append(merged.as<DevToolsX::ResourceDictionary>());
        // A self-reference must not recurse forever.
        merged->merged.Append(dictionary.as<DevToolsX::ResourceDictionary>());
        ResourceKeys candidates;
        CollectResourceDictionary(dictionary.as<DevToolsX::ResourceDictionary>(), L"Double", L"Light", candidates);
        check(candidates.keys.size() == 2 &&
              std::find(candidates.keys.begin(), candidates.keys.end(), L"AppSize") != candidates.keys.end() &&
              std::find(candidates.keys.begin(), candidates.keys.end(), L"MergedSize") != candidates.keys.end(),
              "resource enumeration includes app and merged keys, with boxed numeric types");
        check(candidates.seen.count(L"NotANumber") == 1 &&
              std::find(candidates.keys.begin(), candidates.keys.end(), L"NotANumber") == candidates.keys.end(),
              "nearer incompatible value prevents suggesting a shadowed merged key");
        merged->merged.Clear();
        const auto light = winrt::make_self<ResourceDictionaryFixture>();
        light->values.Insert(L"ThemeSize", winrt::box_value(18.0));
        const auto dark = winrt::make_self<ResourceDictionaryFixture>();
        dark->values.Insert(L"DarkSize", winrt::box_value(30.0));
        const auto contrast = winrt::make_self<ResourceDictionaryFixture>();
        contrast->values.Insert(L"ContrastSize", winrt::box_value(36.0));
        dictionary->themes = dictionary.as<winrt::Windows::Foundation::Collections::IMap<Object, Object>>();
        dictionary->values.Insert(L"Light", light.as<Object>());
        dictionary->values.Insert(L"Dark", dark.as<Object>());
        dictionary->values.Insert(L"HighContrast", contrast.as<Object>());
        check(std::wstring(ResourceTheme(DevToolsX::ElementTheme::Light, false)) == L"Light" &&
              std::wstring(ResourceTheme(DevToolsX::ElementTheme::Dark, false)) == L"Dark" &&
              std::wstring(ResourceTheme(DevToolsX::ElementTheme::Default, false)) == L"Light",
              "resource discovery preserves Light, Dark and default theme selection");
        for (const auto theme : {DevToolsX::ElementTheme::Light, DevToolsX::ElementTheme::Dark}) {
            candidates = {};
            CollectResourceDictionary(dictionary.as<DevToolsX::ResourceDictionary>(), L"Double",
                                      ResourceTheme(theme, true), candidates);
            check(std::find(candidates.keys.begin(), candidates.keys.end(), L"ContrastSize") != candidates.keys.end() &&
                  std::find(candidates.keys.begin(), candidates.keys.end(), L"ThemeSize") == candidates.keys.end() &&
                  std::find(candidates.keys.begin(), candidates.keys.end(), L"DarkSize") == candidates.keys.end(),
                  "high contrast overrides the element's Light or Dark theme for suggestions");
        }
        candidates = {};
        CollectResourceDictionary(dictionary.as<DevToolsX::ResourceDictionary>(), L"Double", L"Light", candidates);
        check(std::find(candidates.keys.begin(), candidates.keys.end(), L"ThemeSize") != candidates.keys.end() &&
              std::find(candidates.keys.begin(), candidates.keys.end(), L"DarkSize") == candidates.keys.end(),
              "only the active theme dictionary supplies suggestions");
        dictionary->values.Remove(L"Light");
        dictionary->values.Insert(L"Default", light.as<Object>());
        candidates = {};
        CollectResourceDictionary(dictionary.as<DevToolsX::ResourceDictionary>(), L"Double", L"Light", candidates);
        check(std::find(candidates.keys.begin(), candidates.keys.end(), L"ThemeSize") != candidates.keys.end(),
              "missing named theme uses Default dictionary");
        dictionary->values.Remove(L"HighContrast");
        candidates = {};
        CollectResourceDictionary(dictionary.as<DevToolsX::ResourceDictionary>(), L"Double",
                                  ResourceTheme(DevToolsX::ElementTheme::Dark, true), candidates);
        check(std::find(candidates.keys.begin(), candidates.keys.end(), L"ThemeSize") != candidates.keys.end() &&
              std::find(candidates.keys.begin(), candidates.keys.end(), L"DarkSize") == candidates.keys.end(),
              "missing high-contrast dictionary uses Default rather than Dark");
        dictionary->themes = winrt::single_threaded_map<Object, Object>();
        dictionary->merged.Clear();
        candidates = {};
        candidates.entries = 4096;
        CollectResourceDictionary(dictionary.as<DevToolsX::ResourceDictionary>(), L"Double", L"Light", candidates);
        check(candidates.partial && candidates.keys.empty(), "resource enumeration is bounded and marks an incomplete list");
        check(ResourceTypeFamily(L"Microsoft.UI.Xaml.Media.SolidColorBrush") == L"Brush",
              "brush subclasses are compatible resource suggestions");
    }
    {
        row = {};
        row.name = L"Background"; row.type = L"SolidColorBrush"; row.value = L"#FF808080";
        resourceBox = winrt::make<ResourceBox>();
        ctx = {std::make_shared<const DevToolsCardRow>(row), L"Background", 0, nullptr, &pathBox, g_selWire};
        resourceBox.Text(L"MissingBrush");
        PendingKindSet(L"Background", DevToolsKind::Theme);
        unsigned resolves = 0, restores = 0;
        g_ctx.resolveFn = [&](InstanceHandle, const std::wstring&, const std::wstring&, bool) {
            ++resolves; return false;
        };
        g_ctx.writeFn = [&](InstanceHandle, const std::wstring&, const std::wstring&, const std::wstring& value,
                            bool, std::wstring*) {
            ++restores;
            check(value == L"#FF808080", "failed resource lookup restores the earlier literal");
            return DevToolsWriteOutcome::Ok;
        };
        CommitKind(ctx);
        check(resolves == 1 && restores == 1 && resourceError.visibility == 0 &&
              resourceError.text.find(L"MissingBrush") != std::wstring::npos &&
              resourceError.text.find(L"previous value was restored") != std::wstring::npos,
              "failed lookup displays the exact key and restoration outcome beside the editor");
        check(resourceBox.Text() == L"MissingBrush" && SessionAssignFind(L"Background") == nullptr &&
              PendingKindFind(L"Background") != nullptr,
              "failed lookup preserves the correction text without claiming the key was assigned");
        g_ctx.writeFn = [](InstanceHandle, const std::wstring&, const std::wstring&, const std::wstring&,
                           bool, std::wstring*) { return DevToolsWriteOutcome::Failed; };
        CommitKind(ctx);
        check(resourceError.text.find(L"could not be restored") != std::wstring::npos,
              "failed restoration is explicit rather than claiming the app was unchanged");
        resourceBox.Text(L"");
        CommitKind(ctx);
        check(resourceError.text == L"Type a resource key first." && resolves == 2,
              "empty key error is inline and makes no lookup");
        auto changed = ResourceKeyChangedHandler(ctx.expFe, ctx.path, ResourceKeys{});
        resourceBox.Text(L"ValidBrush");
        changed(nullptr, nullptr);
        check(resourceError.text.empty() && resourceError.visibility == 1 && resolves == 2,
              "correcting text clears the old inline error without applying another resource");
        resourceError.text = L"new selection";
        ++g_selWire;
        changed(nullptr, nullptr);
        check(resourceError.text == L"new selection", "resource typeahead ignores a replaced selection");
        --g_selWire;
        ++g_route1Gen;
        changed(nullptr, nullptr);
        check(resourceError.text == L"new selection", "resource typeahead ignores a replaced inspector");
        --g_route1Gen;
        auto* changedAbi = reinterpret_cast<IUnknown*>(winrt::detach_abi(changed));
        check(changedAbi->Release() == 0, "resource typeahead delegate is deleted when its last owner releases it");
        bool releasedSnapshots = true;
        ResourceKeys large;
        for (unsigned i = 0; i < 4096; ++i) {
            const auto key = L"Resource" + std::to_wstring(i);
            large.keys.push_back(key);
            large.seen.insert(key);
        }
        for (unsigned i = 0; i < 100; ++i) {
            auto snapshot = ResourceKeyChangedHandler(ctx.expFe, ctx.path, large);
            auto retainedSnapshot = snapshot;
            snapshot = nullptr;
            auto* snapshotAbi = reinterpret_cast<IUnknown*>(winrt::detach_abi(retainedSnapshot));
            releasedSnapshots &= snapshotAbi->Release() == 0;
        }
        check(releasedSnapshots, "repeated editor replacement releases delegates with 4096-key snapshots");
        std::wstring error = L"old failure";
        g_ctx.resolveFn = [](InstanceHandle, const std::wstring&, const std::wstring&, bool) { return true; };
        check(ApplyResourceKey(row, L"Background", L"ValidBrush", true, row.value, error) && error.empty() &&
              SessionAssignFind(L"Background") &&
              SessionAssignFind(L"Background")->detail == L"{ThemeResource ValidBrush}",
              "a corrected valid key is recorded only after the actual assignment succeeds");
        SessionAssignErase(L"Background");
        DirtyErase(L"Background");
        NoteSessionAssign(L"Background", DevToolsKind::Static, L"{StaticResource EarlierBrush}");
        g_ctx.resolveFn = [](InstanceHandle, const std::wstring&, const std::wstring& key, bool theme) {
            return key == L"EarlierBrush" && !theme;
        };
        check(!ApplyResourceKey(row, L"Background", L"MissingBrush", true, row.value, error) &&
              error.find(L"previous value was restored") != std::wstring::npos &&
              SessionAssignFind(L"Background")->detail == L"{StaticResource EarlierBrush}",
              "failed lookup restores the earlier resource key and kind without recording the bad key");
        SessionAssignErase(L"Background");
        PendingKindErase(L"Background");
        g_ctx.writeFn = nullptr;
        g_ctx.resolveFn = nullptr;
        resourceBox = nullptr;
    }
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
    {
        // A Reactor element's own C# (Reactor.element) tells the pane what XAML would: which rows follow a ThemeRef,
        // which the C# sets, and that a re-render can put the C# value back.
        const InstanceHandle wire = 0x5150;
        g_selWire = wire;
        g_reactorFactsWire = wire;
        g_reactorFacts = DevToolsReactorElementFacts_Parse(LR"json({"state":"ok","tagged":true,"element":"TextBlock",
            "file":"C:\\src\\App.cs","line":40,"owner":"ReleaseForm",
            "modifiers":[{"modifier":"Width","property":"Width","value":"320"}],
            "themeBindings":[{"property":"Foreground","resourceKey":"TextFillColorSecondaryBrush"}]})json");
        std::vector<DevToolsCardRow> rows(4);
        rows[0].name = L"Foreground";
        rows[1].name = L"Width";
        rows[2].name = L"Margin";
        rows[3].name = L"Background"; rows[3].authoredKind = L"literal"; rows[3].authored = L"Red";
        ApplyReactorFacts(rows);
        check(rows[0].authoredKind == L"themeResource" && rows[0].authoredKey == L"TextFillColorSecondaryBrush" &&
              rows[0].authored == L"{ThemeResource TextFillColorSecondaryBrush}",
              "a ThemeRef row reads as a {ThemeResource} with its key");
        check(CurrentKindOf(rows[0], L"Foreground") == DevToolsKind::Theme && std::wstring(OriginChip(rows[0])) == L"THEME",
              "so its editor starts at ThemeResource and its row shows THEME");
        check(rows[1].authoredKind.empty() && rows[2].authoredKind.empty() && rows[3].authoredKind == L"literal",
              "rows with no ThemeRef, and a row its XAML authored, are unchanged");
        check(ReactorOriginFor(rows[0], L"Foreground") == L"THEME  \u00B7  ThemeRef TextFillColorSecondaryBrush  \u00B7  App.cs:40",
              "DERIVED FROM names the ThemeRef and its call site");
        check(ReactorOriginFor(rows[1], L"Width") == L"C#  \u00B7  .Width(320)  \u00B7  App.cs:40",
              "DERIVED FROM shows the C# value of a property the C# sets");
        check(ReactorOriginFor(rows[2], L"Margin").empty() && ReactorOriginFor(rows[1], L"Width.Left").empty(),
              "but not for a property the C# does not set, or a part of one");
        {
            // Opening a row, or saving it, reads the rows again; the re-read keeps what the C# says.
            const auto rowsBefore = g_ctx.rowsFn;
            g_ctx.rowsFn = [](InstanceHandle, std::wstring&, std::wstring&, std::vector<DevToolsCardRow>& out, std::wstring&, std::wstring&) {
                out.assign(1, DevToolsCardRow{});
                out[0].name = L"Foreground";
            };
            std::wstring t, n, s, x;
            std::vector<DevToolsCardRow> reread;
            ReadPaneRows(wire, t, n, reread, s, x);
            check(reread.size() == 1 && reread[0].authoredKind == L"themeResource",
                  "a re-read of the element's rows (opening or saving one) keeps its ThemeRef");
            ReadPaneRows(wire + 7, t, n, reread, s, x);
            check(reread.size() == 1 && reread[0].authoredKind.empty(), "another element's rows get nothing from these facts");
            g_ctx.rowsFn = rowsBefore;
        }
        check(ReactorOwnedWarningFor(rows[1], L"Width") ==
                  L"Width is set in C# (App.cs:40); Reactor reapplies its C# value when <ReleaseForm> re-renders with a changed value.",
              "the editor of a C#-set property warns that a re-render can put the C# value back");
        g_selWire = wire + 1;
        check(ReactorOriginFor(rows[1], L"Width").empty() && ReactorOwnedWarningFor(rows[1], L"Width").empty(),
              "facts read for another element say nothing about this one");
        std::vector<DevToolsCardRow> xamlRows(1);
        xamlRows[0].name = L"Foreground";
        g_reactorFactsWire = 0;
        g_reactorFacts = {};
        ApplyReactorFacts(xamlRows);
        check(xamlRows[0].authoredKind.empty() && ReactorOriginFor(xamlRows[0], L"Foreground").empty(),
              "a XAML element, with no Reactor facts, keeps its rows as they are");
        g_selWire = 0;
    }
    g_statusTb = nullptr;
    std::printf("binding editor semantics: %u checks, %u failures\n", checks, failed);
    return failed ? 1 : 0;
}
