// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsAuthored.h"
#include "DevToolsSourcePath.h"
#include "DevToolsCommentAnchor.h"
#include "DevToolsCommentText.h"
#include "DevToolsText.h"

#include <windows.h>
#include <winioctl.h>
#include <xmllite.h>
#include <shellapi.h>
#include <bcrypt.h>

#include <cstdio>
#include <string>
#include <fstream>
#include <iterator>

static int g_authoredFailures = 0;

static std::string FixtureHash(std::string bytes)
{
    unsigned char hash[32]{};
    if (BCryptHash(BCRYPT_SHA256_ALG_HANDLE, nullptr, 0, reinterpret_cast<PUCHAR>(bytes.data()),
        static_cast<ULONG>(bytes.size()), hash, sizeof(hash)) < 0) return {};
    std::string result;
    constexpr char hex[] = "0123456789ABCDEF";
    for (auto byte : hash) { result += hex[byte >> 4]; result += hex[byte & 15]; }
    return result;
}

static void ACheck(bool condition, const char* what)
{
    if (!condition) { ++g_authoredFailures; std::printf("  FAIL  %s\n", what); }
    else            {                       std::printf("  ok    %s\n", what); }
}

static std::wstring MakeFixture()
{
    wchar_t cwd[MAX_PATH]{};
    GetCurrentDirectoryW(MAX_PATH, cwd);
    std::wstring root = std::wstring(cwd) + L"\\winapp-authored-" + std::to_wstring(GetCurrentProcessId());
    CreateDirectoryW(root.c_str(), nullptr);

    const char text[] =
        "<Page>\r\n"
        "  <TextBlock\r\n"
        "      x:Name=\"Title\"\r\n"
        "      Tag='a>b'\r\n"
        "      Text=\"{x:Bind Vm.Title, Mode=OneWay}\" />\r\n"
        "  <StackPanel><TextBlock Text=\"hi\"/></StackPanel>\r\n"
        "  <Border>  <!-- header -->\r\n"
        "  </Border>\r\n"
        "            <StackPanel><TextBlock x:Name=\"RepeatedLabel\" Text=\"Repeated template label\"/><Button Content=\"Template action\"/></StackPanel>\r\n"
        "            <TextBlock Text=\"{x:Bind ViewModel.ListHeading, Mode=OneWay}\" FontFamily=\"Georgia\" FontSize=\"24\" />\r\n"
        "            <TextBlock Text=\"{x:Bind ViewModel.ListSummary}\" AutomationProperties.AutomationId=\"TaskCountText\" />\r\n"
        "  <TextBlock Text=\"{x:Bind Vm.Title,\r\n"
        "      Mode=OneWay}\" />\r\n"
        "</Page>\r\n";
    const std::wstring path = root + L"\\MainWindow.xaml";
    HANDLE file = CreateFileW(path.c_str(), GENERIC_WRITE, 0, nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr);
    ACheck(file != INVALID_HANDLE_VALUE, "the source fixture is created");
    if (file != INVALID_HANDLE_VALUE) {
        DWORD written = 0;
        const BOOL ok = WriteFile(file, text, static_cast<DWORD>(sizeof(text) - 1), &written, nullptr);
        ACheck(ok && written == sizeof(text) - 1, "the complete source fixture is written");
        CloseHandle(file);
    }
    return root;
}

static void Test_ElementSpanIsReusableAndPreservesAuthoredText()
{
    const std::wstring root = MakeFixture();
    DevToolsAuthored_Init(root, 0);

    const std::wstring whole = L"<TextBlock\n  x:Name=\"Title\"\n  Tag='a>b'\n"
                               L"  Text=\"{x:Bind Vm.Title, Mode=OneWay}\" />";

    std::wstring xaml;
    const DevToolsAuthoredState state = DevToolsAuthored_ReadElement(L"ms-appx:///MainWindow.xaml", 2, &xaml);
    ACheck(state == DevToolsAuthoredState::Available, "the element declaration is available");
    ACheck(xaml == whole, "the whole authored declaration is preserved for display");

    // Every continuation line must recover the same complete authored declaration.
    for (unsigned int line = 2; line <= 5; ++line) {
        std::wstring fromInside;
        DevToolsAuthored_ReadElement(L"ms-appx:///MainWindow.xaml", line, &fromInside);
        char what[96];
        std::snprintf(what, sizeof(what), "source line %u resolves to the whole opening tag", line);
        ACheck(fromInside == whole, what);
    }

    // The element that opens AND closes on one line is still its own declaration, not the one above it.
    std::wstring page;
    DevToolsAuthored_ReadElement(L"ms-appx:///MainWindow.xaml", 1, &page);
    ACheck(page == L"<Page>", "a single-line element resolves to itself");

    // The source alone cannot distinguish the parent from its child on a compact line.
    std::wstring compact;
    DevToolsAuthored_ReadElement(L"ms-appx:///MainWindow.xaml", 6, &compact);
    ACheck(compact.empty(), "M4: compact markup without column/identity must not claim the parent");
    ACheck(DevToolsAuthored_ReadElement(L"MainWindow.xaml", 9, &compact, 36,
        L"Microsoft.UI.Xaml.Controls.TextBlock", L"RepeatedLabel") == DevToolsAuthoredState::Available &&
        compact == L"<TextBlock x:Name=\"RepeatedLabel\" Text=\"Repeated template label\"/>",
        "M4: native evidence column 36 selects the child, not StackPanel");
    std::wstring authoredText;
    ACheck(DevToolsAuthored_FindAttribute(compact, L"Text", &authoredText) && authoredText == L"Repeated template label",
        "M4: compact child source supplies its actually authored Text value");
    ACheck(DevToolsAuthored_ReadElement(L"MainWindow.xaml", 9, &compact, 14,
        L"Microsoft.UI.Xaml.Controls.TextBlock", L"RepeatedLabel") == DevToolsAuthoredState::Unavailable && compact.empty(),
        "M4: a parent column cannot pass a child identity check");
    ACheck(DevToolsAuthored_ReadElement(L"MainWindow.xaml", 9, &compact, 36,
        L"Microsoft.UI.Xaml.Controls.TextBlock", L"WrongName") == DevToolsAuthoredState::Unavailable && compact.empty(),
        "M4: a mismatching name is unavailable rather than silently attributed");
    ACheck(DevToolsAuthored_ReadElement(L"MainWindow.xaml", 2, &compact, 5,
        L"Microsoft.UI.Xaml.Controls.TextBlock", L"Title") == DevToolsAuthoredState::Available && compact == whole,
        "M4: multiline named declarations preserve identity and complete markup");
    const std::wstring heading =
        L"<TextBlock Text=\"{x:Bind ViewModel.ListHeading, Mode=OneWay}\" FontFamily=\"Georgia\" FontSize=\"24\" />";
    for (unsigned column : {0u, 13u, 14u, 12u + static_cast<unsigned>(heading.size())}) {
        ACheck(DevToolsAuthored_ReadElement(L"MainWindow.xaml", 10, &compact, column,
            L"Microsoft.UI.Xaml.Controls.TextBlock", L"") == DevToolsAuthoredState::Available && compact == heading,
            "unnamed one-line x:Bind heading resolves at valid source coordinates");
    }
    for (unsigned column : {1u, 12u, 13u + static_cast<unsigned>(heading.size())}) {
        ACheck(DevToolsAuthored_ReadElement(L"MainWindow.xaml", 10, &compact, column,
            L"Microsoft.UI.Xaml.Controls.TextBlock", L"") == DevToolsAuthoredState::Unavailable && compact.empty(),
            "out-of-tag coordinates remain unavailable rather than attributing a neighboring declaration");
    }
    ACheck(DevToolsAuthored_ReadElement(L"MainWindow.xaml", 10, &compact, 14,
        L"Microsoft.UI.Xaml.Controls.TextBlock", L"TaskCountText") == DevToolsAuthoredState::Unavailable,
        "the following summary cannot donate its identity to the unnamed heading");

    // A comment is not an element declaration, and neither is a closing tag.
    std::wstring commented;
    DevToolsAuthored_ReadElement(L"ms-appx:///MainWindow.xaml", 7, &commented);
    ACheck(commented == L"<Border>", "a trailing comment is not mistaken for the element");
    std::wstring closing;
    DevToolsAuthored_ReadElement(L"ms-appx:///MainWindow.xaml", 8, &closing);
    ACheck(closing.empty(), "a closing tag is not an authored element declaration");

    std::wstring text;
    ACheck(DevToolsAuthored_FindAttribute(xaml, L"Text", &text), "the same declaration supplies property authorship");
    ACheck(text == L"{x:Bind Vm.Title, Mode=OneWay}", "property extraction still returns the authored value");
    ACheck(DevToolsAuthored_ReadElement(L"MainWindow.xaml", 12, &xaml, 3, L"TextBlock")
        == DevToolsAuthoredState::Available &&
        xaml == L"<TextBlock Text=\"{x:Bind Vm.Title,\n      Mode=OneWay}\" />",
        "multiline attribute whitespace is preserved rather than display-reindented");

    DeleteFileW((root + L"\\MainWindow.xaml").c_str());
    RemoveDirectoryW(root.c_str());
}

static void Test_RecordedCoordinatesOutsideCurrentDeclaration()
{
    const auto root = MakeFixture();
    const auto path = root + L"\\MainPage.xaml";
    const std::string border = std::string(32, ' ') +
        "<Border Background=\"{ThemeResource SkyBrush}\" CornerRadius=\"9,9,0,0\">";
    const std::string heading = std::string(24, ' ') +
        "<TextBlock Text=\"{x:Bind ViewModel.ListHeading, Mode=OneWay}\" FontFamily=\"Georgia\" FontSize=\"24\" />";
    std::ofstream file(path, std::ios::binary | std::ios::trunc);
    file << std::string(222, '\n') << border << '\n' << std::string(22, '\n') << heading << '\n';
    file.close();
    DevToolsAuthored_Init(root, 0);
    std::wstring span;
    ACheck(border.size() == 101 && heading.size() == 123, "recorded source fixture retains exact ASCII columns");
    ACheck(DevToolsAuthored_ReadElement(L"ms-appx:///MainPage.xaml", 223, &span, 79,
        L"Microsoft.UI.Xaml.Controls.Border", L"") == DevToolsAuthoredState::Available,
        "recorded unnamed Border coordinate identifies its own declaration");
    ACheck(DevToolsAuthored_ReadElement(L"ms-appx:///MainPage.xaml", 246, &span, 128,
        L"Microsoft.UI.Xaml.Controls.TextBlock", L"") == DevToolsAuthoredState::Unavailable && span.empty(),
        "recorded heading column beyond current source reproduces strict unavailability");
    ACheck(DevToolsAuthored_ReadElement(L"ms-appx:///MainPage.xaml", 246, &span, 123,
        L"Microsoft.UI.Xaml.Controls.TextBlock", L"") == DevToolsAuthoredState::Available,
        "same unnamed x:Bind declaration is readable at an in-tag control coordinate");
    const std::string generated = std::string(24, ' ') +
        "<TextBlock x:ConnectionId='36'                                                    FontFamily=\"Georgia\" FontSize=\"24\" />";
    ACheck(generated.size() == 143 && generated.find("FontSize") + 1 == 128 &&
        heading.find("FontSize") + 1 == 108,
        "exact compiler rewrite shifts FontSize while preserving the source line");
    std::ofstream rewritten(path, std::ios::binary | std::ios::trunc);
    rewritten << std::string(245, '\n') << generated << '\n';
    rewritten.close();
    DevToolsAuthored_Init(root, 0);
    ACheck(DevToolsAuthored_ReadElement(L"ms-appx:///MainPage.xaml", 246, &span, 128,
        L"Microsoft.UI.Xaml.Controls.TextBlock", L"") == DevToolsAuthoredState::Available,
        "production reader accepts captured column in generated coordinate space");
    std::wstring binding;
    ACheck(!DevToolsAuthored_FindAttribute(span, L"Text", &binding),
        "using generated XAML as authored source would lose the compiled binding");
    DeleteFileW(path.c_str());
    DeleteFileW((root + L"\\MainWindow.xaml").c_str());
    RemoveDirectoryW(root.c_str());
}

static void Test_HashCoveredCoordinates()
{
    const auto root = MakeFixture();
    const std::string heading = std::string(24, ' ') +
        "<TextBlock Text=\"{x:Bind ViewModel.ListHeading, Mode=OneWay}\" FontFamily=\"Georgia\" FontSize=\"24\" />";
    const std::string original = std::string(245, '\n') + heading + "\n";
    const std::string xbf = "controlled-payload-hash-contract-not-executable-XBF";
    const auto sourcePath = root + L"\\MainPage.xaml", xbfPath = root + L"\\MainPage.xbf", inventoryPath = root + L"\\inventory.json";
    auto write = [](const std::wstring& path, const std::string& bytes) {
        std::ofstream file(path, std::ios::binary | std::ios::trunc); file << bytes;
    };
    write(sourcePath, original); write(xbfPath, xbf);
    const std::string inventory = "{\"coordinates\":[{\"source\":\"MainPage.xaml\",\"resource\":\"MainPage.xaml\","
        "\"sourceHash\":\"" + FixtureHash(original) + "\",\"xbfHash\":\"" + FixtureHash(xbf) + "\",\"elements\":["
        "{\"line\":246,\"endLine\":246,\"column\":25,\"type\":\"TextBlock\"}]}]}";
    write(inventoryPath, inventory);
    const auto hash = FixtureHash(inventory);
    auto initialize = [&](const std::wstring& payload, const std::wstring& expected) {
        DevToolsAuthored_Init(root, 0);
        DevToolsAuthored_InitCoordinates(inventoryPath, expected, payload);
    };
    initialize(root, std::wstring(hash.begin(), hash.end()));
    std::wstring span, value;
    DevToolsAuthoredLocation location;
    ACheck(DevToolsAuthored_ReadElement(L"ms-appx:///MainPage.xaml", 246, &span, 128, L"TextBlock", L"", &location)
        == DevToolsAuthoredState::Available, "hash-covered map resolves exact recorded emitted column");
    ACheck(location.line == 246 && location.column == 25 && location.mapped,
        "raw source coordinates are not replaced; authored coordinates are separately identified");
    ACheck(DevToolsAuthored_FindAttribute(span, L"Text", &value) &&
        value == L"{x:Bind ViewModel.ListHeading, Mode=OneWay}", "mapped reader retains original binding expression");
    {
        HANDLE held = CreateFileW(inventoryPath.c_str(), GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, nullptr,
            OPEN_EXISTING, FILE_FLAG_DELETE_ON_CLOSE, nullptr);
        initialize(root, std::wstring(hash.begin(), hash.end()));
        ACheck(held != INVALID_HANDLE_VALUE && DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock")
            == DevToolsAuthoredState::Available, "inventory the CLI holds delete-on-close is still readable");
        if (held != INVALID_HANDLE_VALUE) CloseHandle(held);
        ACheck(GetFileAttributesW(inventoryPath.c_str()) == INVALID_FILE_ATTRIBUTES,
            "closing the CLI's handle removes the inventory");
        write(inventoryPath, inventory);
    }
    auto partial = inventory;
    partial.insert(1, "\"coordinateExclusions\":[{\"source\":\"MainWindow.xaml\",\"resource\":\"MainWindow.xaml\","
        "\"reason\":\"Saved compiler association is unverified.\"}],");
    write(inventoryPath, partial);
    const auto partialHash = FixtureHash(partial);
    initialize(root, std::wstring(partialHash.begin(), partialHash.end()));
    for (const auto spelling : { L"MainWindow.xaml", L"/MainWindow.xaml", L"\\MainWindow.xaml", L"ms-appx:///MainWindow.xaml" }) {
        ACheck(DevToolsAuthored_ReadElement(spelling, 2, &span, 3, L"TextBlock", L"Title")
            == DevToolsAuthoredState::UnverifiedBuild, "excluded source cannot fall through to plausible original coordinates");
    }
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock")
        == DevToolsAuthoredState::Available, "excluded dictionary does not invalidate another proven source");
    write(inventoryPath, inventory);
    initialize(root, std::wstring(hash.begin(), hash.end()));
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"Button") == DevToolsAuthoredState::Unavailable,
        "mapping does not waive selected runtime type");
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock", L"Wrong") == DevToolsAuthoredState::Unavailable,
        "mapping does not waive selected runtime name");
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 0, L"TextBlock") == DevToolsAuthoredState::Unavailable,
        "mapping never guesses a missing column");
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 144, L"TextBlock") == DevToolsAuthoredState::Available,
        "unique declaration survives uncertain generated ConnectionId width without claiming loaded bytes");
    write(sourcePath, original + " ");
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock") == DevToolsAuthoredState::Stale,
        "source edited after a successful read is rehashed rather than served from the old cache");
    write(sourcePath, original);
    initialize(L"", std::wstring(hash.begin(), hash.end()));
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock") == DevToolsAuthoredState::UnverifiedBuild,
        "an unproved deployed payload cannot use the compiler map");
    initialize(root, std::wstring(64, L'A'));
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 25, L"TextBlock") == DevToolsAuthoredState::UnverifiedBuild,
        "invalid inventory hash cannot fall back to plausible original coordinates");
    initialize(L"", std::wstring(hash.begin(), hash.end()));
    write(xbfPath, "different-deployment");
    initialize(root, std::wstring(hash.begin(), hash.end()));
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock") == DevToolsAuthoredState::UnverifiedBuild,
        "different deployed XBF rejects the selected compiler table");
    write(xbfPath, xbf);
    write(sourcePath, original + " ");
    initialize(root, std::wstring(hash.begin(), hash.end()));
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock") == DevToolsAuthoredState::Stale,
        "changed source bytes fail even when line and column remain plausible");
    DevToolsAuthored_Init(root, 0);
    write(sourcePath, original);
    const auto priPath = root + L"\\resources.pri";
    const std::string pri = "controlled-PRI-host-resource-key-verified-separately";
    write(priPath, pri);
    std::string indexed = inventory;
    indexed.insert(indexed.find("\"elements\""), "\"priHash\":\"" + FixtureHash(pri) + "\",");
    write(inventoryPath, indexed);
    auto indexedHash = FixtureHash(indexed);
    initialize(root, std::wstring(indexedHash.begin(), indexedHash.end()));
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock") == DevToolsAuthoredState::Available,
        "verified whole-PRI identity admits the host-proven embedded resource");
    HANDLE replacement = CreateFileW(priPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    ACheck(replacement == INVALID_HANDLE_VALUE, "native source reader retains the PRI after launcher handoff");
    if (replacement != INVALID_HANDLE_VALUE) CloseHandle(replacement);
    DevToolsAuthored_Init(root, 0);
    std::string pathIndexed = indexed;
    pathIndexed.insert(pathIndexed.find("\"elements\""), "\"payloadPaths\":[\"MainPage.xbf\"],");
    write(inventoryPath, pathIndexed);
    auto pathHash = FixtureHash(pathIndexed);
    initialize(root, std::wstring(pathHash.begin(), pathHash.end()));
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock") == DevToolsAuthoredState::Available,
        "PRI Path admission verifies the referenced XBF as well as the index");
    replacement = CreateFileW(xbfPath.c_str(), GENERIC_WRITE, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    ACheck(replacement == INVALID_HANDLE_VALUE, "native source reader retains referenced PRI Path bytes");
    if (replacement != INVALID_HANDLE_VALUE) CloseHandle(replacement);
    DevToolsAuthored_Init(root, 0);
    write(xbfPath, "changed-reference");
    initialize(root, std::wstring(pathHash.begin(), pathHash.end()));
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock") == DevToolsAuthoredState::UnverifiedBuild,
        "unchanged PRI cannot conceal a changed referenced XBF");
    DevToolsAuthored_Init(root, 0);
    write(xbfPath, xbf);
    write(inventoryPath, indexed);
    DevToolsAuthored_Init(root, 0);
    write(priPath, "changed-PRI");
    initialize(root, std::wstring(indexedHash.begin(), indexedHash.end()));
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock") == DevToolsAuthoredState::UnverifiedBuild,
        "changed PRI cannot reuse the selected resource association");
    DevToolsAuthored_Init(root, 0);
    write(priPath, pri);
    indexed.insert(1, "\"coordinatesAdvisory\":true,");
    write(inventoryPath, indexed);
    indexedHash = FixtureHash(indexed);
    initialize(root, std::wstring(indexedHash.begin(), indexedHash.end()));
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock") == DevToolsAuthoredState::UnverifiedBuild,
        "explicit advisory inventories cannot bypass disk admission");
    ACheck(DevToolsAuthored_ReadElement(L"MainWindow.xaml", 2, &span, 3, L"TextBlock", L"Title") == DevToolsAuthoredState::Available,
        "an advisory map does not disable unaffected original declarations");
    DevToolsAuthored_Init(root, 0);
    auto likely = inventory;
    likely.insert(likely.find("\"elements\""),
        "\"attribution\":\"likely\",\"evidence\":\"Compiler artifacts are missing; line preservation unverified.\",");
    write(inventoryPath, likely);
    const auto likelyHash = FixtureHash(likely);
    initialize(L"", std::wstring(likelyHash.begin(), likelyHash.end()));
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock", L"", &location)
        == DevToolsAuthoredState::Likely && location.mapped && !location.evidence.empty() && location.column == 25,
        "missing compiled proof yields explicit likely source with authored coordinates and missing evidence");
    ACheck(DevToolsAuthored_ReadElement(L"", 246, &span, 128, L"TextBlock") == DevToolsAuthoredState::NoSourceInfo,
        "likely attribution never invents a missing runtime URI");
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"Button") == DevToolsAuthoredState::Unavailable,
        "likely attribution still rejects runtime type mismatch");
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock", L"Other") == DevToolsAuthoredState::Unavailable,
        "likely attribution still rejects runtime name mismatch");
    write(sourcePath, original + " ");
    ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", 246, &span, 128, L"TextBlock") == DevToolsAuthoredState::Stale,
        "likely attribution rehashes current source after disk edits or hot reload");
    DevToolsAuthored_Init(root, 0);
    DeleteFileW(priPath.c_str());
    DeleteFileW(sourcePath.c_str()); DeleteFileW(xbfPath.c_str()); DeleteFileW(inventoryPath.c_str());
    DeleteFileW((root + L"\\MainWindow.xaml").c_str()); RemoveDirectoryW(root.c_str());
}

static void Test_UniqueAuthoredLineCountsEveryOpeningSpan()
{
    const auto root = MakeFixture();
    const auto sourcePath = root + L"\\MainPage.xaml", xbfPath = root + L"\\MainPage.xbf", inventoryPath = root + L"\\inventory.json";
    auto write = [](const std::wstring& path, const std::string& bytes) {
        std::ofstream file(path, std::ios::binary | std::ios::trunc); file << bytes;
    };
    const std::string xbf = "disk-identity-contract-not-loaded-XBF";
    write(xbfPath, xbf);
    auto element = [](int start, int end, int column, const std::string& type, const std::string& name) {
        return "{\"line\":" + std::to_string(start) + ",\"endLine\":" + std::to_string(end) +
            ",\"column\":" + std::to_string(column) +
            ",\"type\":\"" + type + "\",\"name\":\"" + name + "\"}";
    };
    auto check = [&](const std::string& source, const std::string& elements, unsigned int line, const wchar_t* name,
        DevToolsAuthoredState expected, const char* message) {
        DevToolsAuthored_Init(root, 0);
        write(sourcePath, source);
        const std::string inventory = "{\"coordinates\":[{\"source\":\"MainPage.xaml\",\"resource\":\"MainPage.xaml\","
            "\"sourceHash\":\"" + FixtureHash(source) + "\",\"xbfHash\":\"" + FixtureHash(xbf) + "\",\"elements\":[" + elements + "]}]}";
        write(inventoryPath, inventory);
        const auto hash = FixtureHash(inventory);
        DevToolsAuthored_InitCoordinates(inventoryPath, std::wstring(hash.begin(), hash.end()), root);
        std::wstring span;
        ACheck(DevToolsAuthored_ReadElement(L"MainPage.xaml", line, &span, 128, L"TextBlock", name) == expected, message);
    };
    check("<Grid><TextBlock x:Name='Heading'/></Grid>",
        element(1, 1, 1, "Grid", "") + "," + element(1, 1, 7, "TextBlock", "Heading"), 1, L"Heading",
        DevToolsAuthoredState::Unavailable, "different type parent on the same line still makes the declaration ambiguous");
    check("<Border\n/><TextBlock x:Name='Heading'/>",
        element(1, 2, 1, "Border", "") + "," + element(2, 2, 3, "TextBlock", "Heading"), 2, L"Heading",
        DevToolsAuthoredState::Unavailable, "continuation span counts before filtering to the matching named element");
    check("<TextBlock x:Name='Heading'/><TextBlock/>",
        element(1, 1, 1, "TextBlock", "Heading") + "," + element(1, 1, 28, "TextBlock", ""), 1, L"Heading",
        DevToolsAuthoredState::Unavailable, "runtime name cannot select one of multiple declarations on the line");
    check("<TextBlock\n Text='binding'\n/>", element(1, 3, 1, "TextBlock", ""), 2, L"",
        DevToolsAuthoredState::Available, "unique multiline opening span maps its continuation to the authored start");
    check("<TextBlock x:Name='Heading'/>", element(1, 1, 1, "TextBlock", "Heading"), 1, L"",
        DevToolsAuthoredState::Unavailable, "missing runtime name does not waive an authored name mismatch");
    check("<DataTemplate>\n<TextBlock/>\n</DataTemplate>",
        element(1, 1, 1, "DataTemplate", "") + "," + element(2, 2, 1, "TextBlock", ""), 2, L"",
        DevToolsAuthoredState::Available, "one template declaration is not multiple declarations because instances repeat");
    DevToolsAuthored_Init(root, 0);
    DeleteFileW(sourcePath.c_str()); DeleteFileW(xbfPath.c_str()); DeleteFileW(inventoryPath.c_str());
    DeleteFileW((root + L"\\MainWindow.xaml").c_str()); RemoveDirectoryW(root.c_str());
}

static void Test_ClassDeclarationIdentity()
{
    const auto root = MakeFixture();
    const auto path = root + L"\\Class.xaml";
    const auto read = [&](const char* markup, const wchar_t* type, const wchar_t* name, std::wstring* out) {
        std::ofstream file(path, std::ios::binary | std::ios::trunc);
        file << markup;
        file.close();
        DevToolsAuthored_Init(root, 0);
        return DevToolsAuthored_ReadElement(L"Class.xaml", 1, out, 2, type, name);
    };
    std::wstring span, value;
    ACheck(read("<Page xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' "
        "x:Class='App.HomePage' x:Name='Home' Tag='{x:Bind Vm.Title}'/>",
        L"App.HomePage", L"Home", &span) == DevToolsAuthoredState::Available,
        "class identity: runtime HomePage resolves its authored Page root");
    ACheck(DevToolsAuthored_FindAttribute(span, L"Tag", &value) && value == L"{x:Bind Vm.Title}",
        "class identity: Page root compiled binding remains available");
    ACheck(read("<Page xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'\n"
        "xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'\n"
        "x:Class='App.HomePage' Tag='{x:Bind Vm.Title}'>\n<Grid/></Page>",
        L"App.HomePage", L"", &span) == DevToolsAuthoredState::Available &&
        DevToolsAuthored_FindAttribute(span, L"Tag", &value) && value == L"{x:Bind Vm.Title}",
        "class identity: a normal multiline non-self-closing Page root retains its compiled property");
    ACheck(read("<UserControl xmlns:lang='http://schemas.microsoft.com/winfx/2006/xaml' "
        "lang:Class='App.ReviewCard' Tag='{x:Bind Card.Title}'/>",
        L"App.ReviewCard", L"", &span) == DevToolsAuthoredState::Available,
        "class identity: namespace alias resolves a UserControl declaration");
    ACheck(DevToolsAuthored_FindAttribute(span, L"Tag", &value) && value == L"{x:Bind Card.Title}",
        "class identity: UserControl root property authorship is retained");
    ACheck(read("<Page xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' "
        "x:Class='Other.HomePage'/>", L"App.HomePage", L"", &span) == DevToolsAuthoredState::Unavailable && span.empty(),
        "class identity: another namespace with the same short class name is refused");
    ACheck(read("<Page xmlns:x='urn:not-xaml' x:Class='App.HomePage'/>",
        L"App.HomePage", L"", &span) == DevToolsAuthoredState::Unavailable,
        "class identity: a lookalike Class attribute outside the XAML namespace is refused");
    ACheck(read("<Page Tag=\"x:Class='App.HomePage'\"/>",
        L"App.HomePage", L"", &span) == DevToolsAuthoredState::Unavailable,
        "class identity: quoted prose cannot spoof an authored class");
    ACheck(read("<Page xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' "
        "x:Class='App.HomePage' x:Name='Other'/>",
        L"App.HomePage", L"Home", &span) == DevToolsAuthoredState::Unavailable,
        "class identity: a matching class does not bypass selected element name validation");
    ACheck(read("<Grid/>", L"Microsoft.UI.Xaml.Controls.Page", L"", &span) == DevToolsAuthoredState::Unavailable,
        "class identity: wrong ordinary tag still refuses attribution");
    DeleteFileW(path.c_str());
    DeleteFileW((root + L"\\MainWindow.xaml").c_str());
    RemoveDirectoryW(root.c_str());
}

static bool MakeJunction(const std::wstring& link, const std::wstring& target)
{
    if (!CreateDirectoryW(link.c_str(), nullptr)) return false;
    HANDLE handle = CreateFileW(link.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING,
        FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, nullptr);
    if (handle == INVALID_HANDLE_VALUE) { RemoveDirectoryW(link.c_str()); return false; }
    struct MountPoint {
        DWORD tag;
        WORD dataLength, reserved, substituteOffset, substituteLength, printOffset, printLength;
        wchar_t paths[2048];
    } data{};
    const auto substitute = L"\\??\\" + target;
    data.tag = IO_REPARSE_TAG_MOUNT_POINT;
    data.substituteLength = static_cast<WORD>(substitute.size() * sizeof(wchar_t));
    data.printOffset = data.substituteLength + sizeof(wchar_t);
    data.printLength = static_cast<WORD>(target.size() * sizeof(wchar_t));
    data.dataLength = static_cast<WORD>(8 + data.printOffset + data.printLength + sizeof(wchar_t));
    wcscpy_s(data.paths, substitute.c_str());
    wcscpy_s(data.paths + data.printOffset / sizeof(wchar_t),
        _countof(data.paths) - data.printOffset / sizeof(wchar_t), target.c_str());
    DWORD returned = 0;
    const bool ok = DeviceIoControl(handle, FSCTL_SET_REPARSE_POINT, &data,
        data.dataLength + 8, nullptr, 0, &returned, nullptr) != FALSE;
    CloseHandle(handle);
    if (!ok) RemoveDirectoryW(link.c_str());
    return ok;
}

static void Test_CanonicalContainmentAndSpellings()
{
    const auto root = MakeFixture();
    const auto source = root + L"\\source";
    const auto outside = root + L"\\outside";
    ACheck(CreateDirectoryW(source.c_str(), nullptr) != FALSE, "owned source directory is created");
    ACheck(CreateDirectoryW(outside.c_str(), nullptr) != FALSE, "owned outside directory is created");
    ACheck(MoveFileW((root + L"\\MainWindow.xaml").c_str(), (source + L"\\MainWindow.xaml").c_str()) != FALSE,
        "source file is inside the selected source root");
    ACheck(CopyFileW((source + L"\\MainWindow.xaml").c_str(), (outside + L"\\Outside.xaml").c_str(), TRUE) != FALSE,
        "outside source fixture exists independently");
    const auto alias = source + L"\\alias";
    const auto escape = source + L"\\escape";
    ACheck(MakeJunction(alias, source), "contained junction is created without elevation");
    ACheck(MakeJunction(escape, outside), "escape junction targets only our owned outside fixture");
    DevToolsAuthored_Init(source, 0);
    std::wstring resolved, text;
    ACheck(DevToolsSourcePath_ResolveExisting(source, L"ms-appx:///alias/MainWindow.xaml", &resolved),
        "canonical resolver permits a contained junction");
    for (const auto spelling : { L"ms-appx:///MainWindow.xaml", L"MainWindow.xaml", L"/MainWindow.xaml",
            L"\\MainWindow.xaml", L"alias\\MainWindow.xaml", L"ms-appx:///alias/MainWindow.xaml" }) {
        ACheck(DevToolsAuthored_ReadElement(spelling, 1, &text) == DevToolsAuthoredState::Available && text == L"<Page>",
            "legitimate source spelling preserves the authored declaration");
    }
    const auto inventoryPath = source + L"\\excluded.json";
    const std::string excluded = "{\"coordinates\":[],\"coordinateExclusions\":[{\"source\":\"MainWindow.xaml\","
        "\"resource\":\"MainWindow.xaml\",\"reason\":\"Compiler association unavailable\"}]}";
    { std::ofstream output(inventoryPath, std::ios::binary); output << excluded; }
    const auto excludedHash = FixtureHash(excluded);
    DevToolsAuthored_InitCoordinates(inventoryPath, std::wstring(excludedHash.begin(), excludedHash.end()), source);
    for (const auto spelling : { L"alias\\MainWindow.xaml", L"ms-appx:///alias/MainWindow.xaml" }) {
        ACheck(DevToolsAuthored_ReadElement(spelling, 1, &text) == DevToolsAuthoredState::UnverifiedBuild,
            "a contained filesystem alias cannot bypass the source exclusion");
    }
    DevToolsAuthored_Init(source, 0);
    DeleteFileW(inventoryPath.c_str());
    ACheck(!DevToolsSourcePath_ResolveExisting(source, L"ms-appx:///escape/Outside.xaml", &resolved),
        "canonical resolver refuses the outside junction target");
    for (const auto spelling : { L"ms-appx:///escape/Outside.xaml", L"escape\\Outside.xaml" }) {
        ACheck(DevToolsAuthored_ReadElement(spelling, 1, &text) == DevToolsAuthoredState::NoFile && text.empty(),
            "authored reads share canonical junction containment");
    }
    for (const auto spelling : { L"../outside/Outside.xaml", L"ms-appx:///../outside/Outside.xaml", L"missing.xaml" }) {
        ACheck(DevToolsAuthored_ReadElement(spelling, 1, &text) == DevToolsAuthoredState::NoFile && text.empty(),
            "invalid or unavailable source still returns noFile and clears output");
    }
    DevToolsAuthored_Init(source, 1);
    ACheck(DevToolsAuthored_ReadElement(L"MainWindow.xaml", 1, &text) == DevToolsAuthoredState::Stale && text.empty(),
        "canonical reading preserves source staleness");
    ACheck(DevToolsAuthored_ReadElement(L"", 1, &text) == DevToolsAuthoredState::NoSourceInfo && text.empty(),
        "missing source metadata preserves its distinct state");
    DevToolsAuthored_Init(L"", 0);
    ACheck(DevToolsAuthored_ReadElement(L"MainWindow.xaml", 1, &text) == DevToolsAuthoredState::NoFile,
        "missing source root remains unavailable");
    ACheck(RemoveDirectoryW(alias.c_str()) != FALSE, "contained junction is removed without traversing it");
    ACheck(RemoveDirectoryW(escape.c_str()) != FALSE, "escape junction is removed without traversing it");
    DeleteFileW((source + L"\\MainWindow.xaml").c_str());
    DeleteFileW((outside + L"\\Outside.xaml").c_str());
    RemoveDirectoryW(source.c_str());
    RemoveDirectoryW(outside.c_str());
    RemoveDirectoryW(root.c_str());
}

static void Test_SourceQualifiedAnchors()
{
    using Handle = unsigned long long;
    std::map<Handle, std::wstring> names, types, sources;
    std::map<Handle, Handle> parents;
    std::map<Handle, std::vector<Handle>> children;
    const auto add = [&](Handle h, Handle parent, const wchar_t* name, const wchar_t* type, const wchar_t* source) {
        names[h] = name; types[h] = type; sources[h] = source;
        if (parent) { parents[h] = parent; children[parent].push_back(h); }
    };
    const auto capture = [&](Handle h) {
        return DevToolsCommentAnchor::Capture(h, names, types, sources, parents, children);
    };
    const auto resolve = [&](const std::wstring& anchor) {
        return DevToolsCommentAnchor::Resolve(anchor, names, types, sources, parents, children);
    };
    const auto clear = [&] { names.clear(); types.clear(); sources.clear(); parents.clear(); children.clear(); };
    const auto home = [&](Handle base) {
        add(base, 0, L"", L"HomePage", L"ms-appx:///HomePage.xaml");
        add(base + 1, base, L"ActionButton", L"Button", L"ms-appx:///HomePage.xaml");
        add(base + 2, base, L"CardOne", L"ReviewCard", L"ms-appx:///HomePage.xaml");
        add(base + 3, base, L"CardTwo", L"ReviewCard", L"ms-appx:///HomePage.xaml");
        add(base + 4, base + 2, L"CardAction", L"Button", L"ms-appx:///ReviewCard.xaml");
        add(base + 5, base + 3, L"CardAction", L"Button", L"ms-appx:///ReviewCard.xaml");
        add(base + 6, base, L"", L"TextBlock", L"ms-appx:///HomePage.xaml");
    };
    home(100);
    const auto action = capture(101), cardOne = capture(104), cardTwo = capture(105);
    const auto heading = capture(106);
    ACheck(!heading.empty() && resolve(heading) == 106,
        "unnamed heading pin uses source and structural context, not dynamic text or historical columns");
    ACheck(!action.empty() && !cardOne.empty() && cardOne != cardTwo,
        "H1: repeated UserControl names retain distinct authored parent anchors");
    ACheck(DevToolsCommentAnchor::IsUnique(Handle{104}, cardOne, names, types, sources, parents, children),
        "a named authored ancestor distinguishes a captured source scope");
    add(200, 0, L"", L"ReviewDialog", L"ms-appx:///ReviewDialog.xaml");
    add(201, 200, L"DialogEditor", L"TextBox", L"ms-appx:///ReviewDialog.xaml");
    add(202, 200, L"ActionButton", L"Button", L"ms-appx:///ReviewDialog.xaml");
    const auto dialog = capture(201);
    ACheck(resolve(action) == 101 && resolve(dialog) == 201,
        "H1: opening a same-name dialog never steals the HomePage note");
    for (Handle cycle = 1; cycle <= 3; ++cycle) {
        clear();
        const Handle base = cycle * 1000;
        add(base, 0, L"", L"DetailsPage", L"ms-appx:///DetailsPage.xaml");
        add(base + 1, base, L"ActionButton", L"Button", L"ms-appx:///DetailsPage.xaml");
        ACheck(resolve(action) == 0 && resolve(cardOne) == 0 && resolve(cardTwo) == 0,
            "H1: fresh DetailsPage leaves HomePage and ReviewCard notes unplaced");
        clear(); home(base);
        ACheck(resolve(heading) == base + 6,
            "unnamed heading pin survives new handles when source, ancestry and sibling slot are unchanged");
        ACheck(resolve(action) == base + 1 && resolve(cardOne) == base + 4 && resolve(cardTwo) == base + 5,
            "H1: fresh HomePage restores both distinct card anchors and its own note");
        add(base + 100, 0, L"", L"ReviewDialog", L"ms-appx:///ReviewDialog.xaml");
        add(base + 101, base + 100, L"DialogEditor", L"TextBox", L"ms-appx:///ReviewDialog.xaml");
        add(base + 102, base + 100, L"ActionButton", L"Button", L"ms-appx:///ReviewDialog.xaml");
        ACheck(resolve(dialog) == base + 101 && resolve(action) == base + 1,
            "H1: reopened dialog restores its own note without stealing a same-name HomePage note");
    }
    clear();
    home(5000); home(6000);
    ACheck(capture(5001) == capture(6001),
        "H1: identical source trees in two windows do not invent persistent window identity");
    ACheck(!DevToolsCommentAnchor::IsUnique(Handle{5001}, action, names, types, sources, parents, children),
        "repeated source scopes cannot auto-confirm one captured instance");
    sources.erase(6001);
    const wchar_t* reason = nullptr;
    ACheck(!DevToolsCommentAnchor::IsUnique(Handle{5001}, action, names, types, sources, parents, children, &reason) &&
        std::wstring(reason) == L"unclassified-peer",
        "a potential repeated instance with unclassified source prevents uniqueness");
    sources[6001] = L"ms-appx:///HomePage.xaml";
    sources.erase(6000);
    ACheck(!DevToolsCommentAnchor::IsUnique(Handle{5001}, action, names, types, sources, parents, children, &reason) &&
        std::wstring(reason) == L"unclassified-ancestor",
        "a potential repeated instance with unclassified ancestry prevents uniqueness");
    sources[6000] = L"ms-appx:///HomePage.xaml";
    sources[6001] = L"";
    ACheck(DevToolsCommentAnchor::IsUnique(Handle{5001}, action, names, types, sources, parents, children),
        "classified no-source controls do not invent competing authored instances");
    sources[6001] = L"ms-appx:///HomePage.xaml";
    ACheck(resolve(action) == 5001,
        "H1: the source-wide anchor may resolve on either identical-source window");
    const auto before = capture(5004);
    names[5002] = L"DifferentCard";
    ACheck(capture(5004) != before, "H1: a changed authored parent cannot satisfy the captured namescope");
    sources[5001].clear();
    ACheck(capture(5001).empty(), "H1: missing captured source is unavailable rather than name-only");
    ACheck(resolve(L"ActionButton") == 0, "H1: an unqualified name never guesses a live placement");
}

static std::wstring ReadXmlAttribute(const std::wstring& attribute)
{
    const std::wstring xml = L"\xFEFF<note Text=\"" + attribute + L"\"/>";
    IStream* stream = nullptr;
    IXmlReader* reader = nullptr;
    std::wstring value;
    if (SUCCEEDED(CreateStreamOnHGlobal(nullptr, TRUE, &stream))) {
        ULONG written = 0;
        const ULONG bytes = static_cast<ULONG>(xml.size() * sizeof(wchar_t));
        const HRESULT write = stream->Write(xml.data(), bytes, &written);
        LARGE_INTEGER zero{};
        stream->Seek(zero, STREAM_SEEK_SET, nullptr);
        if (SUCCEEDED(write) && written == bytes &&
            SUCCEEDED(CreateXmlReader(__uuidof(IXmlReader), reinterpret_cast<void**>(&reader), nullptr)) &&
            SUCCEEDED(reader->SetInput(stream))) {
            XmlNodeType node{};
            while (reader->Read(&node) == S_OK) {
                if (node != XmlNodeType_Element) continue;
                if (reader->MoveToAttributeByName(L"Text", nullptr) == S_OK) {
                    const wchar_t* text = nullptr; UINT length = 0;
                    if (SUCCEEDED(reader->GetValue(&text, &length))) value.assign(text, length);
                }
                break;
            }
        }
    }
    if (reader) reader->Release();
    if (stream) stream->Release();
    return value;
}

static void Test_ExactCommentTextAndHistoricalLabels()
{
    for (const auto& text : {
        std::wstring(L"Repeat no edit FIRST\nSECOND \u6771\u4eac"),
        std::wstring(L"Upsert preserves exact\r\nsecond line \u03a9 \u6771\u4eac"),
        std::wstring(L"  \"caf\u00e9\" & <tag>\t\r\nline two \U0001f642\\  "),
        std::wstring(L"Single CR\rline") }) {
        const auto attribute = DevToolsXmlEscape(text, true);
        ACheck(ReadXmlAttribute(attribute) == text,
            "M3: real XML attribute parsing preserves exact Unicode, LF, CRLF, tabs and outer whitespace");
            std::wstring oldAttribute = attribute;
            for (const auto& entity : { std::make_pair(L"&#xA;", L'\n'), std::make_pair(L"&#xD;", L'\r'),
                                       std::make_pair(L"&#x9;", L'\t') }) {
                size_t pos = 0;
                while ((pos = oldAttribute.find(entity.first, pos)) != std::wstring::npos) {
                    oldAttribute.replace(pos, wcslen(entity.first), 1, entity.second);
                    ++pos;
                }
            }
            ACheck(ReadXmlAttribute(oldAttribute) != text,
                "M3 negative control: the former literal XML whitespace prefill loses exact text");
        int argc = 0;
        const std::wstring command = L"winapp -t " + DevToolsCommentText::QuoteArgument(text);
        wchar_t** argv = CommandLineToArgvW(command.c_str(), &argc);
        ACheck(argv && argc == 3 && argv[2] == text,
            "M3: CreateProcess argument quoting round-trips exact comment prose without flattening");
        if (argv) LocalFree(argv);
        ACheck(DevToolsCommentText::Unchanged(text, text) &&
            DevToolsCommentText::Unchanged(DevToolsCommentText::NormalizeLineEndings(text), text),
            "M3: no-edit prefill/blur (including TextBox line normalization) skips persistence and updatedAt");
        ACheck(!DevToolsCommentText::Unchanged(text + L" edit", text),
            "M3: an actual text edit still requires persistence");
    }
    ACheck(DevToolsXmlEscape(L"{Binding is just prose}", true) == L"{}{Binding is just prose}",
        "M3: a leading brace is XAML literal text, not a markup extension");
    ACheck(!DevToolsCommentText::Unchanged(L"  note", L"note"),
        "M3: intentional leading/trailing whitespace is not discarded by the no-op check");
    ACheck(DevToolsCommentText::IsBlank(L" \r\n\t") && !DevToolsCommentText::IsBlank(L" \u6771 "),
        "M3: clear/blank detection is independent from exact saved text");
    ACheck(DevToolsCommentText::SourceLabel(L"HomePage.xaml", 7) == L"Historical: HomePage.xaml:7",
        "M6: stored line 7 is explicitly historical when the rebuilt live element moves to line 12");
    ACheck(DevToolsCommentText::SourceLabel(L"", 7).empty(),
        "M6: no captured file does not manufacture source coordinates");
    ACheck(DevToolsCommentText::SavedStatus(false, true) == L"Saved." &&
        DevToolsCommentText::SavedStatus(true, true) == L"Saved on the host.",
        "a comment on authored source reports a plain save");
    ACheck(DevToolsCommentText::SavedStatus(false, false) == L"Saved. Not linked to source." &&
        DevToolsCommentText::SavedStatus(true, false) == L"Saved on the host. Not linked to source.",
        "a comment on an element without source says it is not linked to source");
}

int RunAuthoredTests()
{
    std::printf("DevToolsAuthored tests -- one source read serves property values and full XAML\n");
    Test_ElementSpanIsReusableAndPreservesAuthoredText();
    Test_RecordedCoordinatesOutsideCurrentDeclaration();
    Test_HashCoveredCoordinates();
    Test_UniqueAuthoredLineCountsEveryOpeningSpan();
    Test_ClassDeclarationIdentity();
    Test_CanonicalContainmentAndSpellings();
    Test_SourceQualifiedAnchors();
    Test_ExactCommentTextAndHistoricalLabels();
    return g_authoredFailures;
}

#ifdef WINAPP_DEVTOOLS_AUTHORED_TESTS_ONLY
int main()
{
    const int failures = RunAuthoredTests();
    std::printf("Authored/comments acceptance failures: %d\n", g_authoredFailures);
    return failures || g_authoredFailures ? 1 : 0;
}
#endif
