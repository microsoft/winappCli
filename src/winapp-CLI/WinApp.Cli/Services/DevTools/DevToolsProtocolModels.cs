// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.DevTools;

internal sealed class DevToolsAppInfo
{
    public int Pid { get; set; }

    public string? StartTicksUtc { get; set; }

    public string? AppSelector { get; set; }

    public string? PipeName { get; set; }

    public string? ProcessName { get; set; }

    public string? WindowTitle { get; set; }

    public bool Attached { get; set; }

    public string Status { get; set; } = "available";

    public bool Responsive { get; set; }

    public string? ProtocolVersion { get; set; }

    public bool? Mutation { get; set; }

    public string? Posture { get; set; }

    public int? NodeCount { get; set; }
}

internal sealed class DevToolsListPayload
{
    public List<DevToolsAppInfo> Apps { get; set; } = [];
}

internal sealed class DevToolsAttachPayload
{
    public bool Ok { get; set; }

    public int Pid { get; set; }

    public string? PipeName { get; set; }

    public string? ProtocolVersion { get; set; }

    public bool? Mutation { get; set; }

    public string? Posture { get; set; }

    public bool? Writable { get; set; }

    public int NodeCount { get; set; }

    public bool? OverlayShown { get; set; }

    public bool? WindowOpen { get; set; }

    public string? Error { get; set; }
}

[JsonSerializable(typeof(DevToolsListPayload))]
[JsonSerializable(typeof(DevToolsAttachPayload))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class DevToolsProtocolJsonContext : JsonSerializerContext;
