// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// The pane's STYLE card: Blend's "Edit Style > Edit Current / Edit a Copy...". Both run
// `winapp devtools resources copy-style` on a worker thread (the CLI connects back to this app, so the UI thread must
// keep pumping) and the drain timer renders the answer. State lives here, keyed by the selected element, so a pane
// rebuild redraws the same form and result.

static DevToolsWindowCliFn g_cliFn;

struct StyleEditUi {
    InstanceHandle wire = 0;
    bool formOpen = false;
    bool allOfType = false;
    bool inDocument = false;
    bool busy = false;
    std::wstring key;
    std::wstring busyText;
    bool haveOutcome = false;
    DevToolsStyleEdit::Outcome outcome;
    std::wstring opened; // what happened when the pane opened the outcome's file
};
static StyleEditUi g_styleUi;

struct StyleEditAsk { DevToolsStyleEdit::Request request; unsigned gen; DevToolsWindowCliFn run; };
struct StyleEditDone { DevToolsStyleEdit::Request request; unsigned gen; DevToolsStyleEdit::Outcome outcome; };
static SRWLOCK                    g_styleEditLock = SRWLOCK_INIT;
static std::vector<StyleEditDone> g_styleEditDone;
static UINT_PTR                   g_styleEditTimer = 0;
static volatile LONG              g_styleEditBusy = 0;
static bool                       g_styleEditRedraw = false;

static std::wstring StyleEditOpenFile(const std::wstring& path, unsigned int line)
{
    const DevToolsShellOpen::Result r = DevToolsShellOpen::Decide(
        path, L".xaml",
        [](const std::wstring& ext) { return ShellAssocFor(ext); },
        [](const std::wstring& p)   { return GetFileAttributesW(p.c_str()) != INVALID_FILE_ATTRIBUTES; },
        [](const std::wstring& p)   {
            HINSTANCE rc = ShellExecuteW(nullptr, L"open", p.c_str(), nullptr, nullptr, SW_SHOWNORMAL);
            return static_cast<int>(reinterpret_cast<INT_PTR>(rc));
        });
    DevToolsOverlayLog(L"route1.style open outcome=%d path='%s'", (int)r.outcome, path.c_str());
    std::wstring said = DevToolsShellOpen::Describe(r, DevToolsStyleEdit::FileName(path), line, path, L"");
    if (r.outcome == DevToolsShellOpen::Outcome::Handed && line > 0) said += L" Go to line " + std::to_wstring(line) + L".";
    if (!said.empty()) said[0] = static_cast<wchar_t>(towupper(said[0]));
    return said;
}

static void CALLBACK StyleEditTimerProc(HWND, UINT, UINT_PTR id, DWORD)
{
    std::vector<StyleEditDone> done;
    AcquireSRWLockExclusive(&g_styleEditLock);
    done.swap(g_styleEditDone);
    ReleaseSRWLockExclusive(&g_styleEditLock);
    bool redraw = g_styleEditRedraw;
    g_styleEditRedraw = false;
    if (done.empty() && !redraw) {
        if (InterlockedCompareExchange(&g_styleEditBusy, 0, 0) == 0) {
            KillTimer(nullptr, id);
            g_styleEditTimer = 0;
        }
        return;
    }
    for (const StyleEditDone& d : done) {
        if (d.gen != g_route1Gen || d.request.wire != g_styleUi.wire) continue;
        g_styleUi.busy = false;
        g_styleUi.haveOutcome = true;
        g_styleUi.outcome = d.outcome;
        g_styleUi.opened.clear();
        if (d.outcome.ok && d.request.action == DevToolsStyleEdit::Action::Copy) g_styleUi.formOpen = false;
        // Blend put you straight into the Style it found or copied; the closest we can do is open the file.
        if (d.outcome.ok && d.outcome.editable)
            g_styleUi.opened = StyleEditOpenFile(d.outcome.openPath, d.outcome.openLine);
        redraw = true;
    }
    if (redraw && g_selWire == g_styleUi.wire) ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
}

// Redraws the card from g_styleUi on the next timer tick, outside the Click that changed it.
static void RequestStyleRedraw()
{
    g_styleEditRedraw = true;
    if (!g_styleEditTimer) g_styleEditTimer = SetTimer(nullptr, 0, 100, &StyleEditTimerProc);
}

static DWORD WINAPI StyleEditWorker(LPVOID param)
{
    StyleEditAsk* ask = reinterpret_cast<StyleEditAsk*>(param);
    std::wstring out;
    const int code = ask->run
        ? ask->run(DevToolsStyleEdit::Arguments(ask->request), ask->request.action == DevToolsStyleEdit::Action::Copy, &out)
        : -1;
    StyleEditDone d{ ask->request, ask->gen, DevToolsStyleEdit::Interpret(ask->request, code, out) };
    AcquireSRWLockExclusive(&g_styleEditLock);
    g_styleEditDone.push_back(std::move(d));
    ReleaseSRWLockExclusive(&g_styleEditLock);
    InterlockedExchange(&g_styleEditBusy, 0);
    delete ask;
    return 0;
}

// Starts one copy-style run for the selected element. False when one is already running.
static bool PostStyleEdit(DevToolsStyleEdit::Action action)
{
    if (InterlockedCompareExchange(&g_styleEditBusy, 1, 0) != 0) return false;
    DevToolsStyleEdit::Request request;
    request.wire = static_cast<unsigned long long>(g_selWire);
    request.pid = GetCurrentProcessId();
    request.action = action;
    if (action == DevToolsStyleEdit::Action::Copy) {
        request.allOfType = g_styleUi.allOfType;
        request.key = g_styleUi.allOfType ? std::wstring() : DevToolsStyleEdit::Trim(g_styleUi.key);
        if (g_styleUi.inDocument) request.into = ResolveSourcePath(g_selSourceFile);
    }
    StyleEditAsk* ask = new StyleEditAsk{ request, g_route1Gen, g_cliFn };
    g_styleUi.busy = true;
    g_styleUi.haveOutcome = false;
    g_styleUi.busyText = action == DevToolsStyleEdit::Action::Copy ? L"Copying the Style\u2026" : L"Finding the Style\u2026";
    HANDLE h = CreateThread(nullptr, 0, DevToolsThreadEntry<StyleEditWorker>, ask, 0, nullptr);
    if (!h) {
        delete ask;
        InterlockedExchange(&g_styleEditBusy, 0);
        g_styleUi.busy = false;
        g_styleUi.haveOutcome = true;
        g_styleUi.outcome = DevToolsStyleEdit::Outcome{};
        g_styleUi.outcome.message = DevToolsStyleEdit::RunnerFailure(-2);
        RequestStyleRedraw();
        return true;
    }
    CloseHandle(h);
    RequestStyleRedraw();
    return true;
}

// WinUI's own control styles arrive through XamlControlsResources as implicit styles, so a
// "Style" value source doesn't say whose Style it is; Edit current asks the CLI for that.
static bool StyleRowsMention(const std::vector<DevToolsCardRow>& rows)
{
    for (const DevToolsCardRow& r : rows) {
        if (r.source == L"Style" || r.source == L"Built-in style" || r.name == L"Style") return true;
        if (StyleRowsMention(r.children)) return true;
    }
    return false;
}

// The STYLE card's markup; empty when the element shows no sign of having a Style to edit.
static std::wstring BuildStyleSection(const std::wstring& type, const std::vector<DevToolsCardRow>& rows)
{
    if (g_styleUi.wire != g_selWire) g_styleUi = StyleEditUi{ g_selWire };
    if (!g_selWire || type.empty() || !StyleRowsMention(rows)) return L"";

    const std::wstring shortType = ShortType(type);
    const std::wstring hint = L"Open the Style this " + shortType + L" uses, or edit a copy of it.";
    const std::wstring documentPath = ResolveSourcePath(g_selSourceFile);
    const std::wstring documentName = DevToolsStyleEdit::FileName(documentPath);
    const bool offerDocument = !documentPath.empty() && _wcsicmp(documentName.c_str(), L"App.xaml") != 0;
    if (!offerDocument) g_styleUi.inDocument = false;
    const auto collapsed = [](bool visible) { return visible ? L"" : L" Visibility=\"Collapsed\""; };
    const auto checked = [](bool on) { return on ? L" IsChecked=\"True\"" : L""; };
    const wchar_t* caption = L" Style=\"{StaticResource CaptionTextBlockStyle}\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"";

    std::wstring result;
    if (g_styleUi.busy) result = g_styleUi.busyText;
    else if (g_styleUi.haveOutcome) result = g_styleUi.outcome.message + (g_styleUi.opened.empty() ? L"" : L" " + g_styleUi.opened);
    const bool showOpen = !g_styleUi.busy && g_styleUi.haveOutcome && !g_styleUi.outcome.openLabel.empty();

    std::wstring m;
    m += L"<Border Background=\"{ThemeResource ControlFillColorSecondaryBrush}\" CornerRadius=\"4\" Padding=\"10,8,10,10\""
         L" Margin=\"0,6,0,0\" AutomationProperties.AutomationId=\"WinAppDevToolsStyle\">";
    m += L"<StackPanel Spacing=\"6\">";
    m += L"<TextBlock Text=\"STYLE\" CharacterSpacing=\"60\"" + std::wstring(caption) + L"/>";
    m += L"<TextBlock Text=\"" + DevToolsXmlEscape(hint) + L"\" TextWrapping=\"Wrap\""
         L" Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleHint\"/>";
    m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"8\">";
    m += L"<Button x:Name=\"StyleEditCurrent\" Content=\"Edit current\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleEditCurrent\""
         L" ToolTipService.ToolTip=\"Find the Style this element uses and open it when it is in your app\"/>";
    m += L"<Button x:Name=\"StyleEditCopy\" Content=\"Edit a copy\u2026\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleEditCopy\""
         L" ToolTipService.ToolTip=\"Copy the Style into your XAML so you can change its setters and template\"/>";
    m += L"</StackPanel>";

    m += L"<StackPanel x:Name=\"StyleForm\" Spacing=\"4\" Margin=\"0,4,0,0\"" + std::wstring(collapsed(g_styleUi.formOpen)) +
         L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleForm\">";
    m += L"<TextBlock Text=\"Name (key)\"" + std::wstring(caption) + L"/>";
    m += L"<RadioButton x:Name=\"StyleKeyed\" GroupName=\"WinAppStyleScope\"" + std::wstring(checked(!g_styleUi.allOfType)) +
         L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleKeyed\" AutomationProperties.Name=\"Name the copy\">";
    m += L"<TextBox x:Name=\"StyleKey\" MinWidth=\"180\" PlaceholderText=\"" + DevToolsXmlEscape(shortType) + L"Style1\""
         L" Text=\"" + DevToolsXmlEscape(g_styleUi.key) + L"\"" + std::wstring(g_styleUi.allOfType ? L" IsEnabled=\"False\"" : L"") +
         L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleKey\" AutomationProperties.Name=\"Style key\""
         L" ToolTipService.ToolTip=\"Leave empty to use the next free " + DevToolsXmlEscape(shortType) + L"StyleN\"/>";
    m += L"</RadioButton>";
    m += L"<RadioButton x:Name=\"StyleAll\" GroupName=\"WinAppStyleScope\" Content=\"Apply to all " + DevToolsXmlEscape(shortType) + L"\"" +
         std::wstring(checked(g_styleUi.allOfType)) + L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleAll\"/>";
    m += L"<TextBlock Text=\"Define in\" Margin=\"0,4,0,0\"" + std::wstring(caption) + L"/>";
    m += L"<RadioButton x:Name=\"StyleInApp\" GroupName=\"WinAppStyleWhere\" Content=\"Application (App.xaml)\"" +
         std::wstring(checked(!g_styleUi.inDocument)) + L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleInApp\"/>";
    if (offerDocument) {
        m += L"<RadioButton x:Name=\"StyleInDoc\" GroupName=\"WinAppStyleWhere\" Content=\"This document (" + DevToolsXmlEscape(documentName) + L")\"" +
             std::wstring(checked(g_styleUi.inDocument)) + L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleInDocument\"/>";
    }
    m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"8\" Margin=\"0,4,0,0\">";
    m += L"<Button x:Name=\"StyleOk\" Content=\"OK\" MinWidth=\"72\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleOk\"/>";
    m += L"<Button x:Name=\"StyleCancel\" Content=\"Cancel\" MinWidth=\"72\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleCancel\"/>";
    m += L"</StackPanel>";
    m += L"</StackPanel>";

    m += L"<TextBlock x:Name=\"StyleResult\" Text=\"" + DevToolsXmlEscape(result) + L"\" TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\""
         L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"" + std::wstring(collapsed(!result.empty())) +
         L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleResult\"/>";
    m += L"<HyperlinkButton x:Name=\"StyleOpen\" Padding=\"0\" Content=\"" +
         DevToolsXmlEscape(showOpen ? g_styleUi.outcome.openLabel : std::wstring()) + L"\"" + std::wstring(collapsed(showOpen)) +
         L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleOpen\"/>";
    m += L"</StackPanel></Border>";
    return m;
}

template <class T, class Find>
static T StyleEditFind(Find& findByName, const wchar_t* name)
{
    IInspectable* found = findByName(name);
    if (!found) return nullptr;
    winrt::Windows::Foundation::IInspectable object{ nullptr };
    winrt::copy_from_abi(object, found);
    found->Release();
    return object.try_as<T>();
}

// Wires the card BuildStyleSection emitted. Handlers hold weak references so the pane they live in can go away.
template <class Find>
static void WireStyleSection(Find& findByName)
{
    const auto editCurrent = StyleEditFind<DevToolsXC::Button>(findByName, L"StyleEditCurrent");
    if (!editCurrent) return;
    const auto editCopy = StyleEditFind<DevToolsXC::Button>(findByName, L"StyleEditCopy");
    const auto form = StyleEditFind<DevToolsX::UIElement>(findByName, L"StyleForm");
    const auto keyed = StyleEditFind<DevToolsXC::RadioButton>(findByName, L"StyleKeyed");
    const auto all = StyleEditFind<DevToolsXC::RadioButton>(findByName, L"StyleAll");
    const auto key = StyleEditFind<DevToolsXC::TextBox>(findByName, L"StyleKey");
    const auto inApp = StyleEditFind<DevToolsXC::RadioButton>(findByName, L"StyleInApp");
    const auto inDoc = StyleEditFind<DevToolsXC::RadioButton>(findByName, L"StyleInDoc");
    const auto ok = StyleEditFind<DevToolsXC::Button>(findByName, L"StyleOk");
    const auto cancel = StyleEditFind<DevToolsXC::Button>(findByName, L"StyleCancel");
    const auto result = StyleEditFind<DevToolsXC::TextBlock>(findByName, L"StyleResult");
    const auto open = StyleEditFind<DevToolsXC::HyperlinkButton>(findByName, L"StyleOpen");
    if (!editCopy || !form || !keyed || !all || !key || !inApp || !ok || !cancel || !result || !open) {
        DevToolsOverlayLog(L"route1.wire STYLE card MISS -- the markup and the wiring have drifted");
        return;
    }
    const unsigned gen = g_route1Gen;
    const InstanceHandle wire = g_selWire;
    const bool idle = !g_styleUi.busy;
    editCurrent.IsEnabled(idle); editCopy.IsEnabled(idle); ok.IsEnabled(idle);

    const winrt::weak_ref<DevToolsX::UIElement> formRef{ form };
    const winrt::weak_ref<DevToolsXC::Button> currentRef{ editCurrent }, copyRef{ editCopy }, okRef{ ok };
    const winrt::weak_ref<DevToolsXC::TextBox> keyRef{ key };
    const auto live = [gen, wire] { return gen == g_route1Gen && wire == g_selWire && wire == g_styleUi.wire; };
    // Buttons go disabled in place so a second click can't start another run; the busy line comes from the timer's
    // redraw, since rebuilding the pane from inside its own button's Click would tear the button down.
    const auto showBusy = [currentRef, copyRef, okRef] {
        for (const auto& b : { currentRef.get(), copyRef.get(), okRef.get() }) if (b) b.IsEnabled(false);
    };
    // Kept in g_styleUi and redrawn from the timer: a TextBlock changed in place from inside a Click isn't shown.
    const auto say = [](const std::wstring& text) {
        g_styleUi.haveOutcome = true;
        g_styleUi.outcome = DevToolsStyleEdit::Outcome{};
        g_styleUi.outcome.message = text;
        g_styleUi.opened.clear();
        RequestStyleRedraw();
    };

    editCurrent.Click([live, showBusy, say](auto&&, auto&&) {
        if (!live()) return;
        if (PostStyleEdit(DevToolsStyleEdit::Action::Locate)) showBusy();
        else say(L"winapp is still working on the last request.");
    });
    editCopy.Click([live, formRef, keyRef](auto&&, auto&&) {
        if (!live()) return;
        g_styleUi.formOpen = !g_styleUi.formOpen;
        if (auto f = formRef.get()) f.Visibility(g_styleUi.formOpen ? DevToolsX::Visibility::Visible : DevToolsX::Visibility::Collapsed);
        if (g_styleUi.formOpen && !g_styleUi.allOfType) {
            if (auto k = keyRef.get()) k.Focus(DevToolsX::FocusState::Programmatic);
        }
    });
    cancel.Click([live, formRef](auto&&, auto&&) {
        if (!live()) return;
        g_styleUi.formOpen = false;
        if (auto f = formRef.get()) f.Visibility(DevToolsX::Visibility::Collapsed);
    });
    key.TextChanged([live](auto&& sender, auto&&) {
        if (live()) g_styleUi.key = sender.template as<DevToolsXC::TextBox>().Text().c_str();
    });
    keyed.Checked([live, keyRef](auto&&, auto&&) {
        if (!live()) return;
        g_styleUi.allOfType = false;
        if (auto k = keyRef.get()) k.IsEnabled(true);
    });
    all.Checked([live, keyRef](auto&&, auto&&) {
        if (!live()) return;
        g_styleUi.allOfType = true;
        if (auto k = keyRef.get()) k.IsEnabled(false);
    });
    inApp.Checked([live](auto&&, auto&&) { if (live()) g_styleUi.inDocument = false; });
    if (inDoc) inDoc.Checked([live](auto&&, auto&&) { if (live()) g_styleUi.inDocument = true; });
    ok.Click([live, showBusy, say](auto&&, auto&&) {
        if (!live()) return;
        const std::wstring k = DevToolsStyleEdit::Trim(g_styleUi.key);
        if (!g_styleUi.allOfType && !k.empty() && !DevToolsStyleEdit::ValidKey(k)) {
            say(L"\"" + k + L"\" can't be used as a key. Use letters, digits, '_', '-' and '.', starting with a letter or '_'.");
            return;
        }
        if (PostStyleEdit(DevToolsStyleEdit::Action::Copy)) showBusy();
        else say(L"winapp is still working on the last request.");
    });
    open.Click([live](auto&&, auto&&) {
        if (!live() || g_styleUi.outcome.openPath.empty()) return;
        g_styleUi.opened = StyleEditOpenFile(g_styleUi.outcome.openPath, g_styleUi.outcome.openLine);
        RequestStyleRedraw();
    });
}
