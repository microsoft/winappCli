// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinApp.Cli.Services;

internal interface IStorageDiagnostics
{
    void Warning(string code, string message);
}

internal sealed class StorageDiagnostics(
    TextWriter error, bool json = false, bool quiet = false, bool deferWarnings = false) : IStorageDiagnostics
{
    private readonly Lock _gate = new();
    private readonly HashSet<(string Code, string Message)> _reported = [];
    private bool _completed;

    public void Warning(string code, string message)
    {
        if (quiet)
        {
            return;
        }

        lock (_gate)
        {
            if (!_completed && _reported.Add((code, message)) && !deferWarnings)
            {
                WriteWarning(error, json, code, message);
            }
        }
    }

    internal void Complete(bool succeeded)
    {
        lock (_gate)
        {
            if (!_completed && deferWarnings && succeeded && !quiet)
            {
                foreach (var warning in _reported)
                {
                    WriteWarning(error, json, warning.Code, warning.Message);
                }
            }
            _completed = true;
        }
    }

    internal static void WriteWarning(TextWriter error, bool json, string code, string message)
    {
        error.WriteLine(json
            ? JsonSerializer.Serialize(
                new StorageWarningEnvelope(new StorageWarning(code, message)),
                StorageWarningJsonContext.Default.StorageWarningEnvelope)
            : $"Warning: {message}");
    }
}

internal sealed record StorageWarning(string Code, string Message);
internal sealed record StorageWarningEnvelope(StorageWarning Warning);

[JsonSerializable(typeof(StorageWarningEnvelope))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class StorageWarningJsonContext : JsonSerializerContext
{
}
