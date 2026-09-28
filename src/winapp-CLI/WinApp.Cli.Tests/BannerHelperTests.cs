// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class BannerHelperTests
{
    private const int Utf8CodePage = 65001;

    [TestMethod]
    public void ComputeUseEmoji_Utf8AndVsCodeViaPid_ReturnsTrue()
    {
        Assert.IsTrue(BannerHelper.ComputeUseEmoji(Utf8CodePage, outputRedirected: false,
            vscodePid: "12345", termProgram: null, wtSession: null));
    }

    [TestMethod]
    public void ComputeUseEmoji_Utf8AndVsCodeViaTermProgram_CaseInsensitive_ReturnsTrue()
    {
        Assert.IsTrue(BannerHelper.ComputeUseEmoji(Utf8CodePage, outputRedirected: false,
            vscodePid: null, termProgram: "VSCode", wtSession: null));
    }

    [TestMethod]
    public void ComputeUseEmoji_Utf8AndWindowsTerminal_ReturnsTrue()
    {
        Assert.IsTrue(BannerHelper.ComputeUseEmoji(Utf8CodePage, outputRedirected: false,
            vscodePid: null, termProgram: null, wtSession: "session-guid"));
    }

    [TestMethod]
    public void ComputeUseEmoji_Utf8ButPlainTerminal_ReturnsFalse()
    {
        Assert.IsFalse(BannerHelper.ComputeUseEmoji(Utf8CodePage, outputRedirected: false,
            vscodePid: null, termProgram: "xterm", wtSession: null));
    }

    [TestMethod]
    public void ComputeUseEmoji_NotUtf8_ReturnsFalse()
    {
        Assert.IsFalse(BannerHelper.ComputeUseEmoji(1252, outputRedirected: false,
            vscodePid: "12345", termProgram: null, wtSession: null));
    }

    [TestMethod]
    public void ComputeUseEmoji_NullCodePage_ReturnsFalse()
    {
        Assert.IsFalse(BannerHelper.ComputeUseEmoji(null, outputRedirected: false,
            vscodePid: null, termProgram: null, wtSession: "session-guid"));
    }

    [TestMethod]
    public void ComputeUseEmoji_OutputRedirected_ReturnsFalse()
    {
        Assert.IsFalse(BannerHelper.ComputeUseEmoji(Utf8CodePage, outputRedirected: true,
            vscodePid: "12345", termProgram: null, wtSession: null));
    }

    [TestMethod]
    public void DisplayBanner_ColorForm_WritesGradientAnsiAndVersion()
    {
        using var sw = new StringWriter();
        BannerHelper.DisplayBanner(sw, useColor: true);
        var output = sw.ToString();

        Assert.IsTrue(output.Contains("\x1b[", StringComparison.Ordinal),
            "Color banner must emit ANSI escape sequences.");
        Assert.IsTrue(output.Contains("\x1b[38;2;96;205;255m", StringComparison.Ordinal),
            "Color banner must start with the WinUI light blue.");
        Assert.IsTrue(output.Contains("\x1b[38;2;0;120;212m", StringComparison.Ordinal),
            "Color banner must include the Microsoft blue accent.");
        Assert.IsTrue(output.Contains("\x1b[38;2;0;90;158m", StringComparison.Ordinal),
            "Color banner must end with deep blue.");
        Assert.IsTrue(output.Contains("Windows App Development CLI", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains(VersionHelper.GetVersionString(), StringComparison.Ordinal),
            "Banner must include the CLI version.");
    }

    [TestMethod]
    public void DisplayBanner_PlainForm_WritesAsciiArtAndVersionWithoutAnsi()
    {
        using var sw = new StringWriter();
        BannerHelper.DisplayBanner(sw, useColor: false);
        var output = sw.ToString();

        Assert.IsFalse(output.Contains("\x1b[", StringComparison.Ordinal),
            "Plain banner must not emit ANSI escape sequences.");
        Assert.IsTrue(output.Contains("Windows App Development CLI - Version", StringComparison.Ordinal));
        Assert.IsTrue(output.Contains(VersionHelper.GetVersionString(), StringComparison.Ordinal));
    }

    [TestMethod]
    public void UseEmoji_IsCachedAndReturnsStableValue()
    {
        // Exercises the cached Compute() path; the value must be deterministic across reads.
        var first = BannerHelper.UseEmoji;
        var second = BannerHelper.UseEmoji;
        Assert.AreEqual(first, second);
    }
}
