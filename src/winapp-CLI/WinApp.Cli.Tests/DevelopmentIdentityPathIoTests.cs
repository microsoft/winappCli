// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public class DevelopmentIdentityPathIoTests
{
    [TestMethod]
    public void ResolvedIoPathIsUriCompatibleAndPreservesCanonicalIdentity()
    {
        var directory = Directory.CreateTempSubdirectory("winapp-identity-path-");
        try
        {
            var file = Path.Combine(directory.FullName, "app.csproj");
            File.WriteAllText(file, "<Project />");
            var resolved = DevelopmentIdentityHelper.ResolvePathForIo(file);

            Assert.IsTrue(File.Exists(resolved));
            Assert.IsTrue(new Uri(resolved).IsFile);
            Assert.AreEqual("app.csproj", Path.GetFileName(resolved));
            Assert.AreEqual(resolved, DevelopmentIdentityHelper.ResolvePathForIo(file.ToUpperInvariant()),
                "I/O projection must retain the actual source filename, not an alias's spelling.");
            Assert.AreEqual(DevelopmentIdentityHelper.CanonicalizePath(file),
                DevelopmentIdentityHelper.CanonicalizePath(resolved));
            var future = Path.Combine(directory.FullName, "future", "AppX");
            Assert.AreEqual(DevelopmentIdentityHelper.CanonicalizePath(future),
                DevelopmentIdentityHelper.CanonicalizePath(DevelopmentIdentityHelper.ResolvePathForIo(future)));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
