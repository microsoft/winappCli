// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "../native/WinApp.DevTools.Native/DevToolsProjected.h"
#include <commctrl.h>
static HRESULT FindTestName(void*, HSTRING, IInspectable**);
static HRESULT PutTestText(void*, HSTRING);
static HRESULT GetTestElement(void*, int, IInspectable**);
static HRESULT GetTestToggle(void*, bool*);
static HRESULT PutTestVisibility(void*, int) { return S_OK; }
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
#define DevToolsFindName FindTestName
#define DevToolsPutText PutTestText
#define DevToolsRepeaterTryGetElement GetTestElement
#define DevToolsToggleGetIsOn GetTestToggle
#define DevToolsPutVisibility PutTestVisibility
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
#include "../native/WinApp.DevTools.Native/DevToolsWindow.cpp"

struct TestElement : IInspectable {
    ULONG refs = 1;
    std::wstring text;
    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID, void** value) override {
        *value=static_cast<IInspectable*>(this); AddRef(); return S_OK;
    }
    ULONG STDMETHODCALLTYPE AddRef() override { return ++refs; }
    ULONG STDMETHODCALLTYPE Release() override { return --refs; }
    HRESULT STDMETHODCALLTYPE GetIids(ULONG* count, IID** ids) override { *count=0; *ids=nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetRuntimeClassName(HSTRING* value) override { *value=nullptr; return S_OK; }
    HRESULT STDMETHODCALLTYPE GetTrustLevel(TrustLevel* value) override { *value=BaseTrust; return S_OK; }
};
static TestElement testRow, testValue, testStatus, testInput;
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
    return S_OK;
}
static HRESULT PutTestText(void* element, HSTRING value)
{
    UINT32 length=0;
    const auto* chars=WindowsGetStringRawBuffer(value,&length);
    static_cast<TestElement*>(element)->text.assign(chars ? chars : L"",length);
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
    unsigned checks=0, failed=0, writes=0;
    auto check=[&](bool ok,const char* name){++checks;if(!ok){++failed;std::printf("FAIL %s\n",name);}};
    std::map<InstanceHandle,std::wstring> values{{77,L"True"},{88,L"True"}};
    DevToolsCardRow row;
    row.name=L"IsEnabled";row.type=L"Boolean";row.valueType=L"Windows.Foundation.Boolean";
    row.value=L"True";row.source=L"Default";
    g_ctx.writeFn=[&](InstanceHandle wire,const std::wstring&,const std::wstring&,const std::wstring& value,bool) {
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
    toggle.Invoke(nullptr,nullptr);
    check(values[77]==L"False","boolean commit updates actual target");
    check(testValue.text==L"False","successful commit refreshes shown value in place");
    check(g_propRows[0].value==L"False","recycled row retains committed value");
    check(expansion.ExpansionRow().value==L"False","collapse/reopen resolves the refreshed row instead of captured snapshot");
    check(focusCalls==0,"commit does not move focus");
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
    guarded.Init(std::make_shared<const DevToolsCardRow>(row),row.name,0,&testRow,false);
    guarded.Invoke(nullptr,nullptr);
    check(g_propRows[0].value==L"True" && !CanRereadSelection(),"a pending write keeps the snapshot");
    g_pendingWrites.clear();g_propExpanded.clear();
    check(CanRereadSelection(),"a clean pane can be re-read");
    RowExpandSink opener;
    opener.Init(std::make_shared<const DevToolsCardRow>(row),row.name,0,&testRow,false);
    opener.Invoke(nullptr,nullptr);
    check(g_propRows[0].value==L"False" && testValue.text==L"False","opening a row re-reads its live value");
    check(CanRereadSelection(),"an expanded row whose editor is not being typed in does not block a re-read");
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
    g_ctx.rowsFn=[](InstanceHandle,std::wstring&,std::wstring&,std::vector<DevToolsCardRow>&,std::wstring&,std::wstring&) {
        return false;
    };
    check(ApplyCommitOutcome(DevToolsWriteOutcome::Ok,88,L"Text",L"String",L"before",L"after") &&
        testStatus.text.find(L"couldn't refresh")!=std::wstring::npos,
        "successful write with failed readback reports display refresh failure");
    InstanceHandle written=0;
    g_selWire=77;testStatus.text=L"new selection";
    g_ctx.writeFn=[&](InstanceHandle wire,const std::wstring&,const std::wstring&,const std::wstring&,bool){
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
        check(pane.find(L"Authored values unavailable (why?)")!=std::wstring::npos &&
            pane.find(L"TextWrapping=\"Wrap\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\" AutomationProperties.Name=\"Authored values unavailable\"")==std::wstring::npos,
            "source verification is one line; the reason is on hover");
        g_layoutSectionOpen=-1;
    }
    std::printf("Native window: checks=%u passed=%u failed=%u skipped=0\n",checks,checks-failed,failed);
    return failed ? 1 : 0;
}
