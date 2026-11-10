# Reference solution: Window.SystemBackdrop = new MicaBackdrop() and Window.ExtendsContentIntoTitleBar = true.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
@'
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace PhotoNotes;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        Notes.ItemsSource = new[] { "Beach, July", "Kitchen remodel", "Garden progress" };
    }
}
'@ | Set-Content (Join-Path $Workspace 'MainWindow.xaml.cs') -Encoding utf8NoBOM
dotnet build -nologo 2>&1 | Write-Host
