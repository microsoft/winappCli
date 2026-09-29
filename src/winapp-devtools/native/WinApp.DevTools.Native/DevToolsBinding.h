// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>

struct IXamlDiagnostics;

// Describe the Binding markup object at `bindingValueHandle` as its authored expression, e.g.
std::wstring DevToolsBinding_Describe(IXamlDiagnostics* diag, unsigned long long bindingValueHandle);
