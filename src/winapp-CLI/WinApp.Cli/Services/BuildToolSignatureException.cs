// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace WinApp.Cli.Services;

/// <summary>
/// Thrown when a build tool was found but refused because it does not carry a valid Authenticode
/// signature from Microsoft. Derives from <see cref="InvalidOperationException"/> so existing
/// handlers keep working, while callers that report errors can tell this apart from an install
/// failure and avoid claiming the tool could not be found.
/// </summary>
/// <remarks>
/// It deliberately carries no inner exception. Several callers report a failure with
/// <c>GetBaseException().Message</c>, which returns the innermost exception — so anything nested
/// here would replace this message with a lower-level one and lose the remedy it gives the user.
/// </remarks>
internal sealed class BuildToolSignatureException(string message) : InvalidOperationException(message);
