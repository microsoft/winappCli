// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace WinApp.Cli.Services.Controls;

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// The find-ui vocabulary derived from the Windows samples site (embedded
/// <c>search-vocabulary.json</c>, schema version 1). Only phrases and one-way expansions
/// are consumed; site-specific synonym groups are not used by find-ui.
/// </summary>
internal sealed class SearchVocabulary
{
    public const int SupportedSchemaVersion = 1;

    [JsonPropertyName("schemaVersion")]
    public int SchemaVersion { get; set; }

    /// <summary>Multi-word phrases rewritten to a single token before matching.</summary>
    [JsonPropertyName("phrases")]
    public VocabularyPhrase[] Phrases { get; set; } = [];

    /// <summary>One-way expansions: a query for the term also matches its targets.</summary>
    [JsonPropertyName("expansions")]
    public VocabularyExpansion[] Expansions { get; set; } = [];

    public static SearchVocabulary LoadEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("search-vocabulary.json")!;
        var vocabulary = JsonSerializer.Deserialize(stream, SearchVocabularyJsonContext.Default.SearchVocabulary)!;
        if (vocabulary.SchemaVersion != SupportedSchemaVersion)
        {
            throw new InvalidDataException(
                $"search-vocabulary.json has schemaVersion {vocabulary.SchemaVersion}; this build reads version {SupportedSchemaVersion}.");
        }
        return vocabulary;
    }
}

internal sealed class VocabularyPhrase
{
    [JsonPropertyName("phrase")]
    public string Phrase { get; set; } = "";

    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    /// <summary>true: the token replaces the phrase. false: the token is added and the original words are kept.</summary>
    [JsonPropertyName("replace")]
    public bool Replace { get; set; }
}

internal sealed class VocabularyExpansion
{
    [JsonPropertyName("term")]
    public string Term { get; set; } = "";

    [JsonPropertyName("to")]
    public string[] To { get; set; } = [];
}

[JsonSerializable(typeof(SearchVocabulary))]
internal partial class SearchVocabularyJsonContext : JsonSerializerContext { }
