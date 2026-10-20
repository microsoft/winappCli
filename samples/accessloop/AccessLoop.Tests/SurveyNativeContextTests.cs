using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace AccessLoop.Tests;

[TestClass]
public sealed class SurveyNativeContextTests
{
    private string directory = null!;

    [TestInitialize]
    public void Initialize()
    {
        directory = Path.Combine(Environment.CurrentDirectory, ".survey-context-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
    }

    [TestCleanup]
    public void Cleanup() => Directory.Delete(directory, recursive: true);

    [TestMethod]
    public void HashCacheInvalidatesOnPathLengthOrLastWriteChanges()
    {
        var native = new SurveyNativeContext();
        var executable = Path.Combine(directory, "selected.exe");
        File.WriteAllText(executable, "first binary");
        var first = native.HashFile(executable);
        Assert.AreEqual(first, native.HashFile(executable));
        var originalTime = File.GetLastWriteTimeUtc(executable);
        File.WriteAllText(executable, "other binary");
        File.SetLastWriteTimeUtc(executable, originalTime.AddSeconds(5));
        var second = native.HashFile(executable);
        Assert.AreNotEqual(first, second);
        File.WriteAllText(executable, "different length");
        File.SetLastWriteTimeUtc(executable, originalTime.AddSeconds(5));
        Assert.AreNotEqual(second, native.HashFile(executable));
        var companion = Path.Combine(directory, "selected.dll");
        File.WriteAllText(companion, "first binary");
        Assert.AreEqual(first, native.HashFile(companion));
        Assert.ThrowsExactly<FileNotFoundException>(() => native.HashFile(Path.Combine(directory, "missing.exe")));
    }

    [TestMethod]
    [DataRow("executable")]
    [DataRow("companion")]
    [DataRow("version")]
    public void ChangedBinaryIdentityIsNeverSilentlyAccepted(string changed)
    {
        var fake = new NativeSurveySessionTestFixture();
        var session = fake.Session();
        _ = session.GetContext();
        var application = fake.Native.Context.Application;
        fake.Native.Context = fake.Native.Context with
        {
            Application = changed switch
            {
                "executable" => application with { ExecutableSha256 = new string('C', 64) },
                "companion" => application with { CompanionSha256 = new string('D', 64) },
                _ => application with { ProductVersion = "0.96.0" }
            }
        };
        var error = Assert.ThrowsExactly<SurveyBlockedException>(() => session.GetContext());
        Assert.AreEqual("application-changed", error.Code);
        Assert.IsFalse(error.CanResume);
        Assert.HasCount(0, fake.Calls);
    }

    [TestMethod]
    [DataRow("size")]
    [DataRow("dpi")]
    [DataRow("contrast")]
    [DataRow("theme")]
    [DataRow("culture")]
    public void EnvironmentChangesRequireTheOriginalEnvironmentToBeRestored(string changed)
    {
        var fake = new NativeSurveySessionTestFixture();
        var session = fake.Session();
        _ = session.GetContext();
        if (changed == "size")
            fake.Native.Window = fake.Native.Window with { Width = 1600 };
        else if (changed == "dpi")
            fake.Native.Window = fake.Native.Window with { Dpi = 192 };
        else
        {
            var environment = fake.Native.Context.Environment;
            fake.Native.Context = fake.Native.Context with
            {
                Environment = changed switch
                {
                    "contrast" => environment with { HighContrast = true },
                    "theme" => environment with { AppsUseLightTheme = true },
                    _ => environment with { UiCulture = "zh-CN" }
                }
            };
        }
        var error = Assert.ThrowsExactly<SurveyBlockedException>(() => session.GetContext());
        Assert.AreEqual("environment-changed", error.Code);
        Assert.IsTrue(error.CanResume);
        Assert.HasCount(0, fake.Calls);
    }

    [TestMethod]
    [DataRow("path")]
    [DataRow("hash")]
    [DataRow("version")]
    [DataRow("os")]
    [DataRow("culture")]
    public void UnknownCriticalMetadataIsNotReplacedWithDefaults(string missing)
    {
        var fake = new NativeSurveySessionTestFixture();
        var context = fake.Native.Context;
        fake.Native.Context = missing switch
        {
            "path" => context with { Application = context.Application with { ExecutablePath = "relative.exe" } },
            "hash" => context with { Application = context.Application with { ExecutableSha256 = "" } },
            "version" => context with { Application = context.Application with { ProductVersion = "" } },
            "os" => context with { Environment = context.Environment with { OsVersion = "" } },
            _ => context with { Environment = context.Environment with { UiCulture = "" } }
        };
        Assert.ThrowsExactly<SurveyBlockedException>(() => fake.Session().GetContext());
        Assert.HasCount(0, fake.Calls);
    }
}
