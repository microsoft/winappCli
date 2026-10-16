// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace WinApp.DevTools.Managed.Tests;

[TestClass]
[DoNotParallelize]
public sealed class BindingCaptureLifetimeTests
{
    [TestMethod]
    [TestCategory("RequiresDesktop")]
    public async Task NativeElementOwnsCaptureAcrossWrapperReplacementAndReleasesCycles()
    {
        if (Environment.GetEnvironmentVariable("WINAPP_BINDING_LIFETIME_TESTS") != "1")
            Assert.Inconclusive("Run scripts\\test-binding-lifetime.ps1 on a Windows App Runtime desktop host.");
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        LifetimeApp? app = null;
        DispatcherQueue? queue = null;
        var thread = new Thread(() =>
        {
            try
            {
                Stage("ComWrappers initialization");
                WinRT.ComWrappersSupport.InitializeComWrappers();
                Stage("Application.Start");
                Application.Start(args =>
                {
                    queue = DispatcherQueue.GetForCurrentThread();
                    app = new LifetimeApp(completion);
                });
            }
            catch (Exception error) { Stage(error.ToString()); completion.TrySetException(error); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try { await completion.Task.WaitAsync(TimeSpan.FromSeconds(60)); }
        finally
        {
            if (thread.IsAlive) queue?.TryEnqueue(() => app?.Stop());
            Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(5)), "The owned XAML thread did not exit.");
        }
    }

    private static void Stage(string message)
    {
        Console.Error.WriteLine($"{DateTime.UtcNow:O} binding-lifetime: {message}");
        Console.Error.Flush();
    }

    public sealed class Source : INotifyPropertyChanged
    {
        public FrameworkElement? Target { get; set; }
        public FrameworkElement? Parent { get; set; }
        public string Value { get; private set; } = "before";
        public event PropertyChangedEventHandler? PropertyChanged;
        public void Update()
        {
            Value = "after";
            PropertyChanged?.Invoke(this, new(nameof(Value)));
        }
    }

    private sealed class LifetimeApp : Application
    {
        private readonly BindingCaptureStore store = new();
        private Window? window;
        private StackPanel root = null!;
        private bool stopped;
        private WeakReference<Source> source = null!;
        private WeakReference<object> capture = null!;
        private int originalWrapper;

        internal LifetimeApp(TaskCompletionSource completion)
        {
            Stage("Application base initialized");
            UnhandledException += (_, args) => Stage($"Unhandled 0x{args.Exception.HResult:X8}: {args.Exception}");
            var queue = DispatcherQueue.GetForCurrentThread();
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            Assert.IsTrue(queue.TryEnqueue(async () =>
            {
                Exception? failure = null;
                try
                {
                    Stage("Ready callback: Window");
                    window = new();
                    Stage("Ready callback: StackPanel");
                    root = new();
                    window.Content = root;
                    Stage("Wrapper replacement gate");
                    await Exercise(false);
                    Stage("Reference-cycle gate");
                    await Exercise(true);
                }
                catch (Exception error) { Stage(error.ToString()); failure = error; }
                finally { Stop(); }
                if (failure is null) completion.TrySetResult();
                else completion.TrySetException(failure);
            }));
        }

        internal void Stop()
        {
            if (stopped) return;
            stopped = true;
            window?.Close();
            Exit();
        }

        private async Task Exercise(bool cycle)
        {
            using var native = CreateTarget(cycle);
            await Collect();
            Assert.IsTrue(native.IsAlive(), "The visual tree still owns the native child.");
            Assert.IsTrue(SourceAlive(), "Capture lost its source while the native child remained alive.");
            if (!cycle)
                Assert.AreNotEqual(originalWrapper, WrapperIdentity(), "The gate must actually replace the managed wrapper.");
            RestoreAndUpdate();
            RemoveTarget();
            for (var attempt = 0; attempt < 20 && (native.IsAlive() || SourceAlive() || CaptureAlive()); attempt++)
                await Collect();
            Assert.IsFalse(native.IsAlive(), "Removed native child retained.");
            Assert.IsFalse(SourceAlive(), "Captured source retained after native removal.");
            Assert.IsFalse(CaptureAlive(), "Capture state retained after native removal.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private NativeWeakReference CreateTarget(bool cycle)
        {
            var model = new Source();
            var target = new TextBlock();
            originalWrapper = RuntimeHelpers.GetHashCode(target);
            if (cycle) { model.Target = target; model.Parent = root; }
            var binding = new Binding { Source = model, Path = new PropertyPath(nameof(Source.Value)), Mode = BindingMode.OneWay };
            target.SetBinding(TextBlock.TextProperty, binding);
            root.Children.Add(target);
            store.Set(target, "Text", target.GetBindingExpression(TextBlock.TextProperty).ParentBinding);
            target.SetValue(TextBlock.TextProperty, "override");
            source = new(model);
            capture = new(binding);
            return new(target);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RestoreAndUpdate()
        {
            var target = (TextBlock)root.Children[0];
            Assert.AreEqual("override", target.Text);
            Assert.IsTrue(store.TryGet(target, "Text", out var saved), "Default backend lost the capture.");
            target.SetBinding(TextBlock.TextProperty, (Binding)saved!);
            Assert.AreEqual("before", target.Text);
            Assert.IsTrue(source.TryGetTarget(out var model));
            model.Update();
            Assert.AreEqual("after", target.Text, "Restored binding must receive actual source updates.");
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private int WrapperIdentity() => RuntimeHelpers.GetHashCode(root.Children[0]);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool SourceAlive() => source.TryGetTarget(out _);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private bool CaptureAlive() => capture.TryGetTarget(out _);
        [MethodImpl(MethodImplOptions.NoInlining)]
        private void RemoveTarget() => root.Children.Clear();

        private static async Task Collect()
        {
            // Deliberate collection is only a lifetime gate, never a memory-load measurement.
            await Task.Run(() => { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); });
            await Task.Delay(100);
        }
    }

    private sealed class NativeWeakReference : IDisposable
    {
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetWeak(nint source, out nint weak);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int Resolve(nint weak, in Guid iid, out nint instance);
        private nint weak;

        internal NativeWeakReference(TextBlock element)
        {
            var instance = WinRT.MarshalInspectable<TextBlock>.FromManaged(element);
            nint source = 0;
            try
            {
                var iid = new Guid("00000038-0000-0000-C000-000000000046");
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(instance, in iid, out source));
                var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(source), 3 * IntPtr.Size);
                Marshal.ThrowExceptionForHR(Marshal.GetDelegateForFunctionPointer<GetWeak>(method)(source, out weak));
            }
            finally
            {
                if (source != 0) Marshal.Release(source);
                Marshal.Release(instance);
            }
        }

        internal bool IsAlive()
        {
            var iid = new Guid("AF86E2E0-B12D-4C6A-9C5A-D7AA65101E90");
            var method = Marshal.ReadIntPtr(Marshal.ReadIntPtr(weak), 3 * IntPtr.Size);
            Marshal.ThrowExceptionForHR(Marshal.GetDelegateForFunctionPointer<Resolve>(method)(weak, in iid, out var instance));
            if (instance == 0) return false;
            Marshal.Release(instance);
            return true;
        }

        public void Dispose()
        {
            if (weak != 0) { Marshal.Release(weak); weak = 0; }
        }
    }
}
