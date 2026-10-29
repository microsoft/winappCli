// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "../native/WinApp.DevTools.Native/DevToolsProjected.h"
static HRESULT FindEditorName(void*, HSTRING, IInspectable**);
static HRESULT PutEditorText(void*, HSTRING);
static HRESULT GetEditorSelection(void*, int*);
static HRESULT PutEditorVisibility(void*, int);
static HANDLE RefuseWorker(LPSECURITY_ATTRIBUTES, SIZE_T, LPTHREAD_START_ROUTINE, LPVOID, DWORD, LPDWORD);
#define DevToolsFindName FindEditorName
#define DevToolsPutText PutEditorText
#define DevToolsGetSelectedIndex GetEditorSelection
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
static EditorElement status, pathBox, modeBox, resourceBox, resourceError;
static unsigned submissions = 0;
static std::wstring submittedPath, submittedMode;
static int selectedMode = 0;
static HRESULT FindEditorName(void*, HSTRING name, IInspectable** out)
{
    const std::wstring key(WindowsGetStringRawBuffer(name, nullptr));
    *out = key == L"PPath" ? &pathBox : key == L"PMode" ? &modeBox
        : key == L"PResKey" ? &resourceBox : key == L"PResError" ? &resourceError : nullptr;
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
        const std::vector<std::wstring> many = {L"A1", L"A2", L"A3", L"A4", L"A5", L"A6", L"BBAccentBrush"};
        check(MatchingResourceKeys(many, L"bbAcc") == std::vector<std::wstring>{L"BBAccentBrush"},
              "typeahead filters all keys before limiting suggestions and ignores case");
        check(MatchingResourceKeys(many, L"").size() == 6 &&
              MatchingResourceKeys(many, L"Missing").empty(), "resource suggestions remain bounded and allow no matches");
        check(ResourceTypeFamily(L"Microsoft.UI.Xaml.Media.SolidColorBrush") == L"Brush",
              "brush subclasses are compatible resource suggestions");
    }
    {
        row = {};
        row.name = L"Background"; row.type = L"SolidColorBrush"; row.value = L"#FF808080";
        ctx = {std::make_shared<const DevToolsCardRow>(row), L"Background", 0, nullptr, &resourceBox};
        resourceBox.text = L"MissingBrush";
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
        check(resourceBox.text == L"MissingBrush" && SessionAssignFind(L"Background") == nullptr &&
              PendingKindFind(L"Background") != nullptr,
              "failed lookup preserves the correction text without claiming the key was assigned");
        g_ctx.writeFn = [](InstanceHandle, const std::wstring&, const std::wstring&, const std::wstring&,
                           bool, std::wstring*) { return DevToolsWriteOutcome::Failed; };
        CommitKind(ctx);
        check(resourceError.text.find(L"could not be restored") != std::wstring::npos,
              "failed restoration is explicit rather than claiming the app was unchanged");
        resourceBox.text.clear();
        CommitKind(ctx);
        check(resourceError.text == L"Type a resource key first." && resolves == 2,
              "empty key error is inline and makes no lookup");
        auto changed = ResourceKeyChangedHandler(ctx.expFe, ctx.path, ResourceKeys{});
        resourceBox.text = L"ValidBrush";
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
        bool releasedSuggestions = true, releasedSnapshots = true;
        ResourceKeys large;
        for (unsigned i = 0; i < 4096; ++i) {
            const auto key = L"Resource" + std::to_wstring(i);
            large.keys.push_back(key);
            large.seen.insert(key);
        }
        for (unsigned i = 0; i < 100; ++i) {
            auto suggestion = ResourceSuggestionHandler(nullptr, L"ValidBrush");
            auto retainedSuggestion = suggestion;
            suggestion = nullptr;
            auto* suggestionAbi = reinterpret_cast<IUnknown*>(winrt::detach_abi(retainedSuggestion));
            releasedSuggestions &= suggestionAbi->Release() == 0;
            auto snapshot = ResourceKeyChangedHandler(ctx.expFe, ctx.path, large);
            auto retainedSnapshot = snapshot;
            snapshot = nullptr;
            auto* snapshotAbi = reinterpret_cast<IUnknown*>(winrt::detach_abi(retainedSnapshot));
            releasedSnapshots &= snapshotAbi->Release() == 0;
        }
        check(releasedSuggestions, "repeated suggestion replacement releases each delegate's final reference");
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
    g_statusTb = nullptr;
    std::printf("binding editor semantics: %u checks, %u failures\n", checks, failed);
    return failed ? 1 : 0;
}
