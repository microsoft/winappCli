// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Services;

namespace WinApp.Cli.Tests;

/// <summary>
/// Coverage for <see cref="TemplateCacheReader"/>: it must read the same template engine hive that
/// <c>dotnet new</c> uses, which lives under <c>DOTNET_CLI_HOME</c> when that variable is set.
/// </summary>
[TestClass]
public class TemplateCacheReaderTests
{
    private DirectoryInfo _root = null!;
    private string _profile = null!;
    private string _cliHome = null!;

    [TestInitialize]
    public void Setup()
    {
        _root = new DirectoryInfo(Path.Join(Path.GetTempPath(), $"TplCacheReader_{Guid.NewGuid():N}"));
        _root.Create();
        _profile = Path.Join(_root.FullName, "profile");
        _cliHome = Path.Join(_root.FullName, "clihome");
        WriteCache(_profile, "10.0.200", "profile-cache");
        WriteCache(_cliHome, "10.0.401", "clihome-cache");
    }

    [TestCleanup]
    public void Cleanup()
    {
        try
        {
            _root.Delete(true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            // best-effort cleanup
        }
    }

    private static void WriteCache(string home, string sdkBand, string content)
    {
        var dir = Path.Join(home, ".templateengine", "dotnetcli", sdkBand);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Join(dir, "templatecache.json"), content);
    }

    private static readonly string[] CliHomeCache = ["clihome-cache"];
    private static readonly string[] ProfileCache = ["profile-cache"];

    private static TemplateCacheReader CreateReader(string? dotnetCliHome, string? userProfileVariable, string osProfile) => new()
    {
        EnvironmentVariableProvider = name => name switch
        {
            "DOTNET_CLI_HOME" => dotnetCliHome,
            "USERPROFILE" => userProfileVariable,
            _ => null,
        },
        UserProfileProvider = () => osProfile,
    };

    [TestMethod]
    public void ReadTemplateCacheDocuments_DotnetCliHomeSet_ReadsOnlyThatHive()
    {
        var reader = CreateReader(dotnetCliHome: _cliHome, userProfileVariable: _profile, osProfile: _profile);

        var documents = reader.ReadTemplateCacheDocuments();

        CollectionAssert.AreEqual(CliHomeCache, documents.ToArray(),
            "dotnet new keeps its hive under DOTNET_CLI_HOME, so the user profile's caches describe other packs.");
    }

    [TestMethod]
    public void ReadTemplateCacheDocuments_DotnetCliHomeEmpty_FallsBackToUserProfile()
    {
        var reader = CreateReader(dotnetCliHome: string.Empty, userProfileVariable: _profile, osProfile: _cliHome);

        var documents = reader.ReadTemplateCacheDocuments();

        CollectionAssert.AreEqual(ProfileCache, documents.ToArray(),
            "An empty DOTNET_CLI_HOME is ignored, and USERPROFILE wins over the OS profile folder like in the dotnet CLI.");
    }

    [TestMethod]
    public void ReadTemplateCacheDocuments_NoVariables_UsesOsProfileFolder()
    {
        var reader = CreateReader(dotnetCliHome: null, userProfileVariable: null, osProfile: _profile);

        var documents = reader.ReadTemplateCacheDocuments();

        CollectionAssert.AreEqual(ProfileCache, documents.ToArray());
    }

    [TestMethod]
    public void ReadTemplateCacheDocuments_DotnetCliHomeWithoutHive_ReturnsEmpty()
    {
        var emptyHome = Path.Join(_root.FullName, "fresh");
        Directory.CreateDirectory(emptyHome);
        var reader = CreateReader(dotnetCliHome: emptyHome, userProfileVariable: _profile, osProfile: _profile);

        var documents = reader.ReadTemplateCacheDocuments();

        Assert.AreEqual(0, documents.Count,
            "A fresh DOTNET_CLI_HOME has no hive yet; the user profile's caches must not be used instead.");
    }
}
