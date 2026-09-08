// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>
/// Recognizes system- and shell-reserved key combinations so <c>send-keys --via send-input</c> can refuse
/// to synthesize them. These combos act on the OS/shell (lock, Start, Task Manager, close window), not just
/// the targeted app, when injected OS-wide — so send-input rejects them rather than acting beyond the target.
/// (<c>--via post-message</c> is window-scoped and is not affected.)
/// </summary>
public static class SystemKeyGuard
{
    /// <summary>
    /// A key combo that must NEVER be synthesized via send-input, paired with the reason it stays
    /// blocked even when the caller passes <c>--allow-system-keys</c>. Different combos are blocked
    /// for different reasons (<c>win+l</c> locks the session; <c>ctrl+alt+del</c> is a Secure
    /// Attention Sequence Windows drops from injected input), so the reason travels with the name.
    /// </summary>
    public readonly record struct HardBlockedCombo(string Name, string Reason);

    // Reasons are phrased as predicates ("<name> <reason>") so callers can compose a message.
    private const string WinLReason =
        "locks the workstation (LockWorkStation() via the shell hook), which is unrecoverable from automation";
    private const string CtrlAltDelReason =
        "is a Secure Attention Sequence (SAS) that Windows blocks from injected input regardless of privileges, " +
        "so it can never be synthesized from automation";

    private const ushort VkShift = 0x10;
    private const ushort VkControl = 0x11;
    private const ushort VkAlt = 0x12;
    private const ushort VkEsc = 0x1B;
    private const ushort VkTab = 0x09;
    private const ushort VkDelete = 0x2E;
    private const ushort VkPrintScreen = 0x2C;
    private const ushort VkF4 = 0x73;
    private const ushort VkLWin = 0x5B;
    private const ushort VkRWin = 0x5C;
    private const ushort VkL = 0x4C; // 'L' — win+l triggers LockWorkStation() via the shell hook

    /// <summary>
    /// Returns the combos that must NEVER be synthesized via send-input, even when the caller opts in
    /// with <c>--allow-system-keys</c>, each paired with the reason it stays blocked. Two combos qualify:
    /// <list type="bullet">
    /// <item><c>win+l</c> — VK_LWIN/VK_RWIN + VK_L (0x4C) fires <c>LockWorkStation()</c> via the shell
    /// hook when injected OS-wide, locking the interactive session with no recovery path from automation.</item>
    /// <item><c>ctrl+alt+del</c> — VK_CONTROL + VK_MENU + VK_DELETE (0x2E) is the Secure Attention
    /// Sequence; Windows discards synthesized SAS input at the OS level regardless of privilege or flag,
    /// so it can never take effect and reporting success for it is misleading.</item>
    /// </list>
    /// Unlike soft-blocked combos (<c>alt+f4</c>, <c>ctrl+shift+esc</c>, <c>win+r</c>, …) that can be
    /// opted into for driving global hotkeys, these two can never be usefully or safely driven from
    /// automation. (Extra modifiers alongside the combo — e.g. <c>win+shift+l</c>,
    /// <c>ctrl+alt+shift+del</c> — do not defeat the block.)
    /// </summary>
    public static IReadOnlyList<HardBlockedCombo> FindNeverBypassableCombos(IEnumerable<KeyAction> actions)
    {
        var hits = new List<HardBlockedCombo>();
        foreach (var action in actions)
        {
            if (action is not KeyChord chord)
            {
                continue;
            }

            bool win = chord.Modifiers.Contains(VkLWin) || chord.Modifiers.Contains(VkRWin);
            bool ctrl = chord.Modifiers.Contains(VkControl);
            bool alt = chord.Modifiers.Contains(VkAlt);

            if (win && chord.Vk == VkL)
            {
                AddUnique(hits, new HardBlockedCombo("win+l", WinLReason));
            }
            else if (ctrl && alt && chord.Vk == VkDelete)
            {
                AddUnique(hits, new HardBlockedCombo("ctrl+alt+del", CtrlAltDelReason));
            }
        }

        return hits;
    }

    private static void AddUnique(List<HardBlockedCombo> hits, HardBlockedCombo combo)
    {
        if (!hits.Any(h => h.Name == combo.Name))
        {
            hits.Add(combo);
        }
    }

    /// <summary>
    /// Returns the friendly names of any system-reserved combos present in <paramref name="actions"/>,
    /// in first-seen order with duplicates removed. Empty when none are present.
    /// </summary>
    public static IReadOnlyList<string> FindSystemCombos(IEnumerable<KeyAction> actions)
    {
        var hits = new List<string>();
        foreach (var action in actions)
        {
            if (action is not KeyChord chord)
            {
                continue;
            }

            var name = Describe(chord);
            if (name is not null && !hits.Contains(name))
            {
                hits.Add(name);
            }
        }

        return hits;
    }

    private static string? Describe(KeyChord chord)
    {
        bool ctrl = chord.Modifiers.Contains(VkControl);
        bool shift = chord.Modifiers.Contains(VkShift);
        bool alt = chord.Modifiers.Contains(VkAlt);
        bool win = chord.Modifiers.Contains(VkLWin) || chord.Modifiers.Contains(VkRWin);

        // Any Win-modified combo is a shell shortcut (win+l lock, win+r Run, win+d desktop, win+e, …).
        if (win)
        {
            return "win+<key>";
        }

        if (chord.Modifiers.Count == 0)
        {
            // Lone Win key opens Start; lone PrintScreen captures the screen.
            if (chord.Vk is VkLWin or VkRWin)
            {
                return "win";
            }

            if (chord.Vk == VkPrintScreen)
            {
                return "printscreen";
            }

            return null;
        }

        // Alt+PrintScreen captures the active window to the clipboard.
        if (alt && chord.Vk == VkPrintScreen)
        {
            return "alt+printscreen";
        }

        if (ctrl && shift && chord.Vk == VkEsc)
        {
            return "ctrl+shift+esc";
        }

        if (ctrl && alt && chord.Vk == VkDelete)
        {
            return "ctrl+alt+del";
        }

        if (ctrl && !alt && !shift && chord.Vk == VkEsc)
        {
            return "ctrl+esc";
        }

        if (alt && chord.Vk == VkTab)
        {
            return "alt+tab";
        }

        if (alt && chord.Vk == VkEsc)
        {
            return "alt+esc";
        }

        if (alt && chord.Vk == VkF4)
        {
            return "alt+f4";
        }

        return null;
    }
}
