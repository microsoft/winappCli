// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using WinApp.Cli.Commands;

namespace WinApp.Cli.Tests;

/// <summary>
/// Output paths that can never work must be rejected before the command queues for the desktop.
/// </summary>
/// <remarks>
/// Both of these commands take a turn on a desktop shared with every other UI workflow on the machine
/// — screenshot exclusively. A command whose destination is already impossible still waited for that
/// turn, and made everyone else wait behind it, before failing on the last step. The fixture's
/// <c>DesktopSectionEnters</c> and <c>Runs</c> counters are the assertion: zero of either means the
/// command never reached coordination at all.
/// </remarks>
public partial class UiCommandTests
{
    private string ExistingDirectory(string name)
    {
        var path = Path.Combine(_tempDirectory.FullName, name);
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>
    /// Asserts the JSON error envelope on stderr carries <paramref name="expectedCode"/>.
    /// </summary>
    /// <remarks>
    /// Scoped to the envelope line rather than reusing <c>AssertJsonErrorCode</c>, which parses from the
    /// first brace to the end of the stream: a preflight failure also logs a human-readable line after
    /// the envelope, and that trailing text is not JSON.
    /// </remarks>
    private void AssertPreflightErrorCode(string expectedCode)
    {
        var stderr = ConsoleStdErr.ToString();
        var envelope = stderr
            .Split('\n')
            .Select(line => line.Trim())
            .FirstOrDefault(line => line.StartsWith('{'));

        Assert.IsNotNull(envelope, $"stderr must contain a JSON error envelope; got: {stderr}");

        var error = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(envelope);
        Assert.AreEqual(expectedCode, error.GetProperty("error").GetProperty("code").GetString());
    }

    private void AssertNeverCoordinated(string because)
    {
        Assert.AreEqual(0, _fakeDesktopLock.Runs.Count, because);
        Assert.AreEqual(0, _fakeDesktopLock.DesktopSectionEnters, because);
    }

    /// <summary>
    /// Makes any attempt to capture pixels fail loudly, so a test that expects to be rejected during
    /// preflight cannot quietly pass by failing later for a different reason.
    /// </summary>
    private void ArmCaptureTripwire()
        => _fakeUia.ScreenshotThrow = new InvalidOperationException(
            "capture must not be attempted for a command whose output path was already impossible");

    // ------------------------------------------------------------------------------------ screenshot

    [TestMethod]
    public async Task Screenshot_OutputIsAnExistingDirectory_FailsBeforeTakingTheDesktop()
    {
        ArmCaptureTripwire();
        var target = ExistingDirectory("shot-dir");

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "-o", target, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertPreflightErrorCode("invalid_arguments");
        AssertNeverCoordinated("an unwritable destination must be refused before the desktop is taken");
        // The tripwire is armed in each test below: reaching capture at all would surface as this.
    }

    [TestMethod]
    public async Task Screenshot_OutputEndingInASeparator_FailsBeforeTakingTheDesktop()
    {
        ArmCaptureTripwire();
        var target = Path.Combine(_tempDirectory.FullName, "not-a-file") + Path.DirectorySeparatorChar;

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "-o", target, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertPreflightErrorCode("invalid_arguments");
        AssertNeverCoordinated("a directory-shaped path is not a screenshot destination");
    }

    [TestMethod]
    public async Task Screenshot_ParentDirectoryIsAFile_FailsBeforeTakingTheDesktop()
    {
        ArmCaptureTripwire();
        // The parent cannot be created because a file already occupies its name.
        var blocker = Path.Combine(_tempDirectory.FullName, "blocker");
        await File.WriteAllTextAsync(blocker, "not a directory", TestContext.CancellationToken);
        var target = Path.Combine(blocker, "shot.png");

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "-o", target, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertPreflightErrorCode("invalid_arguments");
        AssertNeverCoordinated("an uncreatable parent directory must be found before queueing");
    }

    [TestMethod]
    public async Task Screenshot_OverwritingAnExistingFileIsStillAllowed()
    {
        // Deliberate: a screenshot is cheap to retake and callers write to a fixed name in a loop.
        var target = Path.Combine(_tempDirectory.FullName, "existing.png");
        await File.WriteAllTextAsync(target, "old", TestContext.CancellationToken);
        _fakeUia.ScreenshotResult = (new byte[4], 1, 1);

        var command = GetRequiredService<UiScreenshotCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(command, ["-a", "TestApp", "-o", target, "--json"]);

        Assert.AreEqual(0, exitCode);
        Assert.AreEqual(1, _fakeDesktopLock.DesktopSectionEnters, "a valid destination still takes its turn");
    }

    // ---------------------------------------------------------------------------------------- record

    [TestMethod]
    public async Task Record_ExplicitOutputAlreadyExists_FailsBeforeTakingTheDesktop()
    {
        // Previously only checked with --frames, so a plain recording queued for the desktop and then
        // refused at the engine's no-clobber check, having made every other workflow wait first.
        var target = Path.Combine(_tempDirectory.FullName, "taken.mp4");
        await File.WriteAllTextAsync(target, "first take", TestContext.CancellationToken);

        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", target, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertPreflightErrorCode("output_exists");
        AssertNeverCoordinated("an existing recording must be found before the command queues");
        Assert.AreEqual("first take", await File.ReadAllTextAsync(target, TestContext.CancellationToken),
            "the existing take must be untouched");
    }

    [TestMethod]
    public async Task Record_DerivedFramesDirectoryAlreadyExists_FailsBeforeTakingTheDesktop()
    {
        var target = Path.Combine(_tempDirectory.FullName, "with-frames.mp4");
        Directory.CreateDirectory(Path.Combine(_tempDirectory.FullName, "with-frames.frames"));

        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", target, "--frames", "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertPreflightErrorCode("output_exists");
        AssertNeverCoordinated("a colliding frame directory must be found before the command queues");
    }

    [TestMethod]
    public async Task Record_OutputIsAnExistingDirectory_FailsBeforeTakingTheDesktop()
    {
        var target = ExistingDirectory("rec-dir");

        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", target, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertPreflightErrorCode("invalid_arguments");
        AssertNeverCoordinated("a directory is not a recording destination");
    }

    [TestMethod]
    public async Task Record_ParentDirectoryIsAFile_FailsBeforeTakingTheDesktop()
    {
        var blocker = Path.Combine(_tempDirectory.FullName, "rec-blocker");
        await File.WriteAllTextAsync(blocker, "not a directory", TestContext.CancellationToken);
        var target = Path.Combine(blocker, "take.mp4");

        var command = GetRequiredService<UiRecordCommand>();
        var exitCode = await ParseAndInvokeWithCaptureAsync(
            command, ["-a", "TestApp", "--duration-sec", "1", "-o", target, "--json"]);

        Assert.AreEqual(1, exitCode);
        AssertPreflightErrorCode("invalid_arguments");
        AssertNeverCoordinated("an uncreatable parent directory must be found before queueing");
    }

    [TestMethod]
    public async Task Record_WithNoExplicitOutput_StillCoordinatesNormally()
    {
        // The generated default carries a timestamp and a GUID, so it cannot collide and must not be
        // treated as a preflight failure.
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(_tempDirectory.FullName);
        try
        {
            var command = GetRequiredService<UiRecordCommand>();
            var exitCode = await ParseAndInvokeWithCaptureAsync(
                command, ["-a", "TestApp", "--duration-sec", "1", "--json"]);

            Assert.AreEqual(0, exitCode);
            Assert.AreEqual(1, _fakeDesktopLock.Runs.Count, "a default-path recording still takes its turn");
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }
    }
}
