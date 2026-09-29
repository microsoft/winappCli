// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <string>
#include <vector>

struct DevToolsReadNode
{
    unsigned long long handle = 0;
    std::wstring name;          // x:Name, or empty for the (common) unnamed element
    std::wstring type;          // diagnostics type name
    // SourceInfo is carried from the census to avoid per-node UI-thread COM reads.
    std::wstring file;
    // Stable structural identity lets a later selector prove it found the same element, not a lookalike.
    std::wstring id;
    bool uniqueName = false;
    unsigned int childCount = 0;
    std::vector<DevToolsReadNode> children;
};

// One overridden property source. Report precedence, value and available file/line information.
struct DevToolsReadChainEntry
{
    std::wstring source;      // precedence label, same vocabulary as DevToolsReadProp::source
    std::wstring value;       // the value at this level (raw diagnostics spelling)
    std::wstring targetType;  // PropertyChainSource::TargetType, e.g. "Microsoft.UI.Xaml.Controls.Button"
    std::wstring file;        // PropertyChainSource::SrcInfo.FileName; empty for the type default
    unsigned int line = 0;    // Zero when the runtime supplies no handle.
    bool winner = false;      // the effective value (exactly one per property)
};

struct DevToolsReadProp
{
    std::wstring name;
    std::wstring type;   // the value's type; serialized under the JSON key "valueType"
    std::wstring value;
    std::wstring source; // Render shape is independent of writability: color, fields, enum, number, text, bool or none.
    std::wstring editKind;

    std::vector<std::wstring> fields;

    // Empty writeType is the read-only signal; a separate flag could disagree with it.
    std::wstring writeType;

    std::vector<std::wstring> enumValues;

    // valueState distinguishes unset/binding/null from concrete text that could be written back.
    std::wstring valueState;

    std::wstring binding;

    std::wstring authored;

    std::wstring authoredKind;

    std::wstring authoredKey;

    // Supported complex values expose one level of ordinary child rows, sharing top-level rendering.
    std::vector<DevToolsReadProp> children;

    std::vector<DevToolsReadChainEntry> chain;
};

std::wstring DevToolsRead_SerializeTree(const std::vector<DevToolsReadNode>& roots);

std::wstring DevToolsRead_SerializeProps(unsigned long long handle, const std::vector<DevToolsReadProp>& props,
                                    const std::wstring& authoredState);

std::wstring DevToolsRead_NormalizeColor(const std::wstring& raw);
bool DevToolsRead_IsHexColor(const std::wstring& value);

// Serialize prop/value/valueType. valueType names the emitted value, never resource provenance.
std::wstring DevToolsRead_SerializeResolve(const std::wstring& prop, const std::wstring& value,
                                      const std::wstring& valueType);

// Serialize available layout groups: desired, render, actual, root offset, parent, inParent and grid.
struct DevToolsReadLayout
{
    unsigned long long handle = 0;
    bool haveDesired = false, haveRender = false, haveActual = false, haveOffset = false;
    float desiredW = 0, desiredH = 0, renderW = 0, renderH = 0;
    double actualW = 0, actualH = 0;
    float offsetX = 0, offsetY = 0;
    std::wstring parentType, parentHandle;
    bool haveParentBox = false;
    float parentW = 0, parentH = 0;
    bool haveInParent = false;
    float inParentX = 0, inParentY = 0;
    std::wstring parentOrientation, parentSpacing, parentPadding;
    int childIndex = -1, childCount = 0;
    std::wstring gridRow, gridColumn, gridRowSpan, gridColumnSpan;
};

std::wstring DevToolsRead_SerializeLayout(const DevToolsReadLayout& L);

// Core-property curation controls ordering and the compact overlay card, never wire read inclusion.
bool DevToolsRead_IsCoreProp(const std::wstring& name);

// Classify what the developer literally wrote into one of the `authoredKind` tokens (see DevToolsReadProp).
std::wstring DevToolsRead_ClassifyAuthored(const std::wstring& authored);

std::vector<std::wstring> DevToolsRead_FieldLabels(const std::wstring& valueType);

std::wstring DevToolsRead_DeriveWriteType(const std::wstring& declaredType, bool isEnum);

// Whether the SUB-PROPERTIES of a complex value may be written in place.
// Child edits mutate a referenced object, so resource-backed/shared values must be refused.
bool DevToolsRead_ChildEditingIsSafe(const std::wstring& valueSource, const std::wstring& authoredKind);

std::wstring DevToolsRead_ExpandableKind(const std::wstring& valueType);

std::wstring DevToolsRead_AuthoredKey(const std::wstring& authored);

// The DISPLAY name for a type string reported by the diagnostics property chain.
// Assembly-qualified CLR type names must be cut at the assembly comma before dropping namespaces.
std::wstring DevToolsRead_ShortTypeName(const std::wstring& type);
