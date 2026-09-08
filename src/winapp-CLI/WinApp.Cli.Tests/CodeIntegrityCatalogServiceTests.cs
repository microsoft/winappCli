// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

extern alias winappcli;

using Microsoft.Extensions.Logging;
using WinApp.Cli.Models;
using WinApp.Cli.Services;

// CsWin32 generates a PInvoke class into every assembly that uses it, so the name is ambiguous
// between winapp and the recording package. These CRYPTCAT_* constants come from winapp.
using PInvoke = winappcli::Windows.Win32.PInvoke;

namespace WinApp.Cli.Tests;

/// <summary>
/// Tests for <see cref="CodeIntegrityCatalogService"/>. The full-catalog tests below invoke the
/// REAL native CryptCATCDF* APIs against real system executables, so the happy-path native flow is
/// genuinely exercised (not faked).
/// </summary>
/// <remarks>
/// Residual uncovered lines in CodeIntegrityCatalogService.cs (~93% line coverage in Debug) are the
/// native-failure paths, each of which needs a genuine native fault a unit test cannot deterministically
/// induce without contriving a flaky fault or adding a product seam. Named precisely:
/// <list type="bullet">
///   <item>ParseErrorCallback body (~47-55): an <c>[UnmanagedCallersOnly]</c> stdcall callback that ONLY
///     the native CryptCATCDF* parser invokes, and only on a CDF parse/member error. Its mapping logic
///     IS unit-tested directly via <see cref="CodeIntegrityCatalogService.DescribeCatalogErrorArea"/> /
///     <see cref="CodeIntegrityCatalogService.DescribeCatalogLocalError"/>; only the ~3-line native shell
///     (read the PWSTR line + LogError) is native-invoked.</item>
///   <item>Line ~276 <c>throw new Win32Exception</c>: reached only when CryptCATCDFOpen returns null (a real
///     native open/parse failure). These tests feed valid CDFs built from real system exes, so open succeeds.</item>
///   <item>Lines ~290-293 (outer <c>catch (Exception)</c>): the only thrower inside the guarded try is the
///     line ~276 Win32Exception (native-gated); the Enumerate* helpers only throw on a native enum fault, so
///     entering this catch likewise requires a real native failure.</item>
///   <item>Line ~304 (empty <c>catch { }</c> around <c>File.Delete(cdfPath)</c>): runs only if deleting the
///     temp CDF itself throws (e.g. the file is locked); not exercised.</item>
/// </list>
/// These are deferred to the wave-2 real-runtime integration test (a genuine malformed-CDF fault on CI).
/// NOT native-only and already covered: the finally <c>if (cdfOutputPath == null) File.Delete</c> cleanup
/// branch (~298-302) via the no-ref overload (seeds cdfOutputPath = null), and the
/// <c>else { cdfOutputPath = cdfPath; }</c> branch (~306-308) via the ref-based tests that seed
/// <c>cdfPath = string.Empty</c> (non-null).
/// </remarks>
[TestClass]
public class CodeIntegrityCatalogServiceTests : BaseCommandTests
{
    private string _testInputDirectory = null!;
    private CodeIntegrityCatalogService _codeIntegrityCatalogService = null!;

    private static void VerifyCdfContent(string content, List<string> files, string expectedCatalogPath, bool usePageHashes, bool computeFlatHashes)
    {
        StringAssert.Contains(content, "[CatalogHeader]");
        StringAssert.Contains(content, $"Name={expectedCatalogPath}");
        StringAssert.Contains(content, "PublicVersion=1");
        StringAssert.Contains(content, "CatalogVersion=2");
        StringAssert.Contains(content, "HashAlgorithms=SHA256");
        StringAssert.Contains(content, "CATATTR1=0x10010001:OSAttr:2:6.2");
        if (usePageHashes)
        {
            StringAssert.Contains(content, "PageHashes=true");
        }
        else
        {
            StringAssert.Contains(content, "PageHashes=false");
        }

        foreach(var file in files)
        {
            StringAssert.Contains(content, $"<HASH>{file}");
            if (computeFlatHashes)
            {
                StringAssert.Contains(content, $"<HASH>{file}ALTSIPID={{DE351A42-8E59-11d0-8C47-00C04FC295EE}}");
            }
        }
    }

    [TestInitialize]
    public void Setup()
    {
        _testInputDirectory = Path.Combine(_tempDirectory.FullName, Guid.NewGuid().ToString());
        Directory.CreateDirectory(_testInputDirectory);
        _codeIntegrityCatalogService = new CodeIntegrityCatalogService(GetRequiredService<ILogger<CodeIntegrityCatalogService>>());
    }

    private static void CopyExecutablesForTest(string destPath)
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "cmd.exe"), Path.Combine(destPath, "cmd.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "sort.exe"), Path.Combine(destPath, "sort.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "cacls.exe"), Path.Combine(destPath, "cacls.exe"));

        var subDirectory1 = Path.Combine(destPath, "1");
        Directory.CreateDirectory(subDirectory1);
        File.Copy(Path.Combine(Environment.SystemDirectory, "chkdsk.exe"), Path.Combine(subDirectory1, "chkdsk.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "conhost.exe"), Path.Combine(subDirectory1, "conhost.exe"));

        var subDirectory2 = Path.Combine(destPath, "2");
        Directory.CreateDirectory(subDirectory2);
        File.Copy(Path.Combine(Environment.SystemDirectory, "dllhost.exe"), Path.Combine(subDirectory2, "dllhost.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "fc.exe"), Path.Combine(subDirectory2, "fc.exe"));

        var subDirectory11 = Path.Combine(subDirectory1, "1");
        Directory.CreateDirectory(subDirectory11);
        File.Copy(Path.Combine(Environment.SystemDirectory, "findstr.exe"), Path.Combine(subDirectory11, "findstr.exe"));
        File.Copy(Path.Combine(Environment.SystemDirectory, "label.exe"), Path.Combine(subDirectory11, "label.exe"));
    }

    #region CreateCatalogDefinitionFile direct tests

    [TestMethod]
    public void CreateCatalogDefinitionFile_GeneratesCorrectHeader()
    {
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var files = new List<string> { @"C:\test\app.exe" };

        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, false, false);

        var content = File.ReadAllText(cdfPath);
        VerifyCdfContent(content, files, outputCatalogPath, false, false);
        File.Delete(cdfPath);
    }

    [TestMethod]
    public void CreateCatalogDefinitionFile_PageHashesTrue_ContainsPageHashesTrue()
    {
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var files = new List<string> { @"C:\test\app.exe" };

        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, true, false);

        var content = File.ReadAllText(cdfPath);
        VerifyCdfContent(content, files, outputCatalogPath, true, false);
        File.Delete(cdfPath);
    }

    [TestMethod]
    public void CreateCatalogDefinitionFile_WithFlatHashes_ContainsAltSipId()
    {
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var files = new List<string> { @"C:\test\app.exe" };

        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, false, true);

        try
        {
            var content = File.ReadAllText(cdfPath);
            VerifyCdfContent(content, files, outputCatalogPath, false, true);
        }
        finally
        {
            File.Delete(cdfPath);
        }
    }

    [TestMethod]
    public void CreateCatalogDefinitionFile_WithoutFlatHashes_DoesNotContainAltSipId()
    {
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var files = new List<string> { @"C:\test\app.exe" };

        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, false, false);

        try
        {
            var content = File.ReadAllText(cdfPath);
            Assert.IsFalse(content.Contains("ALTSIPID", StringComparison.Ordinal),
                "CDF should not contain ALTSIPID when computeFlatHashes is false");
        }
        finally
        {
            File.Delete(cdfPath);
        }
    }

    [TestMethod]
    public void CreateCatalogDefinitionFile_MultipleFiles_ContainsAllFiles()
    {
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var files = new List<string> { @"C:\dir1\app1.exe", @"C:\dir2\app2.exe", @"C:\dir3\app3.dll" };

        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, false, false);

        try
        {
            var content = File.ReadAllText(cdfPath);
            VerifyCdfContent(content, files, outputCatalogPath, false, false);
        }
        finally
        {
            File.Delete(cdfPath);
        }
    }

    [TestMethod]
    public void CreateCatalogDefinitionFile_EmptyFiles_GeneratesCdfWithNoMembers()
    {
        var files = new List<string>();
        var outputCatalogPath = Path.Combine(_tempDirectory.FullName, "output.cat");
        var cdfPath = CodeIntegrityCatalogService.CreateCatalogDefinitionFile(outputCatalogPath, files, false, false);

        try
        {
            var content = File.ReadAllText(cdfPath);
            VerifyCdfContent(content, files, outputCatalogPath, false, false);
            var catalogFilesIndex = content.IndexOf("[CatalogFiles]", StringComparison.Ordinal);
            var afterCatalogFiles = content[(catalogFilesIndex + "[CatalogFiles]".Length)..].Trim();
            Assert.AreEqual(string.Empty, afterCatalogFiles, "No file entries should be present");
        }
        finally
        {
            File.Delete(cdfPath);
        }
    }

    #endregion

    #region CreateExternalCatalog validation tests

    [TestMethod]
    public async Task CreateExternalCatalog_NullDirectories_ThrowsArgumentException()
    {
        var output = new FileInfo(Path.Combine(_tempDirectory.FullName, "test.cat"));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            _codeIntegrityCatalogService.CreateExternalCatalogAsync(null!, false, false, false, IfExists.Error, output));
    }

    [TestMethod]
    public async Task CreateExternalCatalog_EmptyDirectories_ThrowsArgumentException()
    {
        var output = new FileInfo(Path.Combine(_tempDirectory.FullName, "test.cat"));

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            _codeIntegrityCatalogService.CreateExternalCatalogAsync([], false, false, false, IfExists.Error, output));
    }

    [TestMethod]
    public async Task CreateExternalCatalog_NonExistentDirectory_ThrowsDirectoryNotFoundException()
    {
        var output = new FileInfo(Path.Combine(_tempDirectory.FullName, "test.cat"));
        var dirs = new List<string> { Path.Combine(_tempDirectory.FullName, "nonexistent") };

        await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() =>
            _codeIntegrityCatalogService.CreateExternalCatalogAsync(dirs, false, false, false, IfExists.Error, output));
    }

    [TestMethod]
    public async Task CreateExternalCatalog_NoExecutableFiles_ThrowsInvalidOperationException()
    {
        File.WriteAllText(Path.Combine(_testInputDirectory, "readme.txt"), "not an executable");
        var output = new FileInfo(Path.Combine(_tempDirectory.FullName, "test.cat"));

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            _codeIntegrityCatalogService.CreateExternalCatalogAsync([_testInputDirectory], false, false, false, IfExists.Error, output));
    }

    [TestMethod]
    public async Task CreateExternalCatalog_OutputAlreadyExistsWithErrorMode_ThrowsIOException()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        File.WriteAllText(outputPath, "existing");
        var output = new FileInfo(outputPath);

        await Assert.ThrowsExactlyAsync<IOException>(() =>
            _codeIntegrityCatalogService.CreateExternalCatalogAsync([_testInputDirectory], false, false, false, IfExists.Error, output));
    }

    #endregion

    #region CreateExternalCatalog integration tests

    [TestMethod]
    public async Task CreateExternalCatalog_GeneratesCatalogFile()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [_testInputDirectory], false, false, false, IfExists.Error, new FileInfo(outputPath));

        Assert.IsTrue(File.Exists(outputPath), "Catalog file should be generated");
        Assert.IsGreaterThan(0, new FileInfo(outputPath).Length, "Catalog file should not be empty");
    }

    [TestMethod]
    public async Task CreateExternalCatalog_OverwriteMode_ReplacesExistingCatalog()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        File.WriteAllText(outputPath, "old content");
        var oldFileLength = new FileInfo(outputPath).Length;

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [_testInputDirectory], false, false, false, IfExists.Overwrite, new FileInfo(outputPath));

        Assert.IsTrue(File.Exists(outputPath), "Catalog file should exist");
        var newFileLength = new FileInfo(outputPath).Length;
        Assert.AreNotEqual(newFileLength, oldFileLength, "Catalog should be overwritten");
    }

    [TestMethod]
    public async Task CreateExternalCatalog_RecursiveMode_FindsFilesInSubdirectories()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var cdfPath = string.Empty;
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");
        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_testInputDirectory, "*.*", SearchOption.AllDirectories))
        {
            files.Add(file);
        }

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [_testInputDirectory], true, false, false, IfExists.Error, new FileInfo(outputPath), ref cdfPath);

        var cdfContent = File.ReadAllText(cdfPath!);
        VerifyCdfContent(cdfContent, files, outputPath, false, false);
    }

    [TestMethod]
    public async Task CreateExternalCatalog_NonRecursive_SkipsSubdirectoryFiles()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var cdfPath = string.Empty;
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");

        var rootDirectoryFiles = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_testInputDirectory, "*.*", SearchOption.TopDirectoryOnly))
        {
            rootDirectoryFiles.Add(file);
        }

        var subDirectoryFiles = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_testInputDirectory, "*.*", SearchOption.AllDirectories))
        {
            if (!rootDirectoryFiles.Contains(file))
            {
                subDirectoryFiles.Add(file);
            }
        }

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [_testInputDirectory], false, false, false, IfExists.Error, new FileInfo(outputPath), ref cdfPath);

        var cdfContent = File.ReadAllText(cdfPath!);
        VerifyCdfContent(cdfContent, rootDirectoryFiles, outputPath, false, false);

        foreach (var subFile in subDirectoryFiles)
        {
            Assert.IsFalse(cdfContent.Contains(subFile, StringComparison.Ordinal),
                "CDF should not contain files from subdirectories in non-recursive mode");
        }
    }

    [TestMethod]
    public async Task CreateExternalCatalog_SkipsNonExecutableFiles()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var nonExecutableFiles = new List<string>
        {
            Path.Combine(_testInputDirectory, "readme.txt"),
            Path.Combine(_testInputDirectory, "data.json")
        };

        foreach (var file in nonExecutableFiles)
        {
            File.WriteAllText(file, "not executable");
        }


        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(_testInputDirectory, "*.*", SearchOption.TopDirectoryOnly))
        {
            if (!nonExecutableFiles.Contains(file))
            {
                files.Add(file);
            }
        }

        var cdfPath = string.Empty;
        var outputPath = Path.Combine(_tempDirectory.FullName, "test.cat");

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [_testInputDirectory], false, false, false, IfExists.Error, new FileInfo(outputPath), ref cdfPath);

        var cdfContent = File.ReadAllText(cdfPath!);
        VerifyCdfContent(cdfContent, files, outputPath, false, false);
        Assert.IsFalse(cdfContent.Contains("readme.txt", StringComparison.Ordinal),
            "CDF should not contain non-executable files");
        Assert.IsFalse(cdfContent.Contains("data.json", StringComparison.Ordinal),
            "CDF should not contain non-executable files");
    }

    [TestMethod]
    public async Task CreateExternalCatalog_MultipleDirectories_ProcessesAll()
    {
        CopyExecutablesForTest(_testInputDirectory);
        var dir1 = Path.Combine(_testInputDirectory, "1");
        var dir2 = Path.Combine(_testInputDirectory, "2");
        var cdfPath = string.Empty;
        var outputPath = Path.Combine(_testInputDirectory, "test.cat");

        var files = new List<string>();
        foreach (var file in Directory.EnumerateFiles(dir1, "*.*", SearchOption.TopDirectoryOnly))
        {
            files.Add(file);
        }

        foreach (var file in Directory.EnumerateFiles(dir2, "*.*", SearchOption.TopDirectoryOnly))
        {
            files.Add(file);
        }

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [dir1, dir2], false, false, false, IfExists.Error, new FileInfo(outputPath), ref cdfPath);

        var cdfContent = File.ReadAllText(cdfPath!);
        VerifyCdfContent(cdfContent, files, outputPath, false, false);
    }

    #endregion

    #region ReadBytes / IsExecutable / IfExists edge cases

    [TestMethod]
    public void ReadBytes_InsufficientBytes_ThrowsEndOfStream()
    {
        using var stream = new MemoryStream([0x01, 0x02]);
        using var reader = new BinaryReader(stream);

        // int needs 4 bytes but only 2 are available.
        var ex = Assert.ThrowsExactly<EndOfStreamException>(() => CodeIntegrityCatalogService.ReadBytes<int>(reader));
        StringAssert.Contains(ex.Message, "Int32");
    }

    private static void WriteWord(byte[] b, int off, ushort v)
    {
        b[off] = (byte)(v & 0xFF);
        b[off + 1] = (byte)(v >> 8);
    }

    private static void WriteDword(byte[] b, int off, uint v)
    {
        b[off] = (byte)(v & 0xFF);
        b[off + 1] = (byte)((v >> 8) & 0xFF);
        b[off + 2] = (byte)((v >> 16) & 0xFF);
        b[off + 3] = (byte)((v >> 24) & 0xFF);
    }

    private static byte[] BuildPeWithOneSection(byte[] name8, uint characteristics)
    {
        var b = new byte[128];
        WriteWord(b, 0, 0x5A4D);           // e_magic 'MZ'
        WriteDword(b, 60, 64);             // e_lfanew -> NT headers at 64
        WriteDword(b, 64, 0x00004550);     // 'PE\0\0'
        WriteWord(b, 68 + 2, 1);           // FileHeader.NumberOfSections = 1
        WriteWord(b, 68 + 16, 0);          // FileHeader.SizeOfOptionalHeader = 0
        for (var i = 0; i < name8.Length && i < 8; i++)
        {
            b[88 + i] = name8[i];          // SectionHeader.Name
        }
        WriteDword(b, 88 + 36, characteristics); // SectionHeader.Characteristics
        return b;
    }

    private bool InvokeIsExecutable(byte[] peBytes)
    {
        var path = Path.Combine(_tempDirectory.FullName, $"pe_{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, peBytes);
        var method = typeof(CodeIntegrityCatalogService).GetMethod(
            "IsExecutable", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        return (bool)method.Invoke(null, [path])!;
    }

    [TestMethod]
    public void IsExecutable_NotMzMagic_ReturnsFalse()
    {
        // 64 zero bytes: large enough for a DOS header but e_magic != 'MZ'.
        Assert.IsFalse(InvokeIsExecutable(new byte[64]));
    }

    [TestMethod]
    public void IsExecutable_InvalidLfanew_ReturnsFalse()
    {
        var b = new byte[64];
        WriteWord(b, 0, 0x5A4D); // 'MZ' but e_lfanew stays 0 (<= 0)
        Assert.IsFalse(InvokeIsExecutable(b));
    }

    [TestMethod]
    public void IsExecutable_BadNtSignature_ReturnsFalse()
    {
        var b = new byte[72];
        WriteWord(b, 0, 0x5A4D);
        WriteDword(b, 60, 64);
        WriteDword(b, 64, 0xFFFFFFFF); // not 'PE\0\0'
        Assert.IsFalse(InvokeIsExecutable(b));
    }

    [TestMethod]
    public void IsExecutable_TruncatedBeforeFileHeader_ReturnsFalse()
    {
        var b = new byte[80]; // room for PE signature but not the whole file header
        WriteWord(b, 0, 0x5A4D);
        WriteDword(b, 60, 64);
        WriteDword(b, 64, 0x00004550);
        Assert.IsFalse(InvokeIsExecutable(b));
    }

    [TestMethod]
    public void IsExecutable_OptionalHeaderExceedsStream_ReturnsFalse()
    {
        var b = new byte[88]; // exactly a file header, but SizeOfOptionalHeader overflows the stream
        WriteWord(b, 0, 0x5A4D);
        WriteDword(b, 60, 64);
        WriteDword(b, 64, 0x00004550);
        WriteWord(b, 68 + 16, 0xFFFF); // SizeOfOptionalHeader
        Assert.IsFalse(InvokeIsExecutable(b));
    }

    [TestMethod]
    public void IsExecutable_SectionsExceedStream_ReturnsFalse()
    {
        var b = new byte[88];
        WriteWord(b, 0, 0x5A4D);
        WriteDword(b, 60, 64);
        WriteDword(b, 64, 0x00004550);
        WriteWord(b, 68 + 2, 10);  // NumberOfSections far more than the stream can hold
        WriteWord(b, 68 + 16, 0);  // SizeOfOptionalHeader
        Assert.IsFalse(InvokeIsExecutable(b));
    }

    [TestMethod]
    public void IsExecutable_TextSectionWithoutCodeFlag_ReturnsTrue()
    {
        // A ".text" section is treated as executable even without the code/exec characteristics.
        var name = new byte[] { (byte)'.', (byte)'t', (byte)'e', (byte)'x', (byte)'t', 0, 0, 0 };
        Assert.IsTrue(InvokeIsExecutable(BuildPeWithOneSection(name, 0)));
    }

    [TestMethod]
    public void IsExecutable_NoExecutableSection_ReturnsFalse()
    {
        // A single non-code, non-".text" section => not executable.
        var name = new byte[] { (byte)'.', (byte)'d', (byte)'a', (byte)'t', (byte)'a', 0, 0, 0 };
        Assert.IsFalse(InvokeIsExecutable(BuildPeWithOneSection(name, 0)));
    }

    [TestMethod]
    public async Task CreateExternalCatalog_OutputExistsWithSkip_DoesNotRegenerate()
    {
        var outputPath = Path.Combine(_tempDirectory.FullName, "existing.cat");
        File.WriteAllText(outputPath, "SENTINEL");

        await _codeIntegrityCatalogService.CreateExternalCatalogAsync(
            [_testInputDirectory], false, false, false, IfExists.Skip, new FileInfo(outputPath));

        // Skip mode must return before collecting/regenerating, leaving the file untouched.
        // (If Skip were ignored, the empty input directory would throw InvalidOperationException.)
        Assert.AreEqual("SENTINEL", File.ReadAllText(outputPath));
    }

    [TestMethod]
    public void CollectExecutableFiles_WhitespaceDirectoryEntry_IsSkipped()
    {
        var method = typeof(CodeIntegrityCatalogService).GetMethod(
            "CollectExecutableFiles", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;

        var result = (List<string>)method.Invoke(
            _codeIntegrityCatalogService,
            [new List<string> { "   " }, SearchOption.TopDirectoryOnly])!;

        Assert.AreEqual(0, result.Count, "Whitespace-only directory entries must be skipped, not enumerated");
    }

    [TestMethod]
    public void DescribeCatalogErrorArea_KnownAndUnknownAreas_MapToExpectedText()
    {
        Assert.AreEqual("The header section of the CDF",
            CodeIntegrityCatalogService.DescribeCatalogErrorArea(PInvoke.CRYPTCAT_E_AREA_HEADER));
        Assert.AreEqual("A member file entry in the CatalogFiles section of the CDF",
            CodeIntegrityCatalogService.DescribeCatalogErrorArea(PInvoke.CRYPTCAT_E_AREA_MEMBER));
        Assert.AreEqual("An attribute entry in the CDF",
            CodeIntegrityCatalogService.DescribeCatalogErrorArea(PInvoke.CRYPTCAT_E_AREA_ATTRIBUTE));
        StringAssert.Contains(CodeIntegrityCatalogService.DescribeCatalogErrorArea(0xDEAD), "Unknown");
    }

    [TestMethod]
    public void DescribeCatalogLocalError_KnownAndUnknownErrors_MapToExpectedText()
    {
        Assert.AreEqual("The member file name or path is missing.",
            CodeIntegrityCatalogService.DescribeCatalogLocalError(PInvoke.CRYPTCAT_E_CDF_MEMBER_FILE_PATH));
        Assert.AreEqual("The function failed to create a hash of the member subject.",
            CodeIntegrityCatalogService.DescribeCatalogLocalError(PInvoke.CRYPTCAT_E_CDF_MEMBER_INDIRECTDATA));
        Assert.AreEqual("The function failed to find the member file.",
            CodeIntegrityCatalogService.DescribeCatalogLocalError(PInvoke.CRYPTCAT_E_CDF_MEMBER_FILENOTFOUND));
        Assert.AreEqual("The function failed to convert the subject string to a GUID.",
            CodeIntegrityCatalogService.DescribeCatalogLocalError(PInvoke.CRYPTCAT_E_CDF_BAD_GUID_CONV));
        Assert.AreEqual("The attribute contains an invalid OID, or the combination of type, name or OID, and value is not valid.",
            CodeIntegrityCatalogService.DescribeCatalogLocalError(PInvoke.CRYPTCAT_E_CDF_ATTR_TYPECOMBO));
        Assert.AreEqual("The attribute line is missing one or more elements of its composition including type, object identifier (OID) or name, or value.",
            CodeIntegrityCatalogService.DescribeCatalogLocalError(PInvoke.CRYPTCAT_E_CDF_ATTR_TOOFEWVALUES));
        Assert.AreEqual("The function does not support the attribute.",
            CodeIntegrityCatalogService.DescribeCatalogLocalError(PInvoke.CRYPTCAT_E_CDF_UNSUPPORTED));
        Assert.AreEqual("The file member already exists.",
            CodeIntegrityCatalogService.DescribeCatalogLocalError(PInvoke.CRYPTCAT_E_CDF_DUPLICATE));
        Assert.AreEqual("The CatalogHeader or Name tag is missing.",
            CodeIntegrityCatalogService.DescribeCatalogLocalError(PInvoke.CRYPTCAT_E_CDF_TAGNOTFOUND));
        StringAssert.Contains(CodeIntegrityCatalogService.DescribeCatalogLocalError(0xBEEF), "Unknown");
    }

    #endregion
}
