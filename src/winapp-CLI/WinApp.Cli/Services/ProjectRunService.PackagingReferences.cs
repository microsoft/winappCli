// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Security.Cryptography;
using System.Text;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Services;

internal sealed partial class ProjectRunService
{
    /// <summary>
    /// The MSBuild property, set through the environment of the <c>dotnet</c> processes winapp starts, that
    /// imports <see cref="PackagingReferenceTargets"/>. MSBuild imports <c>$(CustomAfterDirectoryBuildTargets)</c>
    /// (a <c>;</c>-separated list) right after <c>Directory.Build.targets</c> in every project (MSBuild 17.8+,
    /// .NET SDK 8.0.100+). An environment value has the lowest precedence, so a project that sets the property
    /// keeps its own value.
    /// </summary>
    internal const string CustomAfterDirectoryBuildTargetsVariable = "CustomAfterDirectoryBuildTargets";

    /// <summary>
    /// MSBuild targets that make Windows App SDK packaging build each referenced project once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// For a reference that doesn't depend on a runtime identifier (a typical class library), the .NET SDK
    /// marks the prepared reference with <c>UndefineProperties=RuntimeIdentifier</c>, so the library builds
    /// once, without the RID. The MSIX/PRI packaging targets query the raw <c>ProjectReferenceWithConfiguration</c>
    /// items instead, which lack that marker, so they build the library a second time with the global
    /// <c>-r</c> into a <c>win-&lt;arch&gt;</c> subfolder. Packaging then finds two copies (APPX1101) or looks
    /// for a file that was never written (PRI175/PRI252, MSB3030). The first target hands the packaging
    /// targets the prepared items, so both queries agree.
    /// </para>
    /// <para>
    /// After a build failed that way, the app's cached PRI input list (<c>pri.resfiles</c>) still names the
    /// missing file, and the PRI targets only regenerate it when their inputs change. The second target
    /// deletes that list when it names a file that no longer exists.
    /// </para>
    /// </remarks>
    internal const string PackagingReferenceTargets = """
        <Project>
          <!-- Imported by winapp's own builds through CustomAfterDirectoryBuildTargets. Makes Windows App SDK packaging build each referenced project once. -->
          <Target Name="WinappAlignPackagingReferences"
                  AfterTargets="PrepareProjectReferences"
                  Condition="'@(_MSBuildProjectReferenceExistent)' != ''">
            <ItemGroup>
              <ProjectReferenceWithConfiguration Remove="@(ProjectReferenceWithConfiguration)" />
              <ProjectReferenceWithConfiguration Include="@(_MSBuildProjectReferenceExistent)">
                <UndefineProperties>%(_MSBuildProjectReferenceExistent.UndefineProperties);%(_MSBuildProjectReferenceExistent.GlobalPropertiesToRemove)</UndefineProperties>
              </ProjectReferenceWithConfiguration>
            </ItemGroup>
          </Target>

          <Target Name="WinappDropStalePriInputs"
                  BeforeTargets="_GenerateProjectPriConfigurationFiles"
                  Condition="'$(_PriResfilesPath)' != '' and Exists('$(_PriResfilesPath)')">
            <ReadLinesFromFile File="$(_PriResfilesPath)">
              <Output TaskParameter="Lines" ItemName="_WinappPriInput" />
            </ReadLinesFromFile>
            <ItemGroup>
              <_WinappMissingPriInput Include="@(_WinappPriInput)" Condition="!Exists('%(Identity)')" />
            </ItemGroup>
            <Delete Files="$(_PriResfilesPath)" Condition="'@(_WinappMissingPriInput)' != ''" />
          </Target>
        </Project>
        """;

    /// <summary>
    /// Returns <paramref name="environment"/> plus <see cref="CustomAfterDirectoryBuildTargetsVariable"/> naming
    /// the <see cref="PackagingReferenceTargets"/> file, for a build or publish winapp starts. A value already in
    /// winapp's environment is kept, with this file appended. Returns <paramref name="environment"/> unchanged,
    /// and the build runs exactly as before, when the file can't be written.
    /// </summary>
    internal static IReadOnlyDictionary<string, string>? WithPackagingReferenceTargets(
        IReadOnlyDictionary<string, string>? environment,
        string? inheritedValue = null,
        string? targetsDirectory = null)
    {
        var path = EnsurePackagingReferenceTargetsFile(targetsDirectory ?? Path.Join(Path.GetTempPath(), "winapp", "msbuild"));
        if (path is null)
        {
            return environment;
        }

        // MSBuild reads the value as escaped text and the import has no existence check, so a '%XX' sequence or ';'
        // in the path (both legal on Windows) must be escaped, or every build fails with MSB4019.
        var escapedPath = path.Replace("%", "%25", StringComparison.Ordinal).Replace(";", "%3B", StringComparison.Ordinal);

        inheritedValue ??= Environment.GetEnvironmentVariable(CustomAfterDirectoryBuildTargetsVariable);
        var existing = (inheritedValue ?? string.Empty)
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var value = existing.Contains(escapedPath, StringComparer.OrdinalIgnoreCase)
            ? string.Join(';', existing)
            : string.Join(';', [.. existing, escapedPath]);

        var merged = environment is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(environment, StringComparer.OrdinalIgnoreCase);
        merged[CustomAfterDirectoryBuildTargetsVariable] = value;
        return merged;
    }

    /// <summary>
    /// Writes <see cref="PackagingReferenceTargets"/> to a content-named file in <paramref name="directory"/>,
    /// only when it's missing or differs, so its timestamp stays stable across builds. Returns its path, or
    /// null when it can't be written.
    /// </summary>
    internal static string? EnsurePackagingReferenceTargetsFile(string directory)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(PackagingReferenceTargets)))[..12];
        var path = Path.Join(directory, $"winapp-packaging-references-{hash}.targets");
        try
        {
            if (File.Exists(path) && File.ReadAllText(path) == PackagingReferenceTargets)
            {
                return path;
            }

            Directory.CreateDirectory(directory);
            AtomicFile.WriteAllText(path, PackagingReferenceTargets);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
