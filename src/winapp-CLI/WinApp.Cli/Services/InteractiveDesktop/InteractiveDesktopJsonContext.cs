// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.InteractiveDesktop;

/// <summary>
/// Source-generated serializer context for <c>interactive-desktop-{session}.state.json</c>. The CLI
/// publishes with NativeAOT and <c>TrimMode=full</c>, so reflection-based serialization is not
/// available — every coordination type must be reachable from here.
/// </summary>
/// <remarks>
/// <see cref="System.Text.Json.JsonSerializerOptions.WriteIndented"/> is intentionally off: the state
/// file is machine-only and is rewritten on every registration and completion, so the smaller payload
/// keeps the atomic temp-write cheap. Nulls are omitted so an absent owner or an
/// <see cref="UiTurnMode.Observe"/> entry's missing ticket round-trip as absent rather than as
/// <c>null</c>.
/// </remarks>
[JsonSerializable(typeof(InteractiveDesktopState))]
[JsonSerializable(typeof(OwnerRecord))]
[JsonSerializable(typeof(OwnerCommandEntry))]
[JsonSerializable(typeof(WaiterEntry))]
[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class InteractiveDesktopJsonContext : JsonSerializerContext;
