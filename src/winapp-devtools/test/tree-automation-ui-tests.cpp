// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include <Windows.h>
#include <UIAutomation.h>
#include <wrl/client.h>
#include <cstdio>
#include <cstdlib>
#include <initializer_list>
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
    Require(root->FindFirst(TreeScope_Subtree, condition.Get(), &element));
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

static void CheckInspectorLayout(IUIAutomation* automation, IUIAutomationElement* window)
{
    const wchar_t* ids[] = { L"PickElementToggle", L"FocusTrackingToggle", L"TreeFilter", L"JustMyXamlToggle" };
    RECT bounds[4]{};
    for (unsigned index = 0; index < 4; ++index) {
        auto element = Find(automation, window, ids[index]);
        Require(element->get_CurrentBoundingRectangle(&bounds[index]));
        Check(bounds[index].right > bounds[index].left && bounds[index].bottom > bounds[index].top,
            "each tree toolbar control has a visible rectangle");
        if (index)
            Check(bounds[index - 1].right <= bounds[index].left, "tree toolbar controls do not overlap");
    }
    const LONG filterHeight = bounds[2].bottom - bounds[2].top;
    for (unsigned index : { 0u, 1u, 3u }) {
        Check(bounds[index].bottom - bounds[index].top >= filterHeight - 2,
            "icon buttons grow to accommodate the scaled filter height");
        Check(std::abs(bounds[index].top - bounds[2].top) <= 2,
            "icon buttons stay aligned with the scaled filter");
    }
    Check(bounds[2].right - bounds[2].left >= 32, "the filter retains a usable input area");
}

static void CheckHiddenSelection(IUIAutomationElement* tree, IUIAutomationElement* row)
{
    BOOL offscreen = FALSE;
    const ULONGLONG deadline = GetTickCount64() + 3000;
    do {
        Require(row->get_CurrentIsOffscreen(&offscreen));
        if (offscreen) break;
        Sleep(20);
    } while (GetTickCount64() < deadline);
    Check(offscreen != FALSE, "selected row is hidden");
    ComPtr<IUIAutomationSelectionPattern> selection;
    Require(tree->GetCurrentPatternAs(UIA_SelectionPatternId, IID_PPV_ARGS(&selection)));
    ComPtr<IUIAutomationElementArray> selected;
    Require(selection->GetCurrentSelection(&selected));
    int count = 0;
    Require(selected->get_Length(&count));
    Check(count == 1, "hidden selection remains in Selection.GetSelection");
    if (count == 1) {
        ComPtr<IUIAutomationElement> element;
        Require(selected->GetElement(0, &element));
        Check(Id(element.Get()) == Id(row), "selection returns the retained hidden row");
    }
    ComPtr<IUIAutomationSelectionItemPattern> item;
    Require(row->GetCurrentPatternAs(UIA_SelectionItemPatternId, IID_PPV_ARGS(&item)));
    BOOL isSelected = FALSE;
    Require(item->get_CurrentIsSelected(&isSelected));
    Check(isSelected != FALSE, "hidden row reports IsSelected");
    ComPtr<IUIAutomationElement> container;
    Require(item->get_CurrentSelectionContainer(&container));
    Check(Id(container.Get()) == Id(tree), "hidden row reports its selection container");
    Check(item->Select() == UIA_E_ELEMENTNOTAVAILABLE, "hidden row cannot be selected");
    Check(item->AddToSelection() == UIA_E_ELEMENTNOTAVAILABLE, "hidden row cannot be added to selection");
    Check(item->RemoveFromSelection() == UIA_E_ELEMENTNOTAVAILABLE, "hidden row cannot clear selection");
}

static void CheckParent(IUIAutomationTreeWalker* walker, IUIAutomationElement* row,
                        const wchar_t* expected, const char* phase)
{
    ComPtr<IUIAutomationElement> parent;
    std::wstring actual;
    const ULONGLONG deadline = GetTickCount64() + 3000;
    do {
        parent.Reset();
        Require(walker->GetParentElement(row, &parent));
        actual = Id(parent.Get());
        if (actual == expected) break;
        Sleep(20);
    } while (GetTickCount64() < deadline);
    ++checks;
    if (actual != expected) {
        ++failures;
        std::wprintf(L"FAIL %hs: %ls parent is %ls, expected %ls\n",
            phase, Id(row).c_str(), actual.c_str(), expected);
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
    CheckInspectorLayout(automation.Get(), window.Get());
    auto tree = Find(automation.Get(), window.Get(), L"WinAppDevToolsTree");
    auto root = Find(automation.Get(), tree.Get(), L"TreeNode0");
    auto title = Find(automation.Get(), tree.Get(), L"TreeNode1");
    auto branch = Find(automation.Get(), tree.Get(), L"TreeNode3");
    auto child = Find(automation.Get(), tree.Get(), L"TreeNode6");
    auto firstText = Find(automation.Get(), tree.Get(), L"TreeNode4");
    auto secondText = Find(automation.Get(), tree.Get(), L"TreeNode5");
    auto filterElement = Find(automation.Get(), window.Get(), L"TreeFilter");
    ComPtr<IUIAutomationValuePattern> filter;
    Require(filterElement->GetCurrentPatternAs(UIA_ValuePatternId, IID_PPV_ARGS(&filter)));

    CheckParent(walker.Get(), branch.Get(), L"TreeNode2", "initial branch");
    CheckParent(walker.Get(), child.Get(), L"TreeNode3", "initial child");
    try {
        ComPtr<IUIAutomationSelectionItemPattern> titleSelection;
        Require(title->GetCurrentPatternAs(UIA_SelectionItemPatternId, IID_PPV_ARGS(&titleSelection)));
        Require(titleSelection->Select());
        ComPtr<IUIAutomationExpandCollapsePattern> rootExpansion;
        Require(root->GetCurrentPatternAs(UIA_ExpandCollapsePatternId, IID_PPV_ARGS(&rootExpansion)));
        Require(rootExpansion->Collapse());
        CheckHiddenSelection(tree.Get(), title.Get());
        Require(rootExpansion->Expand());
        SetFilter(filter.Get(), L"StackPanel");
        CheckHiddenSelection(tree.Get(), title.Get());
        SetFilter(filter.Get(), L"");
        // Keep the same row providers; never enumerate downward after changing the filter.
        for (int pass = 0; pass < 2; ++pass) {
            SetFilter(filter.Get(), L"StackPanel");
            CheckParent(walker.Get(), branch.Get(), L"WinAppDevToolsTree", "filtered branch");
            CheckParent(walker.Get(), child.Get(), L"TreeNode3", "filtered child");
            SetFilter(filter.Get(), L"");
            CheckParent(walker.Get(), branch.Get(), L"TreeNode2", "restored branch");
            CheckParent(walker.Get(), child.Get(), L"TreeNode3", "restored child");
            SetFilter(filter.Get(), L"AppTitleBar");
            CheckParent(walker.Get(), title.Get(), L"WinAppDevToolsTree", "filtered leaf");
            SetFilter(filter.Get(), L"");
            CheckParent(walker.Get(), title.Get(), L"TreeNode0", "restored leaf");
            SetFilter(filter.Get(), L"TextBlock");
            CheckNextSibling(walker.Get(), firstText.Get(), L"TreeNode5");
            CheckNextSibling(walker.Get(), secondText.Get(), L"TreeNode8");
            SetFilter(filter.Get(), L"");
        }
    } catch (...) {
        SetFilter(filter.Get(), L"");
        throw;
    }
    std::printf("Tree parent navigation: checks=%u passed=%u failed=%u\n", checks, checks - failures, failures);
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
