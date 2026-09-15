// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using Windows.Win32.UI.Accessibility;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(10)]
    public async Task Query_NestedRootRejectsBeforeTargetLookup(int maxResults)
    {
        var svc = NewService();
        var resolutions = 0;
        UiAutomationService.s_getRootElement = (_, _, _) => { resolutions++; return null; };
        var selector = new UiSelector
        {
            Query = "Subject",
            Root = new() { Query = "MailRow", Root = new() { Query = "Inbox" } },
        };
        var error = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            svc.SearchAsync(new UiTarget(), selector, maxResults, default));
        Assert.AreEqual("selector", error.ParamName);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            svc.FindSingleElementAsync(new UiTarget(), selector, default));
        Assert.AreEqual(0, resolutions);
    }

    [TestMethod]
    public async Task Query_NestedInboxRootCannotReadArchiveSubject()
    {
        using var fx = new QueryRegressionFixture();
        var svc = NewService();
        var row = new UiSelector { Query = "MailRow", Root = new() { Query = "Inbox" } };
        Assert.IsEmpty(await svc.SearchAsync(fx.Target, row, 10, default));
        var archive = await svc.FindSingleElementAsync(fx.Target,
            new UiSelector { Query = "Subject", Root = new() { Query = "Archive" } }, default);
        Assert.IsNotNull(archive);
        Assert.AreEqual("ARCHIVE-ONLY", await svc.GetTextAsync(fx.Target, archive, default));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            svc.SearchAsync(fx.Target, new UiSelector { Query = "Subject", Root = row }, 10, default));
    }

    [TestMethod]
    [DataRow(false, "GetNextSiblingElement")]
    [DataRow(true, "GetNextSiblingElement")]
    [DataRow(false, "GetFirstChildElement")]
    [DataRow(true, "GetFirstChildElement")]
    [DataRow(false, "get_CurrentControlType")]
    [DataRow(true, "get_CurrentControlType")]
    public async Task Query_StableSlugSurvivesUnrelatedPanelFailure(bool constrained, string operation)
    {
        using var fx = new QueryRegressionFixture();
        var svc = NewService();
        var stable = (await svc.SearchAsync(fx.Target,
            new UiSelector { Query = "Subject", ControlType = "Edit" }, 1, default)).Single();
        var field = typeof(UiAutomationService).GetField("_automation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var automation = (IUIAutomation)field.GetValue(svc)!;
        var realWalker = automation.get_ControlViewWalker();
        var failure = new COMException("Unrelated message panel refreshed.", unchecked((int)0x80040201));
        var failures = 0;
        IUIAutomationElement? staleProxy = null;
        IUIAutomationElement? staleElement = null;
        UiAutomationService.s_getControlViewWalker = _ => ComProxy<IUIAutomationTreeWalker>((method, args) =>
        {
            if (method.Name == operation && args?[0] is IUIAutomationElement element)
            {
                var id = element.get_CurrentAutomationId();
                string? name;
                try { name = id.ToString(); }
                finally { unsafe { Marshal.FreeBSTR((nint)id.Value); } }
                if (name == "Transient")
                {
                    failures++;
                    throw failure;
                }
            }
            var result = method.Invoke(realWalker,
                args?.Select(arg => ReferenceEquals(arg, staleProxy) ? staleElement : arg).ToArray());
            if (operation == "get_CurrentControlType" && result is IUIAutomationElement candidate)
            {
                var id = candidate.get_CurrentAutomationId();
                string? name;
                try { name = id.ToString(); }
                finally { unsafe { Marshal.FreeBSTR((nint)id.Value); } }
                if (name == "Transient")
                {
                    staleElement = candidate;
                    staleProxy = ComProxy<IUIAutomationElement>((getter, getterArgs) =>
                    {
                        if (getter.Name == operation) { failures++; throw failure; }
                        return getter.Invoke(candidate, getterArgs);
                    });
                    return staleProxy;
                }
            }
            return result;
        });
        var selector = new UiSelector { Slug = stable.Selector, ControlType = constrained ? "Edit" : null };
        if (constrained)
        {
            var actual = await Assert.ThrowsExactlyAsync<COMException>(() =>
                svc.FindSingleElementAsync(fx.Target, selector, default));
            Assert.AreSame(failure, actual);
        }
        else
        {
            var found = await svc.FindSingleElementAsync(fx.Target, selector, default);
            Assert.IsNotNull(found);
            Assert.AreEqual("ARCHIVE-ONLY", await svc.GetTextAsync(fx.Target, found, default));
        }
        Assert.IsGreaterThan(0, failures);
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Query_SlugRuntimeFailureIsStrictOnlyWithConstraints(bool constrained, bool nameless)
    {
        using var fx = new QueryRegressionFixture();
        var svc = NewService();
        var slug = (await svc.InspectAsync(fx.Target, null, 0, default))[0].Selector!;
        if (nameless)
        {
            var parsed = SlugGenerator.ParseSlug(slug)!.Value;
            slug = $"{parsed.Prefix}-{parsed.Hash}";
        }
        var field = typeof(UiAutomationService).GetField("_automation", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var automation = (IUIAutomation)field.GetValue(svc)!;
        var realRoot = automation.ElementFromHandle(new((nint)fx.Target.WindowHandle));
        var failure = new COMException("Runtime ID unavailable.", unchecked((int)0x80040201));
        var reads = 0;
        var root = ComProxy<IUIAutomationElement>((method, args) =>
        {
            if (method.Name == "GetRuntimeId") { reads++; throw failure; }
            return method.Invoke(realRoot, args);
        });
        var walker = automation.get_ControlViewWalker();
        UiAutomationService.s_getControlViewWalker = _ => ComProxy<IUIAutomationTreeWalker>((method, args) =>
            method.Invoke(walker, args?.Select(arg => ReferenceEquals(arg, root) ? realRoot : arg).ToArray()));
        UiAutomationService.s_getRootElement = (_, _, _) => root;
        var selector = constrained
            ? new UiSelector { Root = new() { Slug = slug }, ControlType = "Edit" }
            : new UiSelector { Slug = slug };
        if (constrained)
        {
            var actual = await Assert.ThrowsExactlyAsync<COMException>(() =>
                svc.FindSingleElementAsync(fx.Target, selector, default));
            Assert.AreSame(failure, actual);
        }
        else
        {
            Assert.IsNull(await svc.FindSingleElementAsync(fx.Target, selector, default));
        }
        Assert.IsGreaterThan(0, reads);
    }

    [TestMethod]
    public async Task Query_StableSlugSurvivesLiveMessageChurn()
    {
        using var fx = new QueryRegressionFixture();
        var svc = NewService();
        var stable = (await svc.SearchAsync(fx.Target,
            new UiSelector { Query = "Subject", ControlType = "Edit" }, 1, default)).Single();
        fx.StartChurn();
        for (var i = 0; i < 100; i++)
        {
            var found = await svc.FindSingleElementAsync(fx.Target, new UiSelector { Slug = stable.Selector }, default);
            Assert.IsNotNull(found);
            Assert.AreEqual("ARCHIVE-ONLY", await svc.GetTextAsync(fx.Target, found, default));
        }
    }

    private sealed class QueryRegressionFixture : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new();
        private NonactivatingQueryForm _form = null!;
        private Panel _messages = null!;
        private System.Windows.Forms.Timer? _timer;
        private Exception? _error;
        public UiTarget Target { get; private set; } = null!;

        public QueryRegressionFixture()
        {
            _thread = new Thread(() =>
            {
                try
                {
                    _form = new NonactivatingQueryForm
                    {
                        Text = $"QueryRegression_{Guid.NewGuid():N}", Width = 700, Height = 400,
                        ShowInTaskbar = false,
                    };
                    _messages = new Panel { Name = "Messages", Width = 200, Height = 100 };
                    _messages.Controls.Add(new Label { Name = "Transient", Text = "Message" });
                    var inbox = new GroupBox { Name = "Inbox", Top = 110, Width = 200, Height = 150 };
                    var archive = new GroupBox { Name = "Archive", Left = 220, Width = 300, Height = 200 };
                    var row = new Panel { Name = "MailRow", Top = 25, Width = 250, Height = 100 };
                    row.Controls.Add(new TextBox { Name = "Subject", Text = "ARCHIVE-ONLY" });
                    archive.Controls.Add(row);
                    _form.Controls.AddRange([_messages, inbox, archive]);
                    _form.Shown += (_, _) =>
                    {
                        Target = new UiTarget
                        {
                            ProcessId = Environment.ProcessId, WindowHandle = (long)_form.Handle, IsExplicitWindow = true,
                        };
                        _ready.Set();
                    };
                    Application.Run(_form);
                }
                catch (Exception ex) { _error = ex; _ready.Set(); }
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(15))) { throw new TimeoutException("Query fixture startup timed out."); }
            if (_error is not null) { throw new InvalidOperationException("Query fixture startup failed.", _error); }
        }

        public void StartChurn() => _form.Invoke(() =>
        {
            _timer = new System.Windows.Forms.Timer { Interval = 100 };
            _timer.Tick += (_, _) =>
            {
                foreach (var child in _messages.Controls.Cast<Control>().ToArray()) { child.Dispose(); }
                for (var i = 0; i < 12; i++) { _messages.Controls.Add(new Label { Name = $"Transient{i}", Text = "Message" }); }
            };
            _timer.Start();
        });

        public void Dispose()
        {
            _form.Invoke(() => { _timer?.Dispose(); _messages.Dispose(); _form.Close(); _form.Dispose(); });
            if (!_thread.Join(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Query fixture did not close."); }
            _messages.Dispose();
            _form.Dispose();
            _ready.Dispose();
        }
    }

    private sealed class NonactivatingQueryForm : Form
    {
        protected override bool ShowWithoutActivation => true;
        protected override CreateParams CreateParams
        {
            get { var parameters = base.CreateParams; parameters.ExStyle |= 0x08000000; return parameters; }
        }
    }
}
