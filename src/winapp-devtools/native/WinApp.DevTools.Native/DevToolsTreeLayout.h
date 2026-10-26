// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <cstddef>
#include <string>
#include <vector>

namespace DevToolsTreeLayout {

constexpr int kIndentStep       = 12;  // px per level, first kIndentFullLevels levels
constexpr int kIndentFullLevels = 8;
constexpr int kIndentTailStep   = 3;   // px per level beyond that
constexpr int kIndentMaxPx      = 168;

int IndentPxForLevel(int level);

double RevealVerticalOffset(double offset, double viewport, double scrollable, double top, double height);

std::wstring NormalizeTreeFilterQuery(const std::wstring& query);
std::wstring BuildTreeFilterLabel(const std::wstring& type, const std::wstring& name,
                                  const std::wstring& preview);
bool TreeFilterMatches(const std::wstring& type, const std::wstring& name,
                       const std::wstring& preview, const std::wstring& normalizedQuery);

constexpr unsigned long long kIdentitySeed = 1469598103934665603ULL; // FNV-1a 64-bit offset basis

std::wstring IdentitySegment(const std::wstring& type, const std::wstring& name, int sameTypeOrdinal);

// Fold one segment into the parent's identity value (FNV-1a over the segment's UTF-16 code units).
unsigned long long IdentityFold(unsigned long long parentValue, const std::wstring& segment);

// The wire spelling of an identity: kIdentityHexDigits lowercase hex digits.
// 40-bit identities keep large visual-tree selector collisions rare enough to reject ambiguity.
constexpr int kIdentityHexDigits = 10;
std::wstring IdentityToken(unsigned long long value);

constexpr int kDefaultExpandDepth = 5;
constexpr int kMaxAutoExpandDepth = 12;

struct Row
{
    int          depth = 0;
    bool         appAuthored = false;
    std::wstring shortType;
};

struct A11yRow
{
    int  level = 0;          // 1-based; 0 when the row is excluded from the current view
    int  positionInSet = 0;  // 1-based sibling ordinal within the current view
    int  sizeOfSet = 0;      // sibling count within the current view
    bool branch = false;     // true when the next in-view row is a descendant
};

std::vector<Row> BuildRows(size_t count,
                           const std::vector<int>& depths,
                           const std::vector<char>& appAuthored,
                           const std::vector<std::wstring>& shortTypes);

std::vector<size_t> SelectRenderRows(const std::vector<Row>& rows, size_t cap, bool appOnly);

bool IsChromeCollapseType(const std::wstring& shortType);

// True when the "Just my XAML" filter excludes row `j` from the current view.
bool RowHiddenByJmx(const std::vector<Row>& rows, size_t j, bool jmxActive);

// Initial collapsed state for every row, for the CURRENT view. Returns one flag per row.
// Collapse depth is measured from the first app-authored ancestor so framework chrome does not hide app content.
std::vector<char> ComputeCollapse(const std::vector<Row>& rows, bool jmxActive);

std::vector<int> ComputeViewDepths(const std::vector<Row>& rows, bool jmxActive);

std::vector<A11yRow> ComputeA11yRows(const std::vector<Row>& rows, bool jmxActive);

std::vector<char> ComputeVisibility(const std::vector<Row>& rows, const std::vector<char>& collapsed,
                                    bool jmxActive);

constexpr size_t kNoRow = static_cast<size_t>(-1);

// Walk full census depth, not rendered rows; JMX-filtered views omit the framework rows that bridge ancestors.
size_t NearestAppAuthoredAncestor(const std::vector<Row>& rows, size_t target);

// Expand ancestors relationally because filtered-out rows make exact depth steps disappear.
void ExpandAncestors(const std::vector<Row>& rows, std::vector<char>& collapsed, size_t target,
                     bool jmxActive);

// Restore collapse by stable wire key, never by row index; rebuilds can reorder the tree.
size_t RestoreCollapseByKey(const std::vector<unsigned long long>& oldKeys,
                            const std::vector<char>& oldCollapsed,
                            const std::vector<unsigned long long>& newKeys,
                            std::vector<char>& newCollapsed);

std::wstring ElideBreadcrumb(const std::wstring& full, size_t& outSegments);

} // namespace DevToolsTreeLayout
