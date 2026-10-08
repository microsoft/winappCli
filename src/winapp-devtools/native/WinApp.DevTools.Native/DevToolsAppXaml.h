// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>
#include <vector>

// True when `uri` (a XAML source URI from IVisualTreeService's PropertyChainSource::SrcInfo, e.g.
bool DevToolsAppXaml_IsAppAuthored(const std::wstring& uri);


bool DevToolsAppXaml_ScanDiscredited();

void DevToolsAppXaml_ClassifyBatch(const std::vector<std::wstring>& uris, std::vector<char>& out);
