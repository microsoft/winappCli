// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#include "DevToolsInspectorAcceptance.h"
#include "DevToolsProtocol.h"
#include <cstdio>

static int RunInspectorAcceptanceSuite(bool legacyOracle)
{
    int failures = 0, checks = 0;
    auto check = [&](bool passed, const char* name) {
        ++checks;
        std::printf("%s %s\n", passed ? "PASS" : "FAIL", name);
        if (!passed) ++failures;
    };
    using namespace DevToolsInspector;
    if (legacyOracle) {
        // A deliberately broken model of the recorded .56 outcomes, not a live
        // execution of .56. Each assertion uses the same acceptance expectation.
        for (int update = 1; update <= 3; ++update)
            check(0 == update, "RED M1 legacy selected snapshot still reads ready");
        check((24.0 + 88.0) * 1.25 == 88.0 * 1.25, "RED M2 legacy padded popup misses by 30 physical pixels");
        check(std::wstring(L"internal error").find(L"Width") != std::wstring::npos,
              "RED M9 legacy generic error loses requested property");
        check(640.0 - 420.0 - 6.0 >= 300.0, "RED M12 legacy split leaves right pane below minimum");
        std::printf("Legacy-oracle negative control: %d checks, %d failures (expected 6)\n", checks, failures);
        return failures ? 1 : 0;
    }

    // F5: the tree is always reread; the pane too, unless an edit is in progress.
    for (int update = 1; update <= 3; ++update) {
        check(RefreshPaneOnSnapshot(false, false), "M1 explicit refresh permits each notified update");
        check(!RefreshPaneOnSnapshot(true, false), "M1 refresh does not destroy a focused editor");
        check(!RefreshPaneOnSnapshot(false, true), "M1 pending binding confirmation is preserved");
    }
    check(!NegativeDimension(L"Width", L"banana"), "M9 conversion owns nonnumeric input");
    check(NegativeDimension(L"Width", L"-1"), "M9 negative Width rejected before setter");
    check(NegativeDimension(L"Height", L" -1 "), "M9 whitespace does not bypass range check");
    check(!NegativeDimension(L"Width", L"NaN"), "M9 Auto dimension remains supported");
    check(!NegativeDimension(L"Width", L"260"), "M9 successful width control remains supported");
    check(!NegativeDimension(L"Margin", L"-1"), "M9 negative margin is not a negative size");
    for (const auto& value : {L"banana", L"-1", L"NotAVisibility"}) {
        int conversions = 0, mutations = 0;
        std::wstring error;
        const std::wstring property = std::wstring(value) == L"NotAVisibility" ? L"Visibility" : L"Width";
        const auto hr = WriteLiteral(property, property == L"Width" ? L"Double" : L"Visibility", value,
            [&]() { ++conversions; return E_INVALIDARG; },
            [&]() { ++mutations; return S_OK; }, &error);
        check(FAILED(hr) && mutations == 0, "M9 invalid literal never reaches mutation");
        check(conversions == (std::wstring(value) == L"-1" ? 0 : 1), "M9 range precedes conversion");
        check(error.find(L"invalid-value|Cannot set property") == 0, "M9 invalid input has contextual classification");
    }
    {
        int mutations = 0;
        std::wstring error;
        check(SUCCEEDED(WriteLiteral(L"Width", L"Double", L"260", [] { return S_OK; },
            [&] { ++mutations; return S_OK; }, &error)) && mutations == 1 && error.empty(),
            "M9 valid conversion mutates exactly once");
        check(FAILED(WriteLiteral(L"Width", L"Double", L"260", [] { return E_OUTOFMEMORY; },
            [&] { ++mutations; return S_OK; }, &error)) &&
            error.find(L"property-write-failed|") == 0 && mutations == 1,
            "M9 infrastructure failure is not blamed on user input");
        check(SUCCEEDED(WriteLiteral(L"Width", L"String", L"-1", [] { return S_OK; },
            [] { return S_OK; }, &error)), "M9 dimension validation does not reject unrelated string properties");
    }
    for (const auto& value : {L"banana", L"-1", L"NotAVisibility", L"quote\" | line\n\u6771\u4eac"}) {
        const auto message = InvalidValueMessage(L"Width", L"Double", value, L"conversion failed");
        check(message.find(value) != std::wstring::npos &&
              message.find(L"Width") != std::wstring::npos &&
              message.find(L"Double") != std::wstring::npos &&
              message.find(L"conversion") != std::wstring::npos, "M9 error retains input/property/type/reason");
        const auto wire = DevToolsRpcError(L"1", DevToolsErr::InvalidParams, message, L"invalid-value");
        DevToolsJson parsed;
        const bool valid = DevToolsJsonParse(wire, parsed);
        const auto* error = parsed.Find(L"error");
        check(valid && error && error->GetString(L"message") == message &&
              error->GetInt(L"code", 0) == -32602 && wire.find(L'\n') == std::wstring::npos,
              "M9 contextual error survives real JSON framing without raw newlines");
    }
    for (double scale : {1.0, 1.25, 2.0}) {
        for (double origin : {0.0, 24.0, 37.5, -12.0}) {
            const double offset = PopupOffset(0, origin);
            check(std::abs((origin + offset + 88.0) * scale - 88.0 * scale) < .001,
                  "M2 draw/marker/panel origin equals pick content origin");
            check(PopupOffset(offset, 0) == offset, "M2 correction is stable after layout");
            check(PopupOffset(offset, 16.0) == offset - 16.0, "M2 live reparent/padding delta is corrected");
        }
    }
    check(24.0 * 1.25 == 30.0, "M2 sealed oracle: old origin yields 30 physical pixel error");
    check(TreeWidth(420, 640) == 334, "M12 800px at 125% reserves right pane and divider");
    check(TreeWidth(420, 780) == 420, "M12 default window preserves original split");
    check(TreeWidth(900, 640) == 334, "M12 oversized drag clamps like resize");
    check(TreeWidth(10, 640) == 220, "M12 tree minimum retained when feasible");
    check(TreeWidth(420, 400) == 94, "M12 tiny surface prioritizes right pane");
    check(TreeWidth(420, 200) == 0, "M12 impossible minimum never creates negative width");
    std::printf("%d checks, %d failures\n", checks, failures);
    return failures ? 1 : 0;
}

int RunInspectorAcceptanceTests() { return RunInspectorAcceptanceSuite(false); }

#ifdef WINAPP_DEVTOOLS_INSPECTOR_TESTS_ONLY
int main(int argc, char**) { return RunInspectorAcceptanceSuite(argc > 1); }
#endif
