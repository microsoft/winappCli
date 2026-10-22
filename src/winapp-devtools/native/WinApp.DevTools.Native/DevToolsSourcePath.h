// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.
#pragma once

#include <string>
#include <vector>

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

// A C# path the compiler recorded (a Reactor call site) as a file on this machine. Deterministic builds record
// `/_/rel/File.cs` (or `/_1/...` for a second source root); `pathMap` is MSBuild's PathMap (`from=to,from=to`),
// and otherwise `/_/` is tried under the source root and each of its ancestors. Returns false and leaves `mapped`
// as the compiled path when no existing file is found.
//
// Reactor publishes a path relative to the project that compiled the file, without naming the project, so a
// relative path is looked for under the source root and each of `projectDirs` (the app's referenced projects,
// `;`-separated). `../` is allowed when the file stays inside the repository (or the source root's parent outside
// one). When more than one project has the file it is ambiguous: false, with the candidates in `ambiguous`.
bool DevToolsSourcePath_MapCompiled(const std::wstring& compiledPath, const std::wstring& sourceRoot,
                                    const std::wstring& pathMap, std::wstring* mapped,
                                    const std::wstring& projectDirs = {}, std::vector<std::wstring>* ambiguous = nullptr);
