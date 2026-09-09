// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class ExecutionAliasResolverTests
{
    // ---- IsSafeAliasName: accept bare filenames --------------------------------

    [TestMethod]
    [DataRow("myapp.exe", DisplayName = "simple .exe")]
    [DataRow("MyApp.exe", DisplayName = "mixed case .exe")]
    [DataRow("my-app.exe", DisplayName = "hyphen")]
    [DataRow("my_app.exe", DisplayName = "underscore")]
    [DataRow("app123.exe", DisplayName = "digits")]
    [DataRow("a.exe", DisplayName = "single letter")]
    [DataRow("file.with.dots.exe", DisplayName = "internal dots")]
    public void IsSafeAliasName_ValidBareFilename_ReturnsTrue(string alias)
    {
        Assert.IsTrue(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias}' to be accepted as a bare filename");
    }

    [TestMethod]
    public void IsSafeAliasName_AtMaxLength_ReturnsTrue()
    {
        // 255 characters total (the boundary): 251 'a' + ".exe"
        var alias = new string('a', 251) + ".exe";
        Assert.AreEqual(255, alias.Length);
        Assert.IsTrue(ExecutionAliasResolver.IsSafeAliasName(alias),
            "Aliases exactly 255 characters long should be accepted");
    }

    // ---- IsSafeAliasName: reject empties / dot-names --------------------------

    [TestMethod]
    [DataRow(null, DisplayName = "null")]
    [DataRow("", DisplayName = "empty")]
    [DataRow("   ", DisplayName = "whitespace only")]
    [DataRow("\t", DisplayName = "tab only")]
    [DataRow(".", DisplayName = "single dot")]
    [DataRow("..", DisplayName = "double dot (parent)")]
    public void IsSafeAliasName_EmptyOrDotNames_ReturnsFalse(string? alias)
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias ?? "<null>"}' to be rejected");
    }

    // ---- IsSafeAliasName: reject path separators ------------------------------

    [TestMethod]
    [DataRow("dir\\evil.exe", DisplayName = "backslash separator")]
    [DataRow("dir/evil.exe", DisplayName = "forward slash separator")]
    [DataRow("..\\evil.exe", DisplayName = "parent traversal with backslash")]
    [DataRow("../evil.exe", DisplayName = "parent traversal with forward slash")]
    [DataRow("a\\b\\c.exe", DisplayName = "nested backslash path")]
    [DataRow("a/b/c.exe", DisplayName = "nested forward slash path")]
    [DataRow("\\evil.exe", DisplayName = "leading backslash (rooted on current drive)")]
    [DataRow("/evil.exe", DisplayName = "leading forward slash")]
    public void IsSafeAliasName_PathSeparators_ReturnsFalse(string alias)
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias}' to be rejected — contains a path separator");
    }

    // ---- IsSafeAliasName: reject rooted / absolute paths ----------------------

    [TestMethod]
    [DataRow("C:\\Windows\\System32\\calc.exe", DisplayName = "absolute path with drive")]
    [DataRow("C:evil.exe", DisplayName = "drive-relative path")]
    [DataRow("\\\\server\\share\\evil.exe", DisplayName = "UNC path")]
    [DataRow("\\\\?\\C:\\evil.exe", DisplayName = "extended-length UNC")]
    public void IsSafeAliasName_RootedPaths_ReturnsFalse(string alias)
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias}' to be rejected — is a rooted / absolute path");
    }

    // ---- IsSafeAliasName: reject invalid filename characters ------------------

    [TestMethod]
    public void IsSafeAliasName_NullCharacter_ReturnsFalse()
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName("evil\0.exe"));
    }

    [TestMethod]
    [DataRow("evil*.exe", DisplayName = "asterisk")]
    [DataRow("evil?.exe", DisplayName = "question mark")]
    [DataRow("evil<.exe", DisplayName = "less than")]
    [DataRow("evil>.exe", DisplayName = "greater than")]
    [DataRow("evil|.exe", DisplayName = "pipe")]
    [DataRow("evil\".exe", DisplayName = "quote")]
    [DataRow("evil:test.exe", DisplayName = "colon (drive separator / ADS)")]
    public void IsSafeAliasName_InvalidFilenameChars_ReturnsFalse(string alias)
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias}' to be rejected — contains an invalid filename character");
    }

    [TestMethod]
    public void IsSafeAliasName_TooLong_ReturnsFalse()
    {
        var alias = new string('a', 256) + ".exe";
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            "Aliases longer than 255 characters should be rejected");
    }

    // ---- IsSafeAliasName: reject trailing dot/space (Win32 trims these) -------

    [TestMethod]
    [DataRow("evil.exe.", DisplayName = "trailing dot")]
    [DataRow("evil.exe ", DisplayName = "trailing space")]
    [DataRow("evil.exe..", DisplayName = "two trailing dots")]
    [DataRow("evil.exe.  ", DisplayName = "trailing dot then spaces")]
    public void IsSafeAliasName_TrailingDotOrSpace_ReturnsFalse(string alias)
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias}' to be rejected — trailing dot/space is silently trimmed by Win32 path normalization");
    }

    // ---- IsSafeAliasName: require .exe suffix ---------------------------------

    [TestMethod]
    [DataRow("notepad", DisplayName = "no extension")]
    [DataRow("myapp.com", DisplayName = ".com")]
    [DataRow("myapp.bat", DisplayName = ".bat")]
    [DataRow("myapp", DisplayName = "stem only")]
    [DataRow("myapp.exetra", DisplayName = ".exe-like but not .exe")]
    public void IsSafeAliasName_NonExeExtension_ReturnsFalse(string alias)
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias}' to be rejected — Windows App Execution Aliases must end in .exe");
    }

    // ---- IsSafeAliasName: reject DOS reserved device names --------------------

    [TestMethod]
    [DataRow("CON.exe", DisplayName = "CON.exe")]
    [DataRow("con.exe", DisplayName = "con.exe (lowercase)")]
    [DataRow("PRN.exe", DisplayName = "PRN.exe")]
    [DataRow("AUX.exe", DisplayName = "AUX.exe")]
    [DataRow("NUL.exe", DisplayName = "NUL.exe")]
    [DataRow("COM1.exe", DisplayName = "COM1.exe")]
    [DataRow("COM9.exe", DisplayName = "COM9.exe")]
    [DataRow("LPT1.exe", DisplayName = "LPT1.exe")]
    [DataRow("LPT9.exe", DisplayName = "LPT9.exe")]
    [DataRow("CON.txt.exe", DisplayName = "CON.txt.exe (stem still binds to CON)")]
    public void IsSafeAliasName_ReservedDeviceNames_ReturnsFalse(string alias)
    {
        Assert.IsFalse(ExecutionAliasResolver.IsSafeAliasName(alias),
            $"Expected '{alias}' to be rejected — reserved DOS device names bind to the device regardless of extension");
    }

    // ---- ResolveAliasPath: success + base directory behaviour -----------------

    [TestMethod]
    public void ResolveAliasPath_ValidAlias_ReturnsAbsolutePathUnderBaseDirectory()
    {
        var baseDir = @"C:\Users\test\AppData\Local\Microsoft\WindowsApps";

        var result = ExecutionAliasResolver.ResolveAliasPath("myapp.exe", baseDir);

        Assert.IsNotNull(result);
        Assert.AreEqual(Path.Combine(baseDir, "myapp.exe"), result!.FullName);
    }

    [TestMethod]
    public void ResolveAliasPath_DefaultBaseDirectory_UsesWindowsAppsLocation()
    {
        var result = ExecutionAliasResolver.ResolveAliasPath("myapp.exe");

        Assert.IsNotNull(result);
        var expectedBase = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");
        Assert.AreEqual(Path.Combine(expectedBase, "myapp.exe"), result!.FullName);
    }

    [TestMethod]
    public void GetDefaultWindowsAppsDirectory_ReturnsExpectedPath()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft",
            "WindowsApps");

        Assert.AreEqual(expected, ExecutionAliasResolver.GetDefaultWindowsAppsDirectory());
    }

    // ---- TryGetAliasPackageFamilyName: only answers for a real app-exec-link ---

    [TestMethod]
    [DataRow("", DisplayName = "empty path")]
    [DataRow("   ", DisplayName = "whitespace path")]
    public void TryGetAliasPackageFamilyName_EmptyPath_ReturnsFalse(string path)
    {
        Assert.IsFalse(ExecutionAliasResolver.TryGetAliasPackageFamilyName(path, out var family));
        Assert.IsNull(family);
    }

    [TestMethod]
    public void TryGetAliasPackageFamilyName_MissingFile_ReturnsFalse()
    {
        var missing = Path.Join(Path.GetTempPath(), $"winapp-no-such-alias-{Guid.NewGuid():N}.exe");

        Assert.IsFalse(ExecutionAliasResolver.TryGetAliasPackageFamilyName(missing, out var family),
            "A path with no alias proxy must not report an owner");
        Assert.IsNull(family);
    }

    [TestMethod]
    public void TryGetAliasPackageFamilyName_OrdinaryFile_ReturnsFalse()
    {
        // A real file that is not a reparse point must not be mistaken for an alias — reporting a bogus
        // owner here would block a legitimate launch.
        var file = Path.Join(Path.GetTempPath(), $"winapp-plain-{Guid.NewGuid():N}.exe");
        File.WriteAllText(file, "not a reparse point");

        try
        {
            Assert.IsFalse(ExecutionAliasResolver.TryGetAliasPackageFamilyName(file, out var family));
            Assert.IsNull(family);
        }
        finally
        {
            File.Delete(file);
        }
    }

    // ---- ResolveAliasPath: refuse hostile inputs ------------------------------

    [TestMethod]
    [DataRow("..\\evil.exe", DisplayName = "parent traversal must not resolve")]
    [DataRow("dir\\evil.exe", DisplayName = "path separator must not resolve")]
    [DataRow("C:\\Windows\\System32\\calc.exe", DisplayName = "absolute path must not resolve")]
    [DataRow("\\\\server\\share\\evil.exe", DisplayName = "UNC must not resolve")]
    [DataRow("", DisplayName = "empty must not resolve")]
    [DataRow(null, DisplayName = "null must not resolve")]
    [DataRow("evil\0.exe", DisplayName = "NUL must not resolve")]
    public void ResolveAliasPath_HostileAlias_ReturnsNull(string? alias)
    {
        var result = ExecutionAliasResolver.ResolveAliasPath(alias, @"C:\WindowsApps");

        Assert.IsNull(result,
            $"Hostile alias '{alias ?? "<null>"}' must not be resolved to a filesystem path");
    }

    // ---- ResolveAliasPath: refuse non-rooted base directories -----------------

    [TestMethod]
    [DataRow("", DisplayName = "empty base directory")]
    [DataRow("Microsoft\\WindowsApps", DisplayName = "relative base directory")]
    [DataRow(".\\WindowsApps", DisplayName = "explicit-current-dir relative")]
    public void ResolveAliasPath_NonRootedBaseDirectory_ReturnsNull(string baseDir)
    {
        // A non-rooted base directory would cause FileInfo to root the resulting
        // path under the current working directory — reintroducing the very
        // CWD-search RCE this resolver exists to prevent.
        var result = ExecutionAliasResolver.ResolveAliasPath("myapp.exe", baseDir);

        Assert.IsNull(result,
            $"Resolver must refuse non-rooted base directory '{baseDir}' to avoid CWD-relative resolution");
    }


    // ---- ResolveAliasPath: Exists reports filesystem state --------------------

    [TestMethod]
    public void ResolveAliasPath_NonExistentFile_ReturnsFileInfoWithExistsFalse()
    {
        // Use a base directory and alias guaranteed not to exist on the test agent.
        var baseDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var alias = $"winappcli-test-{Guid.NewGuid():N}.exe";

        var result = ExecutionAliasResolver.ResolveAliasPath(alias, baseDir);

        Assert.IsNotNull(result);
        Assert.IsFalse(result!.Exists,
            "ResolveAliasPath must not create the file; callers verify existence themselves");
    }

    [TestMethod]
    public void ResolveAliasPath_ExistingFile_ReturnsFileInfoWithExistsTrue()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"winappcli-resolver-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        try
        {
            var alias = "fake-alias.exe";
            var fullPath = Path.Combine(baseDir, alias);
            File.WriteAllText(fullPath, string.Empty);

            var result = ExecutionAliasResolver.ResolveAliasPath(alias, baseDir);

            Assert.IsNotNull(result);
            Assert.IsTrue(result!.Exists,
                "FileInfo.Exists should be true when the resolved file is present");
            Assert.AreEqual(fullPath, result.FullName);
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }
    [TestMethod]
    [DataRow("-contoso_h4sh", "contoso_h4sh", DisplayName = "leading hyphen")]
    [DataRow(".contoso_h4sh", "contoso_h4sh", DisplayName = "leading period")]
    [DataRow("contoso_h4sh-", "contoso_h4sh", DisplayName = "trailing hyphen")]
    public void BuildDefaultAliasName_PunctuationIsSignificant_DistinctIdentitiesGetDistinctAliases(
        string decorated, string plain)
    {
        // '-' and '.' are valid in an Identity/@Name, so these are two different packages that can be
        // registered side by side. Trimming the punctuation mapped them onto one alias, and Windows gives
        // an alias to the first claimant — the second app would silently lose it, and with it a console
        // app's terminal output.
        var a = ExecutionAliasResolver.BuildDefaultAliasName(decorated);
        var b = ExecutionAliasResolver.BuildDefaultAliasName(plain);

        Assert.IsNotNull(a);
        Assert.IsNotNull(b);
        Assert.AreNotEqual(b, a, "Distinct package identities must not share a generated alias");
    }

    [TestMethod]
    public void BuildDefaultAliasName_KeepsThePrefixSoTheFileNameStaysSafe()
    {
        // Keeping the punctuation is only safe because the prefix leads: the file name itself never
        // starts with '.' or '-'.
        var alias = ExecutionAliasResolver.BuildDefaultAliasName("-contoso_h4sh");

        Assert.IsNotNull(alias);
        Assert.StartsWith("winapp-", alias);
        Assert.IsTrue(ExecutionAliasResolver.IsSafeAliasName(alias), $"'{alias}' must still be a safe file name");
    }
}