// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <functional>
#include <string>

#include <xamlOM.h>

#include "DevToolsBindingAnswer.h"

using DevToolsBindingReadBackFn = std::function<DevToolsBindingReadBack()>;

bool DevToolsBindingInstall_Apply(IXamlDiagnostics* diag, IVisualTreeService3* vts3,
                             InstanceHandle elem, unsigned int propIndex,
                             const std::wstring& path, const std::wstring& mode,
                             const DevToolsBindingReadBackFn& readBack, std::wstring* outJson);
