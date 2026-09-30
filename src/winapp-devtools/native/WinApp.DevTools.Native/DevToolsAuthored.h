// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>

enum class DevToolsAuthoredState
{
    Available,    // source info present, file read, authored text is trustworthy
    NoSourceInfo, // the runtime stored no file/line for this element (no XBF line info, or the launch could
    NoFile,       // the runtime named a file we could not open (no source root, or not on this machine)
    Stale,        // the file is NEWER than the built XAML, so its line numbers no longer describe this app
    Unavailable,  // coordinates/identity cannot be attributed to one opening tag
    UnverifiedBuild, // compiler/source/payload identity could not be established
    Likely,      // unique source line/identity only; explicit confirmation required for a comment
};

// Short wire token for a state ("available" / "noSourceInfo" / "noFile" / "stale" / "unavailable").
const wchar_t* DevToolsAuthored_StateToken(DevToolsAuthoredState s);

void DevToolsAuthored_Init(const std::wstring& sourceRoot, unsigned long long buildTimeUnix);

// True when the launch bound a source root; then IsProjectSource answers whether an element's source URI is one
// of the developer's files under it (framework and library templates never are).
bool DevToolsAuthored_HasSourceRoot();
bool DevToolsAuthored_IsProjectSource(const std::wstring& fileUri);

// True when the declaration at line/column of a project file sits inside a <ControlTemplate>.
bool DevToolsAuthored_IsControlTemplatePart(const std::wstring& fileUri, unsigned int line, unsigned int column);
void DevToolsAuthored_InitCoordinates(const std::wstring& inventoryPath, const std::wstring& inventoryHash,
    const std::wstring& payloadRoot);

struct DevToolsAuthoredLocation
{
    unsigned int line = 0;
    unsigned int column = 0;
    bool mapped = false;
    std::wstring sourceFile;
    std::wstring evidence;
    unsigned int parentLine = 0;
    std::wstring parentType, parentName;
};

DevToolsAuthoredState DevToolsAuthored_ReadElement(const std::wstring& fileUri, unsigned int line, std::wstring* out,
    unsigned int column = 0, const std::wstring& type = L"", const std::wstring& name = L"",
    DevToolsAuthoredLocation* authoredLocation = nullptr);

bool DevToolsAuthored_FindAttribute(const std::wstring& element, const std::wstring& prop, std::wstring* out);

// Convenience wrapper for callers that need only one property.
DevToolsAuthoredState DevToolsAuthored_Read(const std::wstring& fileUri, unsigned int line,
                                  const std::wstring& prop, std::wstring* out);
