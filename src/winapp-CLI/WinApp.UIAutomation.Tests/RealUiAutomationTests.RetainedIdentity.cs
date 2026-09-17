// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitSelection_NoActivateOtherWindowExactId_WinsOverMainSubstrings(bool duplicateMain)
    {
        using var fx = new ExplicitIdentityFixture(duplicateMain, primaryAutomationId: "save-main");
        var hwnd = fx.ShowOwnedButton(false, automationId: "Save");
        var svc = NewService();
        var target = fx.AppTarget;
        var selected = await svc.FindSingleElementAsync(target, new UiSelector { Query = "Save" }, requireUnique: true, CancellationToken.None);
        Assert.IsNotNull(selected);
        Assert.AreEqual("Owned Save", selected.Name);
        Assert.AreEqual(hwnd, selected.WindowHandle);
        Assert.IsNotNull(selected.Context);
        await svc.InvokeAsync(target, selected, UiInvokeAction.Invoke, CancellationToken.None);
        await WaitForAsync(() => Task.FromResult(fx.SecondaryClicks == 1), "The other window's exact match was not invoked.");
        Assert.AreEqual(1, fx.ClickCount);
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
    }

    [TestMethod]
    [DataRow("Primary Save")]
    [DataRow("save")]
    public async Task ExplicitSelection_NoActivateMatchesAcrossWindows_AmbiguousUnlessExplicitHwnd(string query)
    {
        using var fx = new ExplicitIdentityFixture(duplicate: false);
        fx.ShowOwnedButton(false, name: "Primary Save");
        var svc = NewService();
        var selector = new UiSelector { Query = query };
        var error = await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(
            () => svc.FindSingleElementAsync(fx.AppTarget, selector, requireUnique: true, CancellationToken.None));
        StringAssert.Contains(error.Message, "Selector matched 2 elements");
        Assert.AreEqual(0, fx.ClickCount);

        var selected = await svc.FindSingleElementAsync(fx.Target, selector, requireUnique: true, CancellationToken.None);
        Assert.IsNotNull(selected);
        Assert.AreEqual(fx.Target.WindowHandle, selected.WindowHandle);
        await svc.InvokeAsync(fx.Target, selected, UiInvokeAction.Invoke, CancellationToken.None);
        await WaitForAsync(() => Task.FromResult(fx.ClickCount == 1), "The explicit HWND match was not invoked.");
        Assert.AreEqual(0, fx.SecondaryClicks);
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
    }

    [TestMethod]
    public async Task ExplicitAction_NoActivateDuplicateInitialIdentity_RejectsCliEquivalentSelection()
    {
        using var fx = new ExplicitIdentityFixture(duplicate: true);
        var svc = NewService();
        var error = await Assert.ThrowsExactlyAsync<UiAmbiguousSelectorException>(
            () => svc.FindSingleElementAsync(fx.Target, new UiSelector { Query = "save" }, requireUnique: true, CancellationToken.None));
        StringAssert.Contains(error.Message, "Selector matched 2 elements");
        StringAssert.Contains(error.Message, "btn-");
        Assert.AreEqual(0, fx.ClickCount);
    }

    [TestMethod]
    [DataRow("Primary", false)]
    [DataRow("SECONDARY SAVE", true)]
    public async Task ExplicitAction_NoActivateUniqueNameWithSharedAutomationId_InvokesSelectedProvider(string query, bool secondary)
    {
        using var fx = new ExplicitIdentityFixture(duplicate: true);
        var svc = NewService();
        var selected = await svc.FindSingleElementAsync(fx.Target, new UiSelector { Query = query }, requireUnique: true, CancellationToken.None);
        Assert.IsNotNull(selected);
        Assert.IsNotNull(selected.Context);
        Assert.AreEqual("save", selected.AutomationId);
        Assert.AreNotEqual("save", selected.Selector);

        Assert.AreEqual(new UiInvokeActionResult("InvokePattern", "invoke"),
            await svc.InvokeAsync(fx.Target, selected, UiInvokeAction.Invoke, CancellationToken.None));
        await WaitForAsync(() => Task.FromResult(fx.ClickCount == 1), "The selected name match was not invoked.");
        Assert.AreEqual(secondary ? 1 : 0, fx.SecondaryClicks);
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ExplicitAction_NoActivateOwnedWindow_StrictSelectionPreservesScopeAndProvider(bool ownedByMain)
    {
        using var fx = new ExplicitIdentityFixture(duplicate: false);
        var hwnd = fx.ShowOwnedButton(ownedByMain);
        var svc = NewService();
        var selector = new UiSelector { Query = "Owned Save" };
        if (!ownedByMain)
        {
            Assert.IsNull(await svc.FindSingleElementAsync(fx.Target, selector, requireUnique: true, CancellationToken.None));
        }
        else
        {
            var throughMain = await svc.FindSingleElementAsync(fx.Target, selector, requireUnique: true, CancellationToken.None);
            var throughOwned = await svc.FindSingleElementAsync(new UiTarget
            {
                ProcessId = fx.Target.ProcessId, ProcessName = fx.Target.ProcessName,
                WindowHandle = hwnd, IsExplicitWindow = true,
            }, selector, requireUnique: true, CancellationToken.None);
            Assert.IsNotNull(throughMain);
            Assert.IsNotNull(throughOwned);
            Assert.IsTrue(UiAutomationService.s_compareElements(svc,
                throughMain.Context!.AutomationElement, throughOwned.Context!.AutomationElement),
                "The fixture must expose the same provider in both ControlViews.");
        }
        var target = new UiTarget
        {
            ProcessId = fx.Target.ProcessId,
            ProcessName = fx.Target.ProcessName,
            WindowHandle = fx.Target.WindowHandle,
            IsExplicitWindow = false,
        };
        var selected = await svc.FindSingleElementAsync(target, selector, requireUnique: true, CancellationToken.None);
        Assert.IsNotNull(selected);
        Assert.IsNotNull(selected.Context);
        Assert.AreEqual(hwnd, selected.WindowHandle);
        Assert.AreEqual(new UiInvokeActionResult("InvokePattern", "invoke"),
            await svc.InvokeAsync(target, selected, UiInvokeAction.Invoke, CancellationToken.None));
        await WaitForAsync(() => Task.FromResult(fx.SecondaryClicks == 1), "The owned-window provider was not invoked.");
        Assert.AreEqual(1, fx.ClickCount);
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
    }

    [TestMethod]
    public async Task ExplicitAction_NoActivateRuntimeSlug_WithDuplicateIdentityRemainsExact()
    {
        using var fx = new ExplicitIdentityFixture(duplicate: true);
        var svc = NewService();
        var elements = await svc.InspectAsync(fx.Target, null, 3, CancellationToken.None);
        var second = elements.Single(e => e.Name == "Secondary Save" && e.Type == "Button");
        Assert.IsNotNull(SlugGenerator.ParseSlug(second.Selector!));
        var selected = await svc.FindSingleElementAsync(fx.Target, new UiSelector { Slug = second.Selector }, requireUnique: true, CancellationToken.None);
        Assert.IsNotNull(selected);
        Assert.IsNotNull(selected.Context);

        Assert.AreEqual(new UiInvokeActionResult("InvokePattern", "invoke"),
            await svc.InvokeAsync(fx.Target, selected, UiInvokeAction.Invoke, CancellationToken.None));
        await WaitForAsync(() => Task.FromResult(fx.SecondaryClicks == 1), "Selected runtime slug was not invoked.");
        Assert.AreEqual(1, fx.ClickCount);
        Assert.AreEqual(0, svc.SerializedElementResolutionCount);
    }

    [TestMethod]
    public async Task ExplicitAction_NoActivateUniqueRetainedIdentity_ValidatesThenInvokes()
    {
        using var fx = new ExplicitIdentityFixture(duplicate: false);
        var svc = NewService();
        var selected = await ResolveAsync(svc, fx.Target, "save");
        selected.Selector = selected.AutomationId;

        Assert.AreEqual(new UiInvokeActionResult("InvokePattern", "invoke"),
            await svc.InvokeAsync(fx.Target, selected, UiInvokeAction.Invoke, CancellationToken.None));
        await WaitForAsync(() => Task.FromResult(fx.ClickCount == 1), "Unique retained provider was not invoked.");
        Assert.AreEqual(1, svc.SerializedElementResolutionCount, "Committed AutomationId requires uniqueness validation.");
    }

    [TestMethod]
    public async Task ExplicitAction_NoActivateReplacedRetainedIdentity_FailsWithoutRebinding()
    {
        using var fx = new ExplicitIdentityFixture(duplicate: false);
        var svc = NewService();
        var selected = await svc.FindSingleElementAsync(fx.Target, new UiSelector { Query = "Primary" }, requireUnique: true, CancellationToken.None);
        Assert.IsNotNull(selected);
        fx.ReplacePrimary();
        Assert.IsNotNull(await ResolveAsync(svc, fx.Target, "save"));

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(fx.Target, selected, UiInvokeAction.Invoke, CancellationToken.None));
        StringAssert.Contains(error.Message, "stale");
        Assert.AreEqual(0, fx.ClickCount);
    }

    // Separate from UiaTestFixture: these tests must not activate a window on the shared desktop.
    private sealed class ExplicitIdentityFixture : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "Disposed by the UI thread's using statement after Application.Run exits.")]
        private IdentityForm _form = null!;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "Owned and disposed by the form's Controls collection; replacements dispose the removed button.")]
        private Button _primary = null!;
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213", Justification = "Closed and disposed on the UI thread before the main form closes.")]
        private IdentityForm? _other;
        private Exception? _startupError;
        private int _primaryClicks;
        private int _secondaryClicks;
        public UiTarget Target { get; private set; } = null!;
        public UiTarget AppTarget => new()
        {
            ProcessId = Target.ProcessId, ProcessName = Target.ProcessName,
            WindowHandle = Target.WindowHandle, IsExplicitWindow = false,
        };
        public int ClickCount => Volatile.Read(ref _primaryClicks) + Volatile.Read(ref _secondaryClicks);
        public int SecondaryClicks => Volatile.Read(ref _secondaryClicks);

        public ExplicitIdentityFixture(bool duplicate, string primaryAutomationId = "save")
        {
            _thread = new Thread(() =>
            {
                try
                {
                    using var form = new IdentityForm();
                    _form = form;
                    _primary = MakeButton("Primary Save", 10, () => Interlocked.Increment(ref _primaryClicks));
                    _primary.Name = primaryAutomationId;
                    form.Controls.Add(_primary);
                    if (duplicate)
                    {
                        form.Controls.Add(MakeButton("Secondary Save", 50, () => Interlocked.Increment(ref _secondaryClicks)));
                    }
                    form.Shown += (_, _) =>
                    {
                        Target = new UiTarget
                        {
                            ProcessId = Environment.ProcessId,
                            ProcessName = "ExplicitIdentityFixture",
                            WindowHandle = form.Handle,
                            IsExplicitWindow = true,
                        };
                        _ready.Set();
                    };
                    Application.Run(form);
                }
                catch (Exception error)
                {
                    _startupError = error;
                    _ready.Set();
                }
            }) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            if (!_ready.Wait(TimeSpan.FromSeconds(15))) { throw new TimeoutException("Identity fixture startup timed out."); }
            if (_startupError is not null) { throw new InvalidOperationException("Identity fixture startup failed.", _startupError); }
        }

        public void ReplacePrimary() => _form.Invoke(() =>
        {
            _primary.Dispose();
            _primary = MakeButton("Primary Save", 10, () => Interlocked.Increment(ref _primaryClicks));
            _form.Controls.Add(_primary);
            _primary.CreateControl();
        });

        public long ShowOwnedButton(bool ownedByMain, string name = "Owned Save", string automationId = "save") => (long)_form.Invoke(() =>
        {
            var owned = new IdentityForm();
            _other = owned;
            var button = MakeButton(name, 10, () => Interlocked.Increment(ref _secondaryClicks));
            button.Name = automationId;
            owned.Controls.Add(button);
            if (ownedByMain) { owned.Show(_form); }
            else { owned.Show(); }
            return (long)owned.Handle;
        });

        private static Button MakeButton(string name, int top, Action onClick)
        {
            var button = new Button { Name = "save", AccessibleName = name, Text = name, Left = 10, Top = top, Width = 140 };
            button.Click += (_, _) => onClick();
            return button;
        }

        public void Dispose()
        {
            if (_thread.IsAlive)
            {
                _form.Invoke(() =>
                {
                    _other?.Close();
                    _form.Close();
                });
            }
            if (!_thread.Join(TimeSpan.FromSeconds(10))) { throw new TimeoutException("Identity fixture did not close."); }
            _ready.Dispose();
        }

        private sealed class IdentityForm : Form
        {
            public IdentityForm()
            {
                ShowInTaskbar = false;
                Text = "Explicit identity regression";
                StartPosition = FormStartPosition.Manual;
                Size = new System.Drawing.Size(180, 140);
            }

            protected override bool ShowWithoutActivation => true;
            protected override CreateParams CreateParams
            {
                get
                {
                    var parameters = base.CreateParams;
                    parameters.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                    return parameters;
                }
            }
        }
    }
}
