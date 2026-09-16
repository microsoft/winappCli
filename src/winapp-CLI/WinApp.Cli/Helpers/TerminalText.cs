// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text;

namespace WinApp.Cli.Helpers;

/// <summary>
/// Makes text that came from somewhere else safe to print to a terminal.
/// </summary>
/// <remarks>
/// Window titles, element names, and process names are chosen by whatever is running, which for an
/// execution target means software the caller is deliberately testing and does not trust. A terminal
/// treats escape sequences in that text as instructions, not characters: a title can repaint the
/// line it was printed on, move the cursor over output above it, or set the window title. Rendering
/// such a value verbatim would let the thing being reported on decide what the report says.
/// <para>
/// Only human output needs this. JSON is data rather than instructions, so it carries the original
/// value and lets the consumer decide — a caller that diffs titles must see exactly what was there.
/// </para>
/// </remarks>
internal static class TerminalText
{
    private const char Escape = '\u001b';
    private const char Bell = '\u0007';

    /// <summary>Single-glyph stand-in for a line break, so one value stays on one line.</summary>
    private const char LineBreakGlyph = '\u21b5';

    /// <summary>Single-glyph stand-in for a tab, which would otherwise realign the whole row.</summary>
    private const char TabGlyph = '\u2192';

    /// <summary>
    /// Returns <paramref name="text"/> with terminal control sequences removed.
    /// </summary>
    /// <remarks>
    /// Escape sequences are dropped whole — introducer, parameters, and terminator — rather than
    /// having their escape character stripped, because leaving the parameters behind as literal text
    /// would substitute one wrong rendering for another. Line breaks and tabs become single visible
    /// glyphs so that a multi-line title stays one row of a report.
    /// </remarks>
    /// <param name="text">Untrusted text, typically reported by a guest.</param>
    /// <returns>Text safe to write to a terminal, never null.</returns>
    public static string Sanitize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!NeedsSanitizing(text))
        {
            return text;
        }

        var builder = new StringBuilder(text.Length);

        for (var index = 0; index < text.Length; index++)
        {
            var current = text[index];

            switch (current)
            {
                case Escape when index + 1 < text.Length:
                    index = SkipEscapeSequence(text, index);
                    break;

                // A lone trailing escape introduces nothing, so there is nothing to keep.
                case Escape:
                    index = text.Length;
                    break;

                // C1 CSI and C1 OSC do the same work as their two-character escape forms, and are
                // what a UTF-8 terminal sees when these code points arrive directly.
                case '\u009b':
                    index = SkipUntilCsiFinal(text, index + 1) - 1;
                    break;

                case '\u009d':
                    index = SkipUntilStringTerminator(text, index + 1) - 1;
                    break;

                case '\r':
                    // Collapse CRLF so one line break does not become two glyphs.
                    if (index + 1 < text.Length && text[index + 1] == '\n')
                    {
                        index++;
                    }

                    builder.Append(LineBreakGlyph);
                    break;

                case '\n':
                    builder.Append(LineBreakGlyph);
                    break;

                case '\t':
                    builder.Append(TabGlyph);
                    break;

                default:
                    if (!IsControl(current))
                    {
                        builder.Append(current);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    /// <summary>True when the text contains anything a terminal would act on.</summary>
    private static bool NeedsSanitizing(string text)
    {
        foreach (var character in text)
        {
            if (IsControl(character) || character is '\r' or '\n' or '\t')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>C0 and C1 controls, which is every character a terminal interprets.</summary>
    private static bool IsControl(char character) =>
        character < ' ' || character == '\u007f' || (character >= '\u0080' && character <= '\u009f');

    /// <summary>Returns the index of the last character of the sequence starting at the escape.</summary>
    private static int SkipEscapeSequence(string text, int escapeIndex)
    {
        var introducer = text[escapeIndex + 1];

        return introducer switch
        {
            // CSI: parameters and intermediates, then one final byte.
            '[' => SkipUntilCsiFinal(text, escapeIndex + 2) - 1,

            // OSC, DCS, SOS, PM and APC all run until a string terminator. OSC additionally accepts
            // BEL, which is the form most shells emit.
            ']' or 'P' or 'X' or '^' or '_' => SkipUntilStringTerminator(text, escapeIndex + 2) - 1,

            // Everything else is a two-character sequence, such as ESC c (reset).
            _ => escapeIndex + 1,
        };
    }

    /// <summary>Returns the index just past a CSI sequence's final byte.</summary>
    private static int SkipUntilCsiFinal(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] >= '\u0040' && text[index] <= '\u007e')
            {
                return index + 1;
            }
        }

        return text.Length;
    }

    /// <summary>Returns the index just past a string terminator, BEL, or the end of the text.</summary>
    private static int SkipUntilStringTerminator(string text, int start)
    {
        for (var index = start; index < text.Length; index++)
        {
            if (text[index] == Bell || text[index] == '\u009c')
            {
                return index + 1;
            }

            if (text[index] == Escape && index + 1 < text.Length && text[index + 1] == '\\')
            {
                return index + 2;
            }
        }

        return text.Length;
    }
}
