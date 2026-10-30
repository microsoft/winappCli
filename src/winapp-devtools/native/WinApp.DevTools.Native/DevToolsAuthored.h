// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>
#include <vector>

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
    unsigned int endLine = 0;     // last line of the start tag
    unsigned int column = 0;
    bool mapped = false;
    std::wstring sourceFile;
    std::wstring evidence;
    unsigned int parentLine = 0;
    std::wstring parentType, parentName;
    // What the element's body sets, read only when the opening tag does not self-close. bodyRead is false when the
    // body could not be followed, so nothing is known about it.
    bool bodyRead = false;
    bool bodyContent = false;                 // text or a child element: a value for the type's content property
    std::vector<std::wstring> bodyProperties; // <Type.Property> children, as "Property" and "Type.Property"
};

DevToolsAuthoredState DevToolsAuthored_ReadElement(const std::wstring& fileUri, unsigned int line, std::wstring* out,
    unsigned int column = 0, const std::wstring& type = L"", const std::wstring& name = L"",
    DevToolsAuthoredLocation* authoredLocation = nullptr);

// The declaration's line to show for an element, as `inspect` and `get-source` show it: only one the build map
// confirms. An unconfirmed runtime line is the compiled XAML's and can point past the declaration. Zero when unknown.
inline unsigned int DevToolsAuthored_DisplayLine(DevToolsAuthoredState state, const DevToolsAuthoredLocation& location)
{
    return (state == DevToolsAuthoredState::Available || state == DevToolsAuthoredState::Likely) && location.mapped
        ? location.line : 0;
}

// The confirmed declaration for a runtime position: only a verified build map with an unchanged source file answers.
// Reads no source text, so it is cheap enough to call for every element of a tree.
bool DevToolsAuthored_Locate(const std::wstring& fileUri, unsigned int line, unsigned int column,
    const std::wstring& type, const std::wstring& name, DevToolsAuthoredLocation* location);

bool DevToolsAuthored_FindAttribute(const std::wstring& element, const std::wstring& prop, std::wstring* out);

// Convenience wrapper for callers that need only one property.
DevToolsAuthoredState DevToolsAuthored_Read(const std::wstring& fileUri, unsigned int line,
                                  const std::wstring& prop, std::wstring* out);

// How a C# read gets the index of the project's declarations, which says which parameters take a password. Building
// it reads every .cs file of the project and the projects it references.
enum class DevToolsCSharpIndex
{
    // Never builds it here, for the UI thread: uses the index as last built, at any age, and rebuilds a missing or
    // stale one in the background. Until the first one is ready, every string literal of the statement is redacted.
    Cached,
    // Builds it first when there is none yet, for pipe and worker threads, so their answer is complete. A stale one is
    // still used and rebuilt in the background.
    Build,
};

// The C# statement at line/column of a file on this machine (a Reactor call site), secrets redacted. Same file limits
// as XAML reads. False when the file cannot be read or has no such line. With `site`, also how the call site reads now
// (DevToolsReactorNames::SiteState for an element of `kind`), and the file's line count in `lineCount`.
bool DevToolsAuthored_ReadCSharp(const std::wstring& path, unsigned int line, unsigned int column, std::wstring* out,
                                 DevToolsCSharpIndex index, const std::wstring& kind = L"", std::wstring* site = nullptr,
                                 size_t* lineCount = nullptr);

// How many C# files the declaration index has parsed so far. An unchanged file is parsed once, however often the
// index is rebuilt; tests use this to see it.
unsigned long long DevToolsAuthored_CSharpFilesParsed();

// The C# variable or member the call at line/column of a file on this machine is assigned to, read from the line's
// text (DevToolsReactorNames); empty when it is not assigned or the file cannot be read.
std::wstring DevToolsAuthored_CSharpName(const std::wstring& path, unsigned int line, unsigned int column,
    const std::wstring& kind = L"");

// The C# names of a Reactor component's hooks read from its render body in a file on this machine
// (DevToolsCSharpSource::HookNames); false when the source does not name them for certain.
bool DevToolsAuthored_CSharpHookNames(const std::wstring& path, unsigned int line, unsigned int column,
    const std::vector<std::wstring>& kinds, std::vector<std::wstring>* names);

// The lowercase hex SHA256 or SHA1 (as a portable PDB records it) of a file on this machine; empty when the file
// cannot be read or the algorithm is neither.
std::wstring DevToolsAuthored_FileHash(const std::wstring& path, const std::wstring& algorithm);
