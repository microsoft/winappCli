// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Recording;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers window/element video recording with a dependency-injection container.
/// </summary>
public static class UiRecordingServiceCollectionExtensions
{
    /// <summary>
    /// Adds <see cref="IUiRecordingService"/>. Requires the UI Automation services
    /// (<c>AddWinAppUiAutomation</c>) to be registered as well — recording resolves and captures
    /// windows through them.
    /// </summary>
    public static IServiceCollection AddWinAppUiRecording(this IServiceCollection services)
        => services.AddSingleton<IUiRecordingService, UiRecordingService>();
}
