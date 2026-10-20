// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Spectre.Console;
using Spectre.Console.Testing;
using WinApp.Cli.Commands;
using WinApp.Cli.Helpers;

namespace WinApp.Cli.Tests;

[TestClass]
public sealed class UiAuditScreenshotCommandTests : IDisposable
{
    private readonly TestConsole console = new();
    private readonly StringWriter stderr = new();
    private readonly CaptureFake capture = new();
    private readonly SystemFake system = new();
    private readonly ResolverFake resolver = new();
    private ServiceProvider services = null!;
    private string directory = null!;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void Setup()
    {
        directory = Path.GetFullPath(Path.Combine("TestResults", "UiAuditScreenshot-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(directory);
        services = new ServiceCollection().ConfigureCommands()
            .AddSingleton<IAnsiConsole>(console)
            .AddSingleton<IWindowCapture>(capture)
            .AddSingleton<ISystemUiQuery>(system)
            .AddSingleton<IUiTargetResolver>(resolver)
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.None))
            .BuildServiceProvider();
        services.GetRequiredService<UiAuditScreenshotCommand.Handler>().ReadCaptureBounds =
            _ => new UiAuditCaptureBounds(-800, 100, 8, 8);
    }

    [TestCleanup]
    public void Cleanup()
    {
        Dispose();
        Directory.Delete(directory, recursive: true);
    }

    public void Dispose()
    {
        services?.Dispose();
        console.Dispose();
        stderr.Dispose();
    }

    [TestMethod]
    public async Task CapturesOnlyTheRequestedWindowWithoutForegroundFallback()
    {
        var code = await Invoke(["-w", "42", "--output", ImagePath, "--json"]);
        Assert.AreEqual(0, code, stderr.ToString());
        Assert.AreEqual(42L, capture.CapturedWindow);
        Assert.AreEqual(0, capture.LegacyCaptureCalls);
        Assert.IsTrue(capture.Grabber.Disposed);
        Assert.IsTrue(File.Exists(ImagePath));
        using var json = JsonDocument.Parse(console.Output);
        Assert.AreEqual("windows-graphics-capture", json.RootElement.GetProperty("captureMethod").GetString());
        Assert.IsFalse(json.RootElement.GetProperty("activationRequested").GetBoolean());
        Assert.AreEqual(777L, json.RootElement.GetProperty("foregroundBefore").GetInt64());
        Assert.AreEqual(777L, json.RootElement.GetProperty("foregroundAfter").GetInt64());
        Assert.AreEqual("verified", json.RootElement.GetProperty("coordinateMappingStatus").GetString());
        Assert.AreEqual(-800, json.RootElement.GetProperty("captureBoundsBefore").GetProperty("left").GetInt32());
        Assert.IsTrue(json.RootElement.GetProperty("captureStartedAtUtc").GetDateTimeOffset()
            <= json.RootElement.GetProperty("capturedAtUtc").GetDateTimeOffset());
    }

    [TestMethod]
    public async Task MovedWindowRetainsImageButDoesNotClaimCoordinateAlignment()
    {
        var moved = false;
        services.GetRequiredService<UiAuditScreenshotCommand.Handler>().ReadCaptureBounds =
            _ => new UiAuditCaptureBounds(moved ? -700 : -800, 100, 8, 8);
        capture.Grabber.OnReady = () => moved = true;

        Assert.AreEqual(0, await Invoke(["-w", "42", "--output", ImagePath, "--json"]));
        using var json = JsonDocument.Parse(console.Output);
        Assert.AreEqual("unavailable", json.RootElement.GetProperty("coordinateMappingStatus").GetString());
        StringAssert.Contains(json.RootElement.GetProperty("coordinateMappingReason").GetString()!, "moved or resized");
        Assert.IsTrue(File.Exists(ImagePath));
    }

    [TestMethod]
    public void MappingRequiresPhysicalBoundsThatExactlyMatchFramePixels()
    {
        var bounds = new UiAuditCaptureBounds(-900, 100, 120, 60);
        Assert.IsNull(UiAuditScreenshotCommand.Handler.MappingProblem(bounds, bounds, 120, 60));
        Assert.IsNotNull(UiAuditScreenshotCommand.Handler.MappingProblem(null, bounds, 120, 60));
        Assert.IsNotNull(UiAuditScreenshotCommand.Handler.MappingProblem(bounds, bounds, 180, 90));
        Assert.IsNotNull(UiAuditScreenshotCommand.Handler.MappingProblem(bounds, bounds with { Height = 61 }, 120, 60));
    }

    [TestMethod]
    public async Task UnsupportedCaptureDoesNotTryPrintWindowOrResolveAnApp()
    {
        capture.Supported = false;
        Assert.AreEqual(1, await Invoke(["-w", "42", "--output", ImagePath, "--json"]));
        StringAssert.Contains(stderr.ToString(), "capture_unsupported");
        Assert.AreEqual(0, resolver.Calls);
        Assert.AreEqual(0, capture.LegacyCaptureCalls);
        Assert.IsFalse(File.Exists(ImagePath));
    }

    [TestMethod]
    public async Task ExistingOutputIsNeverOverwritten()
    {
        File.WriteAllText(ImagePath, "original");
        Assert.AreEqual(1, await Invoke(["-w", "42", "--output", ImagePath, "--json"]));
        Assert.AreEqual("original", File.ReadAllText(ImagePath));
        Assert.AreEqual(0, resolver.Calls);
    }

    [TestMethod]
    public async Task ScopeChangeBeforePersistenceRejectsTheFrame()
    {
        capture.Grabber.OnReady = () => system.Matches = false;
        Assert.AreEqual(1, await Invoke(["-w", "42", "--output", ImagePath, "--json"]));
        Assert.IsFalse(File.Exists(ImagePath));
        Assert.IsTrue(capture.Grabber.Disposed);
    }

    [TestMethod]
    public async Task NoFrameIsAnErrorNotABlankSuccess()
    {
        capture.Grabber.Ready = false;
        Assert.AreEqual(1, await Invoke(["-w", "42", "--output", ImagePath, "--json"]));
        StringAssert.Contains(stderr.ToString(), "capture_timeout");
        Assert.IsFalse(File.Exists(ImagePath));
    }

    [TestMethod]
    public async Task ExplicitTargetAndOutputAreRequired()
    {
        Assert.AreEqual(1, await Invoke(["--output", ImagePath, "--json"]));
        Assert.AreEqual(0, resolver.Calls);
        Assert.AreEqual(1, await Invoke(["-w", "42", "--json"]));
        Assert.AreEqual(0, resolver.Calls);
    }

    private string ImagePath => Path.Combine(directory, "frame.png");

    private async Task<int> Invoke(string[] args)
    {
        var parsed = services.GetRequiredService<UiAuditScreenshotCommand>().Parse(args);
        parsed.InvocationConfiguration.Output = console.Profile.Out.Writer;
        parsed.InvocationConfiguration.Error = stderr;
        return await parsed.InvokeAsync(parsed.InvocationConfiguration, cancellationToken: TestContext.CancellationToken);
    }

    private sealed class ResolverFake : IUiTargetResolver
    {
        public int Calls { get; private set; }
        public Task<UiTarget> ResolveAsync(string? app, long? window, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new UiTarget { ProcessId = 1234, ProcessName = "Fixture", WindowHandle = 42, IsExplicitWindow = true });
        }
    }

    private sealed class CaptureFake : IWindowCapture
    {
        public bool Supported { get; set; } = true;
        public bool IsFrameCaptureSupported => Supported;
        public long CapturedWindow { get; private set; }
        public int LegacyCaptureCalls { get; private set; }
        public GrabberFake Grabber { get; } = new();
        public IFrameGrabber StartFrameGrabber(nint hwnd, int fps = 0)
        {
            CapturedWindow = hwnd;
            return Grabber;
        }
        public byte[] CaptureWindowPixels(nint hwnd, int width, int height)
        {
            LegacyCaptureCalls++;
            throw new InvalidOperationException("Passive audit capture must never call the foregrounding PrintWindow fallback.");
        }
        public byte[] CaptureScreenPixels(int x, int y, int cropWidth, int cropHeight, int encoderWidth, int encoderHeight, int displayWidth, int displayHeight)
        {
            LegacyCaptureCalls++;
            throw new InvalidOperationException("Passive audit capture must never capture the desktop.");
        }
    }

    private sealed class GrabberFake : IFrameGrabber
    {
        public bool Ready { get; set; } = true;
        public bool Disposed { get; private set; }
        public Action? OnReady { get; set; }
        public bool IsClosed => false;
        public void Dispose() => Disposed = true;
        public (byte[] Pixels, int Width, int Height, long Version)? TryGetLatest() =>
            (Enumerable.Repeat((byte)255, 8 * 8 * 4).ToArray(), 8, 8, 1);
        public Task<bool> WaitForFirstFrameAsync(TimeSpan timeout, CancellationToken ct)
        {
            OnReady?.Invoke();
            return Task.FromResult(Ready);
        }
    }

    private sealed class SystemFake : ISystemUiQuery
    {
        public bool Matches { get; set; } = true;
        public UiProcessInfo? GetProcessById(int pid) => new(pid, "Fixture", 42, "Fixture");
        public IReadOnlyList<UiProcessInfo> GetProcessesByName(string name) => [];
        public IReadOnlyList<UiProcessInfo> GetProcessesMatching(string substring) => [];
        public nint GetForegroundWindow() => 777;
        public uint GetProcessIdForWindow(long hwnd) => Matches ? 1234u : 5678u;
        public string? GetWindowText(long hwnd) => "Fixture";
        public string? GetWindowClassName(long hwnd) => "Fixture";
        public long GetFocusedWindow(long hwnd) => hwnd;
        public (int Width, int Height) GetWindowSize(long hwnd) => (8, 8);
        public nint GetWindowOwner(long hwnd) => 0;
        public long GetRootWindow(long hwnd) => hwnd;
    }
}
