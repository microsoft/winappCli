// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Winsdk.Cli.Models;

internal record CreateMsixPackageResult(FileInfo MsixPath, bool Signed);
