// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Models;

internal enum AddExecutionAliasStatus
{
    Added,
    AlreadyExists,
    ConflictingAliasExists,
    CoexistenceUnsafe,
    NoApplicationElement,
    ApplicationIdNotFound,
    CouldNotInferAlias,
    InvalidAliasName,
    ManifestParseError,
    ManifestEmpty,
}

internal enum ExecutionAliasConflictPolicy
{
    PreserveExisting,
    Reject,
    Coexist,
}

internal sealed record AddExecutionAliasResult(
    AddExecutionAliasStatus Status,
    string? AliasName = null,
    string? ExistingAlias = null,
    string? ErrorMessage = null);
