// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

extern alias winappcli;

using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using WinApp.Cli.Commands;
using WinApp.Cli.Services.Performance;
using Native = winappcli::Windows.Win32.PInvoke;
using EventDescriptor = winappcli::Windows.Win32.System.Diagnostics.Etw.EVENT_DESCRIPTOR;
using EventDataDescriptor = winappcli::Windows.Win32.System.Diagnostics.Etw.EVENT_DATA_DESCRIPTOR;
using RegistrationHandle = winappcli::Windows.Win32.System.Diagnostics.Etw.REGHANDLE;
using Etw = winappcli::Windows.Win32.System.Diagnostics.Etw;
using NativeError = winappcli::Windows.Win32.Foundation.WIN32_ERROR;

namespace WinApp.Cli.Tests;

[TestClass]
[DoNotParallelize]
public sealed class PerfEtwTests
{
    private static readonly uint[] PidAndEventIdFilters = [0x80000004u, 0x80000200u];
    private static readonly uint[] PackageAndEventIdFilters = [0x80000010u, 0x80000200u];
    private static readonly uint[] ExecutableFilterOnly = [0x80000008u];

    [TestMethod]
    public void XamlProviderCapturesParserAppModelAndImageKeywords()
    {
        var provider = PerfProviders.All.Single(candidate => candidate.Id == PerfProviders.Xaml);
        var keywords = Convert.ToUInt64(provider.Keywords, 16);

        Assert.AreNotEqual(0UL, keywords & 0x10UL, "Parser keyword");
        Assert.AreNotEqual(0UL, keywords & 0x100UL, "AppModel keyword");
        Assert.AreNotEqual(0UL, keywords & 0x20000UL, "Images keyword");
    }

    [TestMethod]
    public void ZeroSessionHandleCannotEnableButStillOwnsCleanup()
    {
        var api = new FakeEtwApi();
        using var trace = new PrivateEtwSession("owned-zero", Guid.NewGuid(), Environment.ProcessId,
            @"C:\trace.etl", 1, api);
        Assert.IsFalse(trace.CanEnable);
        Assert.Throws<PerfEtwTargetNotReadyException>(() => trace.Enable(Guid.NewGuid(), ulong.MaxValue));
        Assert.AreEqual(0, api.EnableCalls);
        trace.Stop();
        trace.Stop();
        Assert.AreEqual(1, api.StopCalls);
        Assert.AreEqual(0UL, api.StoppedHandle);
        Assert.AreEqual("owned-zero", api.StoppedName);
        Assert.IsTrue(trace.Stopped);
        Assert.AreEqual(7u, trace.EventsLost);
        Assert.Throws<ObjectDisposedException>(() => trace.Enable(Guid.NewGuid(), 1));
    }

    [TestMethod]
    public void NonzeroSessionHandleEnablesWithPidFilter()
    {
        var api = new FakeEtwApi { StartedHandle = 123 };
        using var trace = new PrivateEtwSession("owned", Guid.NewGuid(), Environment.ProcessId, @"C:\trace.etl", 1, api);
        Assert.IsTrue(trace.CanEnable);
        trace.Enable(Guid.NewGuid(), ulong.MaxValue);
        Assert.AreEqual(1, api.EnableCalls);
        Assert.AreEqual(123UL, api.EnabledHandle);
    }

    [TestMethod]
    public void PidScopeStartsPrivateSessionWithPidFilter()
    {
        var api = new FakeEtwApi { StartedHandle = 123 };
        using var trace = new PrivateEtwSession("owned", Guid.NewGuid(), Environment.ProcessId, @"C:\trace.etl", 1, api);
        Assert.AreEqual(0x801u, api.StartedLogFileMode);
        Assert.AreEqual(1u, api.StartedFilterCount);
        trace.Enable(Guid.NewGuid(), ulong.MaxValue, eventIds: [1, 2]);
        CollectionAssert.AreEqual(PidAndEventIdFilters, api.EnabledFilterTypes.Single());
    }

    [TestMethod]
    public void PackageScopeStartsNormalSessionAndFiltersAtEnable()
    {
        var api = new FakeEtwApi { StartedHandle = 123 };
        using var trace = new PrivateEtwSession("owned", Guid.NewGuid(),
            new PerfEtwScope(PackageFullName: "Contoso.App_1.0.0.0_x64__abc"), @"C:\trace.etl", 1, api);
        Assert.AreEqual(1u, api.StartedLogFileMode);
        Assert.AreEqual(0u, api.StartedFilterCount);
        trace.Enable(Guid.NewGuid(), ulong.MaxValue, eventIds: [1]);
        CollectionAssert.AreEqual(PackageAndEventIdFilters, api.EnabledFilterTypes.Single());
        Assert.AreEqual("Contoso.App_1.0.0.0_x64__abc", api.EnabledScopeName);
    }

    [TestMethod]
    public void ExecutableScopeKeepsNameFilterWhenEventIdsAreRejected()
    {
        var api = new FakeEtwApi { StartedHandle = 123, RejectEventIds = true };
        using var trace = new PrivateEtwSession("owned", Guid.NewGuid(),
            new PerfEtwScope(ExecutableName: "app.exe"), @"C:\trace.etl", 1, api);
        Assert.IsFalse(trace.Enable(Guid.NewGuid(), ulong.MaxValue, eventIds: [1]));
        Assert.AreEqual(2, api.EnableCalls);
        CollectionAssert.AreEqual(ExecutableFilterOnly, api.EnabledFilterTypes[1]);
        Assert.AreEqual("app.exe", api.EnabledScopeName);
    }

    [TestMethod]
    [DataRow(null, null)]
    [DataRow("", null)]
    [DataRow(" ", null)]
    [DataRow(null, "")]
    [DataRow(null, " ")]
    public async Task EmptyScopeFailsBeforeStartingSessionOrPreparingElevatedCapture(string? executableName, string? packageFullName)
    {
        var scope = new PerfEtwScope(ExecutableName: executableName, PackageFullName: packageFullName);
        var api = new FakeEtwApi { StartedHandle = 123 };
        Assert.Throws<ArgumentException>(() => new PrivateEtwSession("owned", Guid.NewGuid(), scope, @"C:\trace.etl", 1, api));
        Assert.AreEqual(0, api.StartCalls);
        Assert.AreEqual(0, api.EnableCalls);

        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Empty-Scope-");
        try
        {
            var service = new PerfCaptureService(new FakeWinappDirectoryService(root), new FakeAppLauncherService());
            var output = Path.Join(root.FullName, "capture");
            var run = new PerfRunCapture(service, output, 30, 128, PerfProfileModes.Elevated, false);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => run.PrepareAsync(scope, CancellationToken.None));
            StringAssert.Contains(error.Message, "--profile-mode attach");
            Assert.IsFalse(Directory.Exists(output));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void OverlongScopeNameIsRejectedBeforeStartingSession()
    {
        var api = new FakeEtwApi { StartedHandle = 123 };
        Assert.Throws<ArgumentException>(() => new PrivateEtwSession("owned", Guid.NewGuid(),
            new PerfEtwScope(ExecutableName: new string('a', 600)), @"C:\trace.etl", 1, api));
        Assert.AreEqual(0, api.StartCalls);
    }

    [TestMethod]
    public async Task WorkerRejectsUnscopedElevatedRecordingWithoutStartingEtw()
    {
        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Worker-Scope-");
        Task<int>? worker = null;
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var directory = root.CreateSubdirectory("capture");
            var registry = Directory.CreateDirectory(Path.Join(root.FullName, "perf-control", id));
            var registrationPath = Path.Join(registry.FullName, "control.json");
            var registration = new PerfControlRegistration(id, directory.FullName, new string('a', 64),
                Guid.NewGuid(), 30, 128, Mode: PerfProfileModes.Elevated);
            File.WriteAllText(registrationPath,
                JsonSerializer.Serialize(registration, PerfJsonContext.Default.PerfControlRegistration));
            new PerfCaptureDocument
            {
                Id = id, Directory = directory.FullName, SessionId = registration.SessionId,
                SessionName = "WinApp-Perf-" + id, Mode = PerfProfileModes.Elevated,
            }.Save();
            var api = new FakeEtwApi { StartedHandle = 123 };
            worker = PerfCaptureWorker.RunAsync([PerfCaptureWorker.InternalVerb, registrationPath], api);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                PerfCaptureService.ArmAsync(registration, new PerfEtwScope(), CancellationToken.None));

            StringAssert.Contains(error.Message, "requires a process ID, executable name, or package full name");
            Assert.AreEqual(1, await worker.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, api.StartCalls);
            Assert.AreEqual(0, api.EnableCalls);
            var capture = PerfCaptureDocument.Load(directory.FullName);
            Assert.AreEqual("failed", capture.State);
            Assert.AreEqual("binding-failed", capture.StopReason);
            Assert.IsNull(capture.ReadyQpc);
            Assert.IsEmpty(directory.GetFiles("trace.etl*"));
        }
        finally
        {
            try
            {
                if (worker is not null)
                {
                    await worker.WaitAsync(TimeSpan.FromSeconds(35));
                }
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }
    }

    [TestMethod]
    public async Task ElevatedWorkerPinsCaptureAndControlPathsThroughFinalization()
    {
        var root = Directory.CreateTempSubdirectory("WinApp-Perf-Worker-Paths-");
        Task<int>? worker = null;
        try
        {
            var id = Guid.NewGuid().ToString("N");
            var directory = root.CreateSubdirectory("output").CreateSubdirectory("capture");
            var registry = root.CreateSubdirectory("perf-control").CreateSubdirectory(id);
            var registrationPath = Path.Join(registry.FullName, "control.json");
            var registration = new PerfControlRegistration(id, directory.FullName, new string('a', 64),
                Guid.NewGuid(), 30, 128, Mode: PerfProfileModes.Elevated);
            File.WriteAllText(registrationPath,
                JsonSerializer.Serialize(registration, PerfJsonContext.Default.PerfControlRegistration));
            new PerfCaptureDocument
            {
                Id = id, Directory = directory.FullName, SessionId = registration.SessionId,
                SessionName = "WinApp-Perf-" + id, Mode = PerfProfileModes.Elevated,
            }.Save();
            var api = new FakeEtwApi { StartedHandle = 123, BeforeStop = AssertPathsPinned };
            using (PerfCaptureDirectoryLease.Open(directory.FullName, registry.FullName))
            {
                worker = PerfCaptureWorker.RunAsync([PerfCaptureWorker.InternalVerb, registrationPath], api);
                await PerfCaptureService.ArmAsync(registration, new PerfEtwScope(ExecutableName: "app.exe"),
                    CancellationToken.None);
            }
            AssertPathsPinned();
            using var target = Process.GetCurrentProcess();
            await PerfCaptureService.BindAsync(registration, PerfProcessIdentity.Read(target),
                "recorded from launch (elevated session)", false, CancellationToken.None);
            Assert.AreEqual(Environment.ProcessId, PerfCaptureService.ReadRegistration(registrationPath).Target?.Pid);
            var bound = PerfCaptureDocument.Load(directory.FullName);
            Assert.AreEqual(1, bound.ProcessResources!.Samples.Count);
            Assert.IsGreaterThan(0L, bound.ProcessResources.Samples[0].Values.PrivateBytes);
            await Task.Delay(1100);

            using (var malformed = new NamedPipeClientStream(".", PerfControlChannel.PipeName(id),
                PipeDirection.Out, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly))
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await malformed.ConnectAsync(timeout.Token);
                await malformed.WriteAsync(BitConverter.GetBytes(5000), timeout.Token);
                await malformed.FlushAsync(timeout.Token);
            }
            var marked = await PerfControlChannel.SendAsync(registration,
                new(registration.Credential, "mark", Name: "paths-still-owned"), CancellationToken.None);
            StringAssert.Contains(marked.LastControlError!, "size limit");
            Assert.AreEqual("paths-still-owned", PerfCaptureDocument.Load(directory.FullName).Markers.Single().Name);
            AssertPathsPinned();

            var stopped = await PerfControlChannel.SendAsync(registration,
                new(registration.Credential, "stop"), CancellationToken.None);
            Assert.AreEqual("requested", stopped.StopReason);
            Assert.AreEqual(1, await worker.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, api.StartCalls);
            Assert.AreEqual(1, api.StopCalls);
            Assert.IsNotNull(PerfCaptureDocument.Load(directory.FullName).StopQpc);
            var resources = PerfCaptureDocument.Load(directory.FullName).ProcessResources!;
            Assert.IsGreaterThanOrEqualTo(3, resources.Samples.Count,
                "Binding, periodic polling, and finalization must all sample the target.");
            Assert.AreEqual(0, resources.FailedSamples);
            Directory.Move(directory.FullName, directory.FullName + "-moved");
            Directory.Move(registry.FullName, registry.FullName + "-moved");

            void AssertPathsPinned()
            {
                foreach (var path in new[] { root, directory.Parent!, directory, registry.Parent!, registry })
                {
                    Assert.Throws<IOException>(() => Directory.Move(path.FullName, path.FullName + "-moved"));
                }
            }
        }
        finally
        {
            try
            {
                if (worker is not null)
                {
                    await worker.WaitAsync(TimeSpan.FromSeconds(35));
                }
            }
            finally
            {
                root.Delete(recursive: true);
            }
        }
    }

    [TestMethod]
    public void FailedStopOfZeroSessionHandleRemainsRetryable()
    {
        var api = new FakeEtwApi { FailStop = true };
        using var trace = new PrivateEtwSession("owned-zero", Guid.NewGuid(), Environment.ProcessId,
            @"C:\trace.etl", 1, api);
        Assert.Throws<System.ComponentModel.Win32Exception>(trace.Stop);
        Assert.IsFalse(trace.Stopped);
        Assert.IsNull(trace.EventsLost);
        api.FailStop = false;
        trace.Stop();
        Assert.AreEqual(2, api.StopCalls);
        Assert.IsTrue(trace.Stopped);
    }

    private sealed unsafe class FakeEtwApi : IPrivateEtwApi
    {
        public int StartCalls { get; private set; }
        public int EnableCalls { get; private set; }
        public ulong EnabledHandle { get; private set; }
        public int StopCalls { get; private set; }
        public ulong StoppedHandle { get; private set; }
        public string? StoppedName { get; private set; }
        public bool FailStop { get; set; }
        public ulong StartedHandle { get; set; }
        public bool FailClr { get; set; }
        public bool FailComposition { get; set; }
        public bool RejectEventIds { get; set; }
        public Action? BeforeStop { get; set; }
        public uint StartedLogFileMode { get; private set; }
        public uint StartedFilterCount { get; private set; }
        public List<uint[]> EnabledFilterTypes { get; } = [];
        public string? EnabledScopeName { get; private set; }

        public NativeError Start(ulong* handle, char* name, Etw.EVENT_TRACE_PROPERTIES* properties)
        {
            StartCalls++;
            var v2 = (Etw.EVENT_TRACE_PROPERTIES_V2*)properties;
            StartedLogFileMode = v2->LogFileMode;
            StartedFilterCount = v2->FilterDescCount;
            *handle = StartedHandle;
            return NativeError.ERROR_SUCCESS;
        }

        public NativeError Enable(ulong handle, Guid* provider, byte level, ulong keywords,
            Etw.ENABLE_TRACE_PARAMETERS* parameters)
        {
            EnableCalls++;
            EnabledHandle = handle;
            var types = new uint[parameters->FilterDescCount];
            for (var i = 0; i < types.Length; i++)
            {
                types[i] = parameters->EnableFilterDesc[i].Type;
            }
            EnabledFilterTypes.Add(types);
            if (types.Length > 0 && types[0] is 0x80000008u or 0x80000010u)
            {
                EnabledScopeName = new string((char*)parameters->EnableFilterDesc[0].Ptr);
            }
            if (RejectEventIds && types.Contains(0x80000200u))
            {
                return NativeError.ERROR_INVALID_PARAMETER;
            }
            if (types.Length > 0 && types[0] == 0x80000004u)
            {
                Assert.AreEqual((uint)Environment.ProcessId, *(uint*)parameters->EnableFilterDesc->Ptr);
            }
            return FailClr && *provider == PerfProviders.Clr || FailComposition && *provider == PerfProviders.DirectComposition
                ? NativeError.ERROR_ACCESS_DENIED : NativeError.ERROR_SUCCESS;
        }

        public NativeError Stop(ulong handle, char* name, Etw.EVENT_TRACE_PROPERTIES* properties)
        {
            BeforeStop?.Invoke();
            StopCalls++;
            StoppedHandle = handle;
            StoppedName = new string(name);
            properties->EventsLost = 7;
            return FailStop ? NativeError.ERROR_ACCESS_DENIED : NativeError.ERROR_SUCCESS;
        }
    }

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        public void OptionalProviderFailurePreservesTheOwnedXamlSessionAndError(bool composition)
        {
            var api = new FakeEtwApi
            {
                StartedHandle = 123, RejectEventIds = true, FailClr = !composition, FailComposition = composition,
            };
            using var trace = new PrivateEtwSession("owned", Guid.NewGuid(), Environment.ProcessId, @"C:\trace.etl", 1, api);
            var xaml = PerfCaptureWorker.EnableProvider(trace, PerfProviders.All.Single(p => p.Id == PerfProviders.Xaml));
            var gc = PerfCaptureWorker.EnableProvider(trace, PerfProviders.All.Single(p =>
                p.Id == (composition ? PerfProviders.DirectComposition : PerfProviders.Clr)));
            Assert.AreEqual("enabled", xaml.State);
            Assert.AreEqual("unavailable", gc.State);
            Assert.IsNotNull(gc.Error);
            Assert.IsTrue(trace.CanEnable);
            Assert.AreEqual(0, api.StopCalls);
        }

        [TestMethod]
        public void EffectiveFilteringIsReportedForEachProvider()
        {
            var api = new FakeEtwApi { StartedHandle = 123, RejectEventIds = true };
            using var trace = new PrivateEtwSession("owned", Guid.NewGuid(), Environment.ProcessId, @"C:\trace.etl", 1, api);
            var gc = PerfCaptureWorker.EnableProvider(trace, PerfProviders.All.Single(p => p.Id == PerfProviders.Clr));
            var diagnostics = PerfCaptureWorker.EnableProvider(trace, PerfProviders.All.Single(p => p.Id == PerfProviders.Diagnostics));
            Assert.AreEqual(false, gc.EventIdFilterApplied);
            Assert.AreEqual(false, diagnostics.EventIdFilterApplied);
            Assert.AreEqual(4, api.EnableCalls);
        }

        [TestMethod]
        public void UnfilteredProvidersAreNamedInOneSizeWarning()
        {
            var capture = new PerfCaptureDocument
            {
                Id = Guid.NewGuid().ToString("N"), Directory = @"C:\capture", SessionName = "owned",
                ProviderStates =
                [
                    new(PerfProviders.Xaml, "enabled", null),
                    new(PerfProviders.Diagnostics, "enabled", false),
                    new(PerfProviders.Clr, "unavailable", null, "Denied."),
                ],
            };
            var warning = PerfCaptureWorker.UnfilteredProvidersWarning(capture);
            StringAssert.Contains(warning, "Microsoft-Windows-XAML-Diagnostics");
            StringAssert.Contains(warning, "--max-size-mib");
            Assert.IsFalse(warning!.Contains("DotNETRuntime"));

            capture.ProviderStates = [new(PerfProviders.Diagnostics, "enabled", true)];
            Assert.IsNull(PerfCaptureWorker.UnfilteredProvidersWarning(capture));
        }

        [TestMethod]
        public void NativePrivateGcEventsDecodeIntoQueryableSuspensions()
        {
            var directory = Directory.CreateTempSubdirectory("WinApp-Perf-Gc-Native-");
            try
            {
                using var process = Process.GetCurrentProcess();
                var provider = PerfProviders.All.Single(p => p.Id == PerfProviders.Clr);
                var id = Guid.NewGuid().ToString("N");
                var capture = new PerfCaptureDocument
                {
                    Id = id, Directory = directory.FullName, SessionName = "WinApp-Perf-" + id,
                    SessionId = Guid.NewGuid(), Target = PerfProcessIdentity.Read(process), Providers = [provider],
                    StartupCoverage = "GC-only test fixture; not WinUI startup coverage",
                };
                using (var trace = new PrivateEtwSession(capture.SessionName, capture.SessionId, process.Id,
                    Path.Join(directory.FullName, "trace.etl"), 16))
                {
                    capture.ProviderStates.Add(PerfCaptureWorker.EnableProvider(trace, provider));
                    Assert.AreEqual("enabled", capture.ProviderStates.Single().State);
                    capture.ReadyQpc = Stopwatch.GetTimestamp();
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                    trace.Stop();
                    Assert.IsTrue(trace.Stopped);
                    capture.StopQpc = Stopwatch.GetTimestamp();
                    capture.EventsLost = trace.EventsLost;
                    capture.BuffersLost = trace.BuffersLost;
                }
                capture.TraceFiles = directory.GetFiles("trace.etl*").Select(f => f.Name).ToArray();
                capture.State = "completed";
                capture.Save();
                var analysis = PerfAnalysisStore.Open(directory.FullName, CancellationToken.None);
                var result = PerfQuery.Execute(analysis, new(View: "gc", Limit: 100));
                Assert.IsTrue(result.Rows.Any(r => r.GcInterval is { IsGcSuspension: true, DurationMs: not null }));
                Assert.IsTrue(result.Rows.Any(r => r.GcInterval is { Kind: "collection", CollectionType: "blocking", DurationMs: not null }));
                Assert.AreEqual(0, analysis.Manifest.DecodeErrors);
                Assert.AreEqual(0u, capture.EventsLost);
                Assert.AreEqual(0u, capture.BuffersLost);
                var firstWrite = File.GetLastWriteTimeUtc(Path.Join(analysis.Directory, "gc.ndjson"));
                var reopened = PerfAnalysisStore.Open(directory.FullName, CancellationToken.None);
                Assert.AreEqual(analysis.Manifest.Fingerprint, reopened.Manifest.Fingerprint);
                Assert.AreEqual(firstWrite, File.GetLastWriteTimeUtc(Path.Join(reopened.Directory, "gc.ndjson")));
            }
            finally
            {
                directory.Delete(recursive: true);
            }
        }

    [TestMethod]
    public unsafe void NativePrivateCapturePreservesManifestPayload()
    {
        var directory = Directory.CreateTempSubdirectory("winapp-perf-test-");
        try
        {
            var provider = Guid.NewGuid();
            RegistrationHandle registration;
            Assert.AreEqual(0u, Native.EventRegister(in provider, null, null, out registration));
            try
            {
                using var trace = new PrivateEtwSession("WinApp-Perf-Test-" + Guid.NewGuid().ToString("N"),
                    Guid.NewGuid(), Environment.ProcessId, Path.Join(directory.FullName, "trace.etl"), 1);
                trace.Enable(provider, ulong.MaxValue);
                var descriptor = new EventDescriptor { Id = 42, Level = 4 };
                uint payload = 42;
                var data = new EventDataDescriptor { Ptr = (ulong)&payload, Size = sizeof(uint) };
                Assert.AreEqual(0u, Native.EventWrite(registration, &descriptor, 1, &data));
                trace.Stop();
                Assert.IsTrue(trace.Stopped);
                Assert.AreEqual(0u, trace.EventsLost);
            }
            finally
            {
                Assert.AreEqual(0u, Native.EventUnregister(registration));
            }
            var events = new List<PerfRawEvent>();
            foreach (var file in directory.GetFiles("trace.etl*"))
            {
                var (frequency, _) = PerfEtwReader.Read(file.FullName, Environment.ProcessId, [provider], events.Add);
                Assert.IsGreaterThan(0L, frequency);
            }
            Assert.IsTrue(events.Any(e => e.EventId == 42),
                $"Files: {string.Join(", ", directory.GetFiles().Select(f => f.Name + ":" + f.Length))}; " +
                $"events: {string.Join("; ", events.Select(e => $"{e.EventId}/{e.TraceLogging}/{e.EventName}/{e.DecodeError}"))}");
            var probe = events.Single(e => e.EventId == 42);
            Assert.IsNull(probe.DecodeError);
            CollectionAssert.AreEqual(new byte[] { 42, 0, 0, 0 }, probe.Payload);
            Assert.IsFalse(probe.TraceLogging);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void PrivateCaptureRejectsInvalidBoundsBeforeStarting()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PrivateEtwSession("test", Guid.NewGuid(), 0, @"C:\trace.etl", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new PrivateEtwSession("test", Guid.NewGuid(), 1, @"C:\trace.etl", 1025));
        Assert.Throws<ArgumentException>(() =>
            new PrivateEtwSession("test", Guid.NewGuid(), 1, "relative.etl", 1));
    }

    [TestMethod]
    public void NativeOwnedRecoveryValidatesPathAndActuallyStopsPrivateSession()
    {
        var directory = Directory.CreateTempSubdirectory("winapp-perf-recovery-");
        try
        {
            var name = "WinApp-Perf-Test-" + Guid.NewGuid().ToString("N");
            var sessionId = Guid.NewGuid();
            var path = Path.Join(directory.FullName, "trace.etl");
            using var trace = new PrivateEtwSession(name, sessionId, Environment.ProcessId, path, 1);
            Assert.Throws<InvalidOperationException>(() =>
                PrivateEtwSession.StopOwned(name, sessionId, Environment.ProcessId, Path.Join(directory.FullName, "other.etl")));
            Assert.IsTrue(PrivateEtwSession.StopOwned(name, sessionId, Environment.ProcessId, path));
            Assert.IsFalse(PrivateEtwSession.StopOwned(name, sessionId, Environment.ProcessId, path));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    public void NativePrivateCaptureNeverStopsAnExistingSession()
    {
        var directory = Directory.CreateTempSubdirectory("winapp-perf-collision-");
        try
        {
            var name = "WinApp-Perf-Test-" + Guid.NewGuid().ToString("N");
            using var first = new PrivateEtwSession(name, Guid.NewGuid(), Environment.ProcessId,
                Path.Join(directory.FullName, "first.etl"), 1);
            Assert.Throws<System.ComponentModel.Win32Exception>(() =>
                new PrivateEtwSession(name, Guid.NewGuid(), Environment.ProcessId,
                    Path.Join(directory.FullName, "second.etl"), 1));
            first.Stop();
            Assert.IsTrue(first.Stopped);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [TestMethod]
    [TestCategory("LiveWinUI")]
    public async Task LiveWinUiNativeProviderCapture()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("WINAPP_PERF_TEST_PID"), out var pid))
        {
            Assert.Inconclusive("Set WINAPP_PERF_TEST_PID to an accessible owned WinUI process.");
            return;
        }
        var xaml = new Guid("531A35AB-63CE-4BCF-AA98-F88C7A89E455");
        var directory = Directory.CreateTempSubdirectory("winapp-perf-live-");
        try
        {
            using (var trace = new PrivateEtwSession("WinApp-Perf-Live-" + Guid.NewGuid().ToString("N"),
                Guid.NewGuid(), pid, Path.Join(directory.FullName, "trace.etl"), 16))
            {
                trace.Enable(xaml, ulong.MaxValue);
                if (Environment.GetEnvironmentVariable("WINAPP_PERF_TEST_SELECTOR") is { Length: > 0 } selector)
                {
                    var uiCli = Environment.GetEnvironmentVariable("WINAPP_PERF_UI_CLI")
                        ?? throw new InvalidOperationException("Set WINAPP_PERF_UI_CLI to the installed or published winapp executable.");
                    using var action = Process.Start(new ProcessStartInfo(uiCli)
                    {
                        UseShellExecute = false,
                        ArgumentList = { "ui", "invoke", selector, "--app", pid.ToString(), "--json" },
                    });
                    Assert.IsNotNull(action);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                    await action.WaitForExitAsync(timeout.Token);
                    Assert.AreEqual(0, action.ExitCode);
                }
                await Task.Delay(TimeSpan.FromSeconds(5));
                trace.Stop();
                Assert.IsTrue(trace.Stopped);
                Assert.AreEqual(0u, trace.EventsLost);
            }
            var events = new List<PerfRawEvent>();
            foreach (var file in directory.GetFiles("trace.etl*"))
            {
                PerfEtwReader.Read(file.FullName, pid, [xaml], events.Add);
            }
            Assert.IsNotEmpty(events, "An ETL file alone is not proof of native events.");
            Assert.IsTrue(events.Any(e => e.TraceLogging && e.EventName is not null && e.DecodeError is null),
                string.Join("\n", events.Where(e => e.DecodeError is not null).Select(e => e.DecodeError).Distinct()));
            Console.WriteLine($"Captured {events.Count} native records; {events.Count(e => e.EventName is not null)} TDH-decoded events.");
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
