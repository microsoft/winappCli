// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Spectre.Console;

namespace WinApp.Cli.Helpers;

internal static class UiSymbols
{
    public static string Rocket => "🚀";
    public static string Folder => "📂";
    public static string Note => "📝";
    public static string New => "🆕";
    public static string Wrench => "🔧";
    public static string Package => "📦";
    public static string Bullet => "•";
    public static string Skip => "⏭";
    public static string Tools => "🛠️";
    public static string Files => "📁";
    public static string Check => "✅";
    public static string Books => "📚";
    public static string Gear => "⚙️";
    public static string Search => "🔎";
    public static string Save => "💾";
    public static string Party => "🎉";
    public static string Warning => "⚠️";
    public static string Error => "❌";
    public static string Info => "ℹ️";
    public static string Trash => "🗑️";
    public static string Sync => "🔄";
    public static string Add => "➕";
    public static string Lock => "🔐";
    public static string User => "👤";
    public static string Id => "🆔";
    public static string Clipboard => "📋";
    public static string Verbose => "🔍";

    public static Spinner DefaultSpinner => Spinner.Known.Dots;
}
