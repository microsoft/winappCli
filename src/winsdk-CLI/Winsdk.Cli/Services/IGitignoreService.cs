// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Winsdk.Cli.Services;

internal interface IGitignoreService
{
    bool UpdateGitignore(DirectoryInfo projectDirectory);
    bool AddCertificateToGitignore(DirectoryInfo projectDirectory, string certificateFileName);
}
