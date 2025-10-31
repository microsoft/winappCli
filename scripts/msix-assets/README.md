# Windows Development CLI MSIX Installation

This package contains a pre-release development build of the Windows Development CLI MSIX bundle. 

> **Note:** The MSIX bundle is signed with a dev certificate. This is temporary until we can sign with a proper certificate. Installing via the `install.cmd` script will install the dev certificate on your machine.

## Quick Installation

1. Double-click `install.cmd`
2. When prompted, allow elevation to Administrator
3. Done! 

> **Note:** When downloading scripts from the internet, Windows blocks execution until they are unblocked. The `instal.cmd` should automatically unblock downloaded files. However, if that fails, you will need to right click on each file -> click Properties -> check Unblock -> click OK.

## What's Included

- **winapp_[version].msixbundle** - The MSIX bundle with x64 and ARM64 packages
- **install.cmd** - Double-click installer (easiest method)
- **install.ps1** - PowerShell installer script (alternative method)

## Version Information

- Version: [version]
- Architectures: x64, ARM64

## Troubleshooting

### "Cannot be loaded because running scripts is disabled"
If you see a script execution error, Right-click `install.ps1` → Properties → Check "Unblock" → OK

### "Windows cannot install this package"
- Make sure you ran `install.ps1` with administrator privileges
- The certificate must be in the Trusted People store

### "This app package is not signed with a trusted certificate"
- Run `install.ps1` with administrator privileges
- Verify the certificate was installed to LocalMachine\TrustedPeople

## Support

For more information, visit: https://github.com/microsoft/WindowsDevCLI
