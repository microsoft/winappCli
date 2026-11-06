// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// The Reactor "Component" section of the inspector's property pane: the props,
// hooks (with an editor for each settable state value), contexts and event handlers of the component that renders the
// selected element. The data comes from the managed agent (Reactor.component and Reactor.element), which needs the UI
// thread, so it is fetched on a worker and applied back on the dispatcher.
#pragma once

#include <atomic>
#include <cwctype>
#include <functional>
#include <memory>
#include <string>
#include <thread>
#include <vector>

#include "DevToolsProtocol.h"
#include "DevToolsText.h"

struct DevToolsComponentHook
{
    int index = 0;
    std::wstring name;                          // the C# variable the hook was assigned to, when Reactor recorded it
    std::wstring kind, type, value, context;
    bool editable = false;
    bool redacted = false;
};

struct DevToolsComponentHandler
{
    std::wstring name, method, site;
    bool invokable = false;
};

struct DevToolsComponentView
{
    bool ok = false;
    bool exact = true;                          // the component is mounted at the element, not an ancestor
    std::wstring problem;                       // why there is no component to show
    std::wstring name, kind, defined, declared; // `<Counter>`, `class component`, `App.cs:71 (Counter.Render)`, `App.cs:44`
    bool stale = false;
    bool hasProps = false;
    std::wstring propsType;
    std::vector<std::pair<std::wstring, std::wstring>> props;
    std::vector<DevToolsComponentHook> hooks;
    std::vector<std::pair<std::wstring, std::wstring>> contexts;
    std::vector<DevToolsComponentHandler> handlers;
    std::vector<std::pair<std::wstring, std::wstring>> themeBindings; // property, resource key (ThemeRef)
};

// Brushes the markup uses: overlay palette colors or {ThemeResource ...} references.
struct DevToolsComponentColors
{
    std::wstring text, textDim, accent, caution;
};

inline std::wstring DevToolsComponentLeaf(const std::wstring& path, long long line)
{
    const size_t slash = path.find_last_of(L"\\/");
    std::wstring leaf = slash == std::wstring::npos ? path : path.substr(slash + 1);
    return line > 0 ? leaf + L":" + std::to_wstring(line) : leaf;
}

// Reads Reactor.component (and, for handlers, Reactor.element) answers into what the section shows.
inline DevToolsComponentView DevToolsComponentPanel_Parse(const std::wstring& componentJson, const std::wstring& elementJson)
{
    DevToolsComponentView view;
    DevToolsJson c;
    if (!DevToolsJsonParse(componentJson, c) || !c.IsObject()) {
        view.problem = L"The app did not describe this component.";
        return view;
    }
    const std::wstring state = c.GetString(L"state");
    if (state == L"none") { view.problem = L"No Reactor component renders this element."; return view; }
    if (state == L"absent") { view.problem = L"This app does not use Microsoft.UI.Reactor."; return view; }
    if (state != L"ok") {
        view.problem = L"Component state is unavailable: " + c.GetString(L"reason", L"the managed DevTools agent did not answer") + L".";
        return view;
    }
    view.ok = true;
    view.exact = c.GetBool(L"exact", true);
    if (const DevToolsJson* component = c.Find(L"component"); component && component->IsObject()) {
        view.name = component->GetString(L"name");
        const std::wstring kind = component->GetString(L"kind");
        view.kind = (component->GetBool(L"isRoot", false) ? L"root " : L"") +
            std::wstring(kind == L"function" ? L"function component" : kind == L"memo" ? L"memo component" : L"class component");
    }
    if (const DevToolsJson* d = c.Find(L"definition"); d && d->IsObject()) {
        const std::wstring file = d->GetString(L"fileOnDisk", d->GetString(L"file"));
        view.defined = DevToolsComponentLeaf(file, d->GetInt(L"line", 0));
        if (!d->GetString(L"method").empty()) view.defined += L" (" + d->GetString(L"method") + L")";
        view.stale = d->GetBool(L"stale", false);
    }
    if (const DevToolsJson* d = c.Find(L"declared"); d && d->IsObject())
        view.declared = DevToolsComponentLeaf(d->GetString(L"fileOnDisk", d->GetString(L"file")), d->GetInt(L"line", 0));
    if (const DevToolsJson* p = c.Find(L"props"); p && p->IsObject()) {
        view.hasProps = true;
        view.propsType = p->GetString(L"type");
        if (const DevToolsJson* values = p->Find(L"values"); values && values->type == DevToolsJsonType::Array)
            for (const auto& v : values->arr) view.props.emplace_back(v.GetString(L"name"), v.GetString(L"value"));
    }
    if (const DevToolsJson* hooks = c.Find(L"hooks"); hooks && hooks->type == DevToolsJsonType::Array) {
        for (const auto& h : hooks->arr) {
            DevToolsComponentHook hook;
            hook.index = static_cast<int>(h.GetInt(L"index", 0));
            hook.name = h.GetString(L"name");
            hook.kind = h.GetString(L"kind");
            hook.type = h.GetString(L"type");
            hook.value = h.GetString(L"value");
            hook.context = h.GetString(L"context");
            hook.editable = h.GetBool(L"editable", false);
            hook.redacted = h.GetBool(L"redacted", false);
            view.hooks.push_back(std::move(hook));
        }
    }
    if (const DevToolsJson* contexts = c.Find(L"contexts"); contexts && contexts->type == DevToolsJsonType::Array)
        for (const auto& x : contexts->arr) view.contexts.emplace_back(x.GetString(L"name"), x.GetString(L"value"));

    DevToolsJson e;
    if (DevToolsJsonParse(elementJson, e) && e.IsObject()) {
        if (const DevToolsJson* bindings = e.Find(L"themeBindings"); bindings && bindings->type == DevToolsJsonType::Array)
            for (const auto& b : bindings->arr) view.themeBindings.emplace_back(b.GetString(L"property"), b.GetString(L"resourceKey"));
        if (const DevToolsJson* handlers = e.Find(L"handlers"); handlers && handlers->type == DevToolsJsonType::Array) {
            for (const auto& h : handlers->arr) {
                DevToolsComponentHandler handler;
                handler.name = h.GetString(L"event");
                handler.method = h.GetString(L"method");
                handler.invokable = h.GetBool(L"invokable", false);
                if (!h.GetString(L"file").empty()) handler.site = DevToolsComponentLeaf(h.GetString(L"file"), h.GetInt(L"line", 0));
                view.handlers.push_back(std::move(handler));
            }
        }
    }
    return view;
}

// A state value as an editor shows it: text without the quotes the agent adds.
inline std::wstring DevToolsComponentEditText(const std::wstring& value)
{
    if (value.size() >= 2 && value.front() == L'"' && value.back() == L'"') {
        std::wstring inner = value.substr(1, value.size() - 2), out;
        for (size_t i = 0; i < inner.size(); ++i) {
            if (inner[i] == L'\\' && i + 1 < inner.size()) {
                const wchar_t next = inner[++i];
                out += next == L'n' ? L'\n' : next == L'r' ? L'\r' : next;
            } else {
                out += inner[i];
            }
        }
        return out;
    }
    return value;
}

// The section body: a StackPanel whose settable hooks are TextBoxes named DevToolsHook<index> (absent when read-only)
// and whose invokable handlers have a Run button named DevToolsHandler<n>. DevToolsComponentStatus carries the result
// of the last edit.
inline std::wstring DevToolsComponentPanel_Markup(const DevToolsComponentView& view, bool readOnly, const DevToolsComponentColors& colors)
{
    auto text = [&](const std::wstring& value, const std::wstring& brush, const wchar_t* extra = L"") {
        return L"<TextBlock Text=\"" + DevToolsXmlEscape(value) + L"\" TextWrapping=\"Wrap\" FontSize=\"12\" Foreground=\"" +
            brush + L"\"" + extra + L"/>";
    };
    auto heading = [&](const std::wstring& value) {
        return L"<TextBlock Text=\"" + DevToolsXmlEscape(value) + L"\" FontSize=\"12\" FontWeight=\"SemiBold\" Margin=\"0,6,0,0\" Foreground=\"" +
            colors.text + L"\"/>";
    };
    std::wstring m = L"<StackPanel xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\""
                     L" xmlns:x=\"http://schemas.microsoft.com/winfx/2006/xaml\" Spacing=\"4\""
                     L" AutomationProperties.AutomationId=\"DevToolsComponentBody\">";
    if (!view.ok) {
        m += text(view.problem, colors.textDim);
        return m + L"</StackPanel>";
    }
    std::wstring where = (view.exact ? L"<" : L"Rendered by <") + view.name + L"> \u00B7 " + view.kind;
    if (!view.defined.empty()) where += L" \u00B7 defined " + view.defined;
    if (!view.declared.empty()) where += L" \u00B7 declared " + view.declared;
    m += text(where, colors.textDim, L" AutomationProperties.AutomationId=\"DevToolsComponentWhere\"");
    if (view.stale) m += text(L"The source file changed since the app was built; lines may have moved.", colors.caution);
    if (!view.themeBindings.empty()) {
        m += heading(L"Theme resources");
        for (const auto& [property, key] : view.themeBindings) m += text(property + L" \u2190 ThemeRef " + key, colors.text);
    }

    if (view.hasProps) {
        m += heading(view.propsType.empty() ? L"Props" : L"Props (" + view.propsType + L")");
        if (view.props.empty()) m += text(L"No props.", colors.textDim);
        for (const auto& [name, value] : view.props) m += text(name + L" = " + value, colors.text);
    }

    m += heading(L"Hooks");
    if (view.hooks.empty()) m += text(L"No hooks.", colors.textDim);
    for (const auto& hook : view.hooks) {
        std::wstring label = L"[" + std::to_wstring(hook.index) + L"] " + (hook.name.empty() ? L"" : hook.name + L" ") + hook.kind;
        if (!hook.type.empty()) label += L" " + hook.type;
        if (!hook.context.empty()) label += L" (" + hook.context + L")";
        if (hook.editable && !readOnly) {
            const std::wstring id = L"DevToolsHook" + std::to_wstring(hook.index);
            m += L"<Grid ColumnSpacing=\"8\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"140\"/>"
                 L"</Grid.ColumnDefinitions>" + text(label, colors.text, L" VerticalAlignment=\"Center\"") +
                 L"<TextBox x:Name=\"" + id + L"\" Grid.Column=\"1\" FontSize=\"12\" MinHeight=\"28\" Padding=\"6,3\" Text=\"" +
                 DevToolsXmlEscape(DevToolsComponentEditText(hook.value), true) + L"\" AutomationProperties.AutomationId=\"" + id +
                 L"\" AutomationProperties.Name=\"" + DevToolsXmlEscape(L"State " + label) +
                 L"\" ToolTipService.ToolTip=\"Enter applies the value live (set-state); the C# is unchanged.\"/></Grid>";
        } else {
            m += text(hook.value.empty() ? label : label + L" = " + hook.value, colors.text);
        }
    }
    if (!view.contexts.empty()) {
        m += heading(L"Contexts");
        for (const auto& [name, value] : view.contexts) m += text(name + L" = " + value, colors.text);
    }
    if (!view.handlers.empty()) {
        m += heading(L"Handlers");
        for (size_t i = 0; i < view.handlers.size(); ++i) {
            const auto& h = view.handlers[i];
            const std::wstring line = h.name + L" \u2192 " + h.method + (h.site.empty() ? L"" : L"  " + h.site);
            if (h.invokable && !readOnly) {
                const std::wstring id = L"DevToolsHandler" + std::to_wstring(i);
                m += L"<Grid ColumnSpacing=\"8\"><Grid.ColumnDefinitions><ColumnDefinition Width=\"*\"/><ColumnDefinition Width=\"Auto\"/>"
                     L"</Grid.ColumnDefinitions>" + text(line, colors.text, L" VerticalAlignment=\"Center\"") +
                     L"<Button x:Name=\"" + id + L"\" Grid.Column=\"1\" Content=\"Run\" FontSize=\"12\" Padding=\"10,2\""
                     L" AutomationProperties.AutomationId=\"" + id + L"\" AutomationProperties.Name=\"" +
                     DevToolsXmlEscape(L"Run " + h.name) + L"\"/></Grid>";
            } else {
                m += text(line, colors.text);
            }
        }
    }
    m += L"<TextBlock x:Name=\"DevToolsComponentStatus\" Visibility=\"Collapsed\" TextWrapping=\"Wrap\" FontSize=\"12\" Foreground=\"" +
         colors.caution + L"\" AutomationProperties.AutomationId=\"DevToolsComponentStatus\" AutomationProperties.LiveSetting=\"Polite\"/>";
    return m + L"</StackPanel>";
}

// The status line after an edit: empty when it worked, the refusal otherwise.
inline std::wstring DevToolsComponentPanel_EditProblem(const std::wstring& answerJson)
{
    DevToolsJson a;
    if (!DevToolsJsonParse(answerJson, a) || !a.IsObject()) return L"The app did not answer.";
    const std::wstring state = a.GetString(L"state");
    if (state == L"ok") return {};
    if (state == L"threw") return L"The handler threw " + a.GetString(L"exception") + L": " + a.GetString(L"message");
    if (a.GetBool(L"indeterminate", false)) return L"The app did not answer in time; the change may or may not have been applied.";
    std::wstring reason = a.GetString(L"reason", L"the app refused the change");
    if (!reason.empty()) reason[0] = static_cast<wchar_t>(towupper(reason[0]));
    return reason;
}

#ifdef DEVTOOLS_COMPONENT_PANEL_WINRT
// Fills a Component section and wires its editors. UI thread only; the agent is asked on a worker. Begin replaces
// whatever it showed before.
class DevToolsComponentSection
{
public:
    using AskFn = std::function<bool(unsigned long long raw, const std::wstring& op, const std::wstring& arg,
                                     const std::wstring& value, std::wstring* out)>;

    void Begin(DevToolsXC::Panel body, unsigned long long raw, bool readOnly, DevToolsComponentColors colors, AskFn ask)
    {
        Clear();
        if (!body || !raw || !ask) return;
        auto s = std::make_shared<State>();
        s->body = body;
        s->raw = raw;
        s->readOnly = readOnly;
        s->colors = std::move(colors);
        s->ask = std::move(ask);
        s->queue = DevToolsD::DispatcherQueue::GetForCurrentThread();
        state_ = s;
        Fetch(s, {});
    }

    void Clear()
    {
        if (state_) state_->live = false;
        state_.reset();
    }

private:
    struct State
    {
        DevToolsXC::Panel body{ nullptr };
        unsigned long long raw = 0;
        bool readOnly = true;
        DevToolsComponentColors colors;
        AskFn ask;
        DevToolsD::DispatcherQueue queue{ nullptr };
        std::atomic<bool> live{ true };
        std::atomic<bool> busy{ false };
    };
    std::shared_ptr<State> state_;

    // Ask for the component (after an optional write) on a worker, then show it on the UI thread.
    static void Fetch(const std::shared_ptr<State>& s, std::function<std::wstring(State&)> write)
    {
        if (s->busy.exchange(true)) return;
        std::thread([s, write]() {
            std::wstring problem;
            if (write) problem = write(*s);
            std::wstring component, element;
            s->ask(s->raw, L"reactorcomponent", L"", L"", &component);
            s->ask(s->raw, L"reactorelement", L"", L"", &element);
            const auto view = std::make_shared<DevToolsComponentView>(DevToolsComponentPanel_Parse(component, element));
            if (!s->queue || !s->live) { s->busy = false; return; }
            s->queue.TryEnqueue([s, view, problem]() {
                s->busy = false;
                if (s->live) Show(s, *view, problem);
            });
        }).detach();
    }

    static void Show(const std::shared_ptr<State>& s, const DevToolsComponentView& view, const std::wstring& problem)
    {
        try {
            auto loaded = DevToolsXMk::XamlReader::Load(DevToolsComponentPanel_Markup(view, s->readOnly, s->colors)).as<DevToolsXC::StackPanel>();
            auto children = s->body.Children();
            children.Clear();
            children.Append(loaded);
            if (!problem.empty()) {
                if (auto status = loaded.FindName(L"DevToolsComponentStatus").try_as<DevToolsXC::TextBlock>()) {
                    status.Text(problem);
                    status.Visibility(DevToolsX::Visibility::Visible);
                }
            }
            if (s->readOnly) return;
            for (const auto& hook : view.hooks) {
                if (!hook.editable) continue;
                auto box = loaded.FindName(L"DevToolsHook" + std::to_wstring(hook.index)).try_as<DevToolsXC::TextBox>();
                if (!box) continue;
                const int index = hook.index;
                const std::wstring original = DevToolsComponentEditText(hook.value);
                std::weak_ptr<State> weak = s;
                // The handlers live on the box, so they hold it weakly.
                winrt::weak_ref<DevToolsXC::TextBox> weakBox = box;
                auto commit = [weak, index, original, weakBox]() {
                    auto st = weak.lock();
                    auto editor = weakBox.get();
                    if (!st || !st->live || !editor) return;
                    const std::wstring value(editor.Text());
                    if (value == original) return;
                    Fetch(st, [index, value](State& state) {
                        std::wstring answer;
                        state.ask(state.raw, L"reactorsetstate", std::to_wstring(index), value, &answer);
                        return DevToolsComponentPanel_EditProblem(answer);
                    });
                };
                box.KeyDown([commit](auto&&, DevToolsXI::KeyRoutedEventArgs const& e) {
                    if (e.Key() == winrt::Windows::System::VirtualKey::Enter) { e.Handled(true); commit(); }
                });
                box.LostFocus([commit](auto&&, auto&&) { commit(); });
            }
            for (size_t i = 0; i < view.handlers.size(); ++i) {
                if (!view.handlers[i].invokable) continue;
                auto button = loaded.FindName(L"DevToolsHandler" + std::to_wstring(i)).try_as<DevToolsXCP::ButtonBase>();
                if (!button) continue;
                const std::wstring name = view.handlers[i].name;
                std::weak_ptr<State> weak = s;
                button.Click([weak, name](auto&&, auto&&) {
                    if (auto st = weak.lock(); st && st->live) {
                        Fetch(st, [name](State& state) {
                            std::wstring answer;
                            state.ask(state.raw, L"reactorinvoke", name, L"", &answer);
                            return DevToolsComponentPanel_EditProblem(answer);
                        });
                    }
                });
            }
        } catch (const winrt::hresult_error&) {
            // The section stays as it was; the rest of the panel is unaffected.
        }
    }
};
#endif
