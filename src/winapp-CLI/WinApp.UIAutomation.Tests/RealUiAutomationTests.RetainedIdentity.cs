// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.Tests;

public partial class RealUiAutomationTests
{
    [TestMethod]
    public async Task ExplicitAction_NoActivateDuplicateInitialIdentity_RejectsCliEquivalentSelection()
    {
        using var fx = new ExplicitIdentityFixture(duplicate: true);
        var svc = NewService();
        var selected = await ResolveAsync(svc, fx.Target, "save");
        Assert.IsNotNull(selected.Context);
        selected.Selector = selected.AutomationId; // UiInvokeCommand's non-slug explicit branch.

        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => svc.InvokeAsync(fx.Target, selected, UiInvokeAction.Invoke, CancellationToken.None));
        StringAssert.Contains(error.Message, "no longer unique");
        Assert.AreEqual(0, fx.ClickCount);
    }

    [TestMethod]
    public async Task ExplicitAction_NoActivateRuntimeSlug_WithDuplicateIdentityRemainsExact()
    {
        using var fx = new ExplicitIdentityFixture(duplicate: true);
        var svc = NewService();
        var elements = await svc.InspectAsync(fx.Target, null, 3, CancellationToken.None);
        var second = elements.Single(e => e.Name == "Secondary Save" && e.Type == "Button");
        Assert.IsNotNull(SlugGenerator.ParseSlug(second.Selector!));
        var selected = await svc.FindSingleElementAsync(fx.Target, new UiSelector { Slug = second.Selector }, CancellationToken.None);
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
        var selected = await ResolveAsync(svc, fx.Target, "save");
        selected.Selector = selected.AutomationId;
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
        private Exception? _startupError;
        private int _primaryClicks;
        private int _secondaryClicks;
        public UiTarget Target { get; private set; } = null!;
        public int ClickCount => Volatile.Read(ref _primaryClicks) + Volatile.Read(ref _secondaryClicks);
        public int SecondaryClicks => Volatile.Read(ref _secondaryClicks);

        public ExplicitIdentityFixture(bool duplicate)
        {
            _thread = new Thread(() =>
            {
                try
                {
                    using var form = new IdentityForm();
                    _form = form;
                    _primary = MakeButton("Primary Save", 10, () => Interlocked.Increment(ref _primaryClicks));
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

        private static Button MakeButton(string name, int top, Action onClick)
        {
            var button = new Button { Name = "save", AccessibleName = name, Text = name, Left = 10, Top = top, Width = 140 };
            button.Click += (_, _) => onClick();
            return button;
        }

        public void Dispose()
        {
            if (_thread.IsAlive) { _form.Invoke(_form.Close); }
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
