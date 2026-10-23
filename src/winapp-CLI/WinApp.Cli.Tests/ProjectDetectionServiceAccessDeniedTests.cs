// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Logging.Abstractions;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Verifies <see cref="ProjectDetectionService"/> degrades gracefully when the OS denies
/// access to directories or files encountered during a scan (permission-restricted folders,
/// unreadable manifests). These exercise the <see cref="UnauthorizedAccessException"/>
/// handlers in FindTauriConfFile, IsElectronProject, FindExecutableCsproj and
/// EnqueueSubdirectories, which cannot be reached with ordinary temp files.
///
/// Access is denied with a real deny ACL for the current user. Administrators bypass deny
/// ACLs, so when the test process runs elevated the denial has no effect; each test verifies
/// the denial is actually in force and is marked inconclusive otherwise (rather than passing
/// vacuously). GitHub Actions Windows runners are non-elevated, so the handlers are covered
/// there.
/// </summary>
[TestClass]
[SupportedOSPlatform("windows")]
public class ProjectDetectionServiceAccessDeniedTests
{
    private string _tempDir = null!;
    private ProjectDetectionService _sut = null!;
    private readonly List<string> _deniedDirs = [];
    private readonly List<string> _deniedFiles = [];

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ProjDetectDenied_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _sut = new ProjectDetectionService(NullLogger<ProjectDetectionService>.Instance, new FakeDotNetService());
    }

    [TestCleanup]
    public void Cleanup()
    {
        // Strip every deny ACE we added (files and directories) before deleting the temp tree,
        // so the recursive delete can enumerate it and no restricted artifact is left in %TEMP%.
        foreach (var file in _deniedFiles)
        {
            TryRemoveDenyRules(new FileInfo(file));
        }

        foreach (var dir in _deniedDirs)
        {
            TryRemoveDenyRules(new DirectoryInfo(dir));
        }

        if (Directory.Exists(_tempDir))
        {
            try { Directory.Delete(_tempDir, true); } catch { }
        }
    }

    private DirectoryInfo Root => new(_tempDir);

    private static SecurityIdentifier CurrentUser =>
        WindowsIdentity.GetCurrent().User ?? throw new InvalidOperationException("No current-user SID available.");

    /// <summary>
    /// Applies a deny ACL blocking the current user from listing/reading <paramref name="path"/>,
    /// then confirms the denial takes effect (it will not when elevated). Returns false if the
    /// directory is still enumerable, in which case the caller should treat the test as inconclusive.
    /// </summary>
    private bool TryDenyDirectory(string path, FileSystemRights rights)
    {
        var di = new DirectoryInfo(path);
        var security = di.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser, rights, InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
        di.SetAccessControl(security);
        _deniedDirs.Add(path);

        try
        {
            _ = di.EnumerateDirectories().Any();
            _ = di.EnumerateFiles().Any();
            return false; // Still enumerable => not actually denied (elevated process).
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// Applies a deny-read ACL to a single file and confirms it takes effect.
    /// </summary>
    private bool TryDenyFileRead(string filePath)
    {
        var fi = new FileInfo(filePath);
        var security = fi.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(
            CurrentUser, FileSystemRights.Read, AccessControlType.Deny));
        fi.SetAccessControl(security);
        _deniedFiles.Add(filePath);

        try
        {
            _ = File.ReadAllText(filePath);
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static void TryRemoveDenyRules(FileSystemInfo info)
    {
        try
        {
            info.Refresh();
            if (!info.Exists)
            {
                return;
            }

            switch (info)
            {
                case DirectoryInfo di:
                {
                    var security = di.GetAccessControl();
                    RemoveDenyAces(security);
                    di.SetAccessControl(security);
                    break;
                }

                case FileInfo fi:
                {
                    var security = fi.GetAccessControl();
                    RemoveDenyAces(security);
                    fi.SetAccessControl(security);
                    break;
                }
            }
        }
        catch
        {
            // Best-effort cleanup.
        }
    }

    private static void RemoveDenyAces(FileSystemSecurity security)
    {
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, false, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Deny)
            {
                security.RemoveAccessRule(rule);
            }
        }
    }

    [TestMethod]
    public void DetectProjectAt_DirectoryListingDenied_ReturnsNullWithoutThrowing()
    {
        // The scanned directory cannot be listed at all: enumerating its subdirectories
        // (Tauri probe) and its *.csproj files both throw UnauthorizedAccessException, which
        // must be swallowed so detection yields no project instead of crashing.
        var denied = Path.Combine(_tempDir, "restricted");
        Directory.CreateDirectory(denied);

        if (!TryDenyDirectory(denied, FileSystemRights.ListDirectory | FileSystemRights.ReadData))
        {
            Assert.Inconclusive("Deny ACL had no effect (test likely running elevated).");
        }

        var result = _sut.DetectProjectAt(new DirectoryInfo(denied));

        Assert.IsNull(result, "A directory that cannot be listed should yield no detected project.");
    }

    [TestMethod]
    public void DetectProjectAt_PackageJsonReadDenied_IsNotDetectedAsElectron()
    {
        // The directory is listable, but package.json itself cannot be read, so File.ReadAllText
        // throws UnauthorizedAccessException inside IsElectronProject. That must be swallowed
        // (treated as not-Electron) rather than propagated.
        var projectDir = Path.Combine(_tempDir, "app");
        Directory.CreateDirectory(projectDir);
        var packageJson = Path.Combine(projectDir, "package.json");
        File.WriteAllText(packageJson, """{ "dependencies": { "electron": "^28.0.0" } }""");

        if (!TryDenyFileRead(packageJson))
        {
            Assert.Inconclusive("Deny ACL had no effect (test likely running elevated).");
        }

        var result = _sut.DetectProjectAt(new DirectoryInfo(projectDir));

        Assert.IsNull(result, "An unreadable package.json must not be detected as Electron.");
    }

    [TestMethod]
    public async Task DetectProjectsAsync_DeniedSubdirectory_IsSkippedAndScanContinues()
    {
        // BFS reaches an unreadable subdirectory: enumerating its children to enqueue them throws
        // UnauthorizedAccessException, which must be swallowed so the scan continues and still finds
        // the accessible sibling project.
        var denied = Path.Combine(_tempDir, "restricted");
        Directory.CreateDirectory(denied);
        Directory.CreateDirectory(Path.Combine(denied, "child"));

        var visible = Path.Combine(_tempDir, "visible");
        Directory.CreateDirectory(visible);
        File.WriteAllText(Path.Combine(visible, "Cargo.toml"), "[package]");

        if (!TryDenyDirectory(denied, FileSystemRights.ListDirectory | FileSystemRights.ReadData))
        {
            Assert.Inconclusive("Deny ACL had no effect (test likely running elevated).");
        }

        var results = await _sut.DetectProjectsAsync(Root, 10, null, CancellationToken.None);

        Assert.AreEqual(1, results.Count, "Only the accessible sibling project should be found.");
        Assert.AreEqual(DetectedProjectType.Rust, results[0].Type);
    }
}
