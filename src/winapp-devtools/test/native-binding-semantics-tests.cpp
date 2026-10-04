// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsBindingAnswer.h"
#include "DevToolsBindingMode.h"
#include "DevToolsPathWalk.h"
#include "DevToolsPathProbe.h"
#include "DevToolsRead.h"
#include "DevToolsProjected.h"
#include <cstdio>

struct NamedProperty : winrt::implements<NamedProperty, DevToolsXD::ICustomProperty>
{
    winrt::Windows::UI::Xaml::Interop::TypeName Type()
    { return { L"String", winrt::Windows::UI::Xaml::Interop::TypeKind::Primitive }; }
    winrt::hstring Name() { return L"Title"; }
    winrt::Windows::Foundation::IInspectable GetValue(winrt::Windows::Foundation::IInspectable const&)
    { return winrt::box_value(L"provider title"); }
    void SetValue(winrt::Windows::Foundation::IInspectable const&, winrt::Windows::Foundation::IInspectable const&)
    { throw winrt::hresult_not_implemented(); }
    winrt::Windows::Foundation::IInspectable GetIndexedValue(winrt::Windows::Foundation::IInspectable const&,
                                                            winrt::Windows::Foundation::IInspectable const&)
    { throw winrt::hresult_not_implemented(); }
    void SetIndexedValue(winrt::Windows::Foundation::IInspectable const&, winrt::Windows::Foundation::IInspectable const&,
                         winrt::Windows::Foundation::IInspectable const&)
    { throw winrt::hresult_not_implemented(); }
    bool CanWrite() { return false; }
    bool CanRead() { return true; }
};

struct Provider : winrt::implements<Provider, DevToolsXD::ICustomPropertyProvider>
{
    DevToolsXD::ICustomProperty GetCustomProperty(winrt::hstring const& name)
    { return name == L"Title" ? winrt::make<NamedProperty>() : nullptr; }
    DevToolsXD::ICustomProperty GetIndexedProperty(winrt::hstring const&, winrt::Windows::UI::Xaml::Interop::TypeName const&)
    { return nullptr; }
    winrt::hstring GetStringRepresentation() { return L"Provider"; }
    winrt::Windows::UI::Xaml::Interop::TypeName Type()
    { return { L"Provider", winrt::Windows::UI::Xaml::Interop::TypeKind::Custom }; }
};

int RunBindingAnswerTests();
int RunPathWalkTests();
int RunPathProbeTests();

int main()
{
    std::setvbuf(stdout, nullptr, _IONBF, 0);
    winrt::init_apartment(winrt::apartment_type::multi_threaded);
    struct ApartmentExit { ~ApartmentExit() { winrt::clear_factory_cache(); winrt::uninit_apartment(); } } apartment;
    unsigned checks = 0, failures = 0;
    auto check = [&](bool ok, const char* name) {
        ++checks;
        std::printf("%s %s\n", ok ? "PASS" : "FAIL", name);
        if (!ok) ++failures;
    };
    using Mode = winrt::Microsoft::UI::Xaml::Data::BindingMode;
    check(DevToolsBindingInstall_ModeOrdinal(L"OneWay") == static_cast<int>(Mode::OneWay), "OneWay matches SDK");
    check(DevToolsBindingInstall_ModeOrdinal(L"OneTime") == static_cast<int>(Mode::OneTime), "OneTime matches SDK");
    check(DevToolsBindingInstall_ModeOrdinal(L"TwoWay") == static_cast<int>(Mode::TwoWay), "TwoWay matches SDK");
    check(DevToolsBindingInstall_ModeOrdinal(L"") == static_cast<int>(Mode::OneWay), "empty defaults to OneWay");
    for (const auto* invalid : {L"TwoWays", L"twoway", L"0", L"3"})
        check(DevToolsBindingInstall_ModeOrdinal(invalid) == -1, "invalid mode rejected instead of mislabeled");
    for (const auto* kind : {L"elementName", L"self", L"relativeSource", L"source", L"unknown"}) {
        auto view = DevToolsPathWalk_FromJson(
            L"{\"state\":\"source-unavailable\",\"againstKind\":\"" + std::wstring(kind) +
            L"\",\"reason\":\"The actual source is unavailable\",\"segments\":[]}", false);
        check(view.show, "unavailable source stays visible");
        check(view.againstNote.find(L"DataContext") == std::wstring::npos,
              "unavailable source is not labeled resolved against DataContext");
    }
    check(DevToolsPathWalk_FromJson(
        L"{\"state\":\"no-xbind-source\",\"againstKind\":\"xbind\",\"segments\":[]}", false).againstNote.empty(),
        "unreachable compiled source is not labeled as successfully resolved");
    const std::wstring warning = DevToolsBindingInstall_ReplacementWarning();
    for (const auto* setting : {L"Converter", L"ConverterParameter", L"ConverterLanguage", L"UpdateSourceTrigger",
                               L"FallbackValue", L"TargetNullValue", L"Source", L"ElementName", L"RelativeSource",
                               L"DataContext", L"x:Bind", L"capture"})
        check(warning.find(setting) != std::wstring::npos, "full replacement warning names discarded setting or boundary");

    DevToolsReadProp prop;
    DevToolsProbeResult result;
    for (const auto* state : {L"noSourceInfo", L"noFile", L"available", L"stale"}) {
        check(!DevToolsPathProbe_Prepare(prop, state, L"", &result), "missing expression cannot be walked");
        check(result.state == L"unknown" && !result.reason.empty(), "no authored expression is unknown, not none");
        check(DevToolsPathWalk_FromJson(DevToolsPathProbe_ToJson(result), false).show,
              "unknown result survives wrapper serialization and UI rendering without host");
    }
    prop.authored = L"plain text";
    check(!DevToolsPathProbe_Prepare(prop, L"available", L"", &result) && result.state == L"none",
          "known authored literal is the unbound negative control");
    prop.source = L"Binding";
    prop.binding = L"{Binding Title, Mode=TwoWay}";
    for (const auto* state : {L"noSourceInfo", L"noFile", L"available"}) {
        check(DevToolsPathProbe_Prepare(prop, state, L"", &result), "runtime SetBinding walk independent of authored source");
        check(result.path == L"Title" && result.expressionSource == L"runtime" && result.againstKind == L"binding",
              "actual runtime binding outranks old authored literal");
    }
    prop.binding.clear();
    check(!DevToolsPathProbe_Prepare(prop, L"available", L"", &result) && result.state == L"path-unavailable",
          "runtime binding with unreadable metadata is not none or authored fallback");
    for (const auto* expression : {
            L"{Binding Title, ElementName=SourceEditor}",
            L"{Binding ElementName=SourceEditor, Path=Text}",
            L"{Binding Tag, RelativeSource={RelativeSource Self}}",
            L"{Binding Tag, RelativeSource={RelativeSource TemplatedParent}}",
            L"{Binding Title, Source=...}"}) {
        prop.binding = expression;
        check(!DevToolsPathProbe_Prepare(prop, L"available", L"", &result), "explicit runtime source refused before object access");
        check(result.state == L"source-unavailable" && result.againstKind != L"binding",
              "explicit source never presented as DataContext");
        check(result.reason.find(L"not retried") != std::wstring::npos, "refusal explains source boundary");
        check(DevToolsPathProbe_Prepare(prop, L"available", L"Replacement", &result) &&
              result.expressionSource == L"proposed" && result.againstKind == L"binding",
              "new replacement validation explicitly uses DataContext");
    }
    prop.binding = L"{Binding Path=Title, ConverterParameter='Source=not-a-source'}";
    check(DevToolsPathProbe_Prepare(prop, L"available", L"", &result) && result.path == L"Title",
          "quoted converter parameter is not mistaken for source selection");
    prop.binding = L"  {Binding Title, ElementName=SourceEditor} ";
    check(!DevToolsPathProbe_Prepare(prop, L"available", L"", &result) &&
          result.state == L"source-unavailable", "leading whitespace does not hide an explicit source");
    prop.binding = L"{Binding Title, RelativeSource={RelativeSource Self}";
    check(!DevToolsPathProbe_Prepare(prop, L"available", L"", &result) &&
          result.againstKind == L"unknown", "incomplete source markup is refused rather than probed against DataContext");
    prop.binding = L"{Binding Converter={StaticResource Tag}}";
    check(!DevToolsPathProbe_Prepare(prop, L"available", L"", &result) && result.state == L"path-unavailable",
          "pathless binding is not unbound");
    prop = {};
    prop.authored = L"{TemplateBinding Content}";
    check(!DevToolsPathProbe_Prepare(prop, L"available", L"", &result) && result.state != L"none",
          "template source is never silently unbound or DataContext");
    prop.authored = L"{x:Bind Vm.Title}";
    check(DevToolsPathProbe_Prepare(prop, L"available", L"", &result) && result.againstKind == L"xbind",
          "source-info-only compiled walking retains separate xbind ownership");

    // Real shipping ABI walker, in-memory provider only: no WinUI activation, host, window or app.
    auto provider = winrt::make<Provider>();
    result = {};
    result.againstKind = L"binding";
    DevToolsPathProbe_Walk(reinterpret_cast<::IInspectable*>(winrt::get_abi(provider)), L"Title", &result);
    check(result.state.empty() && result.segments.size() == 1 && result.segments[0].value == L"provider title",
          "native provider-backed path works without managed host");
    auto opaque = winrt::box_value(L"opaque");
    result = {};
    DevToolsPathProbe_Walk(reinterpret_cast<::IInspectable*>(winrt::get_abi(opaque)), L"Title", &result);
    check(result.state == L"not-probeable" && result.segments.empty(),
          "object without ICustomPropertyProvider cannot claim a missing or resolved member");
    check(result.reason.find(L"[bindable]") != std::wstring::npos, "provider requirement names bindable limitation");
    std::printf("native binding semantics: %u checks, %u failures\n", checks, failures);
    const int regressions = RunBindingAnswerTests() + RunPathWalkTests() + RunPathProbeTests();
    return failures || regressions ? 1 : 0;
}
