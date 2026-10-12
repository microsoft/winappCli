param([string]$Folder)
Get-ChildItem $Folder -Recurse | Remove-Item -Force
