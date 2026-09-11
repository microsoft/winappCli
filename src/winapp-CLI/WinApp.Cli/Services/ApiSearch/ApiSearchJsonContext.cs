// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.ApiSearch;

/// <summary>
/// Source-generated JSON context for the on-disk API metadata cache
/// (package meta, namespace lists, and per-namespace type lists).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(List<WinMdTypeInfo>))]
[JsonSerializable(typeof(ProjectManifest))]
[JsonSerializable(typeof(PackageMeta))]
internal partial class ApiSearchJsonContext : JsonSerializerContext
{
}
