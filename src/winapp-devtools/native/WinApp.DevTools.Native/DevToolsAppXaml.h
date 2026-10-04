// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>
#include <vector>

// True when `uri` (a XAML source URI from IVisualTreeService's PropertyChainSource::SrcInfo, e.g.
bool DevToolsAppXaml_IsAppAuthored(const std::wstring& uri);

// True when the app directory scan found at least one app XAML file, i.e. the allowlist is authoritative.
bool DevToolsAppXaml_HasAppXamlSet();

bool DevToolsAppXaml_ScanDiscredited();

void DevToolsAppXaml_ClassifyBatch(const std::vector<std::wstring>& uris, std::vector<char>& out);
