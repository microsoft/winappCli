Get-AppxPackage |
    Select-Object Name, PackageFullName |
    Export-Csv -Path .\lichen-apps.csv -NoTypeInformation
