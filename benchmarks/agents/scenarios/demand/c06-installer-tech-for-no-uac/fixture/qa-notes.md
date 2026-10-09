Windows 11 23H2 smoke test for `Northstar Shell-3.8.0.msi`:

1. Double-clicking the MSI immediately shows a UAC consent dialog.
2. Choosing "Just for me" in the later installer UI still arrived after elevation.
3. With `msiWrapped.impersonate: true`, the "All users" button shows a shield and then Windows shows:

```
This file does not have an app associated with it for performing this action.
Please install an app or, if one is already installed, create an association in the Default Apps Settings page.
Title: C:\Windows\Installer\MSI9D3.tmp
```

The plain NSIS `.exe` starts without an immediate UAC prompt for a current-user install.
