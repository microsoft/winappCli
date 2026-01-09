// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json.Serialization;

namespace WinApp.Cli.Models;

[JsonConverter(typeof(JsonStringEnumConverter<ManifestTemplates>))]
public enum ManifestTemplates
{
    Packaged,
    Sparse,
    HostedApp
}
