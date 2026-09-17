// Copyright (c) Microsoft Corporation and Contributors. All rights reserved.
// Licensed under the MIT License.

using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Microsoft.Windows.SDK.BuildTools.WinApp.UIAutomation.TestSupport;

public sealed partial class UiaTestFixture
{
    /// <summary>
    /// Hosts a real HWND-based UIA provider whose document has no supported formatting attributes.
    /// Windows receives this provider via WM_GETOBJECT, not a service seam or a client COM proxy.
    /// </summary>
    public ReservedTextControl AddUnsupportedTextDocument() => OnUiThread(() =>
    {
        var control = new ReservedTextControl
        {
            Name = "unsupportedTextDocument",
            Text = "Document without formatting metadata",
            Left = 20,
            Top = 150,
            Width = 420,
            Height = 80,
        };
        _form.Controls.Add(control);
        control.BringToFront();
        return control;
    });

    public sealed class ReservedTextControl : Control
    {
        private ReservedTextProvider? _provider;

        public int AttributeReadCount => _provider?.AttributeReadCount ?? 0;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x003D && unchecked((int)m.LParam) == -25) // WM_GETOBJECT, UiaRootObjectId
            {
                _provider ??= new ReservedTextProvider(Handle);
                m.Result = TextProviderNative.UiaReturnRawElementProvider(Handle, m.WParam, m.LParam, _provider);
                return;
            }
            base.WndProc(ref m);
        }
    }

    // These are the public COM ABI contracts from UIAutomationCore.h. Keeping the small test-only
    // provider here avoids adding a WPF dependency or changing the product's generated interop.
    [ComVisible(true), Guid("D6DD68D1-86FD-4332-8666-9ABEDEA2D24C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITestRawElementProviderSimple
    {
        int ProviderOptions { get; }
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object? GetPatternProvider(int patternId);
        [return: MarshalAs(UnmanagedType.Struct)]
        object? GetPropertyValue(int propertyId);
        ITestRawElementProviderSimple? HostRawElementProvider { get; }
    }

    [ComVisible(true), Guid("3589C92C-63F3-4367-99BB-ADA653B77CF2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITestTextProvider
    {
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)]
        ITestTextRangeProvider[] GetSelection();
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)]
        ITestTextRangeProvider[] GetVisibleRanges();
        ITestTextRangeProvider RangeFromChild(ITestRawElementProviderSimple child);
        ITestTextRangeProvider RangeFromPoint(TextProviderPoint point);
        ITestTextRangeProvider DocumentRange { get; }
        int SupportedTextSelection { get; }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct TextProviderPoint
    {
        public double X;
        public double Y;
    }

    [ComVisible(true), Guid("5347AD7B-C355-46F8-AFF5-909033582F63"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface ITestTextRangeProvider
    {
        ITestTextRangeProvider Clone();
        [return: MarshalAs(UnmanagedType.Bool)]
        bool Compare(ITestTextRangeProvider range);
        int CompareEndpoints(int endpoint, ITestTextRangeProvider targetRange, int targetEndpoint);
        void ExpandToEnclosingUnit(int unit);
        ITestTextRangeProvider? FindAttribute(int attributeId, [MarshalAs(UnmanagedType.Struct)] object value, [MarshalAs(UnmanagedType.Bool)] bool backward);
        ITestTextRangeProvider? FindText([MarshalAs(UnmanagedType.BStr)] string text, [MarshalAs(UnmanagedType.Bool)] bool backward, [MarshalAs(UnmanagedType.Bool)] bool ignoreCase);
        [return: MarshalAs(UnmanagedType.Struct)]
        object GetAttributeValue(int attributeId);
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_R8)]
        double[] GetBoundingRectangles();
        ITestRawElementProviderSimple GetEnclosingElement();
        [return: MarshalAs(UnmanagedType.BStr)]
        string GetText(int maxLength);
        int Move(int unit, int count);
        int MoveEndpointByUnit(int endpoint, int unit, int count);
        void MoveEndpointByRange(int endpoint, ITestTextRangeProvider targetRange, int targetEndpoint);
        [System.Diagnostics.CodeAnalysis.SuppressMessage("Naming", "CA1716:Identifiers should not match keywords",
            Justification = "Matches the native ITextRangeProvider COM contract.")]
        void Select();
        void AddToSelection();
        void RemoveFromSelection();
        void ScrollIntoView([MarshalAs(UnmanagedType.Bool)] bool alignToTop);
        [return: MarshalAs(UnmanagedType.SafeArray, SafeArraySubType = VarEnum.VT_UNKNOWN)]
        ITestRawElementProviderSimple[] GetChildren();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None)]
    public sealed class ReservedTextProvider : ITestRawElementProviderSimple, ITestTextProvider, ITestTextRangeProvider
    {
        private readonly nint _hwnd;
        private readonly object _notSupported;
        private int _attributeReadCount;
        private const string DocumentText = "Document without formatting metadata";

        public ReservedTextProvider(nint hwnd)
        {
            _hwnd = hwnd;
            Marshal.ThrowExceptionForHR(TextProviderNative.UiaGetReservedNotSupportedValue(out _notSupported));
        }

        public int AttributeReadCount => Volatile.Read(ref _attributeReadCount);
        public int ProviderOptions => 2; // ProviderOptions_ServerSideProvider
        public object? GetPatternProvider(int patternId) => patternId == 10014 ? this : null; // TextPattern
        public object? GetPropertyValue(int propertyId) => propertyId switch
        {
            30003 => 50030, // ControlType: Document
            30005 => DocumentText, // Name
            30009 => false, // IsKeyboardFocusable
            30010 => true, // IsEnabled
            30011 => "unsupportedTextDocument", // AutomationId
            30012 => "ReservedTextAttributeProvider", // ClassName
            30016 or 30017 => true, // IsControlElement / IsContentElement
            _ => null, // Let the HWND host provider supply other properties.
        };
        public ITestRawElementProviderSimple? HostRawElementProvider
        {
            get
            {
                Marshal.ThrowExceptionForHR(TextProviderNative.UiaHostProviderFromHwnd(_hwnd, out var host));
                return host;
            }
        }

        public ITestTextRangeProvider[] GetSelection() => [];
        public ITestTextRangeProvider[] GetVisibleRanges() => [this];
        public ITestTextRangeProvider RangeFromChild(ITestRawElementProviderSimple child) => throw new ArgumentException("Document has no children.", nameof(child));
        public ITestTextRangeProvider RangeFromPoint(TextProviderPoint point) => this;
        public ITestTextRangeProvider DocumentRange => this;
        public int SupportedTextSelection => 0; // SupportedTextSelection_None
        public ITestTextRangeProvider Clone() => this;
        public bool Compare(ITestTextRangeProvider range) => ReferenceEquals(this, range);
        public int CompareEndpoints(int endpoint, ITestTextRangeProvider targetRange, int targetEndpoint) => throw new NotSupportedException();
        public void ExpandToEnclosingUnit(int unit) => throw new NotSupportedException();
        public ITestTextRangeProvider? FindAttribute(int attributeId, object value, bool backward) => null;
        public ITestTextRangeProvider? FindText(string text, bool backward, bool ignoreCase) => throw new NotSupportedException();
        public object GetAttributeValue(int attributeId)
        {
            Interlocked.Increment(ref _attributeReadCount);
            // The actual reserved IUnknown from UIAutomationCore.dll, never a string or stand-in.
            return _notSupported;
        }
        public double[] GetBoundingRectangles() => [];
        public ITestRawElementProviderSimple GetEnclosingElement() => this;
        public string GetText(int maxLength) => maxLength < 0 ? DocumentText : DocumentText[..Math.Min(maxLength, DocumentText.Length)];
        public int Move(int unit, int count) => throw new NotSupportedException();
        public int MoveEndpointByUnit(int endpoint, int unit, int count) => throw new NotSupportedException();
        public void MoveEndpointByRange(int endpoint, ITestTextRangeProvider targetRange, int targetEndpoint) => throw new NotSupportedException();
        public void Select() => throw new NotSupportedException();
        public void AddToSelection() => throw new NotSupportedException();
        public void RemoveFromSelection() => throw new NotSupportedException();
        public void ScrollIntoView(bool alignToTop) => throw new NotSupportedException();
        public ITestRawElementProviderSimple[] GetChildren() => [];
    }

    private static class TextProviderNative
    {
        [DllImport("UIAutomationCore.dll", ExactSpelling = true)]
        internal static extern nint UiaReturnRawElementProvider(nint hwnd, nint wParam, nint lParam,
            [MarshalAs(UnmanagedType.Interface)] ITestRawElementProviderSimple provider);

        [DllImport("UIAutomationCore.dll", ExactSpelling = true)]
        internal static extern int UiaHostProviderFromHwnd(nint hwnd,
            [MarshalAs(UnmanagedType.Interface)] out ITestRawElementProviderSimple provider);

        [DllImport("UIAutomationCore.dll", ExactSpelling = true)]
        internal static extern int UiaGetReservedNotSupportedValue([MarshalAs(UnmanagedType.IUnknown)] out object value);
    }
}
