// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;
using System.Text.Json.Serialization;

namespace WinApp.Cli.Models;

internal class CertGenerateJsonOutput
{
    public required string CertificatePath { get; set; }
    public required string Password { get; set; }
    public required string Publisher { get; set; }
    public required string SubjectName { get; set; }
    public string? PublicCertificatePath { get; set; }
}

internal class CertInfoJsonOutput
{
    public required string Subject { get; set; }
    public required string Issuer { get; set; }
    public required string Thumbprint { get; set; }
    public required string SerialNumber { get; set; }
    public required string NotBefore { get; set; }
    public required string NotAfter { get; set; }
    public required bool HasPrivateKey { get; set; }
}

internal class JsonErrorOutput
{
    public required string Error { get; set; }

    /// <summary>
    /// Writes a JSON error object to stdout and returns the given exit code.
    /// Use this from command handlers when --json is active and an error occurs.
    /// </summary>
    public static int Write(IAnsiConsole console, string message, int exitCode = 1)
    {
        var output = new JsonErrorOutput { Error = message };
        console.Profile.Out.Writer.WriteLine(System.Text.Json.JsonSerializer.Serialize(output, WinAppJsonContext.Default.JsonErrorOutput));
        return exitCode;
    }
}

/// <summary>Compact <c>find-ui</c> search result: one entry per matched control,
/// each carrying its top scenario rows (id + header) but no code — callers fetch
/// full XAML/C# with <c>find-ui --id &lt;id&gt;</c>.</summary>
internal sealed class FindUiSearchJsonOutput
{
    public required string Query { get; set; }
    public required int MatchCount { get; set; }
    /// <inheritdoc cref="FindUiCodeJsonOutput.Corpus"/>
    public string? Corpus { get; set; }
    public required List<FindUiMatchJson> Matches { get; set; }
}

internal sealed class FindUiMatchJson
{
    public required string Source { get; set; }
    public required string Control { get; set; }
    public required double Score { get; set; }
    public string? Description { get; set; }
    public required List<FindUiScenarioJson> Scenarios { get; set; }
}

internal sealed class FindUiScenarioJson
{
    public required string Id { get; set; }
    public required string Header { get; set; }
}

/// <summary>Output of <c>find-ui --list</c>: every discoverable scenario id + label.</summary>
internal sealed class FindUiListJsonOutput
{
    public required int Count { get; set; }
    /// <inheritdoc cref="FindUiCodeJsonOutput.Corpus"/>
    public string? Corpus { get; set; }
    public required List<FindUiScenarioJson> Items { get; set; }
}

/// <summary>Output of <c>find-ui --id &lt;id&gt;</c>: full formatted code/notes per id.</summary>
internal sealed class FindUiCodeJsonOutput
{
    /// <summary>
    /// Least-fresh origin backing this response: <c>network</c> (fetched now),
    /// <c>cache</c> (this machine's earlier fetch), or <c>embedded</c> (the corpus baked
    /// into the CLI, so these samples may lag upstream — either the fetch failed or the
    /// local cache predates the bake).
    /// Reported so an agent can tell a live answer from an offline fallback. Omitted when
    /// the result came only from the curated core patterns, which have no upstream.
    /// </summary>
    public string? Corpus { get; set; }
    public required List<FindUiCodeEntryJson> Results { get; set; }
}

internal sealed class FindUiCodeEntryJson
{
    public required string Id { get; set; }
    public required bool Found { get; set; }
    public required string Content { get; set; }
}

/// <summary>
/// Source-generated JSON serializer context for all CLI JSON output models.
/// Add new [JsonSerializable(typeof(...))] attributes here when adding --json output to more commands.
/// </summary>
[JsonSerializable(typeof(CertGenerateJsonOutput))]
[JsonSerializable(typeof(CertInfoJsonOutput))]
[JsonSerializable(typeof(JsonErrorOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiSearchOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiMembersOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiCheckPropertyOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiTypesOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiEnumsOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiNamespacesOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiPackagesOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiStatsOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiProjectsOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiRefreshOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiSearchBatchOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiMembersBatchOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiEnumsBatchOutput))]
[JsonSerializable(typeof(WinApp.Cli.Services.ApiSearch.ApiCheckPropertyBatchOutput))]
[JsonSerializable(typeof(FindUiSearchJsonOutput))]
[JsonSerializable(typeof(FindUiListJsonOutput))]
[JsonSerializable(typeof(FindUiCodeJsonOutput))]
[JsonSourceGenerationOptions(
    WriteIndented = true,
    NewLine = "\n",
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
internal partial class WinAppJsonContext : JsonSerializerContext
{
}
