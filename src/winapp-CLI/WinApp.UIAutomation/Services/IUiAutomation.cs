// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.


namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Low-level UI Automation (UIA) operations. Uses Windows UIA APIs to inspect
/// and interact with any running Windows app.
/// </summary>
public interface IUiAutomation
{
    /// <summary>
    /// Find all top-level windows matching a partial title. Returns (HWND, PID, Title) tuples.
    /// Uses Win32 enumeration to find ALL windows including inactive/background ones.
    /// </summary>
    List<(nint Hwnd, int Pid, string Title)> FindWindowsByTitle(string titleQuery);

    /// <summary>
    /// Find all top-level windows for a specific process ID.
    /// </summary>
    List<(nint Hwnd, int Pid, string Title)> FindWindowsByPid(int pid);

    /// <summary>
    /// Resolve a top-level window's screen rectangle via <c>GetWindowRect</c>. Returns
    /// <see langword="false"/> (and a default rect) when the handle is 0, invalid, or unreadable —
    /// callers must treat that as "no verifiable target window". Used to bounds-check
    /// <c>ui touch</c>/<c>ui pen</c> coordinates before any OS-wide injection.
    /// </summary>
    bool TryGetWindowRect(long hwnd, out PointerRect rect);

    /// <summary>
    /// Walks the element tree under <paramref name="elementId"/>, or the whole window when it is
    /// <see langword="null"/>, down to <paramref name="depth"/> levels.
    /// </summary>
    /// <param name="uiTarget">The app or window to inspect.</param>
    /// <param name="elementId">Selector of the element to start from, or <see langword="null"/> for the window root.</param>
    /// <param name="depth">How many levels below the starting element to include.</param>
    /// <param name="ct">Cancels the walk.</param>
    /// <returns>The elements found, in walk order.</returns>
    Task<UiElement[]> InspectAsync(UiTarget uiTarget, string? elementId, int depth, CancellationToken ct);

    /// <summary>Walks from an element up to the window root, so a caller can see where it sits in the tree.</summary>
    /// <param name="uiTarget">The app or window that owns the element.</param>
    /// <param name="elementId">Selector of the element to walk up from.</param>
    /// <param name="ct">Cancels the walk.</param>
    /// <returns>The chain from the window root down to and including the element itself, which is the last entry.</returns>
    Task<UiElement[]> InspectAncestorsAsync(UiTarget uiTarget, string elementId, CancellationToken ct);

    /// <summary>Finds every element matching <paramref name="selector"/>, up to <paramref name="maxResults"/>.</summary>
    /// <param name="uiTarget">The app or window to search.</param>
    /// <param name="selector">What to match. A slug names one element, so it yields at most one result.</param>
    /// <param name="maxResults">Caps how many matches are returned.</param>
    /// <param name="ct">Cancels the search.</param>
    /// <returns>The matching elements, or an empty array when nothing matched.</returns>
    Task<UiElement[]> SearchAsync(UiTarget uiTarget, UiSelector selector, int maxResults, CancellationToken ct);

    /// <summary>Finds the one element matching <paramref name="selector"/>.</summary>
    /// <param name="uiTarget">The app or window to search.</param>
    /// <param name="selector">What to match.</param>
    /// <param name="ct">Cancels the search.</param>
    /// <returns>The match, or <see langword="null"/> when nothing matched.</returns>
    /// <exception cref="UiAmbiguousSelectorException">More than one element matched.</exception>
    Task<UiElement?> FindSingleElementAsync(UiTarget uiTarget, UiSelector selector, CancellationToken ct);

    /// <summary>Reads an element's UIA properties.</summary>
    /// <param name="uiTarget">The app or window that owns the element.</param>
    /// <param name="element">The element to read.</param>
    /// <param name="propertyName">A single property to read, or <see langword="null"/> for all of them.</param>
    /// <param name="ct">Cancels the read.</param>
    /// <returns>Property names mapped to their values.</returns>
    Task<Dictionary<string, object?>> GetPropertiesAsync(UiTarget uiTarget, UiElement element, string? propertyName, CancellationToken ct);

    /// <summary>Captures the window, or one element's region, as raw BGRA pixels.</summary>
    /// <param name="uiTarget">The app or window to capture.</param>
    /// <param name="elementId">Selector of the element to crop to, or <see langword="null"/> for the whole window.</param>
    /// <param name="captureScreen">Read from the screen instead of the window, which includes anything overlapping it.</param>
    /// <param name="focus">Bring the window to the foreground first.</param>
    /// <param name="ct">Cancels the capture.</param>
    /// <returns>The pixels and their dimensions.</returns>
    Task<(byte[] Pixels, int Width, int Height)> ScreenshotAsync(UiTarget uiTarget, string? elementId, bool captureScreen, bool focus, CancellationToken ct);

    /// <summary>Invokes an element the way a click would, through its UIA pattern rather than the mouse.</summary>
    /// <param name="uiTarget">The app or window that owns the element.</param>
    /// <param name="element">The element to invoke.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The pattern that was used, such as "Invoke" or "Toggle".</returns>
    Task<string> InvokeAsync(UiTarget uiTarget, UiElement element, CancellationToken ct);

    /// <summary>Replaces an editable element's text through its ValuePattern.</summary>
    /// <param name="uiTarget">The app or window that owns the element.</param>
    /// <param name="element">The element to write to.</param>
    /// <param name="text">The replacement text.</param>
    /// <param name="ct">Cancels the call.</param>
    Task SetValueAsync(UiTarget uiTarget, UiElement element, string text, CancellationToken ct);

    /// <summary>Moves keyboard focus to an element.</summary>
    /// <param name="uiTarget">The app or window that owns the element.</param>
    /// <param name="element">The element to focus.</param>
    /// <param name="ct">Cancels the call.</param>
    Task FocusAsync(UiTarget uiTarget, UiElement element, CancellationToken ct);

    /// <summary>Scrolls an element's container until the element is on screen.</summary>
    /// <param name="uiTarget">The app or window that owns the element.</param>
    /// <param name="element">The element to bring into view.</param>
    /// <param name="ct">Cancels the call.</param>
    Task ScrollIntoViewAsync(UiTarget uiTarget, UiElement element, CancellationToken ct);

    /// <summary>Scrolls a scrollable container.</summary>
    /// <param name="uiTarget">The app or window that owns the element.</param>
    /// <param name="element">The container to scroll.</param>
    /// <param name="direction">"up", "down", "left", or "right".</param>
    /// <param name="destination">"top" or "bottom" to jump to either extreme instead of stepping.</param>
    /// <param name="ct">Cancels the call.</param>
    Task ScrollContainerAsync(UiTarget uiTarget, UiElement element, string? direction, string? destination, CancellationToken ct);

    /// <summary>Reads whichever element currently has keyboard focus.</summary>
    /// <param name="uiTarget">The app or window to look in.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The focused element, or <see langword="null"/> when nothing is focused.</returns>
    Task<UiElement?> GetFocusedElementAsync(UiTarget uiTarget, CancellationToken ct);

    /// <summary>Reads an element's text through its TextPattern or ValuePattern.</summary>
    /// <param name="uiTarget">The app or window that owns the element.</param>
    /// <param name="element">The element to read.</param>
    /// <param name="ct">Cancels the call.</param>
    /// <returns>The text, or <see langword="null"/> when the element exposes none.</returns>
    Task<string?> GetTextAsync(UiTarget uiTarget, UiElement element, CancellationToken ct);

    /// <summary>
    /// Resolves the target's root UIA window. Returns <see langword="false"/> when no UIA window
    /// exists for the target. <paramref name="hwnd"/> is 0 when the root element has no native window
    /// handle, in which case callers should fall back to <see cref="UiTarget.WindowHandle"/>.
    /// </summary>
    bool TryResolveRootWindow(UiTarget target, out nint hwnd, out string? title);

    /// <summary>
    /// Resolves the element's top-level native window by walking its UIA ancestors, or 0 when no
    /// ancestor exposes one. Lets a caller retarget capture at the window an element actually lives
    /// in, which for popups and dialogs is not the session window.
    /// </summary>
    nint ResolveElementTopLevelWindow(UiTarget target, UiElement element);

    /// <summary>
    /// The window's bounds excluding the invisible DWM resize border, falling back to
    /// <paramref name="fallback"/> when the extended frame bounds are unavailable.
    /// </summary>
    PointerRect GetVisibleWindowBounds(nint hwnd, PointerRect fallback);
}
