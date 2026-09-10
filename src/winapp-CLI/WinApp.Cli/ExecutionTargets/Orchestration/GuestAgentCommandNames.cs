// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.ExecutionTargets.Orchestration;

/// <summary>Names shared by the bootstrap and hidden guest-agent command.</summary>
internal static class GuestAgentCommandNames
{
    public const string Verb = "guest-agent";
    public const string SelfTestOption = "--self-test";
    public const string BinaryName = "winapp.exe";
}
