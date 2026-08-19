// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using WinApp.Cli.Models;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Source-generated JSON context for the mechanical migration report.
/// </summary>
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(MigrationReport))]
internal partial class MigrateJsonContext : JsonSerializerContext;
