// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Commands;

/// <summary>
/// Provides a short description for a command, used in the help listing.
/// The long description (set via the Command constructor) remains used for
/// --cli-schema, LLM documentation, and individual command --help pages.
/// </summary>
internal interface IShortDescription
{
    string ShortDescription { get; }
}

/// <summary>
/// Example command lines shown in a command's help. Each example is a full command line
/// (starting with <c>winapp</c>) that uses placeholders such as <c>&lt;app&gt;</c>,
/// <c>&lt;hwnd&gt;</c>, and <c>&lt;selector&gt;</c> rather than real values.
/// </summary>
internal interface IHelpExamples
{
    IReadOnlyList<string> Examples { get; }

    /// <summary>Usage line override, for commands whose arguments are optional.</summary>
    string? Usage => null;
}
