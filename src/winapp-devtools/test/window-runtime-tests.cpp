// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "../native/WinApp.DevTools.Native/DevToolsProjected.h"
#include <commctrl.h>
static HRESULT FindTestName(void*, HSTRING, IInspectable**);
static HRESULT PutTestText(void*, HSTRING);
static HRESULT PutTestAutomationName(void*, void*, HSTRING);
static HRESULT PutTestAutomationId(void*, void*, HSTRING);
static HRESULT GetTestElement(void*, int, IInspectable**);
static HRESULT GetTestToggle(void*, bool*);
static HRESULT PutTestVisibility(void*, int);
static HRESULT PutTestChecked(void*, IInspectable*);
static HRESULT PutTestEnabled(void*, bool);
static bool focusTrackingAvailable = true;
static bool TestSetFocusTracking(bool) { return focusTrackingAvailable; }
static int testFontStyle = -1;
static HRESULT PutTestFontStyle(void*, int style) { testFontStyle=style; return S_OK; }
static HRESULT PutTestForeground(void*, void*) { return S_OK; }
static unsigned focusCalls = 0;
static HRESULT TestFocus(void*, int, bool* success) { ++focusCalls; *success=true; return S_OK; }
static HRESULT TestActualTheme(void*, int*);
static HRESULT TestCaptionTheme(void*, DevToolsW::TitleBarTheme);
static HRESULT TestRemoveTheme(void*, __int64);
static HRESULT TestGetAppWindow(void*, IInspectable**);
static HRESULT TestGetCaption(void*, IInspectable**);
static HRESULT TestAddTheme(void*, void*, __int64*);
static LRESULT CALLBACK TestNativeClose(HWND, UINT, WPARAM, LPARAM);
static double testViewport = 0.0;
static HRESULT TestViewport(void*, double* value) { *value = testViewport; return S_OK; }
#define DevToolsFindName FindTestName
#define DevToolsPutText PutTestText
#define DevToolsSetAutomationName PutTestAutomationName
#define DevToolsSetAutomationId PutTestAutomationId
#define DevToolsRepeaterTryGetElement GetTestElement
#define DevToolsToggleGetIsOn GetTestToggle
#define DevToolsPutVisibility PutTestVisibility
#define DevToolsToggleButtonPutIsChecked PutTestChecked
#define DevToolsPutIsEnabled PutTestEnabled
#define DevToolsOverlay_SetFocusTracking TestSetFocusTracking
#define DevToolsPutFontStyle PutTestFontStyle
#define DevToolsPutTextForeground PutTestForeground
#define DevToolsFocus TestFocus
#define DevToolsGetActualTheme TestActualTheme
#define DevToolsTitleBarPutPreferredTheme TestCaptionTheme
#define DevToolsRemoveActualThemeChanged TestRemoveTheme
#define DevToolsWinGetAppWindow TestGetAppWindow
#define DevToolsAppWindowGetTitleBar TestGetCaption
#define DevToolsAddActualThemeChanged TestAddTheme
#define DefSubclassProc TestNativeClose
#define DevToolsScrollGetViewportHeight TestViewport
#include "../native/WinApp.DevTools.Native/DevToolsWindow.cpp"

struct TestElement : IInspectable {
    ULONG refs = 1;
    std::wstring text;
    bool checked = false;
    bool enabled = true;
    int visibility = 1;
    std::wstring automationName, automationId;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** value) override {
        *value=static_cast<IInspectable*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    HRESULT STDMETHODCALLTYPE GetIids(ULONG* count, IID** ids) override { *count=0; *ids=nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRuntimeClassName(HSTRING* value) override { *value=nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetTrustLevel(TrustLevel* value) override { *value=BaseTrust; return S_OK; }
};
static HRESULT PutTestVisibility(void* element, int value)
{
    static_cast<TestElement*>(element)->visibility = value;
    return S_OK;
}
static HRESULT PutTestChecked(void* element, IInspectable* value)
{
    winrt::Windows::Foundation::IInspectable boxed{nullptr};
    winrt::copy_from_abi(boxed, value);
    static_cast<TestElement*>(element)->checked = boxed.as<winrt::Windows::Foundation::IPropertyValue>().GetBoolean();
    return S_OK;
}
static HRESULT PutTestEnabled(void* element, bool value)
{
    static_cast<TestElement*>(element)->enabled = value;
    return S_OK;
}
static TestElement testRow, testValue, testStatus, testInput, testHeader;
static bool exposeHeader = false;
static TestElement testAppWindow, testCaption;
static HRESULT appWindowResult = S_OK, captionResult = S_OK, addThemeResult = S_OK, putThemeResult = S_OK;
static unsigned themeAdds = 0;
static bool nullAppWindow = false;
static HRESULT TestGetAppWindow(void*, IInspectable** value)
{
    *value = SUCCEEDED(appWindowResult) && !nullAppWindow ? &testAppWindow : nullptr;
    if (*value) (*value)->AddRef();
    return appWindowResult;
}
static HRESULT TestGetCaption(void*, IInspectable** value)
{
    *value = SUCCEEDED(captionResult) ? &testCaption : nullptr;
    if (*value) (*value)->AddRef();
    return captionResult;
}
static HRESULT TestAddTheme(void*, void*, __int64* token)
{
    ++themeAdds;
    if (SUCCEEDED(addThemeResult)) *token = 42;
    return addThemeResult;
}
static bool realized = true;
static HRESULT FindTestName(void*, HSTRING name, IInspectable** found)
{
    UINT32 length=0;
    const auto* chars=WindowsGetStringRawBuffer(name,&length);
    *found=nullptr;
    if(std::wstring(chars,length)==L"PValue") { *found=&testValue; testValue.AddRef(); }
    if(exposeHeader && std::wstring(chars,length)==L"PHeader") { *found=&testHeader; testHeader.AddRef(); }
    return S_OK;
}
static HRESULT PutTestText(void* element, HSTRING value)
{
    UINT32 length=0;
    const auto* chars=WindowsGetStringRawBuffer(value,&length);
    static_cast<TestElement*>(element)->text.assign(chars ? chars : L"",length);
    return S_OK;
}
static HRESULT PutTestAutomationName(void*, void* element, HSTRING value)
{
    UINT32 length=0;
    const auto* chars=WindowsGetStringRawBuffer(value,&length);
    static_cast<TestElement*>(element)->automationName.assign(chars ? chars : L"",length);
    return S_OK;
}
static HRESULT PutTestAutomationId(void*, void* element, HSTRING value)
{
    UINT32 length=0;
    const auto* chars=WindowsGetStringRawBuffer(value,&length);
    static_cast<TestElement*>(element)->automationId.assign(chars ? chars : L"",length);
    return S_OK;
}
static HRESULT GetTestElement(void*, int, IInspectable** element)
{
    *element=realized ? &testRow : nullptr;
    if(*element) (*element)->AddRef();
    return S_OK;
}
static HRESULT GetTestToggle(void* toggle, bool* value) { *value=*static_cast<bool*>(toggle); return S_OK; }
static int actualTheme = 0;
static HRESULT themeReadResult = S_OK;
static int captionTheme = -1;
static unsigned captionWrites = 0, themeRemovals = 0;
static HRESULT removeThemeResult = S_OK;
static HRESULT TestActualTheme(void*, int* value) { *value=actualTheme; return themeReadResult; }
static HRESULT TestCaptionTheme(void*, DevToolsW::TitleBarTheme value)
{
    ++captionWrites;
    if (SUCCEEDED(putThemeResult)) captionTheme=static_cast<int>(value);
    return putThemeResult;
}
static HRESULT TestRemoveTheme(void*, __int64 token) { if (token == 42) ++themeRemovals; return removeThemeResult; }
static bool captionReleasedBeforeClose = false;
static unsigned nativeCloses = 0;
static LRESULT CALLBACK TestNativeClose(HWND, UINT message, WPARAM, LPARAM)
{
    if (message == WM_CLOSE) {
        captionReleasedBeforeClose = !g_captionThemeRoot && !g_captionTheme && !g_captionThemeSubscribed;
        ++nativeCloses;
    }
    return 123;
}

int main()
{
    winrt::init_apartment();
    unsigned checks=0, failed=0, writes=0;
    auto check=[&](bool ok,const char* name){++checks;if(!ok){++failed;std::printf("FAIL %s\n",name);}};
    {
        TestElement pickButton, pickIndicator, focusButton, focusIndicator;
        g_pickBtnCtrl = &pickButton; g_pickOnUi = &pickIndicator;
        g_focusBtnCtrl = &focusButton; g_focusOnUi = &focusIndicator;
        for (bool on : {true, false}) {
            SetModeToggleState(g_pickBtnCtrl, g_pickOnUi, on);
            check(pickButton.checked == on && pickIndicator.visibility == (on ? 0 : 1),
                "pick checked state and active indicator follow the actual mode");
        }
        FocusSink focus; focus.Init();
        focusButton.checked = true;
        focusTrackingAvailable = false;
        focus.Invoke(nullptr, nullptr);
        check(!g_focusActive && !focusButton.checked && focusIndicator.visibility == 1,
            "failed focus activation restores the unchecked state");
        focusTrackingAvailable = true;
        focusButton.checked = true;
        focus.Invoke(nullptr, nullptr);
        check(g_focusActive && focusButton.checked && focusIndicator.visibility == 0,
            "focus activation checks the button and shows its indicator");
        focusButton.checked = false;
        focus.Invoke(nullptr, nullptr);
        check(!g_focusActive && !focusButton.checked && focusIndicator.visibility == 1,
            "focus deactivation unchecks the button and hides its indicator");
        focus.Invoke(nullptr, nullptr);
        ResetAppVisibleModes(nullptr);
        check(!g_focusActive && !focusButton.checked && focusIndicator.visibility == 1,
            "closing the inspector clears focus mode and its accessible state");
        g_pickBtnCtrl = g_pickOnUi = g_focusBtnCtrl = g_focusOnUi = nullptr;
    }
    {
        TestElement first, second;
        g_selBrushTried = true;
        SelectRowVisual(&first);
        check(g_selCtrl == &first && first.refs == 2,
            "tree selection is tracked even when selection brushes are unavailable");
        SelectRowVisual(&second);
        check(g_selCtrl == &second && first.refs == 1 && second.refs == 2,
            "changing tree selection releases the old row and retains the new row");
        g_selectedWire = 77;
        SelectNodeByWire(0);
        check(!g_selCtrl && !g_selectedWire && second.refs == 1,
            "clearing the selected element clears tree selection and releases the retained row");
        g_selBrushTried = false;
    }
    {
        TestElement rows[5], expander, glyph;
        for (auto& row : rows) {
            g_treeRows.push_back(&row);
            g_treeControls.push_back(&row);
        }
        g_treeDepth = {0, 1, 1, 2, 2};
        g_treeAppAuthored = {1, 1, 1, 1, 1};
        g_treeShortType = {L"Grid", L"TitleBar", L"Grid", L"Button", L"TextBlock"};
        g_treeLabel = {L"grid", L"titlebar", L"grid", L"button", L"textblock"};
        g_treeCollapsed = {1, 0, 0, 0, 0};
        g_treeVisible.resize(5);
        g_treeA11y.resize(5);
        g_treeA11y[0].branch = g_treeA11y[2].branch = true;
        g_treeAutomationParents = {DevToolsTreeLayout::kNoRow, 0, 0, 2, 2};
        g_treeChevronToggle = {&expander, nullptr, nullptr, nullptr, nullptr};
        g_treeGlyph = {&glyph, nullptr, nullptr, nullptr, nullptr};
        check(!HandleTreeRowKey(0, VK_RETURN) && !HandleTreeRowKey(0, VK_SPACE),
            "tree rows leave Enter and Space to Button selection");
        RecomputeTreeVisibility();
        check(HandleTreeRowKey(0, VK_RIGHT) && !g_treeCollapsed[0] &&
              g_treeVisible == std::vector<char>({1, 1, 1, 1, 1}),
            "Right arrow expands a collapsed row and reveals its descendants");
        const unsigned beforeFocus = focusCalls;
        check(HandleTreeRowKey(0, VK_RIGHT) && focusCalls == beforeFocus + 1 && !g_selCtrl,
            "Right arrow on an expanded row focuses its first child without selecting it");
        check(HandleTreeRowKey(1, VK_LEFT) && focusCalls == beforeFocus + 2 && !g_selCtrl,
            "Left arrow on a leaf focuses its parent without selecting it");
        check(HandleTreeRowKey(0, VK_LEFT) && g_treeCollapsed[0] &&
              g_treeVisible == std::vector<char>({1, 0, 0, 0, 0}),
            "Left arrow collapses an expanded row and hides its descendants");
        ApplyTreeFilter(L"Grid");
        const auto filteredVisibility = g_treeVisible;
        const auto filteredCollapse = g_treeCollapsed;
        check(filteredVisibility == std::vector<char>({1, 0, 1, 0, 0}) && !expander.enabled,
            "filtering shows all matches across collapsed branches and disables expand arrows");
        check(!SetTreeRowExpanded(0, true) && !SetTreeRowExpanded(0, false) &&
              g_treeVisible == filteredVisibility && g_treeCollapsed == filteredCollapse &&
              g_treeFilterQuery == L"grid",
            "provider expansion and collapse preserve the active filter and saved collapse state");
        ChevronSink chevron; chevron.Init(0);
        chevron.Invoke(nullptr, nullptr);
        check(g_treeVisible == filteredVisibility && g_treeCollapsed == filteredCollapse,
            "a chevron callback cannot expose nonmatching rows while filtering");
        check(HandleTreeRowKey(0, VK_RIGHT) && HandleTreeRowKey(2, VK_LEFT) &&
              g_treeVisible == filteredVisibility && g_treeCollapsed == filteredCollapse && !g_selCtrl,
            "arrow keys navigate filtered matches without changing expansion or selection");
        ApplyTreeFilter(L"");
        check(expander.enabled && g_treeCollapsed[0] && g_treeVisible == std::vector<char>({1, 0, 0, 0, 0}),
            "clearing the filter restores the saved collapse state and enables expand arrows");
        check(HandleTreeRowKey(0, VK_RIGHT) && !g_treeCollapsed[0] && g_treeVisible[1],
            "keyboard expansion works again after clearing the filter");
        g_treeRows.clear(); g_treeControls.clear(); g_treeDepth.clear(); g_treeAppAuthored.clear();
        g_treeShortType.clear(); g_treeLabel.clear(); g_treeCollapsed.clear(); g_treeVisible.clear();
        g_treeA11y.clear(); g_treeAutomationParents.clear(); g_treeChevronToggle.clear();
        g_treeGlyph.clear(); g_treeViewDepth.clear();
        focusCalls = 0;
    }
    {
        // Only open comments are pushed; with none open but some resolved, the pane must not claim there are none.
        size_t open = 0;
        DevToolsWindow_SetResolvedCommentCount(2);
        const std::wstring allResolved = BuildCommentsFragment({}, open);
        check(allResolved.find(L"No open comments (2 resolved)") != std::wstring::npos &&
              allResolved.find(L"comments list --all") != std::wstring::npos,
              "an all-resolved Comments pane counts the resolved comments and says how to see them");
        DevToolsWindow_SetResolvedCommentCount(0);
        check(BuildCommentsFragment({}, open).find(L"No comments for this app yet") != std::wstring::npos,
              "a project with no comments at all still says there are none");
    }
    std::map<InstanceHandle,std::wstring> values{{77,L"True"},{88,L"True"}};
    DevToolsCardRow row;
    row.name=L"IsEnabled";row.type=L"Boolean";row.valueType=L"Windows.Foundation.Boolean";
    row.value=L"True";row.source=L"Default";
    g_ctx.writeFn=[&](InstanceHandle wire,const std::wstring&,const std::wstring&,const std::wstring& value,bool,std::wstring*) {
        ++writes;values[wire]=value;return DevToolsWriteOutcome::Ok;
    };
    g_ctx.rowsFn=[&](InstanceHandle wire,std::wstring&,std::wstring&,std::vector<DevToolsCardRow>& rows,std::wstring&,std::wstring&) {
        auto fresh=row;fresh.value=values[wire];fresh.source=L"Local";rows={fresh};return true;
    };
    g_propRepeater=reinterpret_cast<void*>(1);
    g_paneItems={{DevToolsPaneItemKind::Row,0,0,L""}};
    g_propRows={row};g_selWire=g_dirtyNode=77;
    g_statusTb=&testStatus;
    testValue.text=L"True";
    RowExpandSink expansion;
    expansion.Init(std::make_shared<const DevToolsCardRow>(row),row.name,0,&testRow,false);
    bool on=false;
    BoolToggleSink toggle;
    toggle.Init(77,row.name,row.type,L"True",&on);
    g_apStatics=&testRow;exposeHeader=true;
    toggle.Invoke(nullptr,nullptr);
    g_apStatics=nullptr;exposeHeader=false;
    check(values[77]==L"False","boolean commit updates actual target");
    check(testValue.text==L"False","successful commit refreshes shown value in place");
    check(g_propRows[0].value==L"False","recycled row retains committed value");
    check(testHeader.automationName==L"IsEnabled, False" && testHeader.automationId==L"IsEnabled value",
        "committing a value refreshes the header's accessible name without changing its identity");
    check(testValue.automationName==L"IsEnabled, False" && testValue.automationId==L"IsEnabled shown",
        "committing a value refreshes the child text's accessible name without changing its identity");
    check(expansion.ExpansionRow().value==L"False","collapse/reopen resolves the refreshed row instead of captured snapshot");
    check(focusCalls==0,"commit does not move focus");
    g_apStatics=&testRow;exposeHeader=true;
    g_live.wire=g_selWire;g_live.gen=g_route1Gen;g_live.changed={row.name};
    g_valuesFn=[](InstanceHandle, const std::vector<std::wstring>&, std::vector<DevToolsCardRow>* fresh) {
        auto updated=g_propRows[0];updated.value=L"True";*fresh={updated};return true;
    };
    LiveRefreshProc(nullptr,0,0,0);
    check(testValue.text==L"True" && testHeader.automationName==L"IsEnabled, True" &&
        testValue.automationName==L"IsEnabled, True","live property changes refresh header and child accessible names");
    check(testHeader.automationId==L"IsEnabled value" && testValue.automationId==L"IsEnabled shown",
        "live property changes preserve AutomationIds");
    g_live={};g_valuesFn=nullptr;g_apStatics=nullptr;exposeHeader=false;
    realized=false;on=true;
    toggle.Invoke(nullptr,nullptr);
    check(g_propRows[0].value==L"True","offscreen commit refreshes model without a realized element");
    realized=true;
    PopulateRowLine(&testRow,g_propRows[0],row.name,-1);
    check(testValue.text==L"True","newly realized row reads refreshed snapshot");

    g_selWire=g_dirtyNode=88;g_propRows={row};testValue.text=L"True";
    on=false;
    toggle.Invoke(nullptr,nullptr);
    check(values[77]==L"False" && values[88]==L"True","late sink writes only its captured node");
    check(testValue.text==L"True" && g_propRows[0].value==L"True","late sink cannot overwrite new selection display");

    // The pane is a snapshot: opening a row reads the live value, but never over an unconfirmed write.
    values[88]=L"False";
    g_pendingWrites={{L"Other",L"String",L"x"}};
    RowExpandSink guarded;
    guarded.Init(std::make_shared<const DevToolsCardRow>(row),row.name,0,&testRow,true);
    guarded.Invoke(nullptr,nullptr);
    check(g_propRows[0].value==L"True" && !CanRereadSelection(),"a pending write keeps the snapshot");
    g_pendingWrites.clear();g_propExpanded.clear();
    check(CanRereadSelection(),"a clean pane can be re-read");
    RowExpandSink opener;
    opener.Init(std::make_shared<const DevToolsCardRow>(row),row.name,0,&testRow,true);
    opener.Invoke(nullptr,nullptr);
    check(g_propRows[0].value==L"False" && testValue.text==L"False","opening a row re-reads its live value");
    check(CanRereadSelection(),"an expanded row whose editor is not being typed in does not block a re-read");
    check(focusCalls==0,"expanding a property leaves focus on the header");
    RowExpandSink closer;
    closer.Init(std::make_shared<const DevToolsCardRow>(row),row.name,0,&testRow,false);
    closer.Invoke(nullptr,nullptr);
    check(!PropIsExpanded(row.name),"the collapsed event clears saved expansion state");
    closer.Invoke(nullptr,nullptr);
    check(!PropIsExpanded(row.name),"repeated collapsed events cannot reopen a property");
    auto shown = row;
    shown.name=L"Height";shown.value=L"NaN";shown.valueState=L"unset";
    check(RowShownValue(shown)==L"Auto","the accessible summary uses Auto rather than an unset sentinel");
    g_apStatics=&testRow;
    NamePropertyHeader(&testHeader,shown,L"Height");
    PopulateRowValue(&testValue,shown,L"Height");
    check(testHeader.automationName==L"Height, Auto" && testValue.automationName==L"Height, Auto" &&
        testValue.text==L"Auto","unset height names include the displayed Auto value");
    check(testHeader.automationId==L"Height value" && testValue.automationId==L"Height shown",
        "initial property AutomationIds remain stable");
    shown.valueState=L"null";
    check(RowShownValue(shown)==L"(null)","the accessible summary names null values");
    shown.valueState.clear();shown.value=L"200";
    check(RowShownValue(shown)==L"200","the accessible summary includes the current literal");
    PopulateRowValue(&testValue,shown,L"Size.Height");
    NamePropertyHeader(&testHeader,shown,L"Size.Height");
    check(testHeader.automationName==L"Size.Height, 200" && testValue.automationName==L"Size.Height, 200",
        "recycled nested rows name their current path and value");
    shown.type=L"String";shown.value.clear();
    NamePropertyHeader(&testHeader,shown,L"Child.Text");
    PopulateRowValue(&testValue,shown,L"Child.Text");
    check(testHeader.automationName==L"Child.Text, (empty)" && testHeader.automationId==L"Child.Text value",
        "empty and nested properties keep an explicit accessible value and full path");
    check(testValue.text.empty() && testValue.automationName==L"Child.Text, (empty)" &&
        testValue.automationId==L"Child.Text shown","an empty value clears the previous child accessible value");
    g_apStatics=nullptr;
    g_propExpanded.clear();values[88]=L"True";g_propRows={row};testValue.text=L"True";

    g_ctx.readInputFn=[](IInspectable*,std::wstring& text){text=L"bad read";return false;};
    LostFocusSink input;
    input.Init(88,L"Text",L"String",L"before",&testInput);
    const auto before=writes;
    input.Invoke(nullptr,nullptr);
    check(writes==before,"failed editor read never mutates target");
    check(testStatus.text.find(L"read")!=std::wstring::npos,"failed editor read is visible in status");
    g_ctx.readInputFn=[](IInspectable*,std::wstring& text){text.clear();return true;};
    input.Invoke(nullptr,nullptr);
    check(writes==before+1 && values[88].empty(),"successful empty editor read commits empty string");
    {
        // The editor shows the first line only (single-line box) or '\r' breaks (multi-line box): closing it
        // unchanged writes nothing; an edit keeps the value's '\n' breaks.
        static std::wstring shown;
        g_ctx.readInputFn=[](IInspectable*,std::wstring& text){text=shown;return true;};
        values[88]=L"Make room for\nwhat matters.";
        for (const auto* s : {L"Make room for",L"Make room for\rwhat matters."}) {
            shown=s;
            LostFocusSink multi;multi.Init(88,L"Text",L"String",values[88],&testInput);
            const auto w=writes;
            multi.Invoke(nullptr,nullptr);
            check(writes==w && values[88]==L"Make room for\nwhat matters.","closing an unedited multi-line editor writes nothing");
        }
        LostFocusSink multi;multi.Init(88,L"Text",L"String",values[88],&testInput);
        shown=L"Make room for\ryou.";
        multi.Invoke(nullptr,nullptr);
        check(values[88]==L"Make room for\nyou.","a multi-line edit round-trips with the value's line breaks");
        values[88].clear();
        g_ctx.readInputFn=[](IInspectable*,std::wstring& text){text.clear();return true;};
    }
    row.name=L"Text";row.type=L"String";row.value=L"";row.authoredKind=L"literal";row.authored=L"original";
    g_propRows={row};
    check(RowEditSeed(row).empty(),"empty live String is not replaced with authored text");
    for (const auto* text : {L"{SolidColorBrush}", L"(bound)"}) {
        row.value = text;
        check(RowEditSeed(row) == text, "actual placeholder-like String is not discarded");
    }
    row.valueState = L"unresolved";
    check(RowEditSeed(row).empty(), "unavailable values do not seed an editor");
    row.valueState.clear(); row.value.clear();
    DevToolsCardRow font;
    font.name = L"FontFamily"; font.value = L"Georgia"; font.valueType = L"Microsoft.UI.Xaml.Media.FontFamily";
    check(!RowHasExpansion(font, font.name, -1), "read-only font with no detail has no expansion");
    check(!RowHasExpansion(font, font.name, 0), "stale edit index cannot create an empty read-only expansion");
    font.chain.resize(2);
    check(RowHasExpansion(font, font.name, -1), "read-only font retains real precedence details");
    font.chain.clear(); font.source = L"Binding";
    check(RowHasExpansion(font, font.name, -1), "unavailable binding expression remains explicit evidence");
    PopulateRowLine(&testRow,row,row.name,-1);
    check(testValue.text.empty(),"empty String renders empty after row realization");
    for (const auto& text : {L"",L"0",L"<tag>&\"quoted\""}) {
        values[88]=text;
        check(RefreshCommittedValue(L"Text") && testValue.text==text && g_propRows[0].value==text,
            "in-place String refresh preserves exact live value");
    }
    row.name=L"AutomationProperties.Name";
    g_propRows={row};
    check(RefreshCommittedValue(row.name) && testValue.text==values[88],
        "attached property names containing dots refresh as a single row");
    DevToolsCardRow parent;parent.name=L"Foreground";row.name=L"Color";parent.children={row};
    std::vector<DevToolsCardRow> nested{parent};
    check(FindPropertyRow(nested,L"Foreground.Color")==&nested[0].children[0],
        "nested property path resolves the child model");
    row.name=L"Content";row.type=L"";row.valueType=L"Windows.Foundation.Object";row.valueState=L"null";
    values[88]=L"0";g_propRows={row};
    check(RefreshCommittedValue(row.name) && testValue.text==L"(null)" && testFontStyle==2,
        "commit refresh preserves null display and absence formatting");
    row.name=L"Text";row.type=L"String";row.valueType=L"Windows.Foundation.String";row.valueState.clear();
    values[88]=L"";g_propRows={row};
    check(RefreshCommittedValue(row.name) && testValue.text.empty() && testFontStyle==0,
        "real empty String refresh removes prior absence styling");
    {
        int layoutReads=0;
        g_ctx.layoutFn=[&](InstanceHandle,DevToolsWindowLayout&) { ++layoutReads; return true; };
        g_paneHaveLayout=true;
        RefreshCommittedValue(row.name);
        check(layoutReads==0,"editing a property Size & spacing does not show leaves the section alone");
        row.name=L"Padding";g_propRows={row};
        g_apStatics=&testRow;exposeHeader=true;
        RefreshCommittedValue(row.name);
        check(layoutReads==1,"editing Padding redraws Size & spacing even though the live refresh will see no change");
        check(testHeader.automationName==L"Padding, (empty)" && testHeader.automationId==L"Padding value",
            "editing a layout property refreshes its accessible header as well as Size & spacing");
        g_apStatics=nullptr;exposeHeader=false;
        g_ctx.layoutFn=nullptr;g_paneHaveLayout=false;
        row.name=L"Text";g_propRows={row};
    }
    g_ctx.rowsFn=[](InstanceHandle,std::wstring&,std::wstring&,std::vector<DevToolsCardRow>&,std::wstring&,std::wstring&) {
        return false;
    };
    check(ApplyCommitOutcome(DevToolsWriteOutcome::Ok,88,L"Text",L"String",L"before",L"after") &&
        testStatus.text.find(L"couldn't refresh")!=std::wstring::npos,
        "successful write with failed readback reports display refresh failure");
    InstanceHandle written=0;
    g_selWire=77;testStatus.text=L"new selection";
    g_ctx.writeFn=[&](InstanceHandle wire,const std::wstring&,const std::wstring&,const std::wstring&,bool,std::wstring*){
        written=wire;g_selWire=g_dirtyNode=88;g_propRows={row};
        NoteSessionAssign(L"Text",DevToolsKind::Theme,L"NewSelectionResource");
        g_linkedRows={L"Text"};
        return DevToolsWriteOutcome::Ok;
    };
    check(ApplyLiteralValue(L"Text",L"String",L"before",L"after",true) && written==77,
        "reentrant literal commit uses captured target identity");
    check(SessionAssignFind(L"Text") && !LinkLost(L"Text"),
        "reentrant commit preserves new selection resource witness");
    check(testStatus.text==L"new selection" && g_propRows[0].value==row.value,
        "reentrant literal commit never refreshes the new selection");
    {
        void* repeater=g_propRepeater; g_propRepeater=nullptr; g_selWire=88;
        g_ctx.writeFn=[&](InstanceHandle,const std::wstring&,const std::wstring&,const std::wstring&,bool,std::wstring* reason){
            *reason=L"property-write-failed|Couldn't set Content: TitleBar rejected 'x' (String). The previous value was kept (0x80004005)";
            return DevToolsWriteOutcome::Failed;
        };
        check(!ApplyLiteralValue(L"Content",L"String",L"",L"x",false) &&
            testStatus.text==L"Couldn't set Content: TitleBar rejected 'x' (String). The previous value was kept (0x80004005)" &&
            g_writeErrorWire==88 && g_writeErrorPath==L"Content",
            "a refused write shows the control's reason on its row and in the status line");
        g_ctx.writeFn=[&](InstanceHandle,const std::wstring&,const std::wstring&,const std::wstring&,bool,std::wstring*){
            return DevToolsWriteOutcome::Ok;
        };
        ApplyLiteralValue(L"Content",L"String",L"x",L"",false);
        check(g_writeErrorPath.empty(), "the row's error clears on its next successful write");
        g_propRepeater=repeater;
    }
    g_statusTb=nullptr;g_propRepeater=nullptr;
    const ULONG captionRefsBefore=testRow.refs, rootRefsBefore=testValue.refs;
    g_captionTheme=&testRow;testRow.AddRef();
    g_captionThemeRoot=&testValue;testValue.AddRef();
    for (int theme : {0,1,2}) {
        actualTheme=theme;
        check(SyncInspectorCaptionTheme()==S_OK && captionTheme==theme+1,
            "native caption follows default light and dark without fabricated colors");
    }
    themeReadResult=E_FAIL;
    const unsigned writesBefore=captionWrites;
    check(SyncInspectorCaptionTheme()==E_FAIL && captionWrites==writesBefore,
        "unavailable actual theme never substitutes an invented caption theme");
    themeReadResult=S_OK;
    InspectorCaptionThemeSink themeSink;themeSink.Init();
    actualTheme=1;
    themeSink.Invoke(nullptr,nullptr);
    check(captionWrites==writesBefore+1 && captionTheme==2,
        "current window theme event updates the native caption");
    ++g_route1Gen;
    themeSink.Invoke(nullptr,nullptr);
    check(captionWrites==writesBefore+1,"retired window theme callback cannot modify current caption");
    g_captionThemeToken=42;g_captionThemeSubscribed=true;
    ReleaseInspectorCaptionTheme();
    check(themeRemovals==1 && !g_captionThemeRoot && !g_captionTheme && !g_captionThemeSubscribed &&
        testRow.refs==captionRefsBefore && testValue.refs==rootRefsBefore,
        "window teardown unsubscribes theme handler and releases both owned interfaces");
    ReleaseInspectorCaptionTheme();
    check(themeRemovals==1 && SyncInspectorCaptionTheme()==S_FALSE,
        "caption teardown is idempotent and inactive sync does not touch a closed window");
    g_captionTheme=&testRow;testRow.AddRef();
    g_captionThemeRoot=&testValue;testValue.AddRef();
    g_captionThemeToken=42;g_captionThemeSubscribed=true;
    g_route1WindowInsp=&testRow;
    const auto selectionBeforeClose=g_selWire;
    check(Route1MinSizeProc(nullptr,WM_CLOSE,0,0,1,g_route1Gen-1)==123 &&
        g_captionThemeSubscribed && themeRemovals==1 && nativeCloses==1,
        "stale close cannot tear down the newer inspector caption");
    check(Route1MinSizeProc(nullptr,WM_CLOSE,0,0,1,g_route1Gen)==123,
        "native close still reaches the original window procedure");
    check(captionReleasedBeforeClose && themeRemovals==2,
        "caption subscription and interfaces are released before native window destruction");
    check(nativeCloses==2,"theme cleanup preserves native close rather than hiding the window");
    check(g_selWire==selectionBeforeClose,
        "caption teardown does not reset inspector selection or edit state");
    check(Route1MinSizeProc(nullptr,WM_CLOSE,0,0,1,g_route1Gen)==123 &&
        themeRemovals==2 && nativeCloses==3,
        "repeated close forwards normally without repeating caption teardown");
    g_route1WindowInsp=nullptr;
    ReleaseInspectorCaptionTheme();
    g_captionThemeRoot=&testValue;testValue.AddRef();
    g_captionThemeSubscribed=true;g_captionThemeToken=42;
    removeThemeResult=E_FAIL;
    check(ReleaseInspectorCaptionTheme()==E_FAIL && !g_captionThemeRoot && !g_captionThemeSubscribed,
        "subscription removal failure stays explicit while releasing owned references");
    removeThemeResult=S_OK;
    check(testRow.refs==captionRefsBefore && testValue.refs==rootRefsBefore && testInput.refs==1,
        "all teardown paths release their caption and root references");
    const auto addsBefore=themeAdds;
    actualTheme=2;
    check(ConfigureInspectorCaptionTheme(&testRow,&testValue)==S_OK &&
        g_captionThemeSubscribed && themeAdds==addsBefore+1 && captionTheme==3,
        "production caption setup synchronizes dark theme and registers updates");
    check(testAppWindow.refs==1 && testCaption.refs==2 && testValue.refs==rootRefsBefore+1,
        "caption setup retains only the theme interface and root");
    ReleaseInspectorCaptionTheme();
    check(testCaption.refs==1 && testValue.refs==rootRefsBefore,
        "caption setup ownership is fully released");
    appWindowResult=E_NOINTERFACE;
    check(ConfigureInspectorCaptionTheme(&testRow,&testValue)==S_FALSE &&
        !g_captionTheme && !g_captionThemeRoot && themeAdds==addsBefore+1,
        "unsupported caption API keeps system caption without subscribing or retaining state");
    appWindowResult=S_OK;nullAppWindow=true;
    check(ConfigureInspectorCaptionTheme(&testRow,&testValue)==S_FALSE && !g_captionThemeRoot,
        "missing AppWindow cannot create a success-shaped theme setup");
    nullAppWindow=false;captionResult=E_FAIL;
    check(ConfigureInspectorCaptionTheme(&testRow,&testValue)==S_FALSE &&
        testAppWindow.refs==1 && !g_captionTheme,
        "caption acquisition failure releases intermediate AppWindow");
    captionResult=S_OK;
    for (auto* failure : {&themeReadResult, &putThemeResult, &addThemeResult}) {
        *failure=E_FAIL;
        check(ConfigureInspectorCaptionTheme(&testRow,&testValue)==S_FALSE &&
            !g_captionTheme && !g_captionThemeRoot && !g_captionThemeSubscribed &&
            testCaption.refs==1 && testValue.refs==rootRefsBefore,
            "failed read write or subscription cannot retain partially configured caption state");
        *failure=S_OK;
    }
    check(ConfigureInspectorCaptionTheme(&testRow,&testValue)==S_OK,
        "caption setup recovers on a subsequent window after failure");
    g_route1WindowInsp=&testRow;removeThemeResult=E_FAIL;
    const auto closesBeforeFailure=nativeCloses;
    check(Route1MinSizeProc(nullptr,WM_CLOSE,0,0,1,g_route1Gen)==123 &&
        nativeCloses==closesBeforeFailure+1 && captionReleasedBeforeClose,
        "failed theme unsubscription cannot suppress actual native close");
    g_route1WindowInsp=nullptr;removeThemeResult=S_OK;
    {
        // Closing the window with its title bar clears the highlight and tag it drew on the app.
        static int clears=0;
        const auto savedHighlight=g_ctx.highlightFn;
        g_ctx.highlightFn=[](InstanceHandle wire, bool select) { if (!wire && select) ++clears; };
        g_route1WindowInsp=&testRow;clears=0;
        Route1MinSizeProc(nullptr,WM_CLOSE,0,0,1,g_route1Gen);
        check(clears==1 && !DevToolsOverlay_IsCommentFlyoutOpen(),
            "closing the inspector clears the selection it drew on the app");
        Route1MinSizeProc(nullptr,WM_CLOSE,0,0,1,g_route1Gen-1);
        check(clears==1, "a stale close does not touch the current window's selection");
        g_route1WindowInsp=nullptr;g_ctx.highlightFn=savedHighlight;
    }
    {
        DevToolsWindowLayout stretched{};
        stretched.haveDesired=stretched.haveRender=true;
        stretched.desiredW=80;stretched.renderW=400;stretched.renderH=32;
        DevToolsCardRow alignment;alignment.name=L"HorizontalAlignment";alignment.value=L"Stretch";
        check(BuildLayoutSection(stretched,{alignment}).find(L"MORE width")==std::wstring::npos &&
            BuildLayoutSection(stretched,{}).find(L"MORE width")==std::wstring::npos,
            "default Stretch alignment explains extra width without a warning");
        alignment.value=L"Left";
        check(BuildLayoutSection(stretched,{alignment}).find(L"even though HorizontalAlignment is Left")!=std::wstring::npos,
            "extra width despite a non-default alignment is called out");
        {
            // <TextBox Width="320" Margin="18,12"/>: DesiredSize includes the margin, RenderSize does not.
            DevToolsWindowLayout box{};
            box.haveDesired=box.haveRender=true;
            box.desiredW=356;box.desiredH=114;box.renderW=320;box.renderH=90;
            DevToolsCardRow margin;margin.name=L"Margin";margin.value=L"18,12,18,12";
            DevToolsCardRow width;width.name=L"Width";width.value=L"320";
            const auto section=BuildLayoutSection(box,{margin,width});
            check(section.find(L"LESS width")==std::wstring::npos && section.find(L"clipped")==std::wstring::npos,
                "a margin is not reported as a constraint");
            check(section.find(L"with margin")!=std::wstring::npos,
                "the asked-for size says it includes the margin");
            // <Border Width="400"/> in a 275 column: DesiredSize is clipped to the slot, RenderSize keeps Width.
            DevToolsWindowLayout wide{};
            wide.haveDesired=wide.haveRender=true;
            wide.desiredW=275;wide.desiredH=20;wide.renderW=400;wide.renderH=20;
            width.value=L"400";
            for (const wchar_t* align : {L"Stretch", L"Left"}) {
                alignment.value=align;
                const auto clipped=BuildLayoutSection(wide,{width,alignment});
                check(clipped.find(L"Its Width (400) is larger than the 275 its parent offered, so it is clipped")!=std::wstring::npos &&
                    clipped.find(L"MORE width")==std::wstring::npos,
                    "a fixed Width larger than its slot is described as clipped");
            }
            wide.desiredW=275+36;
            check(BuildLayoutSection(wide,{width,margin}).find(L"larger than the 275 its parent offered inside its Margin")!=std::wstring::npos,
                "the clipped explanation subtracts the margin from the offered space");
            DevToolsWindowLayout same=box;
            {
                // <Border Width="10" Margin="-20,0"/> fits; DesiredSize is floored at zero.
                DevToolsWindowLayout neg{};
                neg.haveDesired=neg.haveRender=true;
                neg.desiredW=0;neg.desiredH=10;neg.renderW=10;neg.renderH=10;
                DevToolsCardRow negMargin;negMargin.name=L"Margin";negMargin.value=L"-20,0,-20,0";
                DevToolsCardRow negWidth;negWidth.name=L"Width";negWidth.value=L"10";
                const auto negSection=BuildLayoutSection(neg,{negMargin,negWidth});
                check(negSection.find(L"LESS width")==std::wstring::npos && negSection.find(L"clipped")==std::wstring::npos,
                    "a negative margin is not reported as clipping");
                // <Border Width="400" Margin="10"/> in a 15 slot: the usable width is zero, not -5.
                DevToolsWindowLayout tiny{};
                tiny.haveDesired=tiny.haveRender=true;
                tiny.desiredW=15;tiny.desiredH=20;tiny.renderW=400;tiny.renderH=20;
                DevToolsCardRow tenMargin;tenMargin.name=L"Margin";tenMargin.value=L"10,10,10,10";
                width.value=L"400";
                const auto tinySection=BuildLayoutSection(tiny,{width,tenMargin});
                check(tinySection.find(L"larger than the 0 its parent offered inside its Margin")!=std::wstring::npos &&
                    tinySection.find(L"-5")==std::wstring::npos,
                    "a margin wider than the slot reports zero offered space, not a negative number");
            }
            check(FeedsLayoutSection(L"Margin") && FeedsLayoutSection(L"MaxWidth") &&
                FeedsLayoutSection(L"HorizontalAlignment") && !FeedsLayoutSection(L"Text"),
                "changing a property the explanation reads redraws Size & spacing");
            check(SameLayout(box,same),"an unchanged layout does not rebuild the pane");
            same.parentW=560;same.haveParentBox=true;
            check(!SameLayout(box,same),"a changed offered size rebuilds the pane");
            same=box;same.parentSpacing=L"24";
            check(!SameLayout(box,same),"a changed parent Spacing redraws Size & spacing");
            same=box;same.gridColumn=L"1";
            check(!SameLayout(box,same),"a changed Grid column redraws Size & spacing");
            check(!g_layoutBody && !ReplaceLayoutSection(same),
                "with no Size & spacing section on screen, a layout change falls back to rebuilding the pane");
        }
        DevToolsCardRow text;text.name=L"Text";text.type=L"String";text.value=L"Hi";text.source=L"Local";
        g_layoutSectionOpen=0;
        const auto pane=BuildPropsFragment(L"Microsoft.UI.Xaml.Controls.TextBlock",L"Title",L"TextBlock",L"Window > Grid",
            L"",0,{text},L"noSourceInfo",L"",&stretched);
        const auto crumb=pane.find(L"WinAppDevToolsBreadcrumb"), filter=pane.find(L"PropFilter"),
            section=pane.find(L"WinAppDevToolsSizeSpacing"), box=pane.find(L"WinAppDevToolsBoxModel\"");
        check(crumb<section && section<box && box<filter && filter!=std::wstring::npos,
            "properties pane: header, then the Size & spacing section, then filter and grid");
        check(pane.find(L"SIZE &amp; SPACING")!=std::wstring::npos && pane.find(L"Text=\"LAYOUT\"")==std::wstring::npos,
            "the section is named apart from the grid's Layout category");
        check(pane.find(L"LayoutToggle\"")==std::wstring::npos && pane.find(L"x:Name=\"LayoutMore\"")==std::wstring::npos &&
            BuildLayoutSection(stretched,{}).find(L"WinAppDevToolsBoxModelDetail")!=std::wstring::npos,
            "the section has no nested More disclosure; its details show when it is open");
        check(pane.find(L"x:Name=\"LayoutSectionBody\" Margin=\"0,6,0,0\" Visibility=\"Collapsed\"")!=std::wstring::npos,
            "Size & spacing section is collapsed until opened");
        g_layoutSectionOpen=1;
        check(BuildPropsFragment(L"Microsoft.UI.Xaml.Controls.TextBlock",L"Title",L"TextBlock",L"Window > Grid",
            L"",0,{text},L"noSourceInfo",L"",&stretched).find(L"Visibility=\"Visible\" AutomationProperties.AutomationId=\"WinAppDevToolsSizeSpacingBody\"")!=std::wstring::npos,
            "an opened Size & spacing section stays open");
        check(pane.find(L"x:Bind and resource sources not shown (why?)")!=std::wstring::npos &&
            pane.find(L"TextWrapping=\"Wrap\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" AutomationProperties.Name=\"x:Bind and resource sources not shown\"")==std::wstring::npos,
            "without the XAML source, one line says x:Bind and resource sources are not shown; the reason is on hover");
        check(BuildPropsFragment(L"Microsoft.UI.Xaml.Controls.TextBlock",L"Title",L"TextBlock",L"Window > Grid",
            L"",0,{text},L"noFile",L"",&stretched).find(L"A value an {x:Bind} sets appears as Local.")!=std::wstring::npos,
            "the reason says an x:Bind value reads as Local");
        g_layoutSectionOpen=-1;
    }
    check(!ColorPickerApplies(true,L"#FF102030",L"#FF000000") && ColorPickerApplies(false,L"#FF102030",L"#FF000000") &&
        !ColorPickerApplies(false,L"#ff000000",L"#FF000000"),
        "Esc on the color picker cancels; closing it any other way applies a changed color");
    {
        // The selected element is live: a change the app makes updates only the rows that differ, an edit in
        // progress is not replaced, and a change to an open row or a property the pane does not list rebuilds it.
        using namespace DevToolsLiveRows;
        DevToolsCardRow opacity; opacity.name=L"Opacity"; opacity.value=L"1"; opacity.authored=L"1";
        DevToolsCardRow text; text.name=L"Text"; text.value=L"Start";
        const std::vector<DevToolsCardRow> shown{opacity,text};
        DevToolsCardRow changedOpacity; changedOpacity.name=L"Opacity"; changedOpacity.value=L"0.5";
        const std::vector<DevToolsCardRow> fresh{changedOpacity};
        const auto none=[](const std::wstring&){return false;};
        const auto update=Decide(shown,fresh,false,none);
        check(update.action==Action::UpdateRows && update.changed==std::vector<std::pair<size_t,size_t>>{{0,0}},
            "an app-side change updates only the row that changed, from a read of just that property");
        DevToolsCardRow sameOpacity=changedOpacity; sameOpacity.value=L"1";
        check(Decide(shown,std::vector<DevToolsCardRow>{sameOpacity},false,none).action==Action::None,
            "an unchanged value (our own write echoed back) changes nothing");
        check(Decide(shown,fresh,true,none).action==Action::Defer, "an edit in progress is not replaced; the update waits");
        check(Decide(shown,fresh,false,[](const std::wstring& p){return p==L"Opacity";}).action==Action::Rebuild,
            "a change to an open row rebuilds the pane, which keeps scroll, filter and expansion");
        DevToolsCardRow tag; tag.name=L"Tag"; tag.value=L"x";
        check(Decide(shown,std::vector<DevToolsCardRow>{tag},false,none).action==Action::Rebuild,
            "a property the pane does not list yet rebuilds it");
        check(g_live.tokens.empty() && g_live.wire==0, "no property callbacks are held when nothing is followed");
        // The tree caption is read once per row; a live change reads it again (it stayed "Start focusing").
        static std::wstring appContent;
        g_ctx.previewFn = [](std::vector<DevToolsWindowNode>& nodes, const std::vector<size_t>& indices) {
            for (size_t i : indices) { if (nodes[i].previewRead) continue; nodes[i].previewRead = true; nodes[i].preview = appContent; }
        };
        g_allTreeNodes.assign(1, DevToolsWindowNode{});
        appContent = L"Start focusing";
        g_ctx.previewFn(g_allTreeNodes, {0});
        appContent = L"Pause focus";
        std::wstring before;
        check(RereadTreeCaption(0, &before) && before == L"Start focusing" && g_allTreeNodes[0].preview == L"Pause focus",
            "a live change rereads the selected row's tree caption");
        check(!RereadTreeCaption(0, &before), "an unchanged caption is left alone");
        g_ctx.previewFn = nullptr; g_allTreeNodes.clear();
    }
    {
        // A pick that opens the inspector selects its row before the tree's first layout. That reveal must wait for
        // the tree to get a size rather than be dropped.
        TestElement scroll, rowElement;
        g_treeScrollUi = &scroll;
        g_treeRows.assign(117, nullptr); g_treeVisible.assign(117, 1);
        g_treeRows[116] = &rowElement;
        g_nodeIdxByWire = {{8120172924ull, 116}};
        g_selectedWire = 8120172924ull;
        testViewport = 0.0;
        BringRowIntoView(116);
        check(g_pendingRevealWire == 8120172924ull, "a reveal before the tree's first layout waits instead of being dropped");
        RevealPendingRow();
        check(g_pendingRevealWire == 8120172924ull, "it keeps waiting while the tree still has no viewport");
        testViewport = 752.0;
        g_treeRows[116] = nullptr;   // the reveal itself needs live XAML; reaching it is what is checked here
        RevealPendingRow();
        check(g_pendingRevealWire == 0, "the tree's first size makes the waiting reveal");
        g_treeRows[116] = &rowElement; testViewport = 0.0;
        BringRowIntoView(116);
        g_selectedWire = 42; testViewport = 752.0;
        RevealPendingRow();
        check(g_pendingRevealWire == 0, "a selection that changed while waiting is not revealed");
        g_treeScrollUi = nullptr; g_treeRows.clear(); g_treeVisible.clear(); g_nodeIdxByWire.clear(); g_selectedWire = 0;
    }
    {
        // The inspector's comment box starts from the element's existing comment, found as the Comments pane finds it.
        DevToolsWindowComment todays; todays.id = L"cmt_a0dbf8f331a8"; todays.text = L"hello\rThis is a new comment";
        todays.status = L"open"; todays.anchor = L"source:today";
        DevToolsWindowComment other = todays; other.id = L"cmt_other"; other.anchor = L"source:other";
        DevToolsWindowComment resolved = todays; resolved.id = L"cmt_done"; resolved.status = L"resolved"; resolved.anchor = L"source:done";
        g_commentsFn = [&](std::vector<DevToolsWindowComment>* out) { *out = { other, resolved, todays }; return true; };
        g_anchorWireFn = [](const wchar_t* anchor) -> InstanceHandle {
            const std::wstring a(anchor);
            return a == L"source:today" ? 246 : a == L"source:other" ? 223 : a == L"source:done" ? 300 : 0;
        };
        DevToolsWindowComment found;
        check(SelectedElementComment(246, &found) && found.id == L"cmt_a0dbf8f331a8" && found.text == todays.text,
            "the inspector finds the selected element's existing comment");
        check(!SelectedElementComment(300, &found), "a resolved comment does not refill the inspector's box");
        check(!SelectedElementComment(999, &found), "an element without a comment starts with an empty box");
        g_commentsFn = nullptr; g_anchorWireFn = nullptr;
    }
    {
        // Edit records: Restore puts back what the property was before the session's first edit, on that element,
        // after the user has looked at other elements, and not after the app changed the value since.
        static std::map<InstanceHandle,std::wstring> live;
        static std::wstring liveSource = L"Local";
        static unsigned clears = 0, edits = 0;
        live = {{500,L"Hello"},{501,L"Other"}};
        DevToolsCardRow text; text.name=L"Text"; text.type=L"String"; text.valueType=L"Windows.Foundation.String";
        text.value=L"Hello"; text.source=L"Local"; text.authoredKind=L"literal"; text.authored=L"Hello";
        g_ctx.writeFn=[](InstanceHandle wire,const std::wstring&,const std::wstring&,const std::wstring& value,bool,std::wstring*){
            ++edits; live[wire]=value; liveSource=L"Local"; return DevToolsWriteOutcome::Ok;
        };
        g_ctx.clearFn=[](InstanceHandle wire,const std::wstring&){ ++clears; live[wire]=L""; liveSource=L"Default"; return true; };
        g_ctx.rowsFn=[text](InstanceHandle wire,std::wstring&,std::wstring&,std::vector<DevToolsCardRow>& rows,std::wstring&,std::wstring&) {
            auto fresh=text; fresh.value=live[wire]; fresh.source=liveSource; rows={fresh}; return true;
        };
        g_valuesFn=[](InstanceHandle wire,const std::vector<std::wstring>&,std::vector<DevToolsCardRow>* rows) {
            DevToolsCardRow fresh; fresh.name=L"Text"; fresh.type=L"String"; fresh.value=live[wire]; fresh.source=liveSource;
            rows->push_back(fresh); return true;
        };
        g_statusTb=&testStatus; g_editRecords.clear(); g_appChanged.clear();
        g_linkedRows.clear(); g_linkLost.clear(); g_bindingLost.clear(); g_sessionAssign.clear();
        g_pendingWrites.clear(); g_pendingKind.clear();
        g_selWire=g_dirtyNode=500; g_propRows={text};

        check(ApplyLiteralValue(L"Text",L"String",L"Hello",L"Edited",false) && live[500]==L"Edited",
            "an edit writes to the selected element");
        const EditRecord* rec=DirtyFind(L"Text");
        check(rec && rec->kind==DevToolsOriginal::Value && rec->original==L"Hello", "the first edit records the original value");
        ApplyLiteralValue(L"Text",L"String",L"Edited",L"Again",false);
        check(DirtyFind(L"Text") && DirtyFind(L"Text")->original==L"Hello", "a second edit keeps the first original");

        g_selWire=g_dirtyNode=501;
        check(!DirtyFind(L"Text") && RecordFind(500,L"Text"), "an edit record belongs to its element, not the selection");
        g_selWire=g_dirtyNode=500; g_propRows={text}; g_propRows[0].value=L"Again";
        check(DirtyFind(L"Text") != nullptr, "coming back to an edited element still offers Restore");

        RestoreRowValue(500,L"Text",L"String");
        check(live[500]==L"Hello" && !DirtyFind(L"Text") && testStatus.text.find(L"Restored Text")!=std::wstring::npos,
            "Restore writes the original back and clears the record");

        g_propRows={text};
        ClearRowValue(500,L"Text");
        check(clears==1 && DirtyFind(L"Text") && DirtyFind(L"Text")->original==L"Hello", "Clear is recorded as an edit");
        g_propRows={text}; g_propRows[0].value=L""; g_propRows[0].source=L"Default";
        ApplyLiteralValue(L"Text",L"String",L"",L"New",false);
        RestoreRowValue(500,L"Text",L"String");
        check(live[500]==L"Hello" && !DirtyFind(L"Text"), "Clear, then an edit, then Restore puts the original back");

        // A value from a style is not set on the element: Restore clears the property instead of pinning the value.
        DevToolsCardRow styled=text; styled.source=L"Style"; styled.authoredKind.clear(); styled.authored.clear();
        styled.value=L"FromStyle"; live[500]=L"FromStyle"; liveSource=L"Style";
        g_propRows={styled};
        ApplyLiteralValue(L"Text",L"String",L"FromStyle",L"Mine",false);
        check(DirtyFind(L"Text") && DirtyFind(L"Text")->kind==DevToolsOriginal::Unset, "a style-supplied original is recorded as not set");
        const unsigned clearsBefore=clears, editsBefore=edits;
        RestoreRowValue(500,L"Text",L"String");
        check(clears==clearsBefore+1 && edits==editsBefore && !DirtyFind(L"Text"),
            "restoring a not-set original clears the property rather than writing its old value");

        // The app changes the value after the edit: Restore would undo the app's change, so the record is dropped.
        g_propRows={text}; live[500]=L"Hello"; liveSource=L"Local";
        ApplyLiteralValue(L"Text",L"String",L"Hello",L"Edited",false);
        std::vector<DevToolsCardRow> now{text}; now[0].value=L"Edited";
        ReconcileEditRecords(500,now);
        check(DirtyFind(L"Text") != nullptr, "re-reading our own write keeps the record");
        now[0].value=L"FromApp"; live[500]=L"FromApp";
        ReconcileEditRecords(500,now);
        check(!DirtyFind(L"Text") && AppChanged(500,L"Text"), "an app change since the edit drops Restore and says why");
        // The editor still holds "Edited" from before the app's change; the app's value is the real original.
        ApplyLiteralValue(L"Text",L"String",L"Edited",L"Edited2",false);
        check(DirtyFind(L"Text") && DirtyFind(L"Text")->original==L"FromApp" && !AppChanged(500,L"Text"),
            "a new edit after an app change records the app's value as the original");

        // Every DevTools write first reads the edited properties, so an app change can't hide in the next baseline.
        check(!DirtyFind(L"Text")->needsBaseline && DirtyFind(L"Text")->baseline==L"Edited2",
            "the value read right after a write becomes the baseline");
        live[500]=L"AppAgain";
        const auto writeAll=g_ctx.writeFn;
        g_ctx.writeFn=[](InstanceHandle,const std::wstring&,const std::wstring&,const std::wstring&,bool,std::wstring*){
            return DevToolsWriteOutcome::Ok;
        };
        CommitWrite(500,L"Other",L"String",L"x",false);
        check(!DirtyFind(L"Text") && AppChangedFind(500,L"Text") && AppChangedFind(500,L"Text")->value==L"AppAgain",
            "an app change before another write drops Restore instead of becoming the new baseline");
        g_ctx.writeFn=writeAll;

        // A theme switch changes theme-following values without the app: take a new baseline, keep Restore.
        g_editRecords.clear(); g_appChanged.clear();
        EditRecord themed; themed.wire=500; themed.prop=L"Text"; themed.original=L"Hello";
        themed.baseline=L"Light"; themed.needsBaseline=false; themed.baselineTheme=1;
        g_editRecords.push_back(themed);
        DevToolsCardRow themeRow=text; themeRow.authoredKind=L"themeResource"; themeRow.authoredKey=L"Greeting";
        std::vector<DevToolsCardRow> dark{themeRow}; dark[0].value=L"Dark";
        ReconcileEditRecordsAt(500,dark,2);
        check(DirtyFind(L"Text") && DirtyFind(L"Text")->baseline==L"Dark" && !AppChanged(500,L"Text"),
            "a theme switch keeps Restore");
        dark[0].value=L"FromApp";
        ReconcileEditRecordsAt(500,dark,2);
        check(!DirtyFind(L"Text") && AppChanged(500,L"Text"), "an app change in the same theme still drops Restore");
        // A literal set on the element looks the same in every theme, so a change during a theme switch is the app's.
        g_editRecords.clear(); g_appChanged.clear();
        g_editRecords.push_back(themed);
        std::vector<DevToolsCardRow> literalDark{text}; literalDark[0].value=L"FromApp";
        ReconcileEditRecordsAt(500,literalDark,2);
        check(!DirtyFind(L"Text") && AppChanged(500,L"Text"), "an app change to a literal during a theme switch still drops Restore");
        g_editRecords.push_back(themed);
        literalDark[0].value=L"Light";
        ReconcileEditRecordsAt(500,literalDark,2);
        check(DirtyFind(L"Text") && DirtyFind(L"Text")->baselineTheme==2, "an unchanged literal keeps Restore across a theme switch");
        g_editRecords.clear(); g_appChanged.clear();
        // A {ThemeResource} that DevTools assigned still follows the theme after the selection moves away and back.
        live[500]=L"Hello"; liveSource=L"Local"; g_propRows={text};
        g_ctx.resolveFn=[](InstanceHandle wire,const std::wstring&,const std::wstring&,bool){ live[wire]=L"LightBrush"; return true; };
        std::wstring resolveError;
        check(ApplyResourceKey(text,L"Text",L"Accent",/*asTheme*/ true,L"Hello",resolveError) && DirtyFind(L"Text") &&
              DirtyFind(L"Text")->themeAssigned, "assigning a ThemeResource marks its record as following the theme");
        g_ctx.resolveFn=nullptr;
        if (EditRecord* r=DirtyFind(L"Text")) r->baselineTheme=1;
        g_sessionAssign.clear();
        std::vector<DevToolsCardRow> darkBrush{text}; darkBrush[0].value=L"DarkBrush";
        ReconcileEditRecordsAt(500,darkBrush,2);
        check(DirtyFind(L"Text") && !AppChanged(500,L"Text"),
            "a theme switch after reselecting keeps Restore for a ThemeResource DevTools assigned");
        ApplyLiteralValue(L"Text",L"String",L"DarkBrush",L"Mine",false);
        check(DirtyFind(L"Text") && !DirtyFind(L"Text")->themeAssigned, "typing a literal over it stops following the theme");
        g_editRecords.clear(); g_appChanged.clear(); g_sessionAssign.clear(); live[500]=L"Hello";

        // A failed resource Restore can clear the property; the value before it is put back and Restore stays.
        g_editRecords.clear(); g_appChanged.clear();
        live[500]=L"Lit"; liveSource=L"Local"; g_propRows={text}; g_propRows[0].value=L"Lit";
        EditRecord linked; linked.wire=500; linked.prop=L"Text"; linked.kind=DevToolsOriginal::Resource;
        linked.original=L"{StaticResource Accent}"; linked.key=L"Accent"; linked.baseline=L"Lit"; linked.needsBaseline=false;
        g_editRecords.push_back(linked);
        g_ctx.resolveFn=[](InstanceHandle wire,const std::wstring&,const std::wstring&,bool){ live[wire]=L""; return false; };
        RestoreRowValue(500,L"Text",L"String");
        check(live[500]==L"Lit" && DirtyFind(L"Text") && !AppChanged(500,L"Text") &&
              testStatus.text.find(L"Nothing was changed")!=std::wstring::npos,
            "a failed resource Restore keeps the current value and the record");
        // An empty string is a real value: a failed lookup that cleared it puts the empty string back.
        g_editRecords.clear(); g_appChanged.clear();
        live[500]=L""; liveSource=L"Local"; g_propRows={text}; g_propRows[0].value=L"";
        linked.baseline=L""; g_editRecords.push_back(linked);
        g_ctx.resolveFn=[](InstanceHandle wire,const std::wstring&,const std::wstring&,bool){ live[wire]=L""; liveSource=L"Default"; return false; };
        const unsigned editsEmpty=edits;
        RestoreRowValue(500,L"Text",L"String");
        check(edits==editsEmpty+1 && liveSource==L"Local" && DirtyFind(L"Text") &&
              testStatus.text.find(L"Nothing was changed")!=std::wstring::npos,
            "a failed resource Restore puts back an empty string");
        g_ctx.resolveFn=nullptr; liveSource=L"Local";

        // Coming back to an element forgets that its resource link was replaced; Restore must still write directly.
        g_editRecords.clear(); g_appChanged.clear();
        live[500]=L"Mine"; g_propRows={text}; g_propRows[0].value=L"Mine";
        g_linkedRows={L"Text"}; g_linkLost.clear(); g_pendingWrites.clear();
        EditRecord literalOverLink; literalOverLink.wire=500; literalOverLink.prop=L"Text"; literalOverLink.original=L"Hello";
        literalOverLink.baseline=L"Mine"; literalOverLink.needsBaseline=false;
        g_editRecords.push_back(literalOverLink);
        RestoreRowValue(500,L"Text",L"String");
        check(live[500]==L"Hello" && !DirtyFind(L"Text") && g_pendingWrites.empty(),
            "Restore on a resource-backed row works after leaving and coming back to the element");
        g_linkedRows.clear(); g_linkLost.clear();

        // A field such as Foreground.Color isn't in the live read, so its baseline comes from a full read rather
        // than the pane's cached value, and the next full read doesn't mistake DevTools' own write for the app's.
        {
            static std::wstring color;
            color=L"Red";
            DevToolsCardRow fg; fg.name=L"Foreground"; fg.type=L"Brush"; fg.value=L"SolidColorBrush"; fg.source=L"Local";
            DevToolsCardRow fgColor; fgColor.name=L"Color"; fgColor.type=L"Color"; fgColor.value=L"Blue";
            fg.children={fgColor};
            const auto rowsAll=g_ctx.rowsFn;
            g_ctx.rowsFn=[fg](InstanceHandle,std::wstring&,std::wstring&,std::vector<DevToolsCardRow>& rows,std::wstring&,std::wstring&) {
                auto fresh=fg; fresh.children[0].value=color; rows={fresh}; return true;
            };
            g_editRecords.clear(); g_appChanged.clear(); g_propRows={fg};
            EditRecord colorRec; colorRec.wire=500; colorRec.prop=L"Foreground.Color"; colorRec.original=L"Blue";
            g_editRecords.push_back(colorRec);
            CheckEditRecordsNow(500);
            check(DirtyFind(L"Foreground.Color") && DirtyFind(L"Foreground.Color")->baseline==L"Red",
                "an edited field takes its baseline from a full read, not the pane's cached value");
            std::vector<DevToolsCardRow> full; std::wstring t,n,s,x;
            g_ctx.rowsFn(500,t,n,full,s,x);
            ReconcileEditRecords(500,full);
            check(DirtyFind(L"Foreground.Color") && !AppChanged(500,L"Foreground.Color"),
                "editing a field keeps its Restore");
            DevToolsCardRow liveFg=fg; liveFg.children.clear();
            ReconcileLiveValues(500,{liveFg});
            check(DirtyFind(L"Foreground.Color") != nullptr, "a live read without fields leaves a field's Restore alone");
            g_ctx.rowsFn=rowsAll; g_editRecords.clear(); g_propRows={text};
        }
        g_ctx.resolveFn=nullptr;

        // An editor removed by a rebuild can still deliver a focus-out; it must not write again.
        g_ctx.readInputFn=[](IInspectable*,std::wstring& t){ t=L"Stale"; return true; };
        LostFocusSink late; late.Init(500,L"Text",L"String",L"Edited2",&testInput);
        late.retired=true;
        const unsigned editsLate=edits;
        const std::wstring liveLate=live[500];
        late.Invoke(nullptr,nullptr);
        check(edits==editsLate && live[500]==liveLate, "a retired editor's focus-out does not write");
        // A late save to an element that is no longer selected records the original captured while it was selected,
        // and reads that element back right away so a later app change there still drops Restore.
        g_editRecords.clear(); g_appChanged.clear();
        g_selWire=g_dirtyNode=501;
        EditRecord styledFirst; styledFirst.wire=500; styledFirst.prop=L"Text";
        styledFirst.kind=DevToolsOriginal::Unset; styledFirst.original=L"Before";
        live[500]=L"Typed"; liveSource=L"Local";
        ApplyCommitOutcome(DevToolsWriteOutcome::Ok,500,L"Text",L"String",L"Before",L"Typed",&styledFirst);
        check(RecordFind(500,L"Text") && RecordFind(500,L"Text")->kind==DevToolsOriginal::Unset && !RecordFind(501,L"Text"),
            "a late save to the previous element keeps whether its original was set on that element");
        check(RecordFind(500,L"Text") && !RecordFind(500,L"Text")->needsBaseline && RecordFind(500,L"Text")->baseline==L"Typed",
            "a late save reads its element back right away");
        std::vector<DevToolsCardRow> later{text}; later[0].value=L"FromApp";
        ReconcileEditRecords(500,later);
        check(!RecordFind(500,L"Text") && AppChanged(500,L"Text"), "an app change after a late save still drops Restore");
        g_selWire=g_dirtyNode=500;

        // The app keeps changing a property after its record was dropped: a new edit restores the latest app value.
        g_editRecords.clear(); g_appChanged.clear(); g_propRows={text};
        g_appChanged.push_back({500,L"Text",L"Tick: 10",true});
        live[500]=L"Tick: 11"; liveSource=L"Local";
        ApplyLiteralValue(L"Text",L"String",L"Tick: 10",L"Mine",false);
        check(DirtyFind(L"Text") && DirtyFind(L"Text")->original==L"Tick: 11" && !AppChanged(500,L"Text"),
            "a new edit after repeated app changes records the app's latest value");

        // After an app change, the element's old resource is no longer what Restore should put back.
        g_editRecords.clear(); g_appChanged.clear();
        DevToolsCardRow linkedRow=text; linkedRow.authoredKind=L"staticResource"; linkedRow.authoredKey=L"Greeting";
        g_propRows={linkedRow}; g_linkedRows={L"Text"};
        g_appChanged.push_back({500,L"Text",L"FromApp",true});
        EditRecord fromApp;
        check(OriginalFor(L"Text",L"Stale",fromApp) && fromApp.kind==DevToolsOriginal::Value && fromApp.original==L"FromApp",
            "after an app change the original is the app's value, not the old resource");
        g_appChanged[0].setHere=false;
        check(OriginalFor(L"Text",L"Stale",fromApp) && fromApp.kind==DevToolsOriginal::Unset,
            "after the app clears a property back to its style, Restore clears it again");
        DevToolsCardRow styledNow=text; styledNow.source=L"Style"; styledNow.authoredKind.clear();
        check(!SetHereIn({styledNow},L"Text") && SetHereIn({text},L"Text"), "a style value is not set on the element");
        DevToolsCardRow clearedAuthored=text; clearedAuthored.source=L"Style";
        check(!SetHereIn({clearedAuthored},L"Text"),
            "a property authored in XAML that the app cleared back to its style is not set on the element");
        DevToolsCardRow animated=text; animated.source=L"Animation";
        check(SetHereIn({animated},L"Text"), "an animation over an authored value still counts as set on the element");
        g_propRows={clearedAuthored}; g_appChanged.clear();
        EditRecord clearedOriginal;
        check(OriginalFor(L"Text",L"FromStyle",clearedOriginal) && clearedOriginal.kind==DevToolsOriginal::Unset,
            "Restore clears an authored property the app had cleared, instead of pinning the style's value");
        DevToolsCardRow clearedResource=clearedAuthored; clearedResource.authoredKind=L"staticResource"; clearedResource.authoredKey=L"Wide";
        g_propRows={clearedResource}; g_linkedRows={L"Text"};
        EditRecord clearedResourceOriginal;
        check(OriginalFor(L"Text",L"FromStyle",clearedResourceOriginal) && clearedResourceOriginal.kind==DevToolsOriginal::Unset,
            "Restore clears a XAML resource the app had cleared, instead of bringing the resource back");
        clearedResource.source=L"Local"; g_propRows={clearedResource};
        check(OriginalFor(L"Text",L"Wide",clearedResourceOriginal) && clearedResourceOriginal.kind==DevToolsOriginal::Resource &&
              clearedResourceOriginal.key==L"Wide", "a XAML resource that is still set is restored as the resource");
        g_linkedRows.clear(); g_appChanged.clear(); g_propRows={text};

        // Attached properties keep their full name in the pre-write read; fields read their owning property.
        DevToolsCardRow padding; padding.name=L"Padding"; DevToolsCardRow automation; automation.name=L"AutomationProperties.Name";
        const std::vector<DevToolsCardRow> owners{padding,automation};
        check(OwningRowName(owners,L"Padding.Left")==L"Padding" &&
              OwningRowName(owners,L"AutomationProperties.Name")==L"AutomationProperties.Name",
            "the pre-write read asks for attached properties by their full name");
        RegisterPaneEditor(L"Text",&testInput,&late.retired,&late,nullptr);
        late.retired=false;
        ReleasePaneEditors(/*retire*/ false);
        check(!late.retired && g_paneEditors.empty(),
            "moving to another element keeps its editors live, so a late focus-out still saves to its own element");
        RegisterPaneEditor(L"Text",&testInput,&late.retired,&late,nullptr);
        RetirePaneEditors();
        check(late.retired && g_paneEditors.empty(), "rebuilding the pane retires its editors");
        // Restore and Clear save the focused editor once, then retire it, so the rebuild can't write its old text back.
        g_propRows={text}; live[500]=L"Hello"; liveSource=L"Local"; g_editRecords.clear(); g_appChanged.clear();
        ApplyLiteralValue(L"Text",L"String",L"Hello",L"Edited",false);
        RegisterPaneEditor(L"Text",&testInput,&late.retired,&late,nullptr); late.retired=false;
        RestoreRowValue(500,L"Text",L"String");
        check(late.retired && g_paneEditors.empty() && live[500]==L"Hello", "Restore retires the editors it saved");
        RegisterPaneEditor(L"Text",&testInput,&late.retired,&late,nullptr); late.retired=false;
        ClearRowValue(500,L"Text");
        check(late.retired && g_paneEditors.empty(), "Clear retires the editors it saved");

        // A multi-field editor (Margin's four boxes) reads only its own expansion's boxes. The previous
        // element's boxes have the same property path and can still be alive after the selection moves.
        {
            const auto savedRead = g_ctx.readInputFn;
            g_ctx.readInputFn=[](IInspectable* el,std::wstring& t){ t=static_cast<TestElement*>(el)->text; return true; };
            DevToolsCardRow margin=text; margin.editKind=L"fields"; margin.fields={L"Left",L"Top"};
            TestElement a0,a1,b0,b1,c0,c1,stale0,stale1; int ownerA=0, ownerB=0, ownerC=0;
            a0.text=L"8"; a1.text=L"12"; b0.text=L"5"; b1.text=L"0"; c0.text=L"1"; c1.text=L"2";
            stale0.text=L"9"; stale1.text=L"9";
            auto commitOn=[&](InstanceHandle wire, void* owner) {
                g_selWire=g_dirtyNode=wire; margin.value=live[wire]; g_propRows={margin};
                KindCtx c; c.row=std::make_shared<const DevToolsCardRow>(margin); c.path=L"Text"; c.wire=wire; c.expFe=owner;
                CommitKind(c);
            };
            g_editRecords.clear(); g_appChanged.clear(); liveSource=L"Local"; live[500]=L"0,0"; live[501]=L"0,0";
            g_fieldBoxes.push_back({L"Text",0,&a0,&ownerA}); g_fieldBoxes.push_back({L"Text",1,&a1,&ownerA});
            commitOn(500,&ownerA);
            check(live[500]==L"8,12", "a multi-field edit writes its own boxes");
            g_fieldBoxes.push_back({L"Text",0,&b0,&ownerB}); g_fieldBoxes.push_back({L"Text",1,&b1,&ownerB});
            commitOn(501,&ownerB);
            check(live[501]==L"5,0" && live[500]==L"8,12",
                "after switching elements, a multi-field edit writes the boxes on screen, not the previous element's");
            g_fieldBoxes.push_back({L"Text",0,&c0,&ownerC}); g_fieldBoxes.push_back({L"Text",1,&c1,&ownerC});
            commitOn(500,&ownerC);
            commitOn(501,&ownerB);
            check(live[500]==L"1,2" && live[501]==L"5,0", "switching back and forth keeps each editor on its own boxes");
            g_fieldBoxes.insert(g_fieldBoxes.begin(),{L"Text",0,&stale0,&ownerB});
            g_fieldBoxes.insert(g_fieldBoxes.begin()+1,{L"Text",1,&stale1,&ownerB});
            ReleaseFieldBoxesWhere(L"Text",&ownerB);
            check(std::none_of(g_fieldBoxes.begin(),g_fieldBoxes.end(),[&](const FieldBoxRef& f){ return f.owner==&ownerB; }) &&
                  g_fieldBoxes.size()==4, "rebuilding one expansion's boxes drops only that expansion's old boxes");
            ReleaseFieldBoxes(L"");
            g_ctx.readInputFn=savedRead; g_propRows={text};
        }
        g_editRecords.clear(); g_appChanged.clear(); g_ctx.clearFn=nullptr; g_statusTb=nullptr; g_valuesFn=nullptr;
    }    std::printf("Native window: checks=%u passed=%u failed=%u skipped=0\n",checks,checks-failed,failed);
    return failed ? 1 : 0;
}
