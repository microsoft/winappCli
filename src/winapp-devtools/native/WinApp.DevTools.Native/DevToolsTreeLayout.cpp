// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "DevToolsTreeLayout.h"

#include <algorithm>
#include <climits>
#include <map>
#include <utility>

namespace DevToolsTreeLayout {

int IndentPxForLevel(int level)
{
    if (level <= 0) return 0;
    int px = (level <= kIndentFullLevels)
           ? level * kIndentStep
           : kIndentFullLevels * kIndentStep + (level - kIndentFullLevels) * kIndentTailStep;
    return px < kIndentMaxPx ? px : kIndentMaxPx;
}

double RevealVerticalOffset(double offset, double viewport, double scrollable, double top, double height)
{
    const double bottom = top + height;
    if (top < offset && bottom < offset + viewport) offset = top;
    else if (bottom > offset + viewport && top > offset) offset = bottom - viewport;
    return std::clamp(offset, 0.0, scrollable);
}

std::wstring NormalizeTreeFilterQuery(const std::wstring& query)
{
    std::wstring normalized;
    for (wchar_t c : query) {
        if (c >= L'A' && c <= L'Z') c = (wchar_t)(c + 32);
        if (c != L' ' || !normalized.empty()) normalized += c;
    }
    while (!normalized.empty() && normalized.back() == L' ') normalized.pop_back();
    return normalized;
}

std::wstring BuildTreeFilterLabel(const std::wstring& type, const std::wstring& name,
                                  const std::wstring& preview)
{
    const size_t dot = type.find_last_of(L'.');
    std::wstring label = dot == std::wstring::npos ? type : type.substr(dot + 1);
    if (!name.empty()) { label += L" #"; label += name; }
    if (!preview.empty()) { label += L" "; label += preview; }
    for (wchar_t& c : label) if (c >= L'A' && c <= L'Z') c = (wchar_t)(c + 32);
    return label;
}

bool TreeFilterMatches(const std::wstring& type, const std::wstring& name,
                       const std::wstring& preview, const std::wstring& normalizedQuery)
{
    return BuildTreeFilterLabel(type, name, preview).find(normalizedQuery) != std::wstring::npos;
}

std::wstring IdentitySegment(const std::wstring& type, const std::wstring& name, int sameTypeOrdinal)
{
    const size_t dot = type.find_last_of(L'.');
    std::wstring segment = dot == std::wstring::npos ? type : type.substr(dot + 1);
    if (!name.empty()) { segment += L'#'; segment += name; }
    segment += L'[';
    segment += std::to_wstring(sameTypeOrdinal < 0 ? 0 : sameTypeOrdinal);
    segment += L']';
    return segment;
}

unsigned long long IdentityFold(unsigned long long parentValue, const std::wstring& segment)
{
    // A separator between levels so `A` + `BC` and `AB` + `C` cannot fold to the same value.
    unsigned long long h = parentValue ^ 0x2fULL;
    h *= 1099511628211ULL;
    for (wchar_t c : segment) {
        h ^= static_cast<unsigned long long>(static_cast<unsigned short>(c));
        h *= 1099511628211ULL;
    }
    return h;
}

std::wstring IdentityToken(unsigned long long value)
{
    static const wchar_t* kHex = L"0123456789abcdef";
    std::wstring token(kIdentityHexDigits, L'0');
    for (int i = kIdentityHexDigits; i-- > 0; ) {
        token[static_cast<size_t>(i)] = kHex[value & 0xFULL];
        value >>= 4;
    }
    return token;
}

std::vector<Row> BuildRows(size_t count,
                           const std::vector<int>& depths,
                           const std::vector<char>& appAuthored,
                           const std::vector<std::wstring>& shortTypes)
{
    std::vector<Row> rows(count);
    for (size_t i = 0; i < count; ++i) {
        rows[i].depth       = (i < depths.size())      ? depths[i] : 0;
        rows[i].appAuthored = (i < appAuthored.size())  && appAuthored[i] != 0;
        if (i < shortTypes.size()) rows[i].shortType = shortTypes[i];
    }
    return rows;
}

std::vector<size_t> SelectRenderRows(const std::vector<Row>& rows, size_t cap, bool appOnly)
{
    if (rows.empty() || cap == 0) return {};

    std::vector<std::vector<size_t>> children(rows.size());
    std::vector<size_t> roots;
    std::vector<size_t> ancestors;
    for (size_t i = 0; i < rows.size(); ++i) {
        while (!ancestors.empty() && rows[ancestors.back()].depth >= rows[i].depth) ancestors.pop_back();
        if (ancestors.empty()) roots.push_back(i);
        else                   children[ancestors.back()].push_back(i);
        ancestors.push_back(i);
    }

    std::vector<size_t> selected;
    std::vector<size_t> frontier = std::move(roots);
    while (!frontier.empty() && selected.size() < cap) {
        for (size_t i : frontier) {
            if (!appOnly || rows[i].appAuthored) {
                selected.push_back(i);
                if (selected.size() == cap) break;
            }
        }
        if (selected.size() == cap) break;

        std::vector<size_t> next;
        for (size_t pass = 0;; ++pass) {
            bool admitted = false;
            for (size_t parent : frontier) {
                if (pass >= children[parent].size()) continue;
                next.push_back(children[parent][pass]);
                admitted = true;
            }
            if (!admitted) break;
        }
        frontier.swap(next);
    }

    std::sort(selected.begin(), selected.end());
    return selected;
}

bool IsChromeCollapseType(const std::wstring& shortType)
{
    return shortType == L"ScrollBar";
}

bool RowHiddenByJmx(const std::vector<Row>& rows, size_t j, bool jmxActive)
{
    return jmxActive && j < rows.size() && !rows[j].appAuthored;
}

std::vector<int> ComputeViewDepths(const std::vector<Row>& rows, bool jmxActive)
{
    const size_t n = rows.size();
    std::vector<int> viewDepth(n, -1);
    std::vector<int> stack; // census depths of in-view ancestors, strictly increasing
    for (size_t i = 0; i < n; ++i) {
        if (RowHiddenByJmx(rows, i, jmxActive)) continue;
        const int d = rows[i].depth;
        while (!stack.empty() && stack.back() >= d) stack.pop_back();
        viewDepth[i] = (int)stack.size();
        stack.push_back(d);
    }
    return viewDepth;
}

std::vector<A11yRow> ComputeA11yRows(const std::vector<Row>& rows, bool jmxActive)
{
    const size_t n = rows.size();
    std::vector<A11yRow> out(n);
    std::vector<int> parentOf(n, -1);
    std::vector<int> childCount(n, 0);
    std::vector<int> levelOf(n, 0);
    std::vector<size_t> visible;
    visible.reserve(n);

    std::vector<size_t> stack;
    for (size_t i = 0; i < n; ++i) {
        if (RowHiddenByJmx(rows, i, jmxActive)) continue;
        while (!stack.empty() && rows[stack.back()].depth >= rows[i].depth) stack.pop_back();
        parentOf[i] = stack.empty() ? -1 : (int)stack.back();
        if (parentOf[i] >= 0) ++childCount[(size_t)parentOf[i]];
        levelOf[i] = stack.empty() ? 1 : (levelOf[stack.back()] + 1);
        visible.push_back(i);
        stack.push_back(i);
    }

    int rootCount = 0;
    for (size_t i : visible) if (parentOf[i] < 0) ++rootCount;

    std::vector<int> seenChildren(n, 0);
    int seenRoots = 0;
    for (size_t k = 0; k < visible.size(); ++k) {
        const size_t i = visible[k];
        A11yRow a;
        a.level = levelOf[i];
        if (parentOf[i] < 0) {
            a.positionInSet = ++seenRoots;
            a.sizeOfSet = rootCount;
        } else {
            a.positionInSet = ++seenChildren[(size_t)parentOf[i]];
            a.sizeOfSet = childCount[(size_t)parentOf[i]];
        }
        size_t j = k + 1;
        a.branch = (j < visible.size()) && (rows[visible[j]].depth > rows[i].depth);
        out[i] = a;
    }
    return out;
}

std::vector<char> ComputeCollapse(const std::vector<Row>& rows, bool jmxActive)
{
    const size_t n = rows.size();
    std::vector<char> collapsed(n, 0);

    int appTotal = 0;
    for (size_t i = 0; i < n; ++i)
        if (!RowHiddenByJmx(rows, i, jmxActive) && rows[i].appAuthored) ++appTotal;

    std::vector<int>  viewDepth(n, -1);
    std::vector<int>  contentDepth(n, 0); // depth below the FIRST (outermost) app-authored ancestor
    std::vector<int>  parentOf(n, -1);
    std::vector<int>  childCount(n, 0);
    std::vector<int>  branchDepth(n, 0);  // depth counting only ancestors with >1 visible child
    std::vector<char> hasApp(n, 0);       // this row or an in-view descendant is app-authored

    struct Anc { int d; int vd; int firstAppVd; size_t idx; };
    std::vector<Anc> st;
    for (size_t i = 0; i < n; ++i) {
        if (RowHiddenByJmx(rows, i, jmxActive)) continue;
        const int d = rows[i].depth;
        while (!st.empty() && st.back().d >= d) st.pop_back();
        const int vd = (int)st.size();
        viewDepth[i] = vd;
        int firstApp = st.empty() ? -1 : st.back().firstAppVd;
        if (firstApp < 0 && rows[i].appAuthored) firstApp = vd;
        contentDepth[i] = (firstApp >= 0) ? vd - firstApp : 0;
        parentOf[i] = st.empty() ? -1 : (int)st.back().idx;
        if (parentOf[i] >= 0) ++childCount[parentOf[i]];
        if (rows[i].appAuthored) { hasApp[i] = 1; for (auto& a : st) hasApp[a.idx] = 1; }
        st.push_back(Anc{ d, vd, firstApp, i });
    }
    for (size_t i = 0; i < n; ++i) {
        if (viewDepth[i] < 0) continue;
        const int p = parentOf[i];
        branchDepth[i] = (p < 0) ? 0 : branchDepth[p] + (childCount[p] > 1 ? 1 : 0);
    }

    for (size_t i = 0; i < n; ++i) {
        if (viewDepth[i] < 0) continue;
        size_t j = i + 1;
        while (j < n && RowHiddenByJmx(rows, j, jmxActive)) ++j;
        const bool branch = (j < n) && (rows[j].depth > rows[i].depth);
        if (!branch) continue;
        bool collapse;
        if (appTotal == 0) collapse = branchDepth[i] >= kDefaultExpandDepth || viewDepth[i] >= kMaxAutoExpandDepth;
        else               collapse = !hasApp[i] || contentDepth[i] >= kDefaultExpandDepth;
        if (collapse || IsChromeCollapseType(rows[i].shortType)) collapsed[i] = 1;
    }
    return collapsed;
}

std::vector<char> ComputeVisibility(const std::vector<Row>& rows, const std::vector<char>& collapsed,
                                    bool jmxActive)
{
    const size_t n = rows.size();
    std::vector<char> visible(n, 0);
    int hideBelow = INT_MAX;
    for (size_t j = 0; j < n; ++j) {
        if (RowHiddenByJmx(rows, j, jmxActive)) { visible[j] = 0; continue; }
        const int d = rows[j].depth;
        if (d <= hideBelow) hideBelow = INT_MAX; // walked back out to/above the collapsed ancestor
        const bool hidden = (d > hideBelow);
        if (!hidden && j < collapsed.size() && collapsed[j]) hideBelow = d;
        visible[j] = hidden ? 0 : 1;
    }
    return visible;
}

void ExpandAncestors(const std::vector<Row>& rows, std::vector<char>& collapsed, size_t target,
                     bool jmxActive)
{
    if (target >= rows.size()) return;
    int watermark = rows[target].depth;
    for (size_t k = target; k-- > 0; ) {
        if (RowHiddenByJmx(rows, k, jmxActive)) continue;
        if (rows[k].depth < watermark) {
            if (k < collapsed.size()) collapsed[k] = 0;
            watermark = rows[k].depth;
            if (watermark <= 0) break;
        }
    }
}

size_t NearestAppAuthoredAncestor(const std::vector<Row>& rows, size_t target)
{
    if (target >= rows.size()) return kNoRow;
    int watermark = rows[target].depth;
    for (size_t k = target; k-- > 0; ) {
        if (rows[k].depth >= watermark) continue;   // a sibling or a cousin's subtree, not an ancestor
        if (rows[k].appAuthored) return k;
        watermark = rows[k].depth;
        if (watermark <= 0) break;
    }
    return kNoRow;
}

size_t RestoreCollapseByKey(const std::vector<unsigned long long>& oldKeys,
                            const std::vector<char>& oldCollapsed,
                            const std::vector<unsigned long long>& newKeys,
                            std::vector<char>& newCollapsed)
{
    std::map<unsigned long long, char> prior;
    for (size_t i = 0; i < oldKeys.size() && i < oldCollapsed.size(); ++i) {
        if (oldKeys[i] == 0) continue;
        prior[oldKeys[i]] = oldCollapsed[i];
    }
    size_t restored = 0;
    for (size_t i = 0; i < newKeys.size() && i < newCollapsed.size(); ++i) {
        if (newKeys[i] == 0) continue;
        auto it = prior.find(newKeys[i]);
        if (it == prior.end()) continue;
        newCollapsed[i] = it->second;
        ++restored;
    }
    return restored;
}

std::wstring ElideBreadcrumb(const std::wstring& full, size_t& outSegments)
{
    static const std::wstring kSep = L" \u203A ";
    outSegments = 0;
    if (full.empty()) return full;
    std::vector<std::wstring> seg;
    for (size_t p = 0;;) {
        size_t q = full.find(kSep, p);
        if (q == std::wstring::npos) { seg.push_back(full.substr(p)); break; }
        seg.push_back(full.substr(p, q - p));
        p = q + kSep.size();
    }
    outSegments = seg.size();
    if (seg.size() <= 4) return full; // short enough to show whole -- eliding would only lose information
    return seg.front() + kSep + L"\u2026" + kSep + seg[seg.size() - 2] + kSep + seg.back();
}

} // namespace DevToolsTreeLayout
