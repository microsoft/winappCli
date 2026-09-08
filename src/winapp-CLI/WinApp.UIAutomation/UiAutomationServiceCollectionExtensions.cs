// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the Windows UI Automation engine with a dependency-injection container.
/// </summary>
public static class UiAutomationServiceCollectionExtensions
{
    /// <summary>
    /// Adds the UI Automation services: element inspection and interaction
    /// (<see cref="IUiAutomation"/>), target/window resolution (<see cref="IUiTargetResolver"/>),
    /// selector parsing (<see cref="IUiSelectorParser"/>), window capture
    /// (<see cref="IWindowCapture"/>), and input injection (<see cref="IKeyboardInput"/>,
    /// <see cref="IMouseInput"/>, <see cref="IPointerInput"/>).
    /// </summary>
    public static IServiceCollection AddWinAppUiAutomation(this IServiceCollection services)
    {
        // CA1416: registering the touch/pen implementation does not invoke it. The Windows 10 1809
        // requirement is declared on IPointerInput itself, so callers that actually inject touch or
        // pen input still get the platform diagnostic at their call site.
#pragma warning disable CA1416
        return services
            .AddSingleton<IMouseInput, RealMouseInput>()
            .AddSingleton<IPointerInput, RealPointerInput>()
#pragma warning restore CA1416
            .AddSingleton<IKeyboardInput, RealKeyboardInput>()
            .AddSingleton<IForegroundGuard, RealForegroundGuard>()
            .AddSingleton<IOwnedWindowFinder, RealOwnedWindowFinder>()
            .AddSingleton<IPollDelay, RealPollDelay>()
            .AddSingleton<IUiSelectorParser, UiSelectorParser>()
            .AddSingleton<ISystemUiQuery, SystemUiQuery>()
            .AddSingleton<IUiTargetResolver, UiTargetResolver>()
#if WINDOWS10_0_19041_0_OR_GREATER
            .AddSingleton<IWindowCapture, WgcWindowCapture>()
#else
            .AddSingleton<IWindowCapture, GdiWindowCapture>()
#endif
            .AddSingleton<IUiAutomation, UiAutomationService>();
    }
}
