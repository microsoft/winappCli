// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
//
// Tree-layout tests retain deliberately broken algorithms as negative controls. Each must still
// fail the relevant invariant before the current algorithm is accepted.
// Run through scripts/test-native-units.ps1.

#include "DevToolsTreeLayout.h"
#include <windows.h>
#include <algorithm>
#include <atomic>
#include <climits>
#include <cstdio>
#include <stdexcept>
#include <string>
#include <vector>

using namespace DevToolsTreeLayout;

static int g_failures = 0;

static void Check(bool cond, const char* what)
{
    if (!cond) { ++g_failures; std::printf("  FAIL  %s\n", what); }
    else       {              std::printf("  ok    %s\n", what); }
}

static void CheckEq(int got, int want, const char* what)
{
    if (got != want) { ++g_failures; std::printf("  FAIL  %s (want %d, got %d)\n", what, want, got); }
    else             {              std::printf("  ok    %s (%d)\n", what, got); }
}

static void Test_VerticalRevealPreservesVisibleContext()
{
    Check(RevealVerticalOffset(100, 80, 500, 120, 20) == 100, "visible row does not move vertically");
    Check(RevealVerticalOffset(100, 80, 500, 40, 20) == 40, "row above reveals its top");
    Check(RevealVerticalOffset(100, 80, 500, 200, 20) == 140, "row below reveals its bottom");
    Check(RevealVerticalOffset(0, 80, 500, -5, 20) == 0, "reveal clamps at the start");
    Check(RevealVerticalOffset(450, 80, 500, 600, 20) == 500, "reveal clamps at the end");
    Check(RevealVerticalOffset(100, 80, 500, 80, 140) == 100, "oversized row covering the viewport does not oscillate");
    Check(RevealVerticalOffset(0, 80, 0, 40, 20) == 0, "a tree shorter than its viewport does not scroll");
    double offset = RevealVerticalOffset(0, 100, 500, 200, 20);
    offset = RevealVerticalOffset(offset, 100, 500, 80, 20);
    offset = RevealVerticalOffset(offset, 100, 500, 140, 20);
    Check(offset == 80, "after/before context still leaves the target visible");
    offset = RevealVerticalOffset(0, 30, 500, 200, 20);
    offset = RevealVerticalOffset(offset, 30, 500, 80, 20);
    offset = RevealVerticalOffset(offset, 30, 500, 140, 20);
    Check(offset == 130, "the target wins when the viewport cannot fit surrounding context");
}

// ---- the tree under test -------------------------------------------------------------------------------
// Shaped like AI Dev Gallery, which is what made these bugs visible when synthetic fixtures had missed them:
// a deep framework chrome spine, the app's page content hanging off it ~10 levels down, framework control
// template guts below that, and one branch where the app nests MORE content inside a templated control.
struct Built { std::vector<Row> rows; size_t nestedApp = 0; size_t nestedAppChild = 0; size_t outerApp = 0; };

static Built BuildGalleryShapedTree()
{
    Built b;
    auto push = [&](int depth, bool app, const wchar_t* type = L"Grid") {
        Row r; r.depth = depth; r.appAuthored = app; r.shortType = type; b.rows.push_back(r);
    };
    for (int i = 0; i < 10; ++i) push(i, false);           // framework chrome spine, depth 0..9
    for (int p = 0; p < 12; ++p) {
        if (p == 0) b.outerApp = b.rows.size();
        push(10, true); push(11, true); push(12, true);    // the app's own page content
        push(13, false); push(14, false);                  // framework template guts, no app inside
        if (p == 0) {                                      // one branch nests app content INSIDE a template
            push(13, false);
            push(14, true);  b.nestedApp = b.rows.size() - 1;
            push(15, true);  b.nestedAppChild = b.rows.size() - 1;
        }
    }
    return b;
}

static int CountVisible(const std::vector<char>& v)
{
    int c = 0; for (char x : v) c += x ? 1 : 0; return c;
}

// Negative control: collapse on absolute census depth rather than visible content depth.
static std::vector<char> OldCollapse_AbsoluteDepth(const std::vector<Row>& rows)
{
    std::vector<char> collapsed(rows.size(), 0);
    const int base = rows.empty() ? 0 : rows[0].depth;
    for (size_t i = 0; i + 1 < rows.size(); ++i)
        if (rows[i + 1].depth > rows[i].depth && (rows[i].depth - base) >= kDefaultExpandDepth)
            collapsed[i] = 1;
    return collapsed;
}

// Pre-visibility: hid a row if ANY ancestor by absolute depth was collapsed -- including ancestors the
// "Just my XAML" filter was not showing. That omission is the entire blank-pane bug.
static std::vector<char> OldVisibility_FullTreeAncestry(const std::vector<Row>& rows,
                                                        const std::vector<char>& collapsed, bool jmx)
{
    std::vector<char> visible(rows.size(), 0);
    for (size_t j = 0; j < rows.size(); ++j) {
        bool hidden = RowHiddenByJmx(rows, j, jmx);
        int need = rows[j].depth - 1;
        for (size_t k = j; !hidden && k-- > 0 && need >= 0; )
            if (rows[k].depth == need) { if (collapsed[k]) { hidden = true; break; } --need; }
        visible[j] = hidden ? 0 : 1;
    }
    return visible;
}

// Negative control: exact-depth ancestor matching skips hidden bridging rows.
static void OldExpandAncestors_ExactDepth(const std::vector<Row>& rows, std::vector<char>& collapsed,
                                          size_t target, bool jmx)
{
    int need = rows[target].depth - 1;
    for (size_t k = target; k-- > 0 && need >= 0; ) {
        if (RowHiddenByJmx(rows, k, jmx)) continue;
        if (rows[k].depth == need) { collapsed[k] = 0; --need; }
    }
}

// Negative control: content depth starts at the nearest rather than outermost app ancestor.
static std::vector<int> OldContentDepth_NearestAppAncestor(const std::vector<Row>& rows, bool jmx)
{
    std::vector<int> contentDepth(rows.size(), 0);
    struct Anc { int d; int vd; int appVd; };
    std::vector<Anc> st;
    for (size_t i = 0; i < rows.size(); ++i) {
        if (RowHiddenByJmx(rows, i, jmx)) continue;
        const int d = rows[i].depth;
        while (!st.empty() && st.back().d >= d) st.pop_back();
        const int vd = (int)st.size();
        const int nearestApp = st.empty() ? -1 : st.back().appVd;
        contentDepth[i] = (nearestApp >= 0) ? vd - nearestApp : 0;
        st.push_back(Anc{ d, vd, rows[i].appAuthored ? vd : nearestApp });
    }
    return contentDepth;
}

// Pre-focus target resolution: focus tracking handed the raw focused wire straight to selection, so the
// answer to "what should be selected" was always the focused row itself.
static size_t OldFocusTarget_RawFocusedRow(const std::vector<Row>& /*rows*/, size_t target)
{
    return target;
}

// ---- tests ---------------------------------------------------------------------------------------------

static void Test_BlankPane_TheReportedBug()
{
    std::printf("\nFiltered tree must not hide app rows behind invisible framework ancestors\n");
    const auto t = BuildGalleryShapedTree();

    // CONTROL: the old algorithm must still reproduce the failure. If this ever passes, the control is dead
    // and every assertion below it is worthless.
    const auto oldCollapsed = OldCollapse_AbsoluteDepth(t.rows);
    const auto oldVisible   = OldVisibility_FullTreeAncestry(t.rows, oldCollapsed, /*jmx*/ true);
    CheckEq(CountVisible(oldVisible), 0, "CONTROL: pre-fix code renders zero rows with Just-my-XAML on");

    // The fix.
    const auto collapsed = ComputeCollapse(t.rows, /*jmx*/ true);
    const auto visible   = ComputeVisibility(t.rows, collapsed, /*jmx*/ true);
    Check(CountVisible(visible) > 20, "filtered view renders the app's content, not an empty pane");
}

static void Test_UnfilteredViewShowsAppContent()
{
    std::printf("\nChrome prefix must not eat the expansion budget\n");
    const auto t = BuildGalleryShapedTree();
    const auto collapsed = ComputeCollapse(t.rows, /*jmx*/ false);
    const auto visible   = ComputeVisibility(t.rows, collapsed, /*jmx*/ false);

    int appVisible = 0;
    for (size_t i = 0; i < t.rows.size(); ++i) if (visible[i] && t.rows[i].appAuthored) ++appVisible;
    Check(appVisible > 0, "unfiltered view shows app-authored content, not only framework chrome");
    Check(CountVisible(visible) < (int)t.rows.size(), "unfiltered view does not unfurl as a wall of rows");

    // A framework control the app placed shows as ONE expandable node, never its template guts.
    int deepGuts = 0;
    for (size_t i = 0; i < t.rows.size(); ++i)
        if (visible[i] && !t.rows[i].appAuthored && t.rows[i].depth >= 14) ++deepGuts;
    CheckEq(deepGuts, 0, "framework template internals stay folded behind one node");
    Check(visible[t.nestedApp] != 0, "app content nested inside a templated control is still revealed");
}

static void Test_ContentDepthMeasuredFromOutermostAppAncestor()
{
    std::printf("\nContent depth must be measured from the FIRST app ancestor, not the nearest\n");
    // An all-app chain: every row is app-authored, so with the filter on the nearest app ancestor is always
    // the immediate parent and the old measure could never exceed 1 -- the depth bound was dead.
    std::vector<Row> chain;
    for (int i = 0; i < 12; ++i) { Row r; r.depth = i; r.appAuthored = true; r.shortType = L"Grid"; chain.push_back(r); }

    const auto oldDepths = OldContentDepth_NearestAppAncestor(chain, /*jmx*/ true);
    int oldMax = 0; for (int d : oldDepths) if (d > oldMax) oldMax = d;
    Check(oldMax <= 1, "CONTROL: pre-fix content depth never exceeded 1, so the bound never fired");

    const auto collapsed = ComputeCollapse(chain, /*jmx*/ true);
    const auto visible   = ComputeVisibility(chain, collapsed, /*jmx*/ true);
    Check(CountVisible(visible) < 12, "a deep all-app chain is bounded rather than fully unfurled");
}

static void Test_RevealAcrossAFilteredGap()
{
    std::printf("\nReveal must cross the depth gap the filter creates\n");
    auto t = BuildGalleryShapedTree();
    const size_t target = t.nestedAppChild; // app row at census depth 15, framework rows between it and its
                                            // app ancestor at depth 10 -- all removed by the filter

    // CONTROL: the exact-depth walk must fail to reveal it.
    {
        auto collapsed = ComputeCollapse(t.rows, /*jmx*/ true);
        collapsed[t.outerApp] = 1;
        OldExpandAncestors_ExactDepth(t.rows, collapsed, target, /*jmx*/ true);
        const auto visible = ComputeVisibility(t.rows, collapsed, /*jmx*/ true);
        CheckEq(visible[target] ? 1 : 0, 0, "CONTROL: pre-fix exact-depth walk leaves the picked row hidden");
    }
    // The fix.
    {
        auto collapsed = ComputeCollapse(t.rows, /*jmx*/ true);
        collapsed[t.outerApp] = 1;
        ExpandAncestors(t.rows, collapsed, target, /*jmx*/ true);
        const auto visible = ComputeVisibility(t.rows, collapsed, /*jmx*/ true);
        CheckEq(visible[target] ? 1 : 0, 1, "relational walk reveals the picked row");
    }
}

static void Test_EmptyTreeGuaranteeForUninstrumentedApps()
{
    std::printf("\nAn app with no source info must still get a usable tree\n");
    auto t = BuildGalleryShapedTree();
    for (auto& r : t.rows) r.appAuthored = false; // nothing classifies -> uninstrumented

    const auto collapsed = ComputeCollapse(t.rows, /*jmx*/ false);
    const auto visible   = ComputeVisibility(t.rows, collapsed, /*jmx*/ false);
    Check(CountVisible(visible) > 5, "uninstrumented app does not collapse to nothing");
    Check(CountVisible(visible) < (int)t.rows.size(), "uninstrumented app is still bounded");
}

static void Test_IndentIsMonotonicAndBounded()
{
    std::printf("\nIndent: bounded, and never ambiguous between two depths\n");
    int prev = -1;
    bool monotonic = true, bounded = true, distinct = true;
    for (int lvl = 0; lvl <= 60; ++lvl) {
        const int px = IndentPxForLevel(lvl);
        if (px < prev) monotonic = false;
        if (px > kIndentMaxPx) bounded = false;
        if (lvl > 0 && lvl < 24 && px == prev) distinct = false; // must stay distinguishable before the cap
        prev = px;
    }
    Check(monotonic, "indent never decreases as depth grows");
    Check(bounded,   "indent never exceeds the cap");
    Check(distinct,  "adjacent depths stay visually distinct until the cap bites");
    CheckEq(IndentPxForLevel(0), 0, "a view root sits flush left");
}

static void Test_BreadcrumbElision()
{
    std::printf("\nBreadcrumb: bounded, but the full chain is never lost\n");
    const std::wstring sep = L" \u203A ";
    std::wstring longChain;
    for (int i = 0; i < 24; ++i) { if (i) longChain += sep; longChain += L"Node" + std::to_wstring(i); }

    size_t segs = 0;
    const std::wstring elided = ElideBreadcrumb(longChain, segs);
    CheckEq((int)segs, 24, "reports the true ancestor count");
    Check(elided.size() < longChain.size(), "a 24-level chain is elided");
    Check(elided.find(L"Node0") == 0, "keeps the root");
    Check(elided.find(L"Node23") != std::wstring::npos, "keeps the immediate parent");
    Check(elided.find(L"\u2026") != std::wstring::npos, "marks the elision honestly");

    const std::wstring shortChain = L"A" + sep + L"B" + sep + L"C";
    size_t segs2 = 0;
    Check(ElideBreadcrumb(shortChain, segs2) == shortChain, "a short chain is left whole, not mangled");
    CheckEq((int)segs2, 3, "short chain segment count");

    size_t segs3 = 0;
    Check(ElideBreadcrumb(L"", segs3).empty(), "an empty chain stays empty");
}

static void Test_BuildRowsIsTheUntestedSeam()
{
    std::printf("\n[seam] assembling rows from the window's parallel columns\n");
    // The happy path: three columns, correctly paired.
    {
        std::vector<int>          d = { 0, 1, 2 };
        std::vector<char>         a = { 0, 1, 0 };
        std::vector<std::wstring> t = { L"Grid", L"Button", L"ScrollBar" };
        const auto rows = BuildRows(3, d, a, t);
        CheckEq((int)rows.size(), 3, "row count");
        Check(rows[1].depth == 1 && rows[1].appAuthored && rows[1].shortType == L"Button",
              "depth, classification and type stay paired to the same row");
        Check(!rows[0].appAuthored && !rows[2].appAuthored, "non-app rows are not misclassified");
    }
    // A short column must not read out of bounds or silently mis-pair. This is the case that motivated
    // extracting the seam at all: the columns are assigned together today, and a future edit that adds a
    // fourth and forgets one of these is exactly how a depth would end up paired with another row's flags.
    {
        std::vector<int>          d = { 0, 1, 2, 3 };
        std::vector<char>         a = { 0, 1 };            // deliberately short
        std::vector<std::wstring> t = { L"Grid" };         // deliberately short
        const auto rows = BuildRows(4, d, a, t);
        CheckEq((int)rows.size(), 4, "result is always `count` long regardless of column lengths");
        Check(rows[3].depth == 3, "present column still read correctly");
        Check(!rows[3].appAuthored, "missing classification defaults to not-app rather than reading garbage");
        Check(rows[3].shortType.empty(), "missing type defaults to empty rather than reading garbage");
        Check(rows[1].appAuthored, "the entries that DO exist are still paired correctly");
    }
    // Zero rows must not trip anything downstream.
    {
        std::vector<int> d; std::vector<char> a; std::vector<std::wstring> t;
        const auto rows = BuildRows(0, d, a, t);
        CheckEq((int)rows.size(), 0, "an empty tree yields no rows");
        const auto collapsed = ComputeCollapse(rows, true);
        const auto visible   = ComputeVisibility(rows, collapsed, true);
        CheckEq((int)visible.size(), 0, "the pipeline tolerates an empty tree");
    }
}

static void Test_RenderCapSelectsTheRequestedViewFairly()
{
    std::printf("\nRender cap selects breadth-first and applies Just-my-XAML before the cap\n");
    std::vector<Row> rows;
    auto push = [&](int depth, bool app) { Row r; r.depth = depth; r.appAuthored = app; rows.push_back(r); };

    push(0, false);                       // root
    push(1, false);                       // first branch
    for (int i = 0; i < 12; ++i) push(2, false); // wide framework subtree that consumed the old DFS prefix
    push(1, false);                       // later sibling
    push(2, true);                        // the app's Frame-hosted content
    const size_t appRow = rows.size() - 1;

    // CONTROL: the old implementation was a literal depth-first prefix.
    std::vector<size_t> old;
    for (size_t i = 0; i < rows.size() && i < 5; ++i) old.push_back(i);
    Check(std::find(old.begin(), old.end(), appRow) == old.end(),
          "CONTROL: the depth-first prefix amputates the later app branch");

    const auto full = SelectRenderRows(rows, 5, false);
    Check(std::find(full.begin(), full.end(), appRow) != full.end(),
          "breadth-first round-robin keeps the later sibling's content");

    const auto app = SelectRenderRows(rows, 1, true);
    CheckEq((int)app.size(), 1, "Just-my-XAML spends its cap on app-authored rows");
    Check(app[0] == appRow, "filtering before the cap reaches app content beyond the old prefix");
}

static void Test_A11yRowsFollowTheVisibleHierarchy()
{
    std::printf("\nAccessibility rows follow the hierarchy the filtered tree actually shows\n");
    std::vector<Row> rows;
    auto push = [&](int depth, bool app, const wchar_t* type = L"Grid") {
        Row r; r.depth = depth; r.appAuthored = app; r.shortType = type; rows.push_back(r);
    };

    push(0, false); // framework root, hidden by JMX
    push(1, true);  // app root
    push(2, true);  // first visible child
    push(2, false); // framework template row, hidden by JMX
    push(3, true);  // app grandchild below the filtered gap
    push(1, true);  // second visible root
    push(2, true);  // its child

    const auto a11y = ComputeA11yRows(rows, /*jmxActive*/ true);
    CheckEq(a11y[0].level, 0, "filter-hidden rows report no hierarchy facts");
    CheckEq(a11y[1].level, 1, "the first visible app row becomes a level-1 root");
    CheckEq(a11y[1].positionInSet, 1, "the first root is first in its set");
    CheckEq(a11y[1].sizeOfSet, 2, "both visible roots are counted in the root set");
    Check(a11y[1].branch, "a visible descendant makes the row a branch");

    CheckEq(a11y[2].level, 2, "a visible child is one level deeper than its visible parent");
    CheckEq(a11y[2].positionInSet, 1, "the first visible child is first in the set");
    CheckEq(a11y[2].sizeOfSet, 1, "only visible children count toward the set size");
    Check(a11y[2].branch, "the child that leads to a filtered-gap descendant is still a branch");

    CheckEq(a11y[4].level, 3, "a descendant below a filtered gap keeps its visible hierarchy depth");
    CheckEq(a11y[4].positionInSet, 1, "the only visible grandchild is first in its set");
    CheckEq(a11y[4].sizeOfSet, 1, "the only visible grandchild reports a singleton set");
    Check(!a11y[4].branch, "a leaf reports no branch");

    CheckEq(a11y[5].level, 1, "a later root stays level 1");
    CheckEq(a11y[5].positionInSet, 2, "a later root gets the second root position");
    CheckEq(a11y[6].level, 2, "its child nests under that root");
}

// The identity a devtools selector is built on. A wire handle is exact but opaque; `id` is the readable
// half — and the ONLY thing that lets the CLI refuse a selector that no longer names the element it named
// when it was printed. Three properties have to hold or the selector is worse than the bare handle it
// replaced: it must depend on the whole path (so duplicate x:Names stay distinct), it must not move when an
// unrelated sibling type is inserted, and it must be wide enough that a 20,000-node census does not collide.
static void Test_ElementIdentityIsPathDependentAndStable()
{
    std::printf("\n[selectors] the stable element identity behind a devtools slug\n");

    // The control: an identity that hashes only the element's own type+name — which is what a naive
    // "type-name-hash" slug does — cannot tell two DataTemplate rows apart at all.
    Check(IdentitySegment(L"Microsoft.UI.Xaml.Controls.Button", L"DeleteButton", 0) ==
          IdentitySegment(L"Microsoft.UI.Xaml.Controls.Button", L"DeleteButton", 0),
          "the same element's segment is deterministic");

    const std::wstring rowA = IdentitySegment(L"Microsoft.UI.Xaml.Controls.Button", L"DeleteButton", 0);
    const std::wstring rowB = IdentitySegment(L"Microsoft.UI.Xaml.Controls.Button", L"DeleteButton", 1);
    Check(rowA != rowB, "two same-named siblings get different segments (duplicate x:Name stays addressable)");

    Check(IdentitySegment(L"Microsoft.UI.Xaml.Controls.Button", L"", 0) == L"Button[0]",
          "the segment uses the SHORT type and drops the namespace");
    Check(IdentitySegment(L"Microsoft.UI.Xaml.Controls.Button", L"Submit", 2) == L"Button#Submit[2]",
          "a named element carries its x:Name in the segment");

    // Path dependence: the same segment under two different parents must not fold to the same identity, or a
    // selector printed for one page would resolve on another.
    const unsigned long long parentA = IdentityFold(kIdentitySeed, L"Grid#Left[0]");
    const unsigned long long parentB = IdentityFold(kIdentitySeed, L"Grid#Right[0]");
    Check(IdentityFold(parentA, rowA) != IdentityFold(parentB, rowA),
          "the same element under a different parent folds to a different identity");

    // Level separation: "A" + "BC" and "AB" + "C" describe different trees and must not collide.
    Check(IdentityFold(IdentityFold(kIdentitySeed, L"A"), L"BC") !=
          IdentityFold(IdentityFold(kIdentitySeed, L"AB"), L"C"),
          "level boundaries are part of the identity, so concatenation cannot alias");

    // Inserting an unrelated sibling TYPE must not renumber this element: the ordinal counts same-type
    // siblings only. This is the difference between a selector that survives an ordinary edit and one that
    // goes stale every time a TextBlock is added.
    Check(IdentitySegment(L"Button", L"Save", 0) == IdentitySegment(L"Button", L"Save", 0),
          "a same-type ordinal is all that decides the segment, so a TextBlock insertion is invisible to it");

    const std::wstring token = IdentityToken(0x0123456789abcdefULL);
    CheckEq((int)token.size(), kIdentityHexDigits, "the identity token is a fixed-width hex string");
    Check(token == L"6789abcdef", "the token is the low-order hex digits, lowercase");
    Check(IdentityToken(0) == L"0000000000", "a zero identity still renders full width");

    // Width: 16 bits (the UI Automation slug's 4 hex digits) collides essentially always over a DevTools census.
    // This asserts the property that matters — that the token carries at least 40 bits — rather than
    // re-deriving the birthday bound at runtime.
    Check(kIdentityHexDigits >= 10, "the identity is at least 40 bits wide for a 20,000-node census");
    Check(IdentityToken(0xFFFFFFFFFFULL) == L"ffffffffff" &&
          IdentityToken(0xFFFFFFFFFFFFFFFFULL) == L"ffffffffff",
          "the token keeps exactly the low 40 bits");
}

static void Test_TreeFilterMatcherIsSharedWithTheProtocol()
{
    std::printf("\nThe window and VisualTree.find share one matching dialect\n");

    Check(NormalizeTreeFilterQuery(L"  SUBmit  ") == L"submit", "query normalization trims and lowercases");
    Check(TreeFilterMatches(L"Microsoft.UI.Xaml.Controls.Button", L"SubmitButton", L"", L"submit"),
          "x:Name matches by case-insensitive substring");
    Check(TreeFilterMatches(L"Microsoft.UI.Xaml.Controls.NavigationView", L"", L"", L"navigation"),
          "type matches by case-insensitive substring");
    Check(!TreeFilterMatches(L"Microsoft.UI.Xaml.Controls.Button", L"CancelButton", L"", L"submit"),
          "unrelated identity does not match");
}

// the row list is rebuilt wholesale when the app navigates, so the user's expand/collapse choices have
// to be carried across it. The control is the obvious implementation -- copy the flags across by ROW INDEX --
// which passes on a tree that only grew at the end and silently applies one page's expansion state to a
// different page's rows the moment anything shifts. That is the failure this function exists to prevent, and
// it is invisible: the tree still renders, just expanded in the wrong places.
static std::vector<char> RestoreCollapseByIndex_Old(const std::vector<char>& oldCollapsed,
                                                    std::vector<char> newCollapsed)
{
    for (size_t i = 0; i < oldCollapsed.size() && i < newCollapsed.size(); ++i)
        newCollapsed[i] = oldCollapsed[i];
    return newCollapsed;
}

static void Test_CollapseStateSurvivesARowRebuild()
{
    std::printf("\nExpand/collapse survives a live row rebuild, matched by wire not by index\n");

    // Before: wires 10,11,12,13. The user collapsed 12 and expanded everything else.
    const std::vector<unsigned long long> oldKeys{ 10, 11, 12, 13 };
    const std::vector<char>               oldCollapsed{ 0, 0, 1, 0 };

    // After a navigation two new nodes arrived at the FRONT, so every surviving node shifted by two. The
    // default-collapse pass decided to collapse the two newcomers.
    const std::vector<unsigned long long> newKeys{ 90, 91, 10, 11, 12, 13 };
    const std::vector<char>               defaults{ 1, 1, 0, 0, 0, 0 };

    const std::vector<char> byIndex = RestoreCollapseByIndex_Old(oldCollapsed, defaults);
    Check(byIndex[4] == 0, "CONTROL: restoring by row index loses the collapse on wire 12");
    Check(byIndex[0] == 0, "CONTROL: restoring by row index also overwrites a NEW row's default");

    std::vector<char> byWire = defaults;
    const size_t restored = RestoreCollapseByKey(oldKeys, oldCollapsed, newKeys, byWire);
    Check(restored == 4, "every surviving node's state is restored");
    Check(byWire[4] == 1, "the collapsed node stays collapsed at its NEW row index");
    Check(byWire[2] == 0 && byWire[3] == 0 && byWire[5] == 0, "expanded nodes stay expanded");
    Check(byWire[0] == 1 && byWire[1] == 1, "rows that only exist on the new page keep the default");

    // Wire 0 means "this node had no live wire slot", and several rows can carry it at once. Matching on it
    // would copy one arbitrary row's state onto every unslotted row.
    const std::vector<unsigned long long> zeroOld{ 0, 0 };
    const std::vector<char>               zeroOldCollapsed{ 1, 0 };
    const std::vector<unsigned long long> zeroNew{ 0, 0 };
    std::vector<char> zeroNewCollapsed{ 0, 0 };
    Check(RestoreCollapseByKey(zeroOld, zeroOldCollapsed, zeroNew, zeroNewCollapsed) == 0,
          "handle 0 is never matched -- it is 'no slot', not an identity");
    Check(zeroNewCollapsed[0] == 0, "unslotted rows keep their default");

    // A page that shares nothing with the previous one must restore nothing rather than pattern-match.
    const std::vector<unsigned long long> disjoint{ 70, 71 };
    std::vector<char> disjointCollapsed{ 1, 0 };
    Check(RestoreCollapseByKey(oldKeys, oldCollapsed, disjoint, disjointCollapsed) == 0,
          "a fully replaced page restores nothing");
    Check(disjointCollapsed[0] == 1 && disjointCollapsed[1] == 0, "and its defaults are left intact");
}

static void Test_FocusRedirectsToTheNearestAuthoredAncestor()
{
    std::printf("\nFocus on a framework element resolves to the nearest element the app authored\n");
    const auto t = BuildGalleryShapedTree();

    // A focusable framework row deep inside a control template: depth 14, whose only app-authored ancestor is
    // three census levels up. Found by scanning rather than hard-coded so a change to the fixture shape fails
    // this test loudly instead of silently testing a different row.
    size_t frameworkRow = (size_t)-1;
    for (size_t i = 0; i < t.rows.size(); ++i)
        if (!t.rows[i].appAuthored && t.rows[i].depth == 14) { frameworkRow = i; break; }
    Check(frameworkRow != (size_t)-1, "the fixture still contains a deep framework row to focus");

    // CONTROL: the old behaviour selected the focused row itself, which is not in the app's XAML at all.
    const size_t old = OldFocusTarget_RawFocusedRow(t.rows, frameworkRow);
    Check(!t.rows[old].appAuthored, "CONTROL: pre-fix focus tracking selects a row the app never authored");

    const size_t anc = NearestAppAuthoredAncestor(t.rows, frameworkRow);
    Check(anc != kNoRow, "an app-authored ancestor is found");
    Check(t.rows[anc].appAuthored, "the redirect target is app-authored");
    Check(anc < frameworkRow && t.rows[anc].depth < t.rows[frameworkRow].depth,
          "the redirect target is an ANCESTOR, not a sibling or a cousin");

    // The gap is the point. The immediate parent by census depth is itself framework, so a walk that stopped
    // at "depth - 1" would answer with another element the user never wrote.
    size_t parent = (size_t)-1;
    for (size_t k = frameworkRow; k-- > 0; )
        if (t.rows[k].depth == t.rows[frameworkRow].depth - 1) { parent = k; break; }
    Check(parent != (size_t)-1 && !t.rows[parent].appAuthored,
          "CONTROL: the immediate parent is framework too, so a depth-1 walk would not help");

    // A descendant of an app-authored node nested INSIDE a template still resolves to that node, not to the
    // outer page content -- "nearest", not "first".
    const size_t nested = NearestAppAuthoredAncestor(t.rows, t.nestedAppChild);
    Check(nested == t.nestedApp, "nearest wins over outermost when both are app-authored");

    // No app-authored ancestor anywhere: the caller has to be told, not handed an arbitrary row. This is the
    // case the issue asked about explicitly -- selecting "the nearest" when there is none would mean
    // selecting something arbitrarily far away.
    std::vector<Row> allFramework;
    for (int i = 0; i < 8; ++i) { Row r; r.depth = i; r.appAuthored = false; r.shortType = L"Grid"; allFramework.push_back(r); }
    Check(NearestAppAuthoredAncestor(allFramework, allFramework.size() - 1) == kNoRow,
          "an all-framework chain reports NO ancestor rather than inventing one");

    // A root row has no ancestors at all.
    Check(NearestAppAuthoredAncestor(t.rows, 0) == kNoRow, "the first row has no ancestor");
    Check(NearestAppAuthoredAncestor(t.rows, t.rows.size() + 10) == kNoRow, "an out-of-range row is not a crash");
}

// Defined in protocol-tests.cpp: the DevToolsProtocol JSON reader suite. Both are COM-free pure logic, so they
// share one binary and one runner.
int RunProtocolTests();
// Defined in trust-tests.cpp: DevToolsTrust's posture parsing, the centralized-gate comparison, capability
// advertisement filtering, and the pipe security descriptor/client-identity checks (trust model).
int RunTrustTests();
int RunCrashTests();
// Defined in sourcepath-tests.cpp: canonical source-URI containment before ShellExecute.
int RunSourcePathTests();
// Defined in sink-tests.cpp: DevToolsSinkBase, the one COM event-sink implementation the tap's ~22 sinks derive
// from. Needs combase for the free-threaded marshaler, but no desktop and no app, so it rides along here.
int RunSinkTests();
// Defined in toolbarcorner-tests.cpp: where the DevTools toolbar parks, including the title-bar inset.
int RunToolbarCornerTests();
// Defined in toolbar-tests.cpp: the toolbar action order/wiring and Light/Dark target selection.
int RunToolbarTests();
// Defined in selectionplacement-tests.cpp: screen-safe placement for the in-app quick-edit panel.
int RunSelectionPlacementTests();
int RunInspectorAcceptanceTests();
// Defined in selectiontracking-tests.cpp: scroll-offset translation for live selection tracking.
int RunSelectionTrackingTests();
// Defined in binding-relay-tests.cpp: the wire format and the op allow-list of the tap's client for its own
// process's managed agent. Needs kernel32 only, so it rides along here too.
int RunBindingRelayTests();
// Defined in bindingrow-tests.cpp: what a row SHOWS for a binding answer, including the control that
// a healthy binding shows nothing.
int RunBindingRowTests();
// Defined in pathwalk-tests.cpp: what a row SHOWS for a binding PATH WALK -- including the control
// that a fully resolving path shows nothing, and the assertion that "no such property" and "is null" never
// render as the same thing.
int RunPathWalkTests();
// Defined in pathprobe-tests.cpp: the wire shape the walk produces, round-tripped through the SHIPPING
// renderer -- so a collapse of missing-vs-null on either side fails here rather than in an app.
int RunPathProbeTests();
// is a typed binding path well formed, and WHICH rule did it break.
int RunPathSyntaxTests();
// Defined in read-tests.cpp: the property payload's pure classifiers and serializer, including the
// control that a {ThemeResource} row and a literal row must not report the same origin.
int RunReadTests();
// Defined in authored-tests.cpp: the source declaration read reused by property authorship and Source.get.
int RunAuthoredTests();
// Defined in pickroute-tests.cpp: release-time routing for a pick armed from the DevTools window.
int RunPickRouteTests();
// Defined in perf-tests.cpp: the UI-thread attribution and self-time invariants behind the budget.
int RunPerfTests();
// Defined in uidispatch-tests.cpp: independent operation ownership under the exact three-way detail-read
// concurrency that exposed. Uses a held fake dispatcher, so it is deterministic and desktop-free.
int RunUiDispatchTests();
// Defined in focus-subscription-tests.cpp: process-wide first/last Focus subscriber transitions, including
// idempotence and disconnect cleanup. Uses inert connection writers, so it is deterministic and desktop-free.
int RunFocusSubscriptionTests();
// Defined in overlaystate-tests.cpp: the synchronized selection-arm/overlay state contract's pure
// diff logic (DevToolsOverlayStateTracker) and the Overlay event domain's subscription lifecycle. COM/app-free.
int RunOverlayStateTests();
// Resource substitution must not modify app-authored XAML.
int RunResourceInlineTests();
// Defined in shellopen-tests.cpp: the ONE shell hand-off decision shared by the comments row and the
// properties pane, with the association lookup and the launcher stubbed so "with no handler, ShellExecute is
// never reached" is a fact rather than a reading of the code.
int RunShellOpenTests();
// Defined in ownedstate-tests.cpp: the compare-and-restore ownership contract for process-global DevTools
// UI state, which decides what a disconnecting client may undo without stepping on a newer client.
int RunOwnedStateTests();
// Defined in resourceoverrides-tests.cpp: the undo record behind Resource.set / Resource.reset.
int RunResourceOverrideTests();
// Defined in batch-tests.cpp: the pure request/reply shaping behind VisualTree.getPreviews and
// DevTools.releaseOwnedState, which is otherwise string assembly only a live client can observe.
int RunBatchTests();
int RunQueryTests();
// Defined in bindinganswer-tests.cpp: the native {Binding} install's answer -- the mode-name mapping
// and, the one that matters, that success is the READ-BACK off the element and never the call's HRESULT.
int RunBindingAnswerTests();
// Defined in pipeaccept-tests.cpp: the tap's named-pipe accept loop, driven against REAL kernel pipe
// instances on a private test-only name. Pins the invariant that the pipe name never stops existing under
// concurrent connects -- its absence is what a client reports as "the target is gone".
int RunPipeAcceptTests();
// Fault injection at the per-connection framing boundary (F19). Drives the REAL framing runner the tap calls
// from PipeConnection, so deleting the production try/catch fails here.
int RunFramingTests();
// Fault injection at the DevToolsEvents registry lock: a leaked lock is a permanent hang, not a crash, so each
// gate is a follow-up acquisition on a timeout.
int RunEventsTests();

// A memory fault on a detached worker is otherwise invisible here: the thread dies, the assertions the main
// thread already made still read as satisfied, and the process can reach a clean exit before the OS finishes
// tearing it down. Several of the accept-loop gates are exactly about what a handler touches after its owner
// is gone, so the suite has to see the fault itself rather than hope the process dies of it.
namespace {
std::atomic<long> g_faults{ 0 };
std::atomic<unsigned long> g_firstFaultCode{ 0 };
std::atomic<void*> g_firstFaultAddress{ nullptr };
std::atomic<unsigned long> g_firstFaultThread{ 0 };
std::atomic<bool> g_firstFaultReady{ false };

// Runs on the faulting thread, at first chance, before any handler that might legitimately own the exception.
// Everything here must be safe in that context: the interrupted thread may hold the CRT's stdio lock, so this
// records into lock-free atomics and writes through the raw stdout handle rather than calling printf. It never
// consumes the exception -- returning EXCEPTION_CONTINUE_SEARCH leaves the outcome exactly as it would have
// been, so this can add a failure but can never turn a real fault into a pass.
LONG CALLBACK RecordMemoryFault(EXCEPTION_POINTERS* info)
{
    const DWORD code = info->ExceptionRecord->ExceptionCode;
    if (code == EXCEPTION_ACCESS_VIOLATION || code == EXCEPTION_STACK_OVERFLOW ||
        code == EXCEPTION_ILLEGAL_INSTRUCTION || code == EXCEPTION_IN_PAGE_ERROR) {
        if (g_faults.fetch_add(1, std::memory_order_acq_rel) == 0) {
            g_firstFaultCode.store(code, std::memory_order_relaxed);
            g_firstFaultAddress.store(info->ExceptionRecord->ExceptionAddress, std::memory_order_relaxed);
            g_firstFaultThread.store(GetCurrentThreadId(), std::memory_order_relaxed);
            // Published last, with release, and read with acquire. The count is incremented before the three
            // details are written, so it does not order them: a reader that only checked the count could see a
            // fault reported with zeroed details.
            g_firstFaultReady.store(true, std::memory_order_release);
        }
        // wsprintfA is user32, not the CRT, so it cannot deadlock against a stdio lock the faulting thread
        // may already hold. A fault that kills the process still leaves this line behind as evidence.
        char line[128];
        const int n = wsprintfA(line, "FAULT 0x%08lX at %p on thread %lu\r\n", code,
                                info->ExceptionRecord->ExceptionAddress, GetCurrentThreadId());
        DWORD written = 0;
        WriteFile(GetStdHandle(STD_OUTPUT_HANDLE), line, static_cast<DWORD>(n), &written, nullptr);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

// The negative control below proves the watch does not over-count, but it passes just as happily when the
// watch was never installed at all -- an absence cannot be told from correct restraint. This raises a real
// EXCEPTION_ACCESS_VIOLATION and consumes it in its own __except frame, so the watch sees it at first chance
// and the process is never actually at risk. Without it the entire fault watch could silently do nothing and
// every assertion about it would still pass.
DWORD RaiseMemoryFaultAndReport()
{
    __try {
        RaiseException(EXCEPTION_ACCESS_VIOLATION, 0, 0, nullptr);
        return 0;
    } __except (GetExceptionCode() == EXCEPTION_ACCESS_VIOLATION ? EXCEPTION_EXECUTE_HANDLER
                                                                 : EXCEPTION_CONTINUE_SEARCH) {
        return GetExceptionCode();
    }
}

void Test_FaultWatchActuallyCountsAMemoryFault()
{
    const long before = g_faults.load(std::memory_order_acquire);
    const DWORD handled = RaiseMemoryFaultAndReport();
    Check(handled == EXCEPTION_ACCESS_VIOLATION, "the synthetic access violation reached its own __except frame");

    const long after = g_faults.load(std::memory_order_acquire);
    char label[160];
    std::snprintf(label, sizeof(label),
                  "the fault watch is installed and counted it (before=%ld after=%ld)", before, after);
    Check(after == before + 1, label);

    // Hand the rest of the suite a clean record, so a later real fault publishes its own details.
    g_faults.store(0, std::memory_order_release);
    g_firstFaultReady.store(false, std::memory_order_release);
    g_firstFaultCode.store(0, std::memory_order_relaxed);
    g_firstFaultAddress.store(nullptr, std::memory_order_relaxed);
    g_firstFaultThread.store(0, std::memory_order_relaxed);
}

// The fault watch sits at the head of the vectored chain, ahead of every SEH frame in the process, so it gets
// first look at exceptions that are a normal part of running code. If it ever consumed one, or counted one, the
// suite would either change the behaviour it is measuring or fail for a reason that is not a fault at all.
// Neither of those is visible from reading the handler, so both are asserted here.
DWORD RaiseNonMemoryExceptionAndReport()
{
    // Its own function: __try/__except cannot live in a frame that requires C++ object unwinding.
    __try {
        RaiseException(0xE0DEAD01, 0, 0, nullptr);
        return 0;
    } __except (GetExceptionCode() == 0xE0DEAD01 ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH) {
        return GetExceptionCode();
    }
}

void Test_FaultWatchOnlyObservesMemoryFaults()
{
    const long before = g_faults.load(std::memory_order_acquire);

    const DWORD handled = RaiseNonMemoryExceptionAndReport();
    Check(handled == 0xE0DEAD01, "a non-memory SEH exception still reaches the __except frame that owns it");

    bool caught = false;
    try { throw std::runtime_error("chain"); } catch (const std::runtime_error&) { caught = true; }
    Check(caught, "a C++ exception is still caught by its own handler (non-vacuous)");

    const long after = g_faults.load(std::memory_order_acquire);
    char label[160];
    std::snprintf(label, sizeof(label),
                  "the fault watch counted neither of them (before=%ld after=%ld)", before, after);
    Check(after == before, label);
}
}   // namespace

int main()
{
    PVOID faultWatch = AddVectoredExceptionHandler(1, RecordMemoryFault);
    std::printf("DevToolsTreeLayout tests -- each bug is pinned by a CONTROL asserting the old code still fails\n");
    Check(faultWatch != nullptr, "the memory fault watch installed (without it the suite cannot see a fault at all)");
    Test_FaultWatchActuallyCountsAMemoryFault();
    Test_FaultWatchOnlyObservesMemoryFaults();
    Test_BlankPane_TheReportedBug();
    Test_UnfilteredViewShowsAppContent();
    Test_ContentDepthMeasuredFromOutermostAppAncestor();
    Test_RevealAcrossAFilteredGap();
    Test_EmptyTreeGuaranteeForUninstrumentedApps();
    Test_IndentIsMonotonicAndBounded();
    Test_VerticalRevealPreservesVisibleContext();
    Test_BreadcrumbElision();
    Test_BuildRowsIsTheUntestedSeam();
    Test_RenderCapSelectsTheRequestedViewFairly();
    Test_A11yRowsFollowTheVisibleHierarchy();
    Test_TreeFilterMatcherIsSharedWithTheProtocol();
    Test_ElementIdentityIsPathDependentAndStable();
    Test_CollapseStateSurvivesARowRebuild();
    Test_FocusRedirectsToTheNearestAuthoredAncestor();

    std::printf("\n");
    const int protocolFailures = RunProtocolTests();

    std::printf("\n");
    const int trustFailures = RunTrustTests();
    const int crashFailures = RunCrashTests();

    std::printf("\n");
    const int sourcePathFailures = RunSourcePathTests();

    std::printf("\n");
    const int sinkFailures = RunSinkTests();

    std::printf("\n");
    const int cornerFailures = RunToolbarCornerTests();

    std::printf("\n");
    const int toolbarFailures = RunToolbarTests();

    std::printf("\n");
    const int selectionPlacementFailures = RunSelectionPlacementTests();
    const int inspectorAcceptanceFailures = RunInspectorAcceptanceTests();

    std::printf("\n");
    const int selectionTrackingFailures = RunSelectionTrackingTests();

    std::printf("\n");
    const int relayFailures = RunBindingRelayTests();

    std::printf("\n");
    const int bindingRowFailures = RunBindingRowTests();

    std::printf("\n");
    const int pathWalkFailures = RunPathWalkTests();

    std::printf("\n");
    const int pathProbeFailures = RunPathProbeTests();
    const int pathSyntaxFailures = RunPathSyntaxTests();

    std::printf("\n");
    const int readFailures = RunReadTests();

    std::printf("\n");
    const int authoredFailures = RunAuthoredTests();

    std::printf("\n");
    const int pickRouteFailures = RunPickRouteTests();

    std::printf("\n");
    const int perfFailures = RunPerfTests();

    std::printf("\n");
    const int uiDispatchFailures = RunUiDispatchTests();

    std::printf("\n");
    const int focusSubscriptionFailures = RunFocusSubscriptionTests();

    std::printf("\n");
    const int overlayStateFailures = RunOverlayStateTests();

    std::printf("\n");
    const int resourceInlineFailures = RunResourceInlineTests();

    std::printf("\n");
    const int shellOpenFailures = RunShellOpenTests();

    std::printf("\n");
    const int ownedStateFailures = RunOwnedStateTests();

    std::printf("\n");
    const int resourceOverrideFailures = RunResourceOverrideTests();

    std::printf("\n");
    const int batchFailures = RunBatchTests();
    const int queryFailures = RunQueryTests();

    std::printf("\n");
    const int bindingAnswerFailures = RunBindingAnswerTests();

    std::printf("\n");
    const int pipeAcceptFailures = RunPipeAcceptTests();

    std::printf("\n");
    const int framingFailures = RunFramingTests();

    std::printf("\n");
    const int eventsFailures = RunEventsTests();

    const int total = g_failures + protocolFailures + trustFailures + crashFailures + sourcePathFailures + sinkFailures +
                      cornerFailures + toolbarFailures + authoredFailures +
                      selectionPlacementFailures + inspectorAcceptanceFailures + selectionTrackingFailures + relayFailures +
                      bindingRowFailures + pathWalkFailures + pathProbeFailures + pathSyntaxFailures + readFailures + pickRouteFailures + perfFailures + uiDispatchFailures +
                      focusSubscriptionFailures + overlayStateFailures + resourceInlineFailures +
                      shellOpenFailures + ownedStateFailures + resourceOverrideFailures + batchFailures + queryFailures + bindingAnswerFailures +
                      pipeAcceptFailures + framingFailures + eventsFailures;
    // Unregister BEFORE reading the count, not after: once the handler is gone no further fault can be
    // recorded, so the value read here is final. Reading first would leave a window in which a fault is
    // recorded but not counted, and the suite would exit 0 with a memory fault already on the record.
    // After this the CRT starts tearing down, and a handler that reports through stdout has no business
    // running against a half-destroyed process.
    if (faultWatch) RemoveVectoredExceptionHandler(faultWatch);
    const long faults = g_faults.load(std::memory_order_acquire);
    if (faults) {
        if (g_firstFaultReady.load(std::memory_order_acquire)) {
            std::printf("\nFAILED: %ld memory fault(s) on worker threads; first was 0x%08lX at %p on thread %lu\n",
                        faults, g_firstFaultCode.load(std::memory_order_relaxed),
                        g_firstFaultAddress.load(std::memory_order_relaxed),
                        g_firstFaultThread.load(std::memory_order_relaxed));
        } else {
            std::printf("\nFAILED: %ld memory fault(s) on worker threads; details not published\n", faults);
        }
    }
    if (total || faults) { if (total) std::printf("\nFAILED: %d assertion(s)\n", total); return 1; }
    std::printf("\nAll native unit tests passed.\n");
    return 0;
}
