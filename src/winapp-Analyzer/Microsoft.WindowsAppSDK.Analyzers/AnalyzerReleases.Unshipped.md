; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
WUI0001 | WinUI.Compatibility | Warning | UWP XAML namespace used
WUI0002 | WinUI.Compatibility | Warning | Window.Current not supported in WinUI 3
WUI0003 | WinUI.Compatibility | Warning | CoreDispatcher replaced by DispatcherQueue
WUI0004 | WinUI.Compatibility | Warning | GetForCurrentView not supported in WinUI 3
WUI1001 | WinUI.Migration | Warning | UWP API has a Windows App SDK equivalent
WUI1002 | WinUI.Migration | Warning | UWP API has no Windows App SDK equivalent
WUI1010 | WinUI.Migration | Info | Migration feature-area hint
WUI2001 | WinUI.Runtime | Warning | TabView raw content pitfall
WUI2002 | WinUI.Runtime | Warning | TabView raw content pitfall (cross-file XAML)
WUI2003 | WinUI.Compatibility | Warning | UWP-only XAML control with no WinUI 3 equivalent
WUI2010 | WinUI.Runtime | Warning | Nested x:Bind without fallback
WUI2011 | WinUI.Runtime | Warning | x:Bind missing Mode
WUI2012 | WinUI.Runtime | Warning | Null converter usage
WUI2020 | WinUI.Runtime | Info | Missing AutomationId
WUI2030 | WinUI.Runtime | Warning | Attached-property initializer syntax
WUI3001 | WinUI.Mvvm | Warning | Legacy MVVM syntax
WUI4001 | WinUI.Interop | Warning | WebView2 used before EnsureCoreWebView2Async
WUI4002 | WinUI.Interop | Warning | WebView2 used before init (cross-file XAML)
WUI4101 | WinUI.Interop | Warning | ONNX Runtime GenAI SetInputSequences API change
WUI4102 | WinUI.Interop | Warning | ONNX Runtime GenAI ComputeLogits API change
WUI4103 | WinUI.Interop | Warning | ONNX Runtime GenAI TokenizerStream constructor change
