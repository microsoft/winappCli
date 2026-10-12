// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Reads the .NET template engine's on-disk <c>templatecache.json</c> documents. These hold the
/// structured template metadata (mount points, parameters, host option mappings) that
/// <c>dotnet new</c> exposes no machine-readable command for, letting <c>winapp new</c> derive the
/// correct target-framework option/value per installed pack instead of hard-coding them. Abstracted
/// behind an interface so the file access is fakeable in tests.
/// </summary>
internal interface ITemplateCacheReader
{
    /// <summary>
    /// Returns the raw JSON of every <c>templatecache.json</c> the template engine has written for the
    /// current user (one per SDK version), most recently written first so a cache rebuilt by the active
    /// SDK precedes stale ones left by older SDK bands. Returns an empty sequence when the template
    /// engine cache directory does not exist or can't be read, so callers can fall back gracefully.
    /// </summary>
    IReadOnlyList<string> ReadTemplateCacheDocuments();
}
