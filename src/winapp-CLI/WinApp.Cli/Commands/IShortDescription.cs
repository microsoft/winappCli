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

/// <summary>
/// A command group (<c>winapp ui</c>, <c>winapp devtools</c>) whose help is the compact plain-text
/// layout agents read. Its description is a one-line summary; its commands implement
/// <see cref="IHelpExamples"/>.
/// </summary>
internal interface ICompactHelpGroup
{
    /// <summary>The workflow block shown under the summary in the group's help only, so it stays out of the
    /// command's description (and the CLI schema).</summary>
    string GoldenPath { get; }

    /// <summary>Command-list categories, in display order.</summary>
    IReadOnlyList<(string Category, Type[] CommandTypes)> Categories { get; }

    /// <summary>The option rows shown in the group's help.</summary>
    IReadOnlyList<(string Name, string Description)> GroupOptions { get; }

    /// <summary>How the group's commands take their target app, when they accept both -a and -w.</summary>
    string TargetUsage { get; }

    /// <summary>Words other tools use, mapped to the command that does the job, for "Did you mean".</summary>
    IReadOnlyDictionary<string, string> Synonyms { get; }

    /// <summary>The commands listed when a command name is unknown.</summary>
    IReadOnlyList<string> CommonCommands { get; }
}
