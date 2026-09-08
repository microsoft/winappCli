// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Globalization;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation;

/// <summary>Transport used to deliver synthetic keyboard input.</summary>
public enum KeyTransport
{
    /// <summary>
    /// Posts WM_KEYDOWN/WM_KEYUP/WM_CHAR straight to a specific window's message queue, bypassing the
    /// global input stream — so it never triggers low-level keyboard hooks or global hotkeys. Still
    /// subject to UIPI: a lower-integrity process cannot post to a higher-integrity window.
    /// </summary>
    PostMessage,

    /// <summary>
    /// Injects OS-wide input via SendInput, so low-level hooks and global hotkeys see it. Subject to
    /// UIPI: input from a lower-integrity process does not reach a higher-integrity window.
    /// </summary>
    SendInput,
}

/// <summary>A single parsed keyboard action.</summary>
public abstract record KeyAction;

/// <summary>
/// A key press, optionally with held modifiers (e.g., <c>enter</c>, <c>down</c>, <c>ctrl+shift+t</c>).
/// Modifiers are pressed before and released after the main key.
/// </summary>
public sealed record KeyChord(IReadOnlyList<ushort> Modifiers, ushort Vk, bool Extended) : KeyAction;

/// <summary>Literal text typed character by character (e.g., <c>hello</c>).</summary>
public sealed record TextInput(string Text) : KeyAction;

/// <summary>
/// Parses the friendly key-string grammar used by <c>winapp ui send-keys</c> into a list of
/// <see cref="KeyAction"/>s. Tokens are whitespace separated:
/// <list type="bullet">
/// <item>Named keys: <c>down</c>, <c>enter</c>, <c>tab</c>, <c>esc</c>, <c>f5</c> …</item>
/// <item>Modifier combos: <c>ctrl+shift+t</c>, <c>alt+f4</c></item>
/// <item>Raw virtual keys: <c>vk=0x42</c> or <c>vk=66</c></item>
/// <item>Explicit literal text: <c>text=enter</c> types the word "enter" instead of pressing Enter.</item>
/// <item>Anything else is treated as literal text and typed character by character.</item>
/// </list>
/// <see cref="ParseVerbatim"/> is the command-level counterpart to the per-token <c>text=</c> escape:
/// it types the whole string literally with no interpretation at all.
/// </summary>
public static class KeyStringParser
{
    // Modifier name -> virtual-key code.
    private static readonly Dictionary<string, ushort> Modifiers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ctrl"] = 0x11, ["control"] = 0x11,
        ["shift"] = 0x10,
        ["alt"] = 0x12, ["menu"] = 0x12,
        ["win"] = 0x5B, ["cmd"] = 0x5B, ["super"] = 0x5B, ["meta"] = 0x5B,
    };

    // Named key -> (virtual-key code, is-extended-key).
    private static readonly Dictionary<string, (ushort Vk, bool Extended)> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = (0x0D, false), ["return"] = (0x0D, false),
        ["tab"] = (0x09, false),
        ["esc"] = (0x1B, false), ["escape"] = (0x1B, false),
        ["space"] = (0x20, false), ["spacebar"] = (0x20, false),
        ["backspace"] = (0x08, false), ["bksp"] = (0x08, false), ["bs"] = (0x08, false),
        ["delete"] = (0x2E, true), ["del"] = (0x2E, true),
        ["insert"] = (0x2D, true), ["ins"] = (0x2D, true),
        ["home"] = (0x24, true), ["end"] = (0x23, true),
        ["pageup"] = (0x21, true), ["pgup"] = (0x21, true),
        ["pagedown"] = (0x22, true), ["pgdn"] = (0x22, true),
        ["up"] = (0x26, true), ["down"] = (0x28, true), ["left"] = (0x25, true), ["right"] = (0x27, true),
        ["capslock"] = (0x14, false),
        ["printscreen"] = (0x2C, true), ["prtsc"] = (0x2C, true),
        ["apps"] = (0x5D, true), ["menukey"] = (0x5D, true),
        ["f1"] = (0x70, false), ["f2"] = (0x71, false), ["f3"] = (0x72, false), ["f4"] = (0x73, false),
        ["f5"] = (0x74, false), ["f6"] = (0x75, false), ["f7"] = (0x76, false), ["f8"] = (0x77, false),
        ["f9"] = (0x78, false), ["f10"] = (0x79, false), ["f11"] = (0x7A, false), ["f12"] = (0x7B, false),
        ["f13"] = (0x7C, false), ["f14"] = (0x7D, false), ["f15"] = (0x7E, false), ["f16"] = (0x7F, false),
    };

    /// <summary>
    /// Parses a key string into ordered key actions.
    /// </summary>
    /// <exception cref="FormatException">A token uses the <c>vk=</c> form with an invalid value, or an
    /// unknown modifier/key name was used inside a <c>+</c> combo.</exception>
    public static IReadOnlyList<KeyAction> Parse(string keys)
    {
        if (string.IsNullOrWhiteSpace(keys))
        {
            throw new FormatException("No keys to send. Provide one or more tokens, e.g. \"ctrl+a delete\".");
        }

        var actions = new List<KeyAction>();
        var tokens = keys.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        foreach (var token in tokens)
        {
            // Explicit literal text: text=enter types the word "enter" rather than pressing Enter.
            // Mirrors the vk= escape and lets values that collide with a key/modifier name (enter,
            // del, up, ctrl+a, …) be typed verbatim. Checked first so the escape always wins.
            if (token.StartsWith("text=", StringComparison.OrdinalIgnoreCase))
            {
                actions.Add(new TextInput(DecodeTextEscapes(token["text=".Length..])));
                continue;
            }

            // Modifier combo, e.g. ctrl+shift+t. Only treated as a chord when the leading segments
            // are real modifiers, so literal tokens that merely contain '+' (e.g. "C++", "a+b") are
            // typed as text instead of throwing. (A bare "+" main key is supported via vk=.)
            if (TryParseChord(token, out var chord))
            {
                actions.Add(chord);
                continue;
            }

            // Raw virtual key: vk=0x42 / vk=66
            if (TryParseVk(token, out var rawVk))
            {
                actions.Add(new KeyChord([], rawVk, IsExtendedVk(rawVk)));
                continue;
            }

            // Named key: down, enter, f5 …
            if (NamedKeys.TryGetValue(token, out var named))
            {
                actions.Add(new KeyChord([], named.Vk, named.Extended));
                continue;
            }

            // Otherwise literal text typed character by character.
            actions.Add(new TextInput(token));
        }

        // The tokenizer splits on whitespace, so a quoted phrase like "Hello world" arrives as two
        // literal-text tokens. Re-join adjacent literal runs with a single space so the phrase is
        // typed verbatim; runs separated by a key (e.g. "Hello enter world") stay distinct.
        return CoalesceText(actions);
    }

    /// <summary>
    /// Treats the entire key string as one literal to type verbatim: no whitespace tokenizing, no
    /// named-key / combo / <c>vk=</c> / <c>text=</c> interpretation, no whitespace collapsing, and no
    /// backslash-escape decoding. The command-level counterpart to the per-token <c>text=</c> escape
    /// (exposed as <c>--verbatim</c>), for when the whole payload is literal text that would otherwise
    /// be read as keys — e.g. typing the word <c>enter</c>, the phrase <c>down down enter</c>, or text
    /// with exact internal spacing that <see cref="Parse"/> would collapse.
    /// </summary>
    /// <exception cref="FormatException">The string is null or empty.</exception>
    public static IReadOnlyList<KeyAction> ParseVerbatim(string keys)
    {
        // Only a genuinely empty argument is "nothing to send". Whitespace is legitimate verbatim
        // content (this method's whole point is exact preservation), so "   " types three spaces.
        if (string.IsNullOrEmpty(keys))
        {
            throw new FormatException("No text to send. Provide the literal text to type, e.g. \"down down enter\".");
        }

        return [new TextInput(keys)];
    }

    /// <summary>
    /// Decodes backslash escapes inside a <c>text=</c> value so whitespace the tokenizer can't carry
    /// (it splits on whitespace and re-joins literal runs with a single space) can still be typed
    /// verbatim: <c>\s</c>→space, <c>\t</c>→tab, <c>\n</c>→newline, <c>\r</c>→newline (a carriage return, delivered as one line break),
    /// <c>\\</c>→backslash. So <c>text=a\s\sb</c> types "a  b" (double space) and <c>text=line1\nline2</c>
    /// types a newline. An unknown escape (e.g. <c>\x</c>) is left as-is so literal backslashes in text
    /// don't silently vanish.
    /// </summary>
    private static string DecodeTextEscapes(string value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value;
        }

        var sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] != '\\' || i + 1 >= value.Length)
            {
                sb.Append(value[i]);
                continue;
            }

            char next = value[i + 1];
            switch (next)
            {
                case 's': sb.Append(' '); i++; break;
                case 't': sb.Append('\t'); i++; break;
                case 'n': sb.Append('\n'); i++; break;
                case 'r': sb.Append('\r'); i++; break;
                case '\\': sb.Append('\\'); i++; break;
                default: sb.Append('\\'); break; // unknown escape — keep the backslash literal
            }
        }

        return sb.ToString();
    }

    private static List<KeyAction> CoalesceText(List<KeyAction> actions)
    {
        var merged = new List<KeyAction>(actions.Count);
        foreach (var action in actions)
        {
            if (action is TextInput text && merged.Count > 0 && merged[^1] is TextInput prev)
            {
                merged[^1] = new TextInput(prev.Text + " " + text.Text);
            }
            else
            {
                merged.Add(action);
            }
        }

        return merged;
    }

    /// <summary>
    /// Parses a modifier combo (e.g. <c>ctrl+shift+t</c>). Returns <see langword="false"/> when the token
    /// is not a modifier-led combo (so the caller can fall back to literal text), and throws only when the
    /// token clearly *is* a modifier combo but names an unknown main key (e.g. <c>ctrl+bogus</c>).
    /// </summary>
    private static bool TryParseChord(string token, out KeyChord chord)
    {
        chord = null!;

        if (!token.Contains('+'))
        {
            return false;
        }

        var parts = token.Split('+');

        // Only a modifier-led token is meant as a combo. If the first segment isn't a known modifier,
        // it's ordinary literal text that merely contains '+' ("a+b", "C++", "1+1", a bare "+"); let the
        // caller fall back to literal text.
        if (parts.Length < 2 || !Modifiers.ContainsKey(parts[0]))
        {
            return false;
        }

        // It's modifier-led, so it's unambiguously meant as a combo. An empty segment now means a
        // malformed combo — a doubled or trailing '+' ("ctrl++a", "ctrl+", "ctrl++"). Surface it instead
        // of silently dropping the empty and pressing a *different* chord (e.g. "ctrl++a" → "ctrl+a").
        if (Array.Exists(parts, string.IsNullOrEmpty))
        {
            throw new FormatException(
                $"Malformed key combo '{token}': empty segment around '+'. Separate the modifiers and key with single '+' (e.g. ctrl+shift+t). " +
                $"To type a literal '+', use text={token} or pass --verbatim.");
        }

        // Every segment except the last must be a known modifier; otherwise it's literal ("ctrl+a+b",
        // where 'a' isn't a modifier).
        for (int i = 0; i < parts.Length - 1; i++)
        {
            if (!Modifiers.ContainsKey(parts[i]))
            {
                return false;
            }
        }

        var modifiers = new List<ushort>();
        for (int i = 0; i < parts.Length - 1; i++)
        {
            modifiers.Add(Modifiers[parts[i]]);
        }

        var mainKey = parts[^1];
        if (!TryResolveKey(mainKey, out var vk, out var extended))
        {
            throw new FormatException(
                $"Unknown key '{mainKey}' in '{token}'. Use a named key (enter, down, f5), a single character, or vk=0xNN.");
        }

        chord = new KeyChord(modifiers, vk, extended);
        return true;
    }

    private static bool TryResolveKey(string name, out ushort vk, out bool extended)
    {
        if (TryParseVk(name, out vk))
        {
            extended = IsExtendedVk(vk);
            return true;
        }

        if (NamedKeys.TryGetValue(name, out var named))
        {
            vk = named.Vk;
            extended = named.Extended;
            return true;
        }

        // Single character: map to its virtual key via the active keyboard layout.
        if (name.Length == 1)
        {
            short scan = global::Windows.Win32.PInvoke.VkKeyScan(name[0]);
            if (scan != -1)
            {
                vk = (ushort)(scan & 0xFF);
                extended = false;
                return true;
            }
        }

        extended = false;
        return false;
    }

    private static bool TryParseVk(string token, out ushort vk)
    {
        vk = 0;
        if (!token.StartsWith("vk=", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var value = token[3..];
        bool parsed = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? ushort.TryParse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out vk)
            : ushort.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out vk);

        if (!parsed || vk == 0 || vk > 0xFF)
        {
            throw new FormatException($"Invalid virtual key '{token}'. Use vk=0xNN or vk=NN with a value between 1 and 255.");
        }

        return true;
    }

    /// <summary>Virtual keys that require the extended-key flag for correct delivery.</summary>
    private static bool IsExtendedVk(ushort vk) => vk is
        0x21 or 0x22 or 0x23 or 0x24 or // PgUp PgDn End Home
        0x25 or 0x26 or 0x27 or 0x28 or // arrows
        0x2D or 0x2E or                 // Insert Delete
        0x2C or                         // PrintScreen
        0x5B or 0x5C or 0x5D or         // LWin RWin Apps
        0x90;                           // NumLock
}
