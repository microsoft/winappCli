// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <string>

bool DevToolsSourcePath_ResolveExisting(const std::wstring& sourceRoot,
                                   const std::wstring& sourceUri,
                                   std::wstring* resolvedPath);

bool DevToolsSourcePath_ResolveCompiled(const std::wstring& payloadRoot,
                                   const std::wstring& resourceUri, std::wstring* resolvedPath);
bool DevToolsSourcePath_ResolvePri(const std::wstring& payloadRoot, std::wstring* resolvedPath);

// The runtime reports a classless ResourceDictionary's elements as ms-resource:///Files/<path>, the app package's
// file resource map. Returns that as ms-appx:///<path>; any other URI (another package, a framework component) is
// returned unchanged.
std::wstring DevToolsSourcePath_AppUri(const wchar_t* runtimeUri);

// Exposed for focused containment tests. Both inputs must already be canonical.
bool DevToolsSourcePath_IsWithinRoot(const std::wstring& canonicalRoot,
                                const std::wstring& canonicalCandidate);
