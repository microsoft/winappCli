// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Reflection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
public sealed class StartupHookTests
{
    private static string directory = "";
    private static string childAssembly = "";

    [ClassInitialize]
    public static async Task BuildChild(TestContext context)
    {
        directory = Path.Combine(Path.GetTempPath(), "winapp-binding-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var framework = (Environment.Version.Major >= 10 ? "net10.0" : "net8.0") + "-windows10.0.19041.0";
        var references = string.Join("", new[] { "WinRT.Runtime", "Microsoft.WinUI",
            "Microsoft.Windows.SDK.NET", "Microsoft.InteractiveExperiences.Projection" }.Select(name =>
                $"<Reference Include=\"{name}\"><HintPath>{System.Security.SecurityElement.Escape(Path.Combine(AppContext.BaseDirectory, name + ".dll"))}</HintPath></Reference>"));
        await File.WriteAllTextAsync(Path.Combine(directory, "Child.csproj"),
            $"<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>{framework}</TargetFramework>" +
            "<OutputType>Exe</OutputType><UseAppHost>false</UseAppHost></PropertyGroup>" +
            $"<ItemGroup>{references}</ItemGroup></Project>");
        await File.WriteAllTextAsync(Path.Combine(directory, "Program.cs"),
            """
            using System;
            using System.IO;
            using System.IO.Pipes;
            using System.Security.Principal;
            using System.Text;
            using System.Threading.Tasks;
            using System.Runtime.InteropServices;
            NativeLibrary.SetDllImportResolver(typeof(Native).Assembly, (name, _, _) =>
                name == "binding-transfer-fixture" ? NativeLibrary.Load(Environment.GetEnvironmentVariable("WINAPP_BINDING_TEST_FIXTURE")) : IntPtr.Zero);
            Console.WriteLine("ready");
            string input;
            while (!string.IsNullOrEmpty(input = Console.ReadLine()))
            {
                if (input.StartsWith("native:", StringComparison.Ordinal))
                {
                    Marshal.ThrowExceptionForHR(Native.BindingFixtureCreate(out var fixture));
                    try
                    {
                        var answer = new StringBuilder(65536);
                        int hr = Native.BindingFixtureRelay(fixture, input.Substring(7), answer, 65536);
                        if (hr != 0) throw new InvalidOperationException("Native relay failed: " + hr + " " + answer);
                        Console.WriteLine(answer.ToString());
                    }
                    finally
                    {
                        // Ordinary projected RCWs follow GC lifetime; explicit transport owners are separate.
                        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
                        Marshal.ThrowExceptionForHR(Native.BindingFixtureDestroy(fixture));
                    }
                    continue;
                }
                bool expectClose = input.StartsWith("close:", StringComparison.Ordinal);
                if (expectClose) input = input.Substring(6);
                string[] frames = input.Split('.');
                using var pipe = new NamedPipeClientStream(".", $"winapp-devtools-binding-{Environment.ProcessId}",
                    PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
                await pipe.ConnectAsync(5000);
                await pipe.WriteAsync(Convert.FromBase64String(frames[0]));
                using var reader = new StreamReader(pipe);
                string reply = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                if (expectClose)
                {
                    await pipe.WriteAsync(Convert.FromBase64String(frames[1]));
                    string refusal = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
                    if (refusal is null || !refusal.Contains("incompatible binding protocol")
                        || await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)) is not null)
                        throw new InvalidOperationException("Removed command was not explicitly refused");
                    reply += "|<closed>";
                }
                if (reply != null && reply.StartsWith("BINDING2 ", StringComparison.Ordinal)) reply = reply.Substring(9);
                Console.WriteLine(reply ?? "<closed>");
            }
            internal static class Native
            {
                [DllImport("binding-transfer-fixture")] internal static extern int BindingFixtureCreate(out IntPtr value);
                [DllImport("binding-transfer-fixture")] internal static extern int BindingFixtureDestroy(IntPtr value);
                [DllImport("binding-transfer-fixture", CharSet=CharSet.Unicode)]
                internal static extern int BindingFixtureRelay(IntPtr value, string op, StringBuilder reply, uint capacity);
            }
            """);
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("build");
        start.ArgumentList.Add("--nologo");
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(90)); }
        finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        Assert.AreEqual(0, process.ExitCode, await output + await error);
        childAssembly = Path.Combine(directory, "bin", "Debug", framework, "Child.dll");
        context.WriteLine($"Runtime {Environment.Version}; child {childAssembly}");
    }

    [ClassCleanup]
    public static void Cleanup() => Directory.Delete(directory, recursive: true);

    private static async Task WithHost(Func<Process, Task> test)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            WorkingDirectory = directory
        };
        start.ArgumentList.Add(childAssembly);
        start.Environment["DOTNET_STARTUP_HOOKS"] = typeof(BindingDiagnosis).Assembly.Location;
        start.Environment["WINAPP_BINDING_TEST_FIXTURE"] = BindingFixtureInterop.Path;
        start.Environment.Remove("WINAPP_WATCH_PID");
        start.Environment.Remove("DOTNET_MODIFIABLE_ASSEMBLIES");
        using var child = Process.Start(start)!;
        var errors = child.StandardError.ReadToEndAsync();
        try
        {
            Assert.AreEqual("ready", await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10)));
            await test(child);
            await child.StandardInput.WriteLineAsync();
            await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, child.ExitCode, await errors);
        }
        catch (Exception ex)
        {
            if (!child.HasExited) child.Kill(entireProcessTree: true);
            await child.WaitForExitAsync();
            Assert.Fail($"{ex}\nChild stderr: {await errors}");
        }
        finally
        {
            if (!child.HasExited) { child.Kill(entireProcessTree: true); await child.WaitForExitAsync(); }
        }
    }

    private static async Task<NamedPipeClientStream> Connect(Process child)
    {
        var pipe = new NamedPipeClientStream(".", $"winapp-devtools-binding-{child.Id}", PipeDirection.InOut, PipeOptions.Asynchronous);
        try { await pipe.ConnectAsync(5000); return pipe; }
        catch { pipe.Dispose(); throw; }
    }

    [TestMethod]
    public async Task NonWinUiHostStartsAndExitsNormally()
    {
        await WithHost(async child =>
        {
            var reply = await SendFromHost(child, BindingFrame("diagnose"));
            Assert.IsNotNull(reply);
            Assert.IsTrue(reply.Contains("\"state\":\"unavailable\"", StringComparison.Ordinal), reply);
        });
    }

    private static async Task<string?> SendFromHost(Process child, byte[] frame, byte[]? removedCommand = null)
    {
        var request = Convert.ToBase64String(frame);
        if (removedCommand is not null) request = "close:" + request + "." + Convert.ToBase64String(removedCommand);
        await child.StandardInput.WriteLineAsync(request);
        return await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static byte[] BindingFrame(string op)
    {
        using var frame = new MemoryStream();
        using (var writer = new BinaryWriter(frame, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("BINDING2\n"));
            writer.Write(1);
            writer.Write((byte)0); // Well-framed invalid COM data, not a raw pointer.
            foreach (var field in new[] { op, "Text", "" })
            {
                var bytes = Encoding.UTF8.GetBytes(field);
                writer.Write(bytes.Length);
                writer.Write(bytes);
            }
        }
        return frame.ToArray();
    }

    [TestMethod]
    public async Task DifferentProcessIsRejectedBeforeAnyFrame()
    {
        await WithHost(async child =>
        {
            using var pipe = await Connect(child);
            var buffer = new byte[1];
            Assert.AreEqual(0, await pipe.ReadAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        });
    }

    [TestMethod]
    public async Task RemovedCommandsAreNotAcknowledged()
    {
        await WithHost(async child =>
        {
            foreach (var command in new[] { "PING", "APPLY", "SNAPSHOT", "RESTORE", "RELOAD", "RECONCILE",
                         "WIRE", "MOVE", "REMOVE", "RECONNECT", "SETRES", "SETRESREF", "JOURNALPROP", "REBIND" })
            {
                // A valid request on this same connection proves authentication reached the command reader.
                var reply = await SendFromHost(child, BindingFrame("diagnose"), Encoding.UTF8.GetBytes(command + "\n"));
                Assert.IsNotNull(reply);
                Assert.IsTrue(reply.Contains("\"state\":\"unavailable\"", StringComparison.Ordinal), command);
                Assert.IsTrue(reply.EndsWith("|<closed>", StringComparison.Ordinal), command);
            }
        });
    }

    [TestMethod]
    public async Task BindingFramesReturnExplicitUnavailableWithoutWinUi()
    {
        await WithHost(async child =>
        {
            foreach (var op in new[] { "diagnose", "capture", "restore", "clear", "writesource" })
            {
                var reply = await SendFromHost(child, BindingFrame(op));
                Assert.IsNotNull(reply);
                using var json = JsonDocument.Parse(reply);
                Assert.AreEqual("unavailable", json.RootElement.GetProperty("state").GetString(), op);
                Assert.IsFalse(string.IsNullOrWhiteSpace(json.RootElement.GetProperty("reason").GetString()), op);
            }
        });
    }

    [TestMethod]
    public async Task GenuineNativeRelayReachesActualManagedConsumptionForEveryOperation()
    {
        await WithHost(async child =>
        {
            StringAssert.Contains((await SendFromHost(child, BindingFrame("diagnose")))!,
                "\"state\":\"unavailable\"", "Wait for the asynchronous startup hook through a real request.");
            foreach (string op in new[] { "diagnose", "capture", "restore", "clear", "writesource" })
            {
                await child.StandardInput.WriteLineAsync("native:" + op);
                string? response = await child.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));
                Assert.IsNotNull(response);
                StringAssert.Contains(response, "the selected object is not a FrameworkElement",
                    "A real native COM packet must reach actual projection and consumer, not fail authentication/parsing.");
            }
        });
    }

    [TestMethod]
    public async Task AuthenticatedOldAndOversizedFramesAreExplicitlyRefused()
    {
        await WithHost(async child =>
        {
            StringAssert.Contains((await SendFromHost(child, Encoding.ASCII.GetBytes("BINDING\n")))!,
                "incompatible binding protocol");
            foreach (int length in new[] { -1, 0, 65537, int.MaxValue })
            {
                using var frame = new MemoryStream();
                frame.Write(Encoding.ASCII.GetBytes("BINDING2\n"));
                frame.Write(BitConverter.GetBytes(length));
                StringAssert.Contains((await SendFromHost(child, frame.ToArray()))!, "invalid binding frame");
            }
        });
    }

    [TestMethod]
    public void ActualFieldReaderRejectsTruncationNegativeAndOversizeBeforeUnmarshal()
    {
        var read = typeof(StartupHook).GetMethod("ReadField", BindingFlags.NonPublic | BindingFlags.Static)!;
        foreach ((int length, byte[] bytes, Type failure) in new[]
        {
            (-1, Array.Empty<byte>(), typeof(InvalidDataException)),
            (0, Array.Empty<byte>(), typeof(InvalidDataException)),
            (65537, Array.Empty<byte>(), typeof(InvalidDataException)),
            (5, new byte[4], typeof(EndOfStreamException))
        })
        {
            using var stream = new MemoryStream();
            stream.Write(BitConverter.GetBytes(length)); stream.Write(bytes); stream.Position=0;
            using var reader = new BinaryReader(stream);
            var error = Assert.ThrowsExactly<TargetInvocationException>(() => read.Invoke(null, [reader, false]));
            Assert.IsInstanceOfType(error.InnerException, failure);
        }
        using var valid = new MemoryStream();
        valid.Write(BitConverter.GetBytes(65536)); valid.Write(new byte[65536]); valid.Position=0;
        using var validReader = new BinaryReader(valid);
        Assert.AreEqual(65536, ((byte[])read.Invoke(null, [validReader, false])!).Length);
    }
}
