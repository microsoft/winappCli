// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Resource substitution tests protect app-authored text and limit rewriting to inspector markup.

#include "DevToolsResourceInline.h"

#include <cstdio>
#include <string>

namespace
{
int g_failures = 0;

void Check(bool condition, const char* message)
{
    if (condition) std::printf("  ok    %s\n", message);
    else { ++g_failures; std::printf("  FAIL  %s\n", message); }
}

// Stand-ins for the COM resolvers in DevToolsWindow.cpp. `Brush` refuses one key on purpose, because a
// resolver that declines is the case that decides whether the reference survives or the markup breaks.
std::wstring Brush(const wchar_t*, const std::wstring& key)
{
    if (key == L"NotAColourBrush") return L"";
    return L"#FF112233";
}

std::wstring Style(const wchar_t*, const std::wstring& key)
{
    if (key == L"CaptionTextBlockStyle") return L"FontSize=\"12\" FontWeight=\"Normal\"";
    return L"";
}

std::wstring Apply(const std::wstring& in, size_t* asked = nullptr, size_t* left = nullptr)
{
    size_t a = 0, l = 0;
    std::wstring out = DevToolsResInline::Apply(in, Brush, Style, &a, &l);
    if (asked) *asked = a;
    if (left) *left = l;
    return out;
}

void Test_AValueReferenceBecomesItsValue()
{
    std::printf("== a resource reference in attribute position becomes its value ==\n");
    size_t asked = 0, left = 0;
    const std::wstring out = Apply(L"<TextBlock Foreground=\"{ThemeResource TextFillColorSecondaryBrush}\"/>", &asked, &left);
    Check(out == L"<TextBlock Foreground=\"#FF112233\"/>", "the extension is replaced and the quotes are kept");
    Check(asked == 1, "it is counted as asked");
    Check(left == 0, "and nothing is left for the parser to resolve");

    size_t asked2 = 0;
    const std::wstring two = Apply(L"<A F=\"{ThemeResource K1}\" B=\"{StaticResource K2}\"/>", &asked2);
    Check(two == L"<A F=\"#FF112233\" B=\"#FF112233\"/>", "both extensions are handled, in one pass each");
    Check(asked2 == 2, "and both are counted");
}

void Test_AStyleReferenceBecomesTheAttributesItSets()
{
    std::printf("== a Style reference becomes the attributes it sets ==\n");
    const std::wstring out = Apply(L"<TextBlock Style=\"{StaticResource CaptionTextBlockStyle}\" Text=\"x\"/>");
    Check(out == L"<TextBlock FontSize=\"12\" FontWeight=\"Normal\" Text=\"x\"/>",
          "the whole Style attribute is replaced, not just its value");
}

// THE GATE. This bug was written, built, and caught only by reading the engine log of the after-run --
// where 'MainNavigationViewStyle' showed up as an unresolvable key, because the pane was rendering the
// gallery's own XAML as text and the substitution was reaching into it.
void Test_AuthoredXamlTextIsNotRewritten()
{
    std::printf("== the app's authored XAML, shown as TEXT, is never rewritten ==\n");
    // What BuildPropsFragment emits for the AUTHORED XAML card: the app's source, XML-escaped, inside a
    // Text attribute. Its inner quotes are &quot;, so no `="{` anchor exists -- which is the whole reason
    // the anchor is `="{` and not `{`.
    const std::wstring authored =
        L"<TextBlock Text=\"&lt;NavigationView Style=&quot;{StaticResource MainNavigationViewStyle}&quot; "
        L"Foreground=&quot;{ThemeResource TextFillColorPrimaryBrush}&quot; /&gt;\"/>";
    size_t asked = 0;
    const std::wstring out = Apply(authored, &asked);
    Check(out == authored, "the user's own source is handed to the parser byte for byte");
    Check(asked == 0, "and none of it is even counted as a lookup");

    // The same card in its real shape: a rewritable attribute on the SAME element as the escaped text.
    const std::wstring mixed =
        L"<TextBlock Foreground=\"{ThemeResource TextFillColorPrimaryBrush}\" "
        L"Text=\"Style=&quot;{StaticResource MainNavigationViewStyle}&quot;\"/>";
    const std::wstring mixedOut = Apply(mixed);
    Check(mixedOut == L"<TextBlock Foreground=\"#FF112233\" "
                      L"Text=\"Style=&quot;{StaticResource MainNavigationViewStyle}&quot;\"/>",
          "the real attribute is inlined and the escaped text beside it is untouched");
}

void Test_ARefusedResolutionLeavesTheReferenceIntact()
{
    std::printf("== a key that cannot be resolved keeps its reference, and says so ==\n");
    size_t asked = 0, left = 0;
    const std::wstring in = L"<A F=\"{ThemeResource NotAColourBrush}\" G=\"{ThemeResource Fine}\"/>";
    const std::wstring out = Apply(in, &asked, &left);
    Check(out == L"<A F=\"{ThemeResource NotAColourBrush}\" G=\"#FF112233\"/>",
          "the unresolvable one is left alone rather than emitted as broken markup");
    Check(asked == 2, "both were asked for");
    // A pane that still hands references to the parser still climbs, so this count is the honest signal
    // that the fix is only partial for this markup -- not a number to round down to zero.
    Check(left == 1, "and the one still in the markup is reported, because it still costs the climb");

    // CONTROL: the unresolved reference must remain something XamlReader can still resolve itself.
    Check(out.find(L"{ThemeResource NotAColourBrush}") != std::wstring::npos,
          "CONTROL: the surviving reference is intact, not half-substituted");
}

void Test_MalformedMarkupIsLeftAlone()
{
    std::printf("== an unterminated extension does not run off the end ==\n");
    const std::wstring in = L"<A F=\"{ThemeResource Unclosed";
    Check(Apply(in) == in, "an extension with no closing }\" is left exactly as it was");
}

void Test_FontWeightNamesAreOnlyTheOnesXamlAccepts()
{
    std::printf("== FontWeight is emitted by name, or not at all ==\n");
    Check(std::wstring(DevToolsResInline::FontWeightName(400)) == L"Normal", "400 is Normal");
    Check(std::wstring(DevToolsResInline::FontWeightName(600)) == L"SemiBold", "600 is SemiBold");
    // XAML's converter takes names, not numbers, so an off-ladder weight must produce nothing rather
    // than an attribute that would fail the whole fragment and blank the pane.
    Check(DevToolsResInline::FontWeightName(450) == nullptr, "an off-ladder weight yields no name");
}
}

int RunResourceInlineTests()
{
    std::printf("DevToolsResourceInline tests -- where the substitution is allowed to reach\n");
    Test_AValueReferenceBecomesItsValue();
    Test_AStyleReferenceBecomesTheAttributesItSets();
    Test_AuthoredXamlTextIsNotRewritten();
    Test_ARefusedResolutionLeavesTheReferenceIntact();
    Test_MalformedMarkupIsLeftAlone();
    Test_FontWeightNamesAreOnlyTheOnesXamlAccepts();

    if (g_failures == 0) std::printf("PASS: resource inlining reaches attributes and nothing else\n");
    else std::printf("FAILED: %d assertion(s)\n", g_failures);
    return g_failures;
}
