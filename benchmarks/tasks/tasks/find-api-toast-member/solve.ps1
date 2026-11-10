# Reference solution: the real members are ToastNotification.ExpirationTime and ToastNotifier.Show.
param([string]$Workspace)
$ErrorActionPreference = 'Stop'
Set-Location $Workspace
$file = Join-Path $Workspace 'Notifier.cs'
(Get-Content -Raw $file).Replace('toast.ExpiresAt =', 'toast.ExpirationTime =').Replace('.Display(toast)', '.Show(toast)') |
    Set-Content $file -NoNewline -Encoding utf8NoBOM
dotnet build -nologo 2>&1 | Write-Host
