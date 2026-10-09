// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

public sealed partial class UiaTestFixture
{
    /// <summary>AutomationId of the item inside the simulated windowed popup.</summary>
    public const string WindowedPopupItemId = "popupItem";

    /// <summary>AutomationId of the item drawn inside the main window's simulated island.</summary>
    public const string IslandItemId = "islandItem";

    private IslandHostControl? _island;
    private TestFragment? _popupContent;
    private readonly List<WindowedPopupForm> _popupWindows = [];

    /// <summary>
    /// Opens a window shaped like a XAML windowed popup (<c>Xaml_WindowedPopupClass</c>) and returns
    /// its handle. Each call opens another popup window exposing the same content.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The popup's pixels are drawn in an owned top-level window filled with
    /// <paramref name="color"/>, but the UIA parent of its content is an island hosted by a child
    /// window of the main form. The popup window's own UIA root lists that content as its child, so
    /// UIA finds the item under the main window while its ancestors lead back to the island.
    /// </para>
    /// <para>
    /// The island also exposes <see cref="IslandItemId"/>, an element drawn in the main window.
    /// </para>
    /// </remarks>
    public nint OpenWindowedPopup(System.Drawing.Color color) => OnUiThread(() =>
    {
        if (_island is null)
        {
            _island = new IslandHostControl
            {
                Name = "islandHost",
                Left = 500,
                Top = 300,
                Width = 300,
                Height = 120,
            };
            _form.Controls.Add(_island);
            _island.BringToFront();

            var islandRect = ScreenRect(_island);
            var islandRoot = new TestFragmentRoot(_island.Handle, islandRect);
            var islandItem = new TestFragment(
                islandRoot, islandRoot, 1, IslandItemId, "Island Item", controlType: 50000, // Button
                new TestUiaRect { Left = islandRect.Left, Top = islandRect.Top, Width = 120, Height = 30 });
            islandRoot.Children.Add(islandItem);

            var popupBounds = new TestUiaRect { Left = islandRect.Left + 20, Top = islandRect.Top + 20, Width = 160, Height = 60 };
            _popupContent = new TestFragment(
                islandRoot, islandRoot, 2, automationId: null, "Popup", controlType: 50032, popupBounds); // Window
            _popupContent.Children.Add(new TestFragment(
                _popupContent, islandRoot, 3, WindowedPopupItemId, "Popup Item", controlType: 50011, popupBounds)); // MenuItem
            _island.Root = islandRoot;
        }

        var bounds = _popupContent!.BoundingRectangle;
        var popup = new WindowedPopupForm
        {
            Text = string.Empty,
            BackColor = color,
            FormBorderStyle = FormBorderStyle.None,
            ShowInTaskbar = false,
            StartPosition = FormStartPosition.Manual,
            Location = new System.Drawing.Point((int)bounds.Left, (int)bounds.Top),
            Size = new System.Drawing.Size((int)bounds.Width, (int)bounds.Height),
            Owner = _form,
        };
        popup.Root = new TestFragmentRoot(popup.Handle, bounds);
        popup.Root.Children.Add(_popupContent);
        popup.Show();
        _popupWindows.Add(popup);
        return (nint)popup.Handle;
    });

    private static TestUiaRect ScreenRect(Control control)
    {
        var rect = control.RectangleToScreen(control.ClientRectangle);
        return new TestUiaRect { Left = rect.Left, Top = rect.Top, Width = rect.Width, Height = rect.Height };
    }

    private void CloseWindowedPopups()
    {
        foreach (var popup in _popupWindows)
        {
            popup.Close();
            popup.Dispose();
        }

        _popupWindows.Clear();
    }

    private const int WmGetObject = 0x003D;
    private const int UiaRootObjectId = -25;

    private sealed class IslandHostControl : Control
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public TestFragmentRoot? Root { get; set; }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmGetObject && unchecked((int)m.LParam) == UiaRootObjectId && Root is not null)
            {
                m.Result = TextProviderNative.UiaReturnRawElementProvider(Handle, m.WParam, m.LParam, Root);
                return;
            }

            base.WndProc(ref m);
        }
    }

    private sealed class WindowedPopupForm : Form
    {
        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public TestFragmentRoot? Root { get; set; }

        protected override bool ShowWithoutActivation => true;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WmGetObject && unchecked((int)m.LParam) == UiaRootObjectId && Root is not null)
            {
                m.Result = TextProviderNative.UiaReturnRawElementProvider(Handle, m.WParam, m.LParam, Root);
                return;
            }

            base.WndProc(ref m);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TestUiaRect
    {
        public double Left;
        public double Top;
        public double Width;
        public double Height;
    }

    [ComVisible(true), Guid("F7063DA8-8359-439C-9297-BBC5299A7D87"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITestRawElementProviderFragment
    {
        ITestRawElementProviderFragment? Navigate(int direction);
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_I4)]
        int[]? GetRuntimeId();
        TestUiaRect BoundingRectangle { get; }
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)]
        ITestRawElementProviderFragmentRoot[]? GetEmbeddedFragmentRoots();
        void SetFocus();
        ITestRawElementProviderFragmentRoot FragmentRoot { get; }
    }

    [ComVisible(true), Guid("620CE2A5-AB8F-40A9-86CB-DE3C75599B58"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITestRawElementProviderFragmentRoot
    {
        ITestRawElementProviderFragment? ElementProviderFromPoint(double x, double y);
        ITestRawElementProviderFragment? GetFocus();
    }

    /// <summary>
    /// A windowless UIA element. Its parent and fragment root are fixed when it is created, so it
    /// reports the same ancestors and RuntimeId however UIA reached it, as XAML content does.
    /// </summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public class TestFragment : ITestRawElementProviderSimple, ITestRawElementProviderFragment
    {
        private const int NavigateParent = 0;
        private const int NavigateNextSibling = 1;
        private const int NavigatePreviousSibling = 2;
        private const int NavigateFirstChild = 3;
        private const int NavigateLastChild = 4;

        private readonly TestFragment? _parent;
        private readonly TestFragmentRoot? _root;
        private readonly int _id;
        private readonly string? _automationId;
        private readonly string? _name;
        private readonly int _controlType;
        private readonly TestUiaRect _bounds;

        internal TestFragment(
            TestFragment? parent, TestFragmentRoot? root, int id, string? automationId, string? name, int controlType, TestUiaRect bounds)
        {
            _parent = parent;
            _root = root;
            _id = id;
            _automationId = automationId;
            _name = name;
            _controlType = controlType;
            _bounds = bounds;
        }

        internal List<TestFragment> Children { get; } = [];

        public virtual int ProviderOptions => 2; // ProviderOptions_ServerSideProvider
        public object? GetPatternProvider(int patternId) => null;
        public virtual object? GetPropertyValue(int propertyId) => propertyId switch
        {
            30003 => _controlType,
            30005 => _name,
            30010 => true, // IsEnabled
            30011 => _automationId,
            30016 or 30017 => true, // IsControlElement / IsContentElement
            30022 => false, // IsOffscreen
            _ => null,
        };

        public virtual ITestRawElementProviderSimple? HostRawElementProvider => null;

        public ITestRawElementProviderFragment? Navigate(int direction)
        {
            switch (direction)
            {
                case NavigateParent:
                    return _parent;
                case NavigateFirstChild:
                    return Children.Count > 0 ? Children[0] : null;
                case NavigateLastChild:
                    return Children.Count > 0 ? Children[^1] : null;
                case NavigateNextSibling or NavigatePreviousSibling when _parent is not null:
                    // Popup content is not one of its parent's children, so it has no siblings.
                    var siblings = _parent.Children;
                    var position = siblings.IndexOf(this);
                    if (position < 0)
                    {
                        return null;
                    }

                    var index = position + (direction == NavigateNextSibling ? 1 : -1);
                    return index >= 0 && index < siblings.Count ? siblings[index] : null;
                default:
                    return null;
            }
        }

        // Absolute RuntimeIds in the shape XAML reports: [42, island HWND, 4, element].
        public virtual int[]? GetRuntimeId() => [42, (int)_root!.Hwnd, 4, _id];
        public TestUiaRect BoundingRectangle => _bounds;
        public ITestRawElementProviderFragmentRoot[]? GetEmbeddedFragmentRoots() => null;
        public void SetFocus()
        {
        }

        public virtual ITestRawElementProviderFragmentRoot FragmentRoot => _root!;
    }

    /// <summary>The UIA root a window returns for itself; the host HWND supplies its identity.</summary>
    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class TestFragmentRoot : TestFragment, ITestRawElementProviderFragmentRoot
    {
        internal TestFragmentRoot(nint hwnd, TestUiaRect bounds)
            : base(null, null, 0, null, null, 50033, bounds) // Pane
        {
            Hwnd = hwnd;
        }

        internal nint Hwnd { get; }

        public override object? GetPropertyValue(int propertyId) => null;

        public override ITestRawElementProviderSimple? HostRawElementProvider
        {
            get
            {
                Marshal.ThrowExceptionForHR(TextProviderNative.UiaHostProviderFromHwnd(Hwnd, out var host));
                return host;
            }
        }

        public override int[]? GetRuntimeId() => null;
        public override ITestRawElementProviderFragmentRoot FragmentRoot => this;
        public ITestRawElementProviderFragment? ElementProviderFromPoint(double x, double y) => null;
        public ITestRawElementProviderFragment? GetFocus() => null;
    }
}
