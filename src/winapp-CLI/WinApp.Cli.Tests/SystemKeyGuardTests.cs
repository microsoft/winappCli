// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class SystemKeyGuardTests
{
    [TestMethod]
    [DataRow("win+l")]
    [DataRow("win+r")]
    [DataRow("win+d")]
    [DataRow("win+e")]
    [DataRow("cmd+l")]    // alias for win
    [DataRow("super+tab")]
    public void WinModifiedCombos_AreReportedGenerically(string keys)
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual("win+<key>", combos[0]);
    }

    [TestMethod]
    [DataRow("ctrl+shift+esc", "ctrl+shift+esc")]
    [DataRow("ctrl+alt+del", "ctrl+alt+del")]
    [DataRow("ctrl+esc", "ctrl+esc")]
    [DataRow("alt+tab", "alt+tab")]
    [DataRow("alt+esc", "alt+esc")]
    [DataRow("alt+f4", "alt+f4")]
    [DataRow("alt+printscreen", "alt+printscreen")]
    public void KnownSystemCombos_AreReportedByName(string keys, string expected)
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual(expected, combos[0]);
    }

    [TestMethod]
    public void LoneWinKey_IsReported()
    {
        // The Win key has no named token; it can only be sent raw via vk=0x5B.
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("vk=0x5B"));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual("win", combos[0]);
    }

    [TestMethod]
    public void LonePrintScreen_IsReported()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("printscreen"));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual("printscreen", combos[0]);
    }

    [TestMethod]
    [DataRow("ctrl+a")]
    [DataRow("ctrl+c")]
    [DataRow("ctrl+shift+t")]
    [DataRow("enter")]
    [DataRow("down down up")]
    [DataRow("f5")]
    [DataRow("alt+a")]
    [DataRow("ctrl+alt+a")]
    public void OrdinaryCombos_AreNotReported(string keys)
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(0, combos.Count);
    }

    [TestMethod]
    public void LiteralText_IsNotReported()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("Hello world"));
        Assert.AreEqual(0, combos.Count);
    }

    [TestMethod]
    public void Duplicates_AreCollapsed()
    {
        // Two Win combos collapse to a single generic entry.
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("win+l win+r"));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual("win+<key>", combos[0]);
    }

    [TestMethod]
    public void MultipleDistinctCombos_PreserveFirstSeenOrder()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("alt+f4 ctrl+esc alt+tab"));
        Assert.AreEqual(3, combos.Count);
        Assert.AreEqual("alt+f4", combos[0]);
        Assert.AreEqual("ctrl+esc", combos[1]);
        Assert.AreEqual("alt+tab", combos[2]);
    }

    [TestMethod]
    public void MixedSafeAndSystemKeys_ReportsOnlySystem()
    {
        var combos = SystemKeyGuard.FindSystemCombos(KeyStringParser.Parse("ctrl+a win+l enter"));
        Assert.AreEqual(1, combos.Count);
        Assert.AreEqual("win+<key>", combos[0]);
    }

    [TestMethod]
    public void EmptyActions_ReturnsEmpty()
    {
        Assert.AreEqual(0, SystemKeyGuard.FindSystemCombos([]).Count);
    }

    // --- FindNeverBypassableCombos ---

    [TestMethod]
    [DataRow("win+l")]   // standard win+l
    [DataRow("cmd+l")]   // cmd is an alias for win
    public void NeverBypassable_WinL_IsDetected(string keys)
    {
        // win+l (and its aliases) must always be refused — it locks the workstation via the shell hook.
        var hits = SystemKeyGuard.FindNeverBypassableCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("win+l", hits[0].Name);
        Assert.Contains("locks the workstation", hits[0].Reason);
    }

    [TestMethod]
    [DataRow("ctrl+alt+del")]
    [DataRow("ctrl+alt+delete")]      // "delete" alias resolves to the same VK
    [DataRow("ctrl+alt+shift+del")]   // extra modifier must not defeat the block
    public void NeverBypassable_CtrlAltDel_IsDetected(string keys)
    {
        // ctrl+alt+del is the Secure Attention Sequence — Windows drops synthesized SAS input, so it can
        // never take effect and must be hard-blocked (reporting success for it would be misleading).
        var hits = SystemKeyGuard.FindNeverBypassableCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("ctrl+alt+del", hits[0].Name);
        Assert.Contains("SAS", hits[0].Reason);
    }

    [TestMethod]
    [DataRow("win+r")]
    [DataRow("win+d")]
    [DataRow("win+shift+v")]
    [DataRow("alt+f4")]
    [DataRow("ctrl+shift+esc")]
    [DataRow("ctrl+alt+a")]  // ctrl+alt with a non-Delete key is an ordinary combo, not the SAS
    [DataRow("vk=0x5B")]  // lone win key
    public void NeverBypassable_OtherCombos_AreNotHardBlocked(string keys)
    {
        // Soft-blocked combos (win+r, alt+f4, etc.) must NOT appear in the never-bypassable list —
        // callers may legitimately opt in to them with --allow-system-keys.
        var hits = SystemKeyGuard.FindNeverBypassableCombos(KeyStringParser.Parse(keys));
        Assert.AreEqual(0, hits.Count);
    }

    [TestMethod]
    public void NeverBypassable_EmptyActions_ReturnsEmpty()
    {
        Assert.AreEqual(0, SystemKeyGuard.FindNeverBypassableCombos([]).Count);
    }

    [TestMethod]
    public void NeverBypassable_Duplicates_AreCollapsed()
    {
        // Multiple win+l tokens collapse to a single entry.
        var hits = SystemKeyGuard.FindNeverBypassableCombos(KeyStringParser.Parse("win+l win+l"));
        Assert.AreEqual(1, hits.Count);
        Assert.AreEqual("win+l", hits[0].Name);
    }

    [TestMethod]
    public void NeverBypassable_DistinctHardBlocks_PreserveFirstSeenOrder()
    {
        // A sequence containing both hard-blocked combos reports each once, in first-seen order.
        var hits = SystemKeyGuard.FindNeverBypassableCombos(KeyStringParser.Parse("ctrl+alt+del win+l"));
        Assert.AreEqual(2, hits.Count);
        Assert.AreEqual("ctrl+alt+del", hits[0].Name);
        Assert.AreEqual("win+l", hits[1].Name);
    }
}
