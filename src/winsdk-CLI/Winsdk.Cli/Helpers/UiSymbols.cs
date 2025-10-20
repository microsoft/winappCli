// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace Winsdk.Cli.Helpers;

internal static class UiSymbols
{
    private static bool? _useEmoji;
    public static bool UseEmoji => _useEmoji ??= Compute();

    private static bool Compute()
    {
        try
        {
            bool isUtf8 = Console.OutputEncoding?.CodePage == 65001;
            bool isVsCode = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VSCODE_PID")) ||
                            string.Equals(Environment.GetEnvironmentVariable("TERM_PROGRAM"), "vscode", StringComparison.OrdinalIgnoreCase);
            bool isWindowsTerminal = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION"));
            bool notRedirected = !Console.IsOutputRedirected;
            return isUtf8 && notRedirected && (isVsCode || isWindowsTerminal);
        }
        catch
        {
            return false;
        }
    }

    public static string Rocket => UseEmoji ? "🚀" : "[INIT]";
    public static string Folder => UseEmoji ? "📂" : "[DIR]";
    public static string Note => UseEmoji ? "📝" : "[CFG]";
    public static string New => UseEmoji ? "🆕" : "[NEW]";
    public static string Wrench => UseEmoji ? "🔧" : "[TOOL]";
    public static string Package => UseEmoji ? "📦" : "[PKG]";
    public static string Bullet => UseEmoji ? "•" : "-";
    public static string Skip => UseEmoji ? "⏭" : "SKIP";
    public static string Tools => UseEmoji ? "🛠️" : "[TOOL]";
    public static string Files => UseEmoji ? "📁" : "[COPY]";
    public static string Check => UseEmoji ? "✅" : "[OK]";
    public static string Books => UseEmoji ? "📚" : "[LIB]";
    public static string Gear => UseEmoji ? "⚙️" : "[GEN]";
    public static string Search => UseEmoji ? "🔎" : "[SCAN]";
    public static string Save => UseEmoji ? "💾" : "[SAVE]";
    public static string Party => UseEmoji ? "🎉" : "[DONE]";
    public static string Warning => UseEmoji ? "⚠️" : "[WARN]";
    public static string Error => UseEmoji ? "❌" : "[ERR]";
    public static string Info => UseEmoji ? "ℹ️" : "[INFO]";
    public static string Trash => UseEmoji ? "🗑️" : "[DEL]";
    public static string Sync => UseEmoji ? "🔄" : "[SYNC]";
    public static string Add => UseEmoji ? "➕" : "[ADD]";
    public static string Lock => UseEmoji ? "🔐" : "[LOCK]";
    public static string User => UseEmoji ? "👤" : "[USER]";
    public static string Id => UseEmoji ? "🆔" : "[ID]";
    public static string Clipboard => UseEmoji ? "📋" : "[CLIP]";
}
