// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
// Part of DevToolsWindow.cpp; included into that translation unit, not compiled on its own.

// Blend's "Edit Style / Edit Template > Edit Current / Edit a Copy...". The menu is on a tree row's context menu and
// on the pane's STYLE card. Both actions run `winapp devtools resources copy-style` on a worker thread (the CLI
// connects back to this app, so the UI thread must keep pumping), open the XAML file that holds the Style, and then
// watch that file: each save re-reads the Style and applies it to the elements that use it, so the running app shows
// the edit without a rebuild.

static DevToolsWindowCliFn g_cliFn;
static IInspectable* ResolveLiveElement(InstanceHandle wire); // DevToolsWindow.LiveProps.inc

enum class StyleIntent { Current, Preview, Copy };

struct StyleEditUi {
    InstanceHandle wire = 0;
    bool busy = false;
    std::wstring message; // the last thing an action said, or what it is doing now
};
static StyleEditUi g_styleUi;

struct StyleEditAsk { DevToolsStyleEdit::Request request; StyleIntent intent; bool templ; unsigned gen; DevToolsWindowCliFn run; };
struct StyleEditDone { DevToolsStyleEdit::Request request; StyleIntent intent; bool templ; unsigned gen; DevToolsStyleEdit::Outcome outcome; };
static SRWLOCK                    g_styleEditLock = SRWLOCK_INIT;
static std::vector<StyleEditDone> g_styleEditDone;
static UINT_PTR                   g_styleEditTimer = 0;
static volatile LONG              g_styleEditBusy = 0;
static bool                       g_styleEditRedraw = false;

// The Style being edited live: the file it is in, how to find it there, and the elements it is applied to on save.
struct StyleLive {
    bool active = false;
    std::wstring path;
    std::wstring key;        // empty: the implicit Style for targetType
    std::wstring targetType;
    std::vector<winrt::weak_ref<DevToolsX::FrameworkElement>> targets;
    ULONGLONG applied = 0;   // the file's write time last applied
    ULONGLONG seen = 0;      // a newer write time, applied once it holds still for a tick
    std::wstring status;
    bool failed = false;
};
static StyleLive g_styleLive;
static UINT_PTR  g_styleLiveTimer = 0;

// The open "Create Style Resource" dialog.
struct StyleDialog {
    DevToolsXC::ContentDialog dialog{ nullptr };
    DevToolsXC::TextBox key{ nullptr };
    DevToolsXC::RadioButton keyed{ nullptr }, all{ nullptr }, inApp{ nullptr }, inDoc{ nullptr };
    DevToolsXC::TextBlock source{ nullptr }, error{ nullptr };
    DevToolsXC::ProgressRing ring{ nullptr };
    InstanceHandle wire = 0;
    bool templ = false;
    bool busy = false;
    std::wstring documentPath;
};
static StyleDialog g_styleDialog;

static void RequestStyleRedraw();
static void ShowCopyDialog(InstanceHandle wire, bool templ);

static bool StyleChecked(const DevToolsXCP::ToggleButton& b)
{
    const auto v = b ? b.IsChecked() : nullptr;
    return v && v.Value();
}

static DevToolsX::FrameworkElement StyleElement(InstanceHandle wire)
{
    IInspectable* raw = wire ? ResolveLiveElement(wire) : nullptr;
    if (!raw) return nullptr;
    winrt::Windows::Foundation::IInspectable object{ nullptr };
    winrt::attach_abi(object, raw);
    return object.try_as<DevToolsX::FrameworkElement>();
}

static std::wstring StyleNodeType(InstanceHandle wire)
{
    for (const auto& n : g_allTreeNodes) if (n.handle == wire) return n.type;
    return L"";
}

static void SayStyle(InstanceHandle wire, const std::wstring& text)
{
    if (g_styleUi.wire != wire) return;
    g_styleUi.busy = false;
    g_styleUi.message = text;
    RequestStyleRedraw();
}

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
    DevToolsOverlayLog(L"route1.style open outcome=%d path='%s' line=%u", (int)r.outcome, path.c_str(), line);
    std::wstring said = DevToolsShellOpen::Describe(r, DevToolsStyleEdit::FileName(path), line, path, L"");
    if (r.outcome == DevToolsShellOpen::Outcome::Handed && line > 0) said += L" Go to line " + std::to_wstring(line) + L".";
    if (!said.empty()) said[0] = static_cast<wchar_t>(towupper(said[0]));
    return said;
}

static bool StyleReadFile(const std::wstring& path, std::string& bytes, ULONGLONG* written = nullptr)
{
    HANDLE h = CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
                           nullptr, OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, nullptr);
    if (h == INVALID_HANDLE_VALUE) return false;
    LARGE_INTEGER size{};
    bool ok = GetFileSizeEx(h, &size) && size.QuadPart < 64ll * 1024 * 1024;
    if (ok) {
        bytes.resize(static_cast<size_t>(size.QuadPart));
        DWORD read = 0;
        ok = bytes.empty() || (ReadFile(h, bytes.data(), static_cast<DWORD>(bytes.size()), &read, nullptr) && read == bytes.size());
    }
    if (ok && written) {
        FILETIME ft{};
        GetFileTime(h, nullptr, nullptr, &ft);
        *written = (static_cast<ULONGLONG>(ft.dwHighDateTime) << 32) | ft.dwLowDateTime;
    }
    CloseHandle(h);
    return ok;
}

static ULONGLONG StyleWriteTime(const std::wstring& path)
{
    WIN32_FILE_ATTRIBUTE_DATA a{};
    if (!GetFileAttributesExW(path.c_str(), GetFileExInfoStandard, &a)) return 0;
    return (static_cast<ULONGLONG>(a.ftLastWriteTime.dwHighDateTime) << 32) | a.ftLastWriteTime.dwLowDateTime;
}

static DevToolsStyleSource::Found StyleFind(const std::wstring& path, const std::wstring& key, const std::wstring& targetType,
                                            ULONGLONG* written = nullptr)
{
    std::string bytes;
    if (!StyleReadFile(path, bytes, written)) {
        DevToolsStyleSource::Found f;
        f.error = L"Can't read " + DevToolsStyleEdit::FileName(path) + L".";
        return f;
    }
    return DevToolsStyleSource::Extract(bytes, key, targetType);
}

static std::wstring StyleClock()
{
    SYSTEMTIME t{};
    GetLocalTime(&t);
    wchar_t buf[16]{};
    swprintf_s(buf, L"%02u:%02u:%02u", t.wHour, t.wMinute, t.wSecond);
    return buf;
}

static std::wstring StyleLiveName()
{
    return g_styleLive.key.empty() ? L"implicit " + DevToolsStyleSource::ShortName(g_styleLive.targetType) + L" style"
                                   : g_styleLive.key;
}

// Re-reads the Style from its file and applies it to every target. Leaves the app untouched when the file doesn't parse.
static void ApplyStyleLive()
{
    ULONGLONG written = 0;
    const DevToolsStyleSource::Found f = StyleFind(g_styleLive.path, g_styleLive.key, g_styleLive.targetType, &written);
    g_styleLive.applied = written ? written : StyleWriteTime(g_styleLive.path);
    g_styleLive.seen = g_styleLive.applied;
    g_styleLive.failed = true;
    if (!f.ok) { g_styleLive.status = f.error; return; }

    std::wstring markup = f.markup;
    std::vector<std::wstring> skipped;
    std::wstring error;
    const auto load = [&]() -> DevToolsX::Style {
        IInspectable* raw = nullptr;
        error.clear();
        const HRESULT hr = LoadMarkupRaw(markup, &raw, &error);
        if (FAILED(hr) || !raw) {
            if (raw) raw->Release();
            DevToolsOverlayLog(L"route1.style live parse hr=0x%08X '%s'", (unsigned)hr, error.c_str());
            return nullptr;
        }
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::attach_abi(object, raw);
        return object.try_as<DevToolsX::Style>();
    };
    DevToolsX::Style style = load();
    // An attached property the app's compiled XAML never used (AnimatedIcon.State in WinUI's Button template) is
    // unknown to XamlReader until the app is rebuilt; preview without it rather than not at all.
    for (int round = 0; !style && round < 8; ++round) {
        std::wstring type, member;
        if (!DevToolsStyleSource::UnknownAttachable(error, &type, &member)) break;
        std::wstring next = DevToolsStyleSource::WithoutAttachable(markup, type, member);
        if (next.empty()) break;
        markup = std::move(next);
        skipped.push_back(type + L"." + member);
        style = load();
    }
    if (!style) {
        const unsigned line = DevToolsStyleSource::FileLine(error, f.line);
        // XamlReader's "[Line: N Position: M]" counts in the extracted Style, not the file; the file line leads instead.
        if (line) {
            const auto at = error.rfind(L" [Line: ");
            if (at != std::wstring::npos) error.erase(at);
        }
        g_styleLive.status = (line ? DevToolsStyleEdit::FileName(g_styleLive.path) + L" line " + std::to_wstring(line) + L": " : std::wstring()) +
                             (error.empty() ? L"XAML couldn't load the Style in " + DevToolsStyleEdit::FileName(g_styleLive.path) + L"." : error);
        return;
    }

    size_t applied = 0;
    std::wstring refused;
    for (const auto& target : g_styleLive.targets) {
        const auto element = target.get();
        if (!element) continue;
        try { element.Style(style); ++applied; }
        catch (const winrt::hresult_error& e) { if (refused.empty()) refused = e.message().c_str(); }
    }
    DevToolsOverlayLog(L"route1.style live applied %zu of %zu", applied, g_styleLive.targets.size());
    if (!applied) {
        g_styleLive.status = refused.empty() ? L"The elements that used this Style are gone." : L"The app refused the Style: " + refused;
        return;
    }
    g_styleLive.failed = false;
    g_styleLive.status = L"Updated " + StyleClock() + L" \u00B7 " + std::to_wstring(applied) +
                         (applied == 1 ? L" element" : L" elements");
    if (!skipped.empty()) {
        std::wstring list;
        for (const auto& s : skipped) list += (list.empty() ? L"" : L", ") + s;
        g_styleLive.status += L" \u00B7 previewed without " + list + L" until the app is rebuilt";
    }
}

static void StopStyleLive()
{
    if (g_styleLiveTimer) { KillTimer(nullptr, g_styleLiveTimer); g_styleLiveTimer = 0; }
    g_styleLive = StyleLive{};
}

static void CALLBACK StyleLiveTimerProc(HWND, UINT, UINT_PTR, DWORD)
{
    if (!g_styleLive.active || !DevToolsWindow_IsOpen()) { StopStyleLive(); return; }
    const ULONGLONG now = StyleWriteTime(g_styleLive.path);
    if (!now || now == g_styleLive.applied) return;
    // Editors save in more than one write; apply once the time stays put for a tick.
    if (now != g_styleLive.seen) { g_styleLive.seen = now; return; }
    ApplyStyleLive();
    RequestStyleRedraw();
}

// The elements a Style edit should reach: just the element, or every element of its type that shares its Style
// (the same keyed Style object, or none, which is how an implicit or default Style shows).
static std::vector<winrt::weak_ref<DevToolsX::FrameworkElement>> StyleTargets(InstanceHandle wire, bool shared)
{
    std::vector<winrt::weak_ref<DevToolsX::FrameworkElement>> out;
    const auto selected = StyleElement(wire);
    if (!selected) return out;
    out.emplace_back(selected);
    if (!shared) return out;
    const std::wstring type = StyleNodeType(wire);
    const auto original = selected.Style();
    for (const auto& n : g_allTreeNodes) {
        if (out.size() >= 500) break;
        if (n.handle == wire || n.type != type) continue;
        const auto element = StyleElement(n.handle);
        if (element && element.Style() == original) out.emplace_back(element);
    }
    return out;
}

static void StartStyleLive(InstanceHandle wire, const std::wstring& path, const std::wstring& key,
                           const std::wstring& targetType, bool shared)
{
    StopStyleLive();
    g_styleLive.active = true;
    g_styleLive.path = path;
    g_styleLive.key = key;
    g_styleLive.targetType = targetType;
    g_styleLive.targets = StyleTargets(wire, shared);
    ApplyStyleLive();
    g_styleLiveTimer = SetTimer(nullptr, 0, 400, &StyleLiveTimerProc);
    DevToolsOverlayLog(L"route1.style live start '%s' key='%s' type='%s' targets=%zu",
                       path.c_str(), key.c_str(), targetType.c_str(), g_styleLive.targets.size());
}

static unsigned StyleOpenLine(const DevToolsStyleSource::Found& f, unsigned fallback, bool templ)
{
    if (!f.ok) return fallback;
    return templ && f.templateLine ? f.templateLine : f.line;
}

// ---- runs ---------------------------------------------------------------------------------------------------------

static DWORD WINAPI StyleEditWorker(LPVOID param)
{
    StyleEditAsk* ask = reinterpret_cast<StyleEditAsk*>(param);
    std::wstring out;
    const int code = ask->run
        ? ask->run(DevToolsStyleEdit::Arguments(ask->request), ask->request.action == DevToolsStyleEdit::Action::Copy, &out)
        : -1;
    StyleEditDone d{ ask->request, ask->intent, ask->templ, ask->gen, DevToolsStyleEdit::Interpret(ask->request, code, out) };
    AcquireSRWLockExclusive(&g_styleEditLock);
    g_styleEditDone.push_back(std::move(d));
    ReleaseSRWLockExclusive(&g_styleEditLock);
    InterlockedExchange(&g_styleEditBusy, 0);
    delete ask;
    return 0;
}

static void CALLBACK StyleEditTimerProc(HWND, UINT, UINT_PTR id, DWORD);

static void StartStyleEditTimer()
{
    if (!g_styleEditTimer) g_styleEditTimer = SetTimer(nullptr, 0, 100, &StyleEditTimerProc);
}

// Redraws the card from g_styleUi on the next timer tick, outside whatever event changed it.
static void RequestStyleRedraw()
{
    g_styleEditRedraw = true;
    StartStyleEditTimer();
}

// Starts one copy-style run. False when one is already running.
static bool PostStyleEdit(const DevToolsStyleEdit::Request& request, StyleIntent intent, bool templ)
{
    if (InterlockedCompareExchange(&g_styleEditBusy, 1, 0) != 0) return false;
    StyleEditAsk* ask = new StyleEditAsk{ request, intent, templ, g_route1Gen, g_cliFn };
    HANDLE h = CreateThread(nullptr, 0, DevToolsThreadEntry<StyleEditWorker>, ask, 0, nullptr);
    if (!h) {
        delete ask;
        InterlockedExchange(&g_styleEditBusy, 0);
        return false;
    }
    CloseHandle(h);
    StartStyleEditTimer();
    return true;
}

static DevToolsStyleEdit::Request StyleRequest(InstanceHandle wire, DevToolsStyleEdit::Action action)
{
    DevToolsStyleEdit::Request r;
    r.wire = static_cast<unsigned long long>(wire);
    r.pid = GetCurrentProcessId();
    r.action = action;
    return r;
}

// ---- the dialogs ----------------------------------------------------------------------------------------------------

static DevToolsX::XamlRoot InspectorXamlRoot()
{
    if (!g_inspectorContent) return nullptr;
    winrt::Windows::Foundation::IInspectable content{ nullptr };
    winrt::copy_from_abi(content, g_inspectorContent);
    const auto element = content.try_as<DevToolsX::UIElement>();
    return element ? element.XamlRoot() : nullptr;
}

static DevToolsXC::ContentDialog NewStyleDialog(const std::wstring& title)
{
    DevToolsXC::ContentDialog d;
    d.XamlRoot(InspectorXamlRoot());
    try {
        const auto style = DevToolsX::Application::Current().Resources().TryLookup(winrt::box_value(L"DefaultContentDialogStyle"));
        if (const auto s = style.try_as<DevToolsX::Style>()) d.Style(s);
    } catch (const winrt::hresult_error&) {}
    d.Title(winrt::box_value(winrt::hstring(title)));
    d.DefaultButton(DevToolsXC::ContentDialogButton::Primary);
    return d;
}

static void ShowStyleDialog(const DevToolsXC::ContentDialog& d)
{
    try { d.ShowAsync(); }
    catch (const winrt::hresult_error& e) { DevToolsOverlayLog(L"route1.style dialog failed: %s", e.message().c_str()); }
}

static void StyleDialogError(const std::wstring& text)
{
    if (!g_styleDialog.error) return;
    g_styleDialog.error.Text(text);
    g_styleDialog.error.Visibility(text.empty() ? DevToolsX::Visibility::Collapsed : DevToolsX::Visibility::Visible);
}

static void StyleDialogBusy(bool busy)
{
    g_styleDialog.busy = busy;
    if (!g_styleDialog.dialog) return;
    g_styleDialog.ring.IsActive(busy);
    g_styleDialog.ring.Visibility(busy ? DevToolsX::Visibility::Visible : DevToolsX::Visibility::Collapsed);
    g_styleDialog.dialog.IsPrimaryButtonEnabled(!busy);
    g_styleDialog.key.IsEnabled(!busy && !StyleChecked(g_styleDialog.all));
    for (const auto& r : { g_styleDialog.keyed, g_styleDialog.all, g_styleDialog.inApp, g_styleDialog.inDoc }) if (r) r.IsEnabled(!busy);
}

// Blend's "Create Style Resource": name the copy (or apply it to every element of the type) and pick where it goes.
static void ShowCopyDialog(InstanceHandle wire, bool templ)
{
    if (g_styleDialog.dialog) return;
    const std::wstring shortType = ShortType(StyleNodeType(wire));
    const std::wstring documentPath = wire == g_selWire ? ResolveSourcePath(g_selSourceFile) : std::wstring();
    const std::wstring documentName = DevToolsStyleEdit::FileName(documentPath);
    const bool offerDocument = !documentPath.empty() && _wcsicmp(documentName.c_str(), L"App.xaml") != 0;

    std::wstring m = L"<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
                     L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Spacing=\"6\" MinWidth=\"360\">";
    m += L"<TextBlock x:Name=\"Source\" TextWrapping=\"Wrap\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\""
         L" Text=\"Finding the Style this " + DevToolsXmlEscape(shortType) + L" uses\u2026\" Margin=\"0,0,0,6\"/>";
    m += L"<TextBlock Text=\"Name (Key)\" Style=\"{ThemeResource BodyStrongTextBlockStyle}\"/>";
    m += L"<RadioButton x:Name=\"Keyed\" GroupName=\"WinAppStyleScope\" IsChecked=\"True\" AutomationProperties.Name=\"Name the copy\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleKeyed\">";
    m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"8\">";
    m += L"<TextBox x:Name=\"Key\" Width=\"240\" IsEnabled=\"False\" PlaceholderText=\"" + DevToolsXmlEscape(shortType) + L"Style1\""
         L" AutomationProperties.Name=\"Style key\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleKey\"/>";
    m += L"<ProgressRing x:Name=\"Ring\" IsActive=\"True\" Width=\"16\" Height=\"16\"/>";
    m += L"</StackPanel></RadioButton>";
    m += L"<RadioButton x:Name=\"All\" GroupName=\"WinAppStyleScope\" Content=\"Apply to all " + DevToolsXmlEscape(shortType) + L" elements\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleAll\"/>";
    m += L"<TextBlock Text=\"Define in\" Style=\"{ThemeResource BodyStrongTextBlockStyle}\" Margin=\"0,8,0,0\"/>";
    m += L"<RadioButton x:Name=\"InApp\" GroupName=\"WinAppStyleWhere\" Content=\"Application (App.xaml)\" IsChecked=\"True\""
         L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleInApp\"/>";
    if (offerDocument) {
        m += L"<RadioButton x:Name=\"InDoc\" GroupName=\"WinAppStyleWhere\" Content=\"This document (" + DevToolsXmlEscape(documentName) + L")\""
             L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleInDocument\"/>";
    }
    m += L"<TextBlock x:Name=\"Error\" TextWrapping=\"Wrap\" Visibility=\"Collapsed\" Margin=\"0,6,0,0\""
         L" Foreground=\"{ThemeResource SystemFillColorCriticalBrush}\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleError\"/>";
    m += L"</StackPanel>";

    IInspectable* raw = nullptr;
    std::wstring error;
    if (FAILED(LoadMarkupRaw(m, &raw, &error)) || !raw) {
        DevToolsOverlayLog(L"route1.style dialog markup failed: %s", error.c_str());
        SayStyle(wire, L"Couldn't open the dialog: " + error);
        return;
    }
    winrt::Windows::Foundation::IInspectable object{ nullptr };
    winrt::attach_abi(object, raw);
    const auto panel = object.as<DevToolsX::FrameworkElement>();
    const auto find = [&](const wchar_t* name) { return panel.FindName(name); };

    StyleDialog s;
    s.wire = wire;
    s.templ = templ;
    s.documentPath = offerDocument ? documentPath : std::wstring();
    s.source = find(L"Source").as<DevToolsXC::TextBlock>();
    s.key = find(L"Key").as<DevToolsXC::TextBox>();
    s.ring = find(L"Ring").as<DevToolsXC::ProgressRing>();
    s.keyed = find(L"Keyed").as<DevToolsXC::RadioButton>();
    s.all = find(L"All").as<DevToolsXC::RadioButton>();
    s.inApp = find(L"InApp").as<DevToolsXC::RadioButton>();
    if (offerDocument) s.inDoc = find(L"InDoc").as<DevToolsXC::RadioButton>();
    s.error = find(L"Error").as<DevToolsXC::TextBlock>();

    s.dialog = NewStyleDialog(L"Create Style Resource");
    s.dialog.Content(panel);
    s.dialog.PrimaryButtonText(L"OK");
    s.dialog.CloseButtonText(L"Cancel");
    s.dialog.IsPrimaryButtonEnabled(false);

    s.keyed.Checked([](auto&&, auto&&) { if (g_styleDialog.key) g_styleDialog.key.IsEnabled(!g_styleDialog.busy); });
    s.all.Checked([](auto&&, auto&&) { if (g_styleDialog.key) g_styleDialog.key.IsEnabled(false); });
    s.dialog.PrimaryButtonClick([](auto&&, DevToolsXC::ContentDialogButtonClickEventArgs const& args) {
        args.Cancel(true);
        if (g_styleDialog.busy) return;
        const bool allOfType = StyleChecked(g_styleDialog.all);
        const std::wstring key = DevToolsStyleEdit::Trim(g_styleDialog.key.Text().c_str());
        if (!allOfType && !key.empty() && !DevToolsStyleEdit::ValidKey(key)) {
            StyleDialogError(L"\"" + key + L"\" can't be a key. Use letters, digits, '_', '-' and '.', starting with a letter or '_'.");
            g_styleDialog.key.Focus(DevToolsX::FocusState::Programmatic);
            return;
        }
        auto request = StyleRequest(g_styleDialog.wire, DevToolsStyleEdit::Action::Copy);
        request.allOfType = allOfType;
        request.key = allOfType ? std::wstring() : key;
        if (g_styleDialog.inDoc && StyleChecked(g_styleDialog.inDoc)) request.into = g_styleDialog.documentPath;
        StyleDialogError(L"");
        if (!PostStyleEdit(request, StyleIntent::Copy, g_styleDialog.templ)) {
            StyleDialogError(L"winapp is still working on the last request. Try again in a moment.");
            return;
        }
        StyleDialogBusy(true);
    });
    // Cancel can't abandon a copy that is being written.
    s.dialog.Closing([](auto&&, DevToolsXC::ContentDialogClosingEventArgs const& args) {
        if (g_styleDialog.busy && args.Result() != DevToolsXC::ContentDialogResult::Primary) args.Cancel(true);
    });
    s.dialog.Closed([](auto&&, auto&&) { g_styleDialog = StyleDialog{}; });

    g_styleDialog = s;
    ShowStyleDialog(g_styleDialog.dialog);

    // Ask winapp which Style this is and what it would call the copy, the way Blend proposed "ButtonStyle1".
    g_styleDialog.busy = true;
    if (!PostStyleEdit(StyleRequest(wire, DevToolsStyleEdit::Action::Locate), StyleIntent::Preview, templ)) {
        g_styleDialog.source.Text(L"Copies the Style this " + shortType + L" uses.");
        StyleDialogBusy(false);
    }
}

// Edit Current on a Style WinUI owns: there is no file of yours to open, so offer the copy instead.
static void ShowBuiltInDialog(InstanceHandle wire, bool templ, const DevToolsStyleEdit::Outcome& o)
{
    const auto d = NewStyleDialog(templ ? L"Edit Template" : L"Edit Style");
    DevToolsXC::TextBlock text;
    text.TextWrapping(DevToolsX::TextWrapping::Wrap);
    text.MaxWidth(420);
    text.Text(L"This " + ShortType(StyleNodeType(wire)) + L" uses " + o.sourceName + L", which is built into WinUI, "
              L"so there is no file of yours to open. Edit a copy to change it.");
    d.Content(text);
    d.PrimaryButtonText(L"Edit a Copy\u2026");
    d.CloseButtonText(L"Cancel");
    auto copy = std::make_shared<bool>(false);
    d.PrimaryButtonClick([copy](auto&&, auto&&) { *copy = true; });
    d.Closed([copy, wire, templ](auto&&, auto&&) { if (*copy) ShowCopyDialog(wire, templ); });
    ShowStyleDialog(d);
}

// ---- answers --------------------------------------------------------------------------------------------------------

static void StylePreviewArrived(const StyleEditDone& d)
{
    if (!g_styleDialog.dialog || g_styleDialog.wire != d.request.wire) return;
    const auto& o = d.outcome;
    if (o.ok) {
        g_styleDialog.source.Text(L"Copies " + o.sourceName + (o.sourceInApp && !o.sourceFile.empty()
                                  ? L" from " + DevToolsStyleEdit::FileName(o.sourceFile) : std::wstring()) + L".");
        g_styleDialog.key.Text(o.key);
    } else {
        g_styleDialog.source.Text(L"");
        StyleDialogError(o.message);
    }
    StyleDialogBusy(false);
    if (o.ok && !StyleChecked(g_styleDialog.all)) {
        g_styleDialog.key.SelectAll();
        g_styleDialog.key.Focus(DevToolsX::FocusState::Programmatic);
    }
}

static void StyleCopyArrived(const StyleEditDone& d)
{
    const auto& o = d.outcome;
    const bool dialogOpen = g_styleDialog.dialog && g_styleDialog.wire == d.request.wire;
    if (!o.ok) {
        if (dialogOpen) { StyleDialogBusy(false); StyleDialogError(o.message); }
        else SayStyle(d.request.wire, o.message);
        return;
    }
    if (dialogOpen) {
        g_styleDialog.busy = false;
        g_styleDialog.dialog.Hide();
    }
    if (o.copyPath.empty()) { SayStyle(d.request.wire, o.message); return; }
    const std::wstring key = o.implicit ? std::wstring() : o.key;
    const auto found = StyleFind(o.copyPath, key, o.targetType);
    const std::wstring opened = StyleEditOpenFile(o.copyPath, StyleOpenLine(found, o.copyLine, d.templ));
    // A named copy is only on this element; an implicit one reaches every element of the type that had no Style.
    StartStyleLive(d.request.wire, o.copyPath, key, o.targetType, o.implicit);
    SayStyle(d.request.wire, o.message + L" " + opened);
}

static void StyleCurrentArrived(const StyleEditDone& d)
{
    const auto& o = d.outcome;
    if (!o.ok) { SayStyle(d.request.wire, o.message); return; }
    if (!o.sourceInApp) {
        SayStyle(d.request.wire, o.message);
        ShowBuiltInDialog(d.request.wire, d.templ, o);
        return;
    }
    if (o.sourcePath.empty()) {
        SayStyle(d.request.wire, o.message + L" Its file isn't on this machine, so it can't be opened.");
        return;
    }
    const auto found = StyleFind(o.sourcePath, o.sourceKey, o.targetType);
    std::wstring note;
    if (d.templ && found.ok && !found.templateLine)
        note = L" It doesn't set a Template; edit a copy of the template to change it.";
    const std::wstring opened = StyleEditOpenFile(o.sourcePath, StyleOpenLine(found, o.sourceLine, d.templ));
    StartStyleLive(d.request.wire, o.sourcePath, o.sourceKey, o.targetType, /*shared*/ true);
    SayStyle(d.request.wire, o.message + L" " + opened + note);
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
        if (d.gen != g_route1Gen) continue;
        try {
            switch (d.intent) {
            case StyleIntent::Preview: StylePreviewArrived(d); break;
            case StyleIntent::Copy:    StyleCopyArrived(d); break;
            case StyleIntent::Current: StyleCurrentArrived(d); break;
            }
        } catch (const winrt::hresult_error& e) {
            DevToolsOverlayLog(L"route1.style answer failed: %s", e.message().c_str());
        }
        redraw = true;
    }
    if (redraw && g_selWire) ShowPropsForNode(g_selWire, g_selLabel, g_selBreadcrumb, /*force*/ true);
}

// ---- the menu -------------------------------------------------------------------------------------------------------

static void StyleEditCurrent(InstanceHandle wire, bool templ)
{
    if (g_styleUi.wire == wire) { g_styleUi.busy = true; g_styleUi.message = L"Finding the Style\u2026"; }
    if (!PostStyleEdit(StyleRequest(wire, DevToolsStyleEdit::Action::Locate), StyleIntent::Current, templ))
        SayStyle(wire, L"winapp is still working on the last request.");
    else RequestStyleRedraw();
}

static void AddStyleItems(const winrt::Windows::Foundation::Collections::IVector<DevToolsXC::MenuFlyoutItemBase>& items,
                          InstanceHandle wire, bool templ)
{
    const unsigned gen = g_route1Gen;
    DevToolsXC::MenuFlyoutItem current;
    current.Text(L"Edit Current");
    current.Click([gen, wire, templ](auto&&, auto&&) { if (gen == g_route1Gen) StyleEditCurrent(wire, templ); });
    DevToolsXC::MenuFlyoutItem copy;
    copy.Text(L"Edit a Copy\u2026");
    copy.Click([gen, wire, templ](auto&&, auto&&) { if (gen == g_route1Gen) ShowCopyDialog(wire, templ); });
    items.Append(current);
    items.Append(copy);
}

static bool StyleIsControl(InstanceHandle wire)
{
    const auto element = StyleElement(wire);
    return element && element.try_as<DevToolsXC::Control>();
}

// The tree row's context menu: Edit Style and Edit Template, each with Edit Current and Edit a Copy.
static DevToolsXC::MenuFlyout BuildStyleMenu(InstanceHandle wire)
{
    DevToolsXC::MenuFlyout menu;
    DevToolsXC::MenuFlyoutSubItem style;
    style.Text(L"Edit Style");
    AddStyleItems(style.Items(), wire, false);
    DevToolsXC::MenuFlyoutSubItem templ;
    templ.Text(L"Edit Template");
    templ.IsEnabled(StyleIsControl(wire));
    AddStyleItems(templ.Items(), wire, true);
    menu.Items().Append(style);
    menu.Items().Append(templ);
    return menu;
}

// Gives a tree row the Edit Style / Edit Template context menu (right-click, Shift+F10 or the menu key).
static void AttachStyleMenu(IInspectable* row, InstanceHandle wire)
{
    try {
        winrt::Windows::Foundation::IInspectable object{ nullptr };
        winrt::copy_from_abi(object, row);
        const auto element = object.try_as<DevToolsX::UIElement>();
        if (!element) return;
        const unsigned gen = g_route1Gen;
        element.ContextRequested([gen, wire](DevToolsX::UIElement const& sender, DevToolsXI::ContextRequestedEventArgs const& args) {
            if (gen != g_route1Gen) return;
            args.Handled(true);
            try {
                SelectNodeByWire(wire);
                const auto menu = BuildStyleMenu(wire);
                DevToolsXCP::FlyoutShowOptions options;
                winrt::Windows::Foundation::Point at{};
                if (args.TryGetPosition(sender, at)) options.Position(at);
                menu.ShowAt(sender, options);
            } catch (const winrt::hresult_error& e) {
                DevToolsOverlayLog(L"route1.style menu failed: %s", e.message().c_str());
            }
        });
    } catch (const winrt::hresult_error&) {}
}

// ---- the pane's STYLE card ------------------------------------------------------------------------------------------

// WinUI's own control styles arrive through XamlControlsResources as implicit styles, so a "Style" value source
// doesn't say whose Style it is; Edit Current asks the CLI for that.
static bool StyleRowsMention(const std::vector<DevToolsCardRow>& rows)
{
    for (const DevToolsCardRow& r : rows) {
        if (r.source == L"Style" || r.source == L"Built-in style" || r.name == L"Style") return true;
        if (StyleRowsMention(r.children)) return true;
    }
    return false;
}

// The STYLE card's markup; empty when the element has no Style to edit and nothing is being edited live.
static std::wstring BuildStyleSection(const std::wstring& type, const std::vector<DevToolsCardRow>& rows)
{
    if (g_styleUi.wire != g_selWire) g_styleUi = StyleEditUi{ g_selWire };
    const bool control = g_selWire && StyleIsControl(g_selWire);
    const bool editable = g_selWire && !type.empty() && (control || StyleRowsMention(rows));
    if (!editable && !g_styleLive.active) return L"";

    const auto collapsed = [](bool visible) { return visible ? L"" : L" Visibility=\"Collapsed\""; };
    const wchar_t* caption = L" Style=\"{StaticResource CaptionTextBlockStyle}\" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"";

    std::wstring m;
    m += L"<Border Background=\"{ThemeResource ControlFillColorSecondaryBrush}\" CornerRadius=\"4\" Padding=\"10,8,10,10\""
         L" Margin=\"0,6,0,0\" AutomationProperties.AutomationId=\"WinAppDevToolsStyle\">";
    m += L"<StackPanel Spacing=\"6\">";
    m += L"<TextBlock Text=\"STYLE\" CharacterSpacing=\"60\"" + std::wstring(caption) + L"/>";
    m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"8\"" + std::wstring(collapsed(editable)) + L">";
    m += L"<DropDownButton x:Name=\"StyleMenu\" Content=\"Edit Style\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleMenu\"" +
         std::wstring(g_styleUi.busy ? L" IsEnabled=\"False\"" : L"") + L"/>";
    m += L"<DropDownButton x:Name=\"TemplateMenu\" Content=\"Edit Template\" AutomationProperties.AutomationId=\"WinAppDevToolsTemplateMenu\"" +
         std::wstring(g_styleUi.busy || !control ? L" IsEnabled=\"False\"" : L"") + L"/>";
    m += L"</StackPanel>";
    m += L"<TextBlock Text=\"" + DevToolsXmlEscape(g_styleUi.message) + L"\" TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\""
         L" Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"" + std::wstring(collapsed(editable && !g_styleUi.message.empty())) +
         L" AutomationProperties.AutomationId=\"WinAppDevToolsStyleResult\"/>";

    if (g_styleLive.active) {
        const std::wstring status = g_styleLive.status.empty() ? L"Save the file to see your changes here." : g_styleLive.status;
        m += L"<Border BorderThickness=\"1\" BorderBrush=\"{ThemeResource AccentFillColorDefaultBrush}\" CornerRadius=\"4\" Padding=\"8,6\""
             L" Margin=\"0,2,0,0\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleLive\"><StackPanel Spacing=\"2\">";
        m += L"<TextBlock TextWrapping=\"Wrap\"><Run Text=\"Live \" Foreground=\"{ThemeResource AccentTextFillColorPrimaryBrush}\" FontWeight=\"SemiBold\"/>"
             L"<Run Text=\"" + DevToolsXmlEscape(StyleLiveName() + L" \u00B7 " + DevToolsStyleEdit::FileName(g_styleLive.path)) + L"\"/></TextBlock>";
        m += L"<TextBlock Text=\"" + DevToolsXmlEscape(status) + L"\" TextWrapping=\"Wrap\" IsTextSelectionEnabled=\"True\" Foreground=\"" +
             std::wstring(g_styleLive.failed ? L"{ThemeResource SystemFillColorCriticalBrush}" : L"{ThemeResource TextFillColorSecondaryBrush}") +
             L"\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleLiveStatus\"/>";
        m += L"<StackPanel Orientation=\"Horizontal\" Spacing=\"12\">";
        m += L"<HyperlinkButton x:Name=\"StyleLiveOpen\" Padding=\"0\" Content=\"Open file\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleLiveOpen\"/>";
        m += L"<HyperlinkButton x:Name=\"StyleLiveStop\" Padding=\"0\" Content=\"Stop\" AutomationProperties.AutomationId=\"WinAppDevToolsStyleLiveStop\""
             L" ToolTipService.ToolTip=\"Stop applying saves to the app. What is already applied stays until the app restarts.\"/>";
        m += L"</StackPanel></StackPanel></Border>";
    }
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

// Wires the card BuildStyleSection emitted.
template <class Find>
static void WireStyleSection(Find& findByName)
{
    const InstanceHandle wire = g_selWire;
    if (const auto style = StyleEditFind<DevToolsXC::DropDownButton>(findByName, L"StyleMenu")) {
        DevToolsXC::MenuFlyout menu;
        menu.Placement(DevToolsXCP::FlyoutPlacementMode::BottomEdgeAlignedLeft);
        AddStyleItems(menu.Items(), wire, false);
        style.Flyout(menu);
    }
    if (const auto templ = StyleEditFind<DevToolsXC::DropDownButton>(findByName, L"TemplateMenu")) {
        DevToolsXC::MenuFlyout menu;
        menu.Placement(DevToolsXCP::FlyoutPlacementMode::BottomEdgeAlignedLeft);
        AddStyleItems(menu.Items(), wire, true);
        templ.Flyout(menu);
    }
    const unsigned gen = g_route1Gen;
    if (const auto open = StyleEditFind<DevToolsXC::HyperlinkButton>(findByName, L"StyleLiveOpen")) {
        open.Click([gen](auto&&, auto&&) {
            if (gen != g_route1Gen || !g_styleLive.active) return;
            const auto found = StyleFind(g_styleLive.path, g_styleLive.key, g_styleLive.targetType);
            StyleEditOpenFile(g_styleLive.path, found.ok ? found.line : 0);
        });
    }
    if (const auto stop = StyleEditFind<DevToolsXC::HyperlinkButton>(findByName, L"StyleLiveStop")) {
        stop.Click([gen](auto&&, auto&&) {
            if (gen != g_route1Gen) return;
            StopStyleLive();
            RequestStyleRedraw();
        });
    }
}
