# 🧪 Windows Identity App Usage

This is an **experimental** app/sample (GUI) that wraps the CLI and provides an intuitive, drag-and-drop experience with the following features:

- Supports .NET (Winforms, WPF..etc) apps, Python scripts/folders, MSIX
- Drop in a WinForms, WPF executable (.exe) to add development/debug app identity (via external location/sparse packaging) in a single click!
- Drop in a WinForms, WPF folder to package your app (MSIX) in a single click
- Drop in an MSIX to sign and register it locally in a single click
- Drop in a Python (.py) file to add debug identity in a single click

We would love your feedback on this UI-based approach and whether it adds value or doesn't fit with your development workflows.

<div align="center">
  <table>
    <tr>
      <td width="50%">
        <img src="./images/identity-gui-tool.png" alt="Windows Identity Tool Interface" width="100%" />
      </td>
      <td width="50%">
        <img src="./images/identity-gui-tool-options.png" alt="Windows Identity Tool Options" width="100%" />
      </td>
    </tr>
  </table>
</div>

## Install the GUI Tool

There are 2 ways to run the GUI (experimental). The Windows Development CLI **must** be in your [PATH](#adding-to-path) for the Identity Tool to function since it calls the CLI.

### Build the repository

Clone and build this repository. Run winapp.cli in Visual Studio to build and run the app.

### Download the MSIX (build pipeline in progress)

1. **[👉 Download Latest Experimental Build (unsigned .msix)](https://github.com/microsoft/WinAppCli/releases/tag/v0.1.1-gui)**
2. Run Powershell as **Administrator** and `Add-AppPackage -Path <msix> -AllowUnsigned`

`<msix>` should be replaced with the full path of the downloaded build (msix file).


## Usage

### .NET apps (WPF, WinForms)

- Drop in an .exe from your binaries folder to add debug identity to it. The app will find the .csproj for the .exe (ie. if your .exe is in the /bin folder, the app will find the parent .csproj and create Assets and appxmanifest in that location). The .exe will be granted app identity via external location (sparse) packaging.
- Drop in a folder, and that app will be packaged into an MSIX

### Python

- This is currently a feature we are experimenting with. Currently, python files/scripts (.py) are supported or entire folders for packaging

### MSIX

- Drop in an MSIX and a cert will be created, installed and registered locally

## Feedback for this Experimental App

Please note that this app is experimental and may have issues as we gather feedback on the functionality, usefulness and value of the UI-based solution. If you see value or issues in this app, please let us know:

- [File an issue](https://github.com/microsoft/WinAppCli/issues): please ensure that you are not filing a duplicate issue or bug
- Send any feedback to <windowsdevelopertoolkit@microsoft.com>: Do you love this tool? Are there features or fixes you want to see? Let us know!

The app will add functionality for Electron and mirror the CLI going forward depending on user feedback.
