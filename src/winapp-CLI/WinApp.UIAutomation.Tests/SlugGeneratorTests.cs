// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using Windows.Win32.System.Com;

using Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

/// <summary>
/// Exhaustive unit tests for <see cref="SlugGenerator"/> — the deterministic, shell-safe
/// semantic slug generator used to give UIA elements stable, token-efficient identifiers.
/// Covers the pure string/hash logic plus the COM-interop (SAFEARRAY) overloads, which are
/// exercised by allocating a real OLE SAFEARRAY so the unsafe pointer walk runs end-to-end.
/// </summary>
[TestClass]
public class SlugGeneratorTests
{
    // ---------------------------------------------------------------------
    // GetPrefix
    // ---------------------------------------------------------------------

    [TestMethod]
    public void GetPrefix_KnownControlTypes_MapToThreeLetterCodes()
    {
        Assert.AreEqual("btn", SlugGenerator.GetPrefix("Button"));
        Assert.AreEqual("txt", SlugGenerator.GetPrefix("Edit"));
        Assert.AreEqual("txt", SlugGenerator.GetPrefix("TextBox"));
        Assert.AreEqual("chk", SlugGenerator.GetPrefix("CheckBox"));
        Assert.AreEqual("lbl", SlugGenerator.GetPrefix("Text"));
        Assert.AreEqual("win", SlugGenerator.GetPrefix("Window"));
        Assert.AreEqual("mnu", SlugGenerator.GetPrefix("MenuItem"));
    }

    [TestMethod]
    public void GetPrefix_IsCaseInsensitive()
    {
        // The prefix map uses an OrdinalIgnoreCase comparer.
        Assert.AreEqual("btn", SlugGenerator.GetPrefix("button"));
        Assert.AreEqual("btn", SlugGenerator.GetPrefix("BUTTON"));
    }

    [TestMethod]
    public void GetPrefix_UnknownControlType_FallsBackToElm()
    {
        Assert.AreEqual("elm", SlugGenerator.GetPrefix("SomeCustomControl"));
        Assert.AreEqual("elm", SlugGenerator.GetPrefix(""));
    }

    // ---------------------------------------------------------------------
    // GetTypesForPrefix
    // ---------------------------------------------------------------------

    [TestMethod]
    public void GetTypesForPrefix_UniquePrefix_ReturnsSingleType()
    {
        string[] expected = ["Button"];
        CollectionAssert.AreEquivalent(expected, SlugGenerator.GetTypesForPrefix("btn"));
    }

    [TestMethod]
    public void GetTypesForPrefix_SharedPrefix_ReturnsAllMappedTypes()
    {
        // Both Tab and TabItem map to "tab"; Edit and TextBox map to "txt".
        string[] tab = ["Tab", "TabItem"];
        string[] txt = ["Edit", "TextBox"];
        string[] mnu = ["Menu", "MenuItem"];
        CollectionAssert.AreEquivalent(tab, SlugGenerator.GetTypesForPrefix("tab"));
        CollectionAssert.AreEquivalent(txt, SlugGenerator.GetTypesForPrefix("txt"));
        CollectionAssert.AreEquivalent(mnu, SlugGenerator.GetTypesForPrefix("mnu"));
    }

    [TestMethod]
    public void GetTypesForPrefix_UnknownPrefix_ReturnsUnknownSentinel()
    {
        string[] unknown = ["Unknown"];
        CollectionAssert.AreEqual(unknown, SlugGenerator.GetTypesForPrefix("zzz"));
        // "elm" is the generic fallback prefix and is not itself a mapped value.
        CollectionAssert.AreEqual(unknown, SlugGenerator.GetTypesForPrefix("elm"));
    }

    // ---------------------------------------------------------------------
    // Normalize
    // ---------------------------------------------------------------------

    [TestMethod]
    public void Normalize_NullOrWhitespace_ReturnsNull()
    {
        Assert.IsNull(SlugGenerator.Normalize(null));
        Assert.IsNull(SlugGenerator.Normalize(""));
        Assert.IsNull(SlugGenerator.Normalize("   "));
        Assert.IsNull(SlugGenerator.Normalize("\t\n"));
    }

    [TestMethod]
    public void Normalize_LowercasesAndStripsNonAlphanumeric()
    {
        Assert.AreEqual("hello", SlugGenerator.Normalize("Hello"));
        Assert.AreEqual("helloworld", SlugGenerator.Normalize("Hello World!"));
        Assert.AreEqual("abc123", SlugGenerator.Normalize("abc123"));
        Assert.AreEqual("minimize", SlugGenerator.Normalize("Minimize"));
    }

    [TestMethod]
    public void Normalize_AllSymbols_ReturnsNull()
    {
        // After stripping, nothing alphanumeric remains -> null (not empty string).
        Assert.IsNull(SlugGenerator.Normalize("!@#$%^&*()"));
        Assert.IsNull(SlugGenerator.Normalize("---"));
    }

    [TestMethod]
    public void Normalize_StripsNonAsciiLetters_Unicode()
    {
        // é, ü, ï, ö are outside [a-z0-9] and are removed.
        Assert.AreEqual("ncd", SlugGenerator.Normalize("Ünïcödé"));
        Assert.AreEqual("caf", SlugGenerator.Normalize("Café"));
        Assert.AreEqual("nave", SlugGenerator.Normalize("naïve"));
    }

    [TestMethod]
    public void Normalize_LongInput_TruncatedTo15Chars()
    {
        var result = SlugGenerator.Normalize("abcdefghijklmnopqrstuvwxyz");
        Assert.AreEqual("abcdefghijklmno", result);
        Assert.AreEqual(15, result!.Length);
    }

    [TestMethod]
    public void Normalize_Exactly15Chars_NotTruncated()
    {
        Assert.AreEqual("abcdefghijklmno", SlugGenerator.Normalize("abcdefghijklmno"));
    }

    // ---------------------------------------------------------------------
    // ComputeHash (int[])
    // ---------------------------------------------------------------------

    [TestMethod]
    public void ComputeHash_EmptyArray_IsDeterministicSeed()
    {
        // Seed 17 -> (uint)0x00000011 -> last four hex chars "0011".
        Assert.AreEqual("0011", SlugGenerator.ComputeHash([]));
    }

    [TestMethod]
    public void ComputeHash_SingleElement_KnownValue()
    {
        // 17 * 31 + 1 = 528 = 0x210 -> "00000210" -> "0210".
        Assert.AreEqual("0210", SlugGenerator.ComputeHash([1]));
    }

    [TestMethod]
    public void ComputeHash_IsDeterministicAndFourHexChars()
    {
        var a = SlugGenerator.ComputeHash([42, 7, 3]);
        var b = SlugGenerator.ComputeHash([42, 7, 3]);
        Assert.AreEqual(a, b, "Hash must be deterministic for identical input.");
        Assert.AreEqual(4, a.Length);
        Assert.IsTrue(a.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')), $"Hash '{a}' must be lowercase hex.");
    }

    [TestMethod]
    public void ComputeHash_DifferentInputs_ProduceDifferentHashes()
    {
        Assert.AreNotEqual(SlugGenerator.ComputeHash([1, 2, 3]), SlugGenerator.ComputeHash([3, 2, 1]));
    }

    // ---------------------------------------------------------------------
    // ComputeHashFromSafeArray / GenerateSlugFromSafeArray (COM interop)
    // ---------------------------------------------------------------------

    [TestMethod]
    public unsafe void ComputeHashFromSafeArray_Null_ReturnsZeros()
    {
        Assert.AreEqual("0000", SlugGenerator.ComputeHashFromSafeArray(null));
    }

    [TestMethod]
    public void ComputeHashFromSafeArray_MatchesManagedComputeHash()
    {
        // The SAFEARRAY overload must produce the exact same hash as the int[] overload.
        Assert.AreEqual(SlugGenerator.ComputeHash([1]), HashFromSafeArray(1));
        Assert.AreEqual(SlugGenerator.ComputeHash([42, 7, 3]), HashFromSafeArray(42, 7, 3));
        Assert.AreEqual("0210", HashFromSafeArray(1));
    }

    [TestMethod]
    public void GenerateSlugFromSafeArray_WithAutomationId_UsesAutomationId()
    {
        var slug = SlugFromSafeArray("Button", "MinimizeBtn", "Minimize", 1);
        Assert.AreEqual("btn-minimizebtn-0210", slug);
    }

    [TestMethod]
    public void GenerateSlugFromSafeArray_NoAutomationId_FallsBackToName()
    {
        var slug = SlugFromSafeArray("Button", automationId: null, name: "Close", 1);
        Assert.AreEqual("btn-close-0210", slug);
    }

    [TestMethod]
    public void GenerateSlugFromSafeArray_NoNameOrAutomationId_OmitsNameSegment()
    {
        var slug = SlugFromSafeArray("Button", automationId: null, name: null, 1);
        Assert.AreEqual("btn-0210", slug);
    }

    [TestMethod]
    public void GenerateSlugFromSafeArray_UnknownControlType_UsesElmPrefix()
    {
        var slug = SlugFromSafeArray("CustomThing", automationId: null, name: null, 1);
        Assert.AreEqual("elm-0210", slug);
    }

    // ---------------------------------------------------------------------
    // ParseSlug
    // ---------------------------------------------------------------------

    [TestMethod]
    public void ParseSlug_PrefixAndHashOnly_ParsesWithNullName()
    {
        var parsed = SlugGenerator.ParseSlug("btn-1a2b");
        Assert.IsNotNull(parsed);
        Assert.AreEqual("btn", parsed.Value.Prefix);
        Assert.IsNull(parsed.Value.NameSlug);
        Assert.AreEqual("1a2b", parsed.Value.Hash);
    }

    [TestMethod]
    public void ParseSlug_PrefixNameHash_ParsesAllThreeParts()
    {
        var parsed = SlugGenerator.ParseSlug("btn-minimize-c4b9");
        Assert.IsNotNull(parsed);
        Assert.AreEqual("btn", parsed.Value.Prefix);
        Assert.AreEqual("minimize", parsed.Value.NameSlug);
        Assert.AreEqual("c4b9", parsed.Value.Hash);
    }

    [TestMethod]
    public void ParseSlug_MultiPartName_JoinsMiddleSegments()
    {
        var parsed = SlugGenerator.ParseSlug("btn-foo-bar-c4b9");
        Assert.IsNotNull(parsed);
        Assert.AreEqual("btn", parsed.Value.Prefix);
        Assert.AreEqual("foo-bar", parsed.Value.NameSlug);
        Assert.AreEqual("c4b9", parsed.Value.Hash);
    }

    [TestMethod]
    public void ParseSlug_ElmGenericPrefix_IsAccepted()
    {
        var parsed = SlugGenerator.ParseSlug("elm-dead");
        Assert.IsNotNull(parsed);
        Assert.AreEqual("elm", parsed.Value.Prefix);
        Assert.AreEqual("dead", parsed.Value.Hash);
    }

    [TestMethod]
    public void ParseSlug_UnknownPrefix_ReturnsNull()
    {
        Assert.IsNull(SlugGenerator.ParseSlug("zzz-1a2b"));
    }

    [TestMethod]
    public void ParseSlug_NullOrWhitespace_ReturnsNull()
    {
        Assert.IsNull(SlugGenerator.ParseSlug(null!));
        Assert.IsNull(SlugGenerator.ParseSlug(""));
        Assert.IsNull(SlugGenerator.ParseSlug("   "));
    }

    [TestMethod]
    public void ParseSlug_TooFewParts_ReturnsNull()
    {
        Assert.IsNull(SlugGenerator.ParseSlug("btn"));
    }

    [TestMethod]
    public void ParseSlug_HashWrongLength_ReturnsNull()
    {
        Assert.IsNull(SlugGenerator.ParseSlug("btn-abc"));     // 3 chars
        Assert.IsNull(SlugGenerator.ParseSlug("btn-12345"));   // 5 chars
    }

    [TestMethod]
    public void ParseSlug_HashNotHex_ReturnsNull()
    {
        // Correct length (4) but contains non-hex characters.
        Assert.IsNull(SlugGenerator.ParseSlug("btn-wxyz"));
        Assert.IsNull(SlugGenerator.ParseSlug("btn-12g4"));
    }

    [TestMethod]
    public void ParseSlug_RoundTripsGeneratedSlug()
    {
        var slug = SlugFromSafeArray("Button", "SaveAll", null, 5, 9);
        var parsed = SlugGenerator.ParseSlug(slug);
        Assert.IsNotNull(parsed);
        Assert.AreEqual("btn", parsed.Value.Prefix);
        Assert.AreEqual("saveall", parsed.Value.NameSlug);
    }

    // ---------------------------------------------------------------------
    // SAFEARRAY interop helpers (test-only; build a real OLE vector).
    // ---------------------------------------------------------------------

    private const ushort VT_I4 = 3;

    [DllImport("oleaut32.dll")]
    private static extern unsafe SAFEARRAY* SafeArrayCreateVector(ushort vt, int lLbound, uint cElements);

    [DllImport("oleaut32.dll")]
    private static extern unsafe int SafeArrayAccessData(SAFEARRAY* psa, out nint ppvData);

    [DllImport("oleaut32.dll")]
    private static extern unsafe int SafeArrayUnaccessData(SAFEARRAY* psa);

    [DllImport("oleaut32.dll")]
    private static extern unsafe int SafeArrayDestroy(SAFEARRAY* psa);

    private static unsafe string HashFromSafeArray(params int[] runtimeId)
    {
        SAFEARRAY* psa = CreateInt32SafeArray(runtimeId);
        try
        {
            return SlugGenerator.ComputeHashFromSafeArray(psa);
        }
        finally
        {
            _ = SafeArrayDestroy(psa);
        }
    }

    private static unsafe string SlugFromSafeArray(string controlType, string? automationId, string? name, params int[] runtimeId)
    {
        SAFEARRAY* psa = CreateInt32SafeArray(runtimeId);
        try
        {
            return SlugGenerator.GenerateSlugFromSafeArray(controlType, automationId, name, psa);
        }
        finally
        {
            _ = SafeArrayDestroy(psa);
        }
    }

    private static unsafe SAFEARRAY* CreateInt32SafeArray(int[] values)
    {
        SAFEARRAY* psa = SafeArrayCreateVector(VT_I4, 0, (uint)values.Length);
        Assert.IsTrue(psa != null, "SafeArrayCreateVector returned null.");

        try
        {
            Assert.AreEqual(0, SafeArrayAccessData(psa, out nint data), "SafeArrayAccessData failed.");
            try
            {
                var p = (int*)data;
                for (var i = 0; i < values.Length; i++)
                {
                    p[i] = values[i];
                }
            }
            finally
            {
                _ = SafeArrayUnaccessData(psa);
            }
        }
        catch
        {
            // Don't leak the unmanaged SAFEARRAY if an assertion fails mid-initialization.
            _ = SafeArrayDestroy(psa);
            throw;
        }

        return psa;
    }
}
