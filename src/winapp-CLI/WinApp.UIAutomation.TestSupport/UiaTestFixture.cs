// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Windows.Forms;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

/// <summary>
/// A live, in-process WinForms window used as a real UI Automation (UIA) target so the real
/// <see cref="UiAutomationService"/> can be driven end-to-end against genuine
/// controls (no fakes). The form runs its own message loop on a dedicated STA thread; UIA client
/// calls happen on the test thread. The two never block each other, which is the standard,
/// deadlock-free pattern for driving an in-process UIA provider.
///
/// The form exposes a variety of controls with known, unique <c>Name</c>s (which the WinForms UIA
/// provider surfaces as AutomationId) so tests can locate them deterministically:
///   * <see cref="InvokeButton"/> — a Button whose click sets <see cref="ResultBox"/>'s text.
///   * <see cref="ValueBox"/> — a single-line TextBox for SetValue / GetText round-trips.
///   * <see cref="ResultBox"/> — a read-only TextBox that records the button click ("clicked").
///   * <see cref="ToggleCheck"/> — a CheckBox exercising TogglePattern.
///   * <see cref="ItemsList"/> — a ListBox with many items (selection + potential scroll).
///   * <see cref="ScrollPanel"/> — an AutoScroll Panel taller than its viewport.
///   * <see cref="MultilineBox"/> — a multiline TextBox with many lines.
///   * <see cref="TextLabel"/> — a Label for GetText fallback (element Name).
/// </summary>
public sealed class UiaTestFixture : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private Form _form = null!;
    private Exception? _startupError;

    public string Title { get; }
    public nint Hwnd { get; private set; }
    public int ProcessId { get; } = Environment.ProcessId;

    /// <summary>The hosted form. Access members only via <see cref="OnUiThread(Action)"/>.</summary>
    public Form Form => _form;

    public Button InvokeButton { get; private set; } = null!;
    public TextBox ValueBox { get; private set; } = null!;
    public TextBox ResultBox { get; private set; } = null!;
    public CheckBox ToggleCheck { get; private set; } = null!;
    public ListBox ItemsList { get; private set; } = null!;
    public Panel ScrollPanel { get; private set; } = null!;
    public Panel ScrollNestedPanel { get; private set; } = null!;
    public Label ScrollNestedLabel { get; private set; } = null!;
    public TextBox MultilineBox { get; private set; } = null!;
    public Label TextLabel { get; private set; } = null!;
    public Button ParentInvokeButton { get; private set; } = null!;
    public Panel InvokableMiddlePanel { get; private set; } = null!;
    public Label InvokableChildLabel { get; private set; } = null!;

    // Additional controls exercising a broad range of UIA control types / patterns so ToUiElement,
    // GetControlTypeName, GetText, Invoke and the property extractors are driven against real providers.
    public ComboBox SelectCombo { get; private set; } = null!;
    public RadioButton OptionRadio { get; private set; } = null!;
    public GroupBox OptionGroup { get; private set; } = null!;
    public CheckBox CheckedBox { get; private set; } = null!;
    public CheckBox TriCheck { get; private set; } = null!;
    public ProgressBar Progress { get; private set; } = null!;
    public TrackBar Slider { get; private set; } = null!;
    public TabControl Tabs { get; private set; } = null!;
    public MenuStrip Menu { get; private set; } = null!;

    /// <summary>
    /// The "File" menu. Its drop-down is genuine transient UI: Windows dismisses it when another
    /// window takes the foreground, which is what makes it a faithful subject for the cooperative-turn
    /// acceptance tests (issue #764 §18.3).
    /// </summary>
    public ToolStripMenuItem FileMenu { get; private set; } = null!;
    public TreeView Tree { get; private set; } = null!;
    public Panel HScrollPanel { get; private set; } = null!;
    public NumericUpDown Spinner { get; private set; } = null!;
    public LinkLabel Link { get; private set; } = null!;
    public PictureBox Picture { get; private set; } = null!;
    public HScrollBar RangeBar { get; private set; } = null!;
    public Panel NamelessPanel { get; private set; } = null!;

    // A Button and a Label that share the same accessible Name (but different AutomationIds) so a
    // name query matches both — exercising FindSingle's "prefer the only invokable match" branch.
    public Button DupButton { get; private set; } = null!;
    public Label DupLabel { get; private set; } = null!;

    private Form? _ownedWindow;

    public UiaTestFixture()
    {
        Title = "WinAppUiaFixture_" + Guid.NewGuid().ToString("N")[..8];
        _thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "UiaTestFixtureUiThread",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        if (!_ready.Wait(TimeSpan.FromSeconds(15)))
        {
            throw new TimeoutException("UiaTestFixture window did not become ready within 15s.");
        }
        if (_startupError is not null)
        {
            throw new InvalidOperationException("UiaTestFixture failed to start.", _startupError);
        }
    }

    private void ThreadMain()
    {
        try
        {
            _form = BuildForm();
            _form.Shown += (_, _) =>
            {
                Hwnd = _form.Handle;
                _ready.Set();
            };
            Application.Run(_form);
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _ready.Set();
        }
    }

    private Form BuildForm()
    {
        var form = new Form
        {
            Text = Title,
            Name = "fixtureForm",
            Width = 960,
            Height = 700,
            StartPosition = FormStartPosition.CenterScreen,
            // Keep the form on-screen and non-topmost; screenshot tests foreground it explicitly.
            ShowInTaskbar = true,
        };

        InvokeButton = new Button
        {
            Name = "btnInvoke",
            Text = "Click Me",
            AccessibleName = "Click Me",
            Left = 10,
            Top = 10,
            Width = 120,
            Height = 30,
        };
        ResultBox = new TextBox
        {
            Name = "txtResult",
            AccessibleName = "Result",
            Left = 150,
            Top = 12,
            Width = 320,
            ReadOnly = true,
            Text = "unclicked",
        };
        InvokeButton.Click += (_, _) => ResultBox.Text = "clicked";

        ValueBox = new TextBox
        {
            Name = "txtValue",
            AccessibleName = "Value",
            Left = 10,
            Top = 50,
            Width = 460,
            Text = "initial",
        };

        ToggleCheck = new CheckBox
        {
            Name = "chkToggle",
            Text = "Toggle Me",
            AccessibleName = "Toggle Me",
            Left = 10,
            Top = 90,
            Width = 200,
            Checked = false,
        };

        TextLabel = new Label
        {
            Name = "lblText",
            Text = "Hello Label",
            AccessibleName = "Hello Label",
            Left = 220,
            Top = 92,
            Width = 250,
        };

        ParentInvokeButton = new Button
        {
            Name = "btnParentInvoke",
            Text = "",
            AccessibleName = "Parent Invoke",
            Left = 220,
            Top = 10,
            Width = 120,
            Height = 30,
        };
        InvokableChildLabel = new Label
        {
            Name = "lblInsideInvoke",
            Text = "Inside Invoke",
            AccessibleName = "Inside Invoke",
            Left = 2,
            Top = 2,
            Width = 95,
            Height = 16,
        };
        InvokableMiddlePanel = new Panel
        {
            Name = "pnlInsideInvoke",
            Left = 6,
            Top = 6,
            Width = 105,
            Height = 20,
        };
        InvokableMiddlePanel.Controls.Add(InvokableChildLabel);
        ParentInvokeButton.Controls.Add(InvokableMiddlePanel);

        ItemsList = new ListBox
        {
            Name = "lstItems",
            AccessibleName = "Items",
            Left = 10,
            Top = 120,
            Width = 200,
            Height = 120,
        };
        for (var i = 0; i < 60; i++)
        {
            ItemsList.Items.Add($"Item {i:D2}");
        }
        ItemsList.SelectedIndex = 0;

        MultilineBox = new TextBox
        {
            Name = "txtMultiline",
            AccessibleName = "Multiline",
            Left = 220,
            Top = 120,
            Width = 250,
            Height = 120,
            Multiline = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = false,
            Text = string.Join(Environment.NewLine, Enumerable.Range(0, 80).Select(i => $"Line {i:D2} of the multiline text box")),
        };

        // Scrollable container: an AutoScroll panel whose content is taller than the viewport,
        // producing a real vertical scrollbar.
        ScrollPanel = new Panel
        {
            Name = "pnlScroll",
            AccessibleName = "ScrollContainer",
            Left = 10,
            Top = 260,
            Width = 460,
            Height = 320,
            AutoScroll = true,
            BorderStyle = BorderStyle.FixedSingle,
        };
        for (var i = 0; i < 40; i++)
        {
            ScrollPanel.Controls.Add(new Button
            {
                Name = $"pnlChild{i:D2}",
                Text = $"Child {i:D2}",
                AccessibleName = $"Child {i:D2}",
                Left = 10,
                Top = 10 + (i * 40),
                Width = 200,
                Height = 30,
            });
        }
        ScrollNestedPanel = new Panel
        {
            Name = "pnlScrollNested",
            Left = 240,
            Top = 900,
            Width = 160,
            Height = 60,
            BorderStyle = BorderStyle.FixedSingle,
        };
        ScrollNestedLabel = new Label
        {
            Name = "lblScrollNested",
            Text = "Nested Scroll Label",
            AccessibleName = "Nested Scroll Label",
            Left = 8,
            Top = 8,
            Width = 130,
            Height = 20,
        };
        ScrollNestedPanel.Controls.Add(ScrollNestedLabel);
        ScrollPanel.Controls.Add(ScrollNestedPanel);

        form.Controls.Add(InvokeButton);
        form.Controls.Add(ResultBox);
        form.Controls.Add(ValueBox);
        form.Controls.Add(ToggleCheck);
        form.Controls.Add(TextLabel);
        form.Controls.Add(ParentInvokeButton);
        form.Controls.Add(ItemsList);
        form.Controls.Add(MultilineBox);
        form.Controls.Add(ScrollPanel);

        BuildExtraControls(form);

        return form;
    }

    // A broad second column of controls exercising more UIA control types and patterns:
    // ComboBox (ExpandCollapse/Selection/Value), RadioButton+GroupBox, checked & indeterminate
    // CheckBoxes (ToggleState on/indeterminate), ProgressBar, TrackBar (Slider), TabControl,
    // MenuStrip/MenuItem, TreeView/TreeItem, NumericUpDown (Spinner), LinkLabel, PictureBox, and a
    // horizontally-scrollable Panel. UIA enumerates these regardless of on-screen visibility.
    private void BuildExtraControls(Form form)
    {
        const int col = 500;

        SelectCombo = new ComboBox
        {
            Name = "cboSelect",
            AccessibleName = "Select One",
            DropDownStyle = ComboBoxStyle.DropDownList,
            Left = col,
            Top = 10,
            Width = 200,
        };
        SelectCombo.Items.AddRange(new object[] { "Alpha", "Beta", "Gamma" });
        SelectCombo.SelectedIndex = 1;

        OptionGroup = new GroupBox
        {
            Name = "grpOptions",
            AccessibleName = "Options Group",
            Left = col,
            Top = 44,
            Width = 200,
            Height = 70,
        };
        OptionRadio = new RadioButton
        {
            Name = "rdoOption",
            Text = "Option A",
            AccessibleName = "Option A",
            Left = 10,
            Top = 20,
            Width = 160,
            Checked = true,
        };
        OptionGroup.Controls.Add(OptionRadio);

        CheckedBox = new CheckBox
        {
            Name = "chkChecked",
            Text = "Already Checked",
            AccessibleName = "Already Checked",
            Left = col,
            Top = 120,
            Width = 200,
            Checked = true,
        };

        TriCheck = new CheckBox
        {
            Name = "chkTri",
            Text = "Tri State",
            AccessibleName = "Tri State",
            Left = col,
            Top = 146,
            Width = 200,
            ThreeState = true,
            CheckState = CheckState.Indeterminate,
        };

        Progress = new ProgressBar
        {
            Name = "prgValue",
            AccessibleName = "Progress",
            Left = col,
            Top = 176,
            Width = 200,
            Minimum = 0,
            Maximum = 100,
            Value = 50,
        };

        Slider = new TrackBar
        {
            Name = "trkSlider",
            AccessibleName = "Slider",
            Left = col,
            Top = 206,
            Width = 200,
            Minimum = 0,
            Maximum = 10,
            Value = 3,
        };

        Tabs = new TabControl
        {
            Name = "tabMain",
            AccessibleName = "Tabs",
            Left = col,
            Top = 256,
            Width = 200,
            Height = 80,
        };
        Tabs.TabPages.Add(new TabPage { Name = "tabOne", Text = "One" });
        Tabs.TabPages.Add(new TabPage { Name = "tabTwo", Text = "Two" });

        Tree = new TreeView
        {
            Name = "treeView",
            AccessibleName = "Tree",
            Left = col,
            Top = 342,
            Width = 200,
            Height = 80,
        };
        var root = new TreeNode("Root");
        root.Nodes.Add(new TreeNode("Leaf"));
        Tree.Nodes.Add(root);
        Tree.ExpandAll();

        Spinner = new NumericUpDown
        {
            Name = "numSpin",
            AccessibleName = "Spinner",
            Left = col,
            Top = 428,
            Width = 200,
            Minimum = 0,
            Maximum = 100,
            Value = 7,
        };

        // HScrollBar maps to a UIA ScrollBar exposing RangeValuePattern whose SetValue updates the
        // control's Value immediately — the deterministic target for the RangeValue set path.
        RangeBar = new HScrollBar
        {
            Name = "scrRange",
            AccessibleName = "Range Bar",
            Left = col,
            Top = 458,
            Width = 200,
            Minimum = 0,
            Maximum = 100,
            LargeChange = 1,
            Value = 10,
        };

        Link = new LinkLabel
        {
            Name = "lnkGo",
            Text = "Open Link",
            AccessibleName = "Open Link",
            Left = col,
            Top = 458,
            Width = 200,
        };

        Picture = new PictureBox
        {
            Name = "picBox",
            AccessibleName = "Picture",
            Left = col,
            Top = 484,
            Width = 200,
            Height = 40,
            BorderStyle = BorderStyle.FixedSingle,
        };

        // Horizontally-scrollable container: children extend well beyond the viewport width.
        HScrollPanel = new Panel
        {
            Name = "pnlHScroll",
            AccessibleName = "HScrollContainer",
            Left = col,
            Top = 530,
            Width = 200,
            Height = 90,
            AutoScroll = true,
            BorderStyle = BorderStyle.FixedSingle,
        };
        for (var i = 0; i < 20; i++)
        {
            HScrollPanel.Controls.Add(new Button
            {
                Name = $"hchild{i:D2}",
                Text = $"H{i:D2}",
                AccessibleName = $"H Child {i:D2}",
                Left = 10 + (i * 120),
                Top = 10,
                Width = 100,
                Height = 30,
            });
        }

        NamelessPanel = new Panel
        {
            Left = col + 220,
            Top = 10,
            Width = 40,
            Height = 40,
            BorderStyle = BorderStyle.FixedSingle,
        };

        Menu = new MenuStrip { Name = "menuMain", AccessibleName = "Main Menu" };
        // AccessibleName (not Name) is what UIA exposes, so an external winapp.exe can select this item.
        FileMenu = new ToolStripMenuItem("File") { Name = "menuFile", AccessibleName = "File Menu" };
        FileMenu.DropDownItems.Add(new ToolStripMenuItem("Open") { Name = "menuOpen", AccessibleName = "Open Item" });
        Menu.Items.Add(FileMenu);

        DupButton = new Button
        {
            Name = "btnShared",
            Text = "Shared",
            AccessibleName = "Shared Widget",
            Left = col,
            Top = 626,
            Width = 90,
            Height = 30,
        };
        DupLabel = new Label
        {
            Name = "lblShared",
            Text = "Shared",
            AccessibleName = "Shared Widget",
            Left = col + 100,
            Top = 632,
            Width = 90,
        };

        form.Controls.Add(SelectCombo);
        form.Controls.Add(OptionGroup);
        form.Controls.Add(CheckedBox);
        form.Controls.Add(TriCheck);
        form.Controls.Add(Progress);
        form.Controls.Add(Slider);
        form.Controls.Add(Tabs);
        form.Controls.Add(Tree);
        form.Controls.Add(Spinner);
        form.Controls.Add(Link);
        form.Controls.Add(Picture);
        form.Controls.Add(RangeBar);
        form.Controls.Add(HScrollPanel);
        form.Controls.Add(NamelessPanel);
        form.Controls.Add(DupButton);
        form.Controls.Add(DupLabel);
        form.Controls.Add(Menu);
        form.MainMenuStrip = Menu;
    }

    /// <summary>
    /// Opens (once) a second top-level Form with its own title and controls, and returns its
    /// native window handle. Used to exercise the popup / other-window search code paths
    /// (GetAllAppWindows, FindElementOnOtherWindows) and the multi-window PID-root resolution.
    /// </summary>
    public (nint Hwnd, string Title) OpenOwnedWindow(string title, bool ownedByMain = false)
    {
        return OnUiThread(() =>
        {
            if (_ownedWindow is null || _ownedWindow.IsDisposed)
            {
                _ownedWindow = new Form
                {
                    Text = title,
                    Name = "ownedForm",
                    Width = 300,
                    Height = 200,
                    StartPosition = FormStartPosition.Manual,
                    Location = new System.Drawing.Point(40, 40),
                    ShowInTaskbar = false,
                };
                if (ownedByMain)
                {
                    _ownedWindow.Owner = _form;
                }
                _ownedWindow.Controls.Add(new Button
                {
                    Name = "btnOwned",
                    Text = "Owned Button",
                    AccessibleName = "Owned Button",
                    Left = 20,
                    Top = 20,
                    Width = 160,
                    Height = 30,
                });
                _ownedWindow.Controls.Add(new Button
                {
                    Name = "btnOwnedShared",
                    Text = "Owned Shared",
                    AccessibleName = "Owned Shared Widget",
                    Left = 20,
                    Top = 60,
                    Width = 120,
                    Height = 30,
                });
                _ownedWindow.Controls.Add(new Label
                {
                    Name = "lblOwnedShared",
                    Text = "Owned Shared",
                    AccessibleName = "Owned Shared Widget",
                    Left = 150,
                    Top = 66,
                    Width = 120,
                });
                _ownedWindow.Controls.Add(new Button
                {
                    Name = "btnOwnedOnly",
                    Text = "Owned Only",
                    AccessibleName = "OwnedOnly",
                    Left = 20,
                    Top = 105,
                    Width = 160,
                    Height = 30,
                });
                _ownedWindow.Show();
            }

            return ((nint)_ownedWindow.Handle, _ownedWindow.Text);
        });
    }

    /// <summary>Runs an action on the fixture's UI thread and waits for it to complete.</summary>
    public void OnUiThread(Action action)
    {
        if (_form.IsHandleCreated && _form.InvokeRequired)
        {
            _form.Invoke(action);
        }
        else
        {
            action();
        }
    }

    /// <summary>Reads a value from the fixture's UI thread.</summary>
    public T OnUiThread<T>(Func<T> func)
    {
        if (_form.IsHandleCreated && _form.InvokeRequired)
        {
            return (T)_form.Invoke(func);
        }
        return func();
    }

    /// <summary>Native window handle of a hosted control (read on the UI thread).</summary>
    public nint HandleOf(Control control) => OnUiThread(() => control.Handle);

    /// <summary>
    /// Opens the File drop-down, producing real transient UI on the desktop, and waits for it to be
    /// shown. Used by the cooperative-turn acceptance tests (issue #764 §18.3).
    /// </summary>
    /// <remarks>
    /// A drop-down only appears on an active window, and under load the activation that
    /// <c>ShowDropDown</c> relies on can be dropped. Forcing the foreground and retrying keeps this
    /// setup step deterministic, so a coordination test never fails for an unrelated reason. Throws
    /// rather than returning quietly: a test that proceeds without its transient UI would assert
    /// nothing meaningful.
    /// </remarks>
    public void OpenFileMenu()
    {
        var deadline = Environment.TickCount64 + 8000;
        while (Environment.TickCount64 < deadline)
        {
            DesktopTestHelpers.ForceForeground(Hwnd);
            OnUiThread(() =>
            {
                Form.Activate();
                Form.BringToFront();
                FileMenu.ShowDropDown();
            });

            // ShowDropDown posts the popup; give the menu window a moment to actually appear so a test
            // never asserts against a drop-down that has been requested but not yet realized.
            var attemptDeadline = Environment.TickCount64 + 1000;
            while (Environment.TickCount64 < attemptDeadline)
            {
                if (IsFileMenuOpen)
                {
                    return;
                }

                Thread.Sleep(25);
            }
        }

        throw new TimeoutException(
            "The File drop-down did not open within 8s, so the fixture never presented the transient UI under test.");
    }

    /// <summary>Whether the File drop-down is currently displayed.</summary>
    public bool IsFileMenuOpen => OnUiThread(() => FileMenu.DropDown.Visible);

    /// <summary>
    /// Closes the File drop-down and leaves menu mode.
    /// </summary>
    /// <remarks>
    /// An open drop-down puts the thread into Windows menu mode, which captures keyboard input for the
    /// whole desktop. Disposing the form without closing it can leave that capture behind and silently
    /// swallow input in whatever runs next, so tests that open the menu must close it.
    /// </remarks>
    public void CloseFileMenu()
    {
        OnUiThread(() =>
        {
            FileMenu.DropDown.Close();
            // Explicitly leave menu mode; closing only the drop-down can leave the strip focused.
            Menu.Items.OfType<ToolStripMenuItem>().ToList().ForEach(item => item.DropDown.Close());
            Form.Focus();
        });
    }

    /// <summary>Screen-space center point of a hosted control (read on the UI thread).</summary>
    public (int X, int Y) ScreenCenterOf(Control control) => OnUiThread(() =>
    {
        var rect = control.RectangleToScreen(control.ClientRectangle);
        return (rect.Left + (rect.Width / 2), rect.Top + (rect.Height / 2));
    });

    public void Dispose()
    {
        try
        {
            if (_form is not null && _form.IsHandleCreated)
            {
                _form.Invoke(() =>
                {
                    if (_ownedWindow is not null && !_ownedWindow.IsDisposed)
                    {
                        _ownedWindow.Close();
                        _ownedWindow.Dispose();
                    }
                    _form.Close();
                    _form.Dispose();
                });
            }
        }
        catch
        {
            // Best-effort teardown; the STA thread is a background thread and will exit with the process.
        }

        _thread.Join(TimeSpan.FromSeconds(5));

        // Idempotent final dispose (the message loop already disposes the form on exit) so the
        // IDisposable field is unambiguously released.
        _form?.Dispose();
        _ready.Dispose();
    }
}
