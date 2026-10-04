// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinApp.Cli.Services.DevTools;

/// <summary>
/// Shared reader settings for the tap's wire replies. <c>System.Text.Json</c> defaults to a nesting ceiling of
/// 64, but a <c>visualtree.enumerate</c> reply nests one object plus one <c>children</c> array PER VISUAL-TREE
/// LEVEL, so the default only admits ~31 levels of UI. Real apps go far deeper (AI Dev Gallery measures 97 JSON
/// levels), and past the ceiling every parse throws and the census reads as "the agent did not answer" — which
/// is how a comment came to be stored with an empty identity bundle. The tap's own walk is bounded by
/// node budget, not depth, so the client must not impose a depth limit the server never promised to respect.
/// </summary>
internal static class TapWireJson
{
    public const int MaxDepth = 1024;

    public static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = MaxDepth };

    public static readonly TapWireJsonContext Context =
        new(new JsonSerializerOptions(TapWireJsonContext.Default.Options) { MaxDepth = MaxDepth });
}

/// <summary>
/// DTOs for the native tap's single-line JSON replies (<c>visualtree.enumerate</c>, <c>property.get</c>,
/// <c>element.source</c>). Read-only and source-generated so they work under the CLI's AOT publish. Only the
/// fields the CLI consumes are modeled; the tap may emit more, which is ignored.
/// </summary>
/// <summary>Reply of <c>property.get|&lt;handle&gt;</c>: the element's curated live properties.</summary>
internal sealed class TapPropsReply
{
    public string? Handle { get; set; }

    public List<TapProp> Props { get; set; } = [];
}

internal sealed class TapProp
{
    public string? Name { get; set; }

    public string? Value { get; set; }

    public string? ValueType { get; set; }
}

internal sealed class TapElementSource
{
    public string? FileName { get; set; }

    public int LineNumber { get; set; }

    public int ColumnNumber { get; set; }

    public string? AuthoredState { get; set; }
    public string? AuthoredFileName { get; set; }
    public int AuthoredLineNumber { get; set; }
    public int AuthoredColumnNumber { get; set; }
    public string? CoordinateProvenance { get; set; }
    public string? SourceEvidence { get; set; }
    public string? Xaml { get; set; }
}

[JsonSerializable(typeof(TapPropsReply))]
[JsonSerializable(typeof(TapElementSource))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class TapWireJsonContext : JsonSerializerContext;
