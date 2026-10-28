// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <Windows.h>
#include <UIAutomation.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>
#include <stdexcept>
#include <string>

using Microsoft::WRL::ComPtr;

static void Require(HRESULT hr)
{
    if (FAILED(hr)) throw std::runtime_error("UI Automation call failed: " + std::to_string(hr));
}

static ComPtr<IUIAutomationElement> Find(IUIAutomation* automation, IUIAutomationElement* root, const wchar_t* id)
{
    VARIANT value;
    VariantInit(&value);
    value.vt = VT_BSTR;
    value.bstrVal = SysAllocString(id);
    ComPtr<IUIAutomationCondition> condition;
    const HRESULT hr = automation->CreatePropertyCondition(UIA_AutomationIdPropertyId, value, &condition);
    VariantClear(&value);
    Require(hr);
    ComPtr<IUIAutomationElement> element;
    const ULONGLONG deadline = GetTickCount64() + 3000;
    do {
        Require(root->FindFirst(TreeScope_Subtree, condition.Get(), &element));
        if (element) break;
        Sleep(20);
    } while (GetTickCount64() < deadline);
    if (!element) throw std::runtime_error("Required inspector element was not found");
    return element;
}

static std::wstring Id(IUIAutomationElement* element)
{
    if (!element) return L"(null)";
    BSTR id = nullptr;
    Require(element->get_CurrentAutomationId(&id));
    const std::wstring result = id ? id : L"";
    SysFreeString(id);
    return result;
}

static unsigned checks = 0, failures = 0;

static void Check(bool condition, const char* message)
{
    ++checks;
    if (!condition) {
        ++failures;
        std::printf("FAIL %s\n", message);
    }
}

static void CheckCaption(IUIAutomation* automation, IUIAutomationElement* row, const wchar_t* expected)
{
    VARIANT value;
    VariantInit(&value);
    value.vt = VT_I4;
    value.lVal = UIA_TextControlTypeId;
    ComPtr<IUIAutomationCondition> condition;
    Require(automation->CreatePropertyCondition(UIA_ControlTypePropertyId, value, &condition));
    std::wstring actual;
    const ULONGLONG deadline = GetTickCount64() + 3000;
    do {
        ComPtr<IUIAutomationElement> caption;
        Require(row->FindFirst(TreeScope_Descendants, condition.Get(), &caption));
        if (caption) {
            BSTR text = nullptr;
            Require(caption->get_CurrentName(&text));
            actual = text ? text : L"";
            SysFreeString(text);
        }
        if (actual == expected) break;
        Sleep(20);
    } while (GetTickCount64() < deadline);
    Check(actual == expected, "rendered styled caption preserves the type, name and preview text");
}

static int IntegerProperty(IUIAutomationElement* element, PROPERTYID property)
{
    VARIANT value;
    VariantInit(&value);
    Require(element->GetCurrentPropertyValue(property, &value));
    const int result = value.vt == VT_I4 ? value.lVal : -1;
    VariantClear(&value);
    return result;
}

static void CheckSelection(IUIAutomation* automation, IUIAutomationElement* tree, const wchar_t* expected)
{
    auto row = Find(automation, tree, expected);
    // WinUI's tree returns no items from Selection.GetSelection. Its native items expose IsSelected.
    VARIANT value;
    VariantInit(&value);
    value.vt = VT_BOOL;
    value.boolVal = VARIANT_TRUE;
    ComPtr<IUIAutomationCondition> condition;
    Require(automation->CreatePropertyCondition(UIA_SelectionItemIsSelectedPropertyId, value, &condition));
    ComPtr<IUIAutomationElementArray> selected;
    Require(tree->FindAll(TreeScope_Descendants, condition.Get(), &selected));
    int count = 0;
    Require(selected->get_Length(&count));
    Check(count == 1, "exactly one visible native tree item is selected");
    if (count == 1) {
        ComPtr<IUIAutomationElement> element;
        Require(selected->GetElement(0, &element));
        Check(Id(element.Get()) == expected, "selection returns the inspected row after revealing it");
    }
    ComPtr<IUIAutomationSelectionItemPattern> item;
    Require(row->GetCurrentPatternAs(UIA_SelectionItemPatternId, IID_PPV_ARGS(&item)));
    BOOL isSelected = FALSE;
    Require(item->get_CurrentIsSelected(&isSelected));
    Check(isSelected != FALSE, "revealed row reports IsSelected");
    ComPtr<IUIAutomationElement> container;
    Require(item->get_CurrentSelectionContainer(&container));
    Check(Id(container.Get()) == Id(tree), "native tree item reports its selection container");
}

static void CheckLevel(IUIAutomation* automation, IUIAutomationElement* tree, const wchar_t* id,
                       int expected, const char* phase)
{
    int actual = -1;
    const ULONGLONG deadline = GetTickCount64() + 3000;
    do {
        auto row = Find(automation, tree, id);
        actual = IntegerProperty(row.Get(), UIA_LevelPropertyId);
        if (actual == expected) break;
        Sleep(20);
    } while (GetTickCount64() < deadline);
    ++checks;
    if (actual != expected) {
        ++failures;
        std::wprintf(L"FAIL %hs: %ls level is %d, expected %d\n", phase, id, actual, expected);
    }
}

static void SetFilter(IUIAutomationValuePattern* filter, const wchar_t* query)
{
    BSTR value = SysAllocString(query);
    const HRESULT hr = filter->SetValue(value);
    SysFreeString(value);
    Require(hr);
}

static void CheckNextSibling(IUIAutomationTreeWalker* walker, IUIAutomationElement* row, const wchar_t* expected)
{
    std::wstring actual;
    const ULONGLONG deadline = GetTickCount64() + 3000;
    do {
        ComPtr<IUIAutomationElement> sibling;
        Require(walker->GetNextSiblingElement(row, &sibling));
        actual = Id(sibling.Get());
        if (actual == expected) break;
        Sleep(20);
    } while (GetTickCount64() < deadline);
    ++checks;
    if (actual != expected) {
        ++failures;
        std::wprintf(L"FAIL filtered sibling: %ls next sibling is %ls, expected %ls\n",
            Id(row).c_str(), actual.c_str(), expected);
    }
}

static int Run(HWND hwnd)
{
    ComPtr<IUIAutomation> automation;
    Require(CoCreateInstance(CLSID_CUIAutomation, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&automation)));
    ComPtr<IUIAutomationTreeWalker> walker;
    Require(automation->get_ControlViewWalker(&walker));
    ComPtr<IUIAutomationElement> window;
    Require(automation->ElementFromHandle(hwnd, &window));
    auto tree = Find(automation.Get(), window.Get(), L"WinAppDevToolsTree");
    auto root = Find(automation.Get(), tree.Get(), L"TreeNode0");
    auto title = Find(automation.Get(), tree.Get(), L"TreeNode1");
    auto filterElement = Find(automation.Get(), window.Get(), L"TreeFilter");
    ComPtr<IUIAutomationValuePattern> filter;
    Require(filterElement->GetCurrentPatternAs(UIA_ValuePatternId, IID_PPV_ARGS(&filter)));

    CheckLevel(automation.Get(), tree.Get(), L"TreeNode0", 1, "initial root");
    CheckLevel(automation.Get(), tree.Get(), L"TreeNode1", 2, "initial leaf");
    CheckLevel(automation.Get(), tree.Get(), L"TreeNode3", 3, "initial branch");
    CheckLevel(automation.Get(), tree.Get(), L"TreeNode6", 4, "initial child");
    CheckCaption(automation.Get(), title.Get(), L"TitleBar  #AppTitleBar");
    CheckCaption(automation.Get(), Find(automation.Get(), tree.Get(), L"TreeNode4").Get(),
        L"TextBlock  \u201CWinUI Sample App\u201D");
    try {
        ComPtr<IUIAutomationSelectionItemPattern> titleSelection;
        Require(title->GetCurrentPatternAs(UIA_SelectionItemPatternId, IID_PPV_ARGS(&titleSelection)));
        Require(titleSelection->Select());
        ComPtr<IUIAutomationExpandCollapsePattern> rootExpansion;
        Require(root->GetCurrentPatternAs(UIA_ExpandCollapsePatternId, IID_PPV_ARGS(&rootExpansion)));
        Require(rootExpansion->Collapse());
        CheckLevel(automation.Get(), tree.Get(), L"TreeNode0", 1, "collapsed root");
        Require(rootExpansion->Expand());
        CheckSelection(automation.Get(), tree.Get(), L"TreeNode1");
        SetFilter(filter.Get(), L"StackPanel");
        CheckLevel(automation.Get(), tree.Get(), L"TreeNode3", 1, "filter excludes selection");
        SetFilter(filter.Get(), L"");
        CheckSelection(automation.Get(), tree.Get(), L"TreeNode1");
        // WinUI virtualizes item containers. Reacquire rows after filtering instead of retaining recycled visuals.
        for (int pass = 0; pass < 2; ++pass) {
            SetFilter(filter.Get(), L"StackPanel");
            CheckLevel(automation.Get(), tree.Get(), L"TreeNode3", 1, "filtered branch");
            CheckLevel(automation.Get(), tree.Get(), L"TreeNode6", 2, "filtered child");
            auto filteredBranch = Find(automation.Get(), tree.Get(), L"TreeNode3");
            ComPtr<IUIAutomationExpandCollapsePattern> filteredExpansion;
            Require(filteredBranch->GetCurrentPatternAs(UIA_ExpandCollapsePatternId, IID_PPV_ARGS(&filteredExpansion)));
            Require(filteredExpansion->Collapse());
            CheckLevel(automation.Get(), tree.Get(), L"TreeNode6", 2, "matching child survives collapse");
            SetFilter(filter.Get(), L"");
            CheckLevel(automation.Get(), tree.Get(), L"TreeNode3", 3, "restored branch");
            CheckLevel(automation.Get(), tree.Get(), L"TreeNode6", 4, "restored child");
            CheckSelection(automation.Get(), tree.Get(), L"TreeNode1");
            SetFilter(filter.Get(), L"AppTitleBar");
            CheckLevel(automation.Get(), tree.Get(), L"TreeNode1", 1, "filtered leaf");
            CheckSelection(automation.Get(), tree.Get(), L"TreeNode1");
            SetFilter(filter.Get(), L"");
            CheckLevel(automation.Get(), tree.Get(), L"TreeNode1", 2, "restored leaf");
            CheckCaption(automation.Get(), Find(automation.Get(), tree.Get(), L"TreeNode1").Get(),
                L"TitleBar  #AppTitleBar");
            SetFilter(filter.Get(), L"TextBlock");
            auto firstText = Find(automation.Get(), tree.Get(), L"TreeNode4");
            auto secondText = Find(automation.Get(), tree.Get(), L"TreeNode5");
            CheckLevel(automation.Get(), tree.Get(), L"TreeNode4", 1, "filtered text root");
            Check(IntegerProperty(firstText.Get(), UIA_PositionInSetPropertyId) == 1,
                "first filtered text reports its native sibling position");
            Check(IntegerProperty(secondText.Get(), UIA_PositionInSetPropertyId) == 2,
                "second filtered text reports its native sibling position");
            Check(IntegerProperty(firstText.Get(), UIA_SizeOfSetPropertyId) >= 3,
                "filtered text reports its native sibling count");
            CheckNextSibling(walker.Get(), firstText.Get(), L"TreeNode5");
            CheckNextSibling(walker.Get(), secondText.Get(), L"TreeNode8");
            SetFilter(filter.Get(), L"");
        }
    } catch (...) {
        SetFilter(filter.Get(), L"");
        throw;
    }
    std::printf("Native tree hierarchy and selection: checks=%u passed=%u failed=%u\n", checks, checks - failures, failures);
    return failures ? 1 : 0;
}

int wmain(int argc, wchar_t** argv)
{
    if (argc != 2) return 2;
    const HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr)) return 2;
    int result;
    try {
        result = Run(reinterpret_cast<HWND>(_wcstoui64(argv[1], nullptr, 0)));
    } catch (const std::exception& error) {
        std::printf("FAIL tree parent navigation: %s\n", error.what());
        result = 1;
    }
    CoUninitialize();
    return result;
}
