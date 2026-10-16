// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <set>
#include <string>
#include <vector>

// True when `uri` (a XAML source URI from IVisualTreeService's PropertyChainSource::SrcInfo, e.g.
bool DevToolsAppXaml_IsAppAuthored(const std::wstring& uri);


bool DevToolsAppXaml_ScanDiscredited();

void DevToolsAppXaml_ClassifyBatch(const std::vector<std::wstring>& uris, std::vector<char>& out);
// Pure core of ClassifyBatch: classifies against `scanned` (package-relative keys) and returns whether the scan was
// trusted. Exposed for tests.
bool DevToolsAppXaml_ClassifyWith(const std::vector<std::wstring>& uris, const std::set<std::wstring>& scanned,
                                  std::vector<char>& out);
