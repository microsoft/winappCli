// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Models;

internal sealed record DevelopmentRegistration
{
    internal const int CurrentSchemaVersion = 1;
    [JsonRequired]
    public int SchemaVersion { get; init; } = CurrentSchemaVersion;
    [JsonRequired]
    public string AlgorithmVersion { get; init; } = DevelopmentIdentityHelper.SeedVersion;
    public required DevelopmentIdentity Identity { get; init; }
    public required string ManifestHash { get; init; }
    public DateTimeOffset UpdatedAtUtc { get; init; }
    public DateTimeOffset? RegisteredAtUtc { get; init; }
    [JsonIgnore]
    public bool IsPending { get; init; }
    [JsonIgnore]
    public long Revision => Identity.Revision;
}

internal sealed record PendingDevelopmentRegistration
{
    [JsonRequired]
    public int SchemaVersion { get; init; } = DevelopmentRegistration.CurrentSchemaVersion;
    public DevelopmentRegistration? Prior { get; init; }
    public required DevelopmentRegistration Candidate { get; init; }
}

[JsonSerializable(typeof(DevelopmentRegistration))]
[JsonSerializable(typeof(PendingDevelopmentRegistration))]
[JsonSourceGenerationOptions(WriteIndented = true)]
internal partial class DevelopmentRegistrationJsonContext : JsonSerializerContext;
