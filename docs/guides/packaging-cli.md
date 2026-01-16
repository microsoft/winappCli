# Packaging a CLI Executable as MSIX

This guide walks you through packaging an existing command-line executable as an MSIX package for distribution via Windows Package Manager (winget), the Microsoft Store, or direct distribution.

## Prerequisites

- An existing CLI executable (`.exe`) that you want to package
- Windows 10 version 1809 or later


## Steps

### 1. Organize Your CLI Application

Place your CLI executable and any dependencies in a dedicated folder. This folder will contain all files that should be included in your MSIX package.

```powershell
mkdir MyCliPackage
cd MyCliPackage
# Copy your CLI executable and dependencies here
```

### 2. Install winapp CLI

The quickest way to get started is to install winapp CLI via Windows Package Manager:

```powershell
winget install microsoft.winappcli --source winget
```

### 3. Generate the appxmanifest.xml

Generate a base appxmanifest.xml and required assets for your CLI executable:

```powershell
winapp manifest generate --executable .\yourcli.exe
```

This command creates an `appxmanifest.xml` file in the current directory with default values populated from your executable.

### 4. Configure the Manifest

You'll need to edit the generated `appxmanifest.xml` to:
- Add an execution alias so users can run your CLI from any directory
- Hide the app from the Start menu app list
- Update application details to match your CLI

#### 4.1 Add Required Namespace

Add the `uap5` namespace to the `Package` element if it's not already present:

```xml
<Package
  xmlns="http://schemas.microsoft.com/appx/manifest/foundation/windows10"
  ...
  xmlns:uap="http://schemas.microsoft.com/appx/manifest/uap/windows10"
  xmlns:uap5="http://schemas.microsoft.com/appx/manifest/uap/windows10/5"
  xmlns:rescap="http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities"
  IgnorableNamespaces="uap uap5 rescap">
```

#### 4.2 Configure the Application Element

In the `<uap:VisualElements>` element, add `AppListEntry="none"` to prevent the app from appearing in the Start menu:

```xml
<uap:VisualElements
    DisplayName="YourApp"
    Description="My Application"
    BackgroundColor="transparent"
    Square150x150Logo="Assets\Square150x150Logo.png"
    Square44x44Logo="Assets\Square44x44Logo.png"
    AppListEntry="none">
</uap:VisualElements>
```

#### 4.3 Add Execution Alias Extension

Add the execution alias extension within the `<Application>` element (after `<uap:VisualElements>`):

```xml
<Extensions>
  <uap5:Extension Category="windows.appExecutionAlias">
    <uap5:AppExecutionAlias>
      <uap5:ExecutionAlias Alias="yourcli.exe" />
    </uap5:AppExecutionAlias>
  </uap5:Extension>
</Extensions>
```

Replace `yourcli.exe` with the desired command name for your CLI. Once a user installs the MSIX, they will be able to invoke your CLI with this command.

#### 4.4 Update Application Metadata

Update the following fields to match your CLI application:

- **Identity**: Update `Name`, `Publisher`, and `Version`
  ```xml
  <Identity
    Name="YourCompany.YourCLI"
    Publisher="CN=Your Company"
    Version="1.0.0.0" />
  ```

- **Properties**: Update display name, publisher display name, and description
  ```xml
  <Properties>
    <DisplayName>Your CLI Tool</DisplayName>
    <PublisherDisplayName>Your Company</PublisherDisplayName>
    <Description>Description of your CLI tool</Description>
    <Logo>Assets\StoreLogo.png</Logo>
  </Properties>
  ```

- **VisualElements**: Update display name and asset references
  ```xml
  <uap:VisualElements
    DisplayName="Your CLI Tool"
    Description="Description of your CLI tool"
    BackgroundColor="transparent"
    Square150x150Logo="Assets\Square150x150Logo.png"
    Square44x44Logo="Assets\Square44x44Logo.png">
    <uap:DefaultTile Wide310x150Logo="Assets\Wide310x150Logo.png" />
    <uap:SplashScreen Image="Assets\SplashScreen.png" />
  </uap:VisualElements>
  ```

**Note**: You should also add proper icon assets to an `Assets` folder in your package directory. While the app won't appear in the Start menu, icons are still required for Store submission and may appear in other contexts.

### 5. (Optional) Generate a Development Certificate

For local testing and distribution outside the Microsoft Store, you'll need to sign your MSIX package with a certificate.

Generate a development certificate:

```powershell
# Navigate to a location outside your CLI folder (e.g., your home directory)
cd ~
winapp cert generate
```

This creates a `devcert.pfx` file. To trust this certificate on your development machine, install it (requires administrator privileges):

```powershell
# Run PowerShell as Administrator
winapp cert install
```

**Important**: Keep your development certificate outside the folder containing your CLI executable to avoid accidentally including it in the package.

### 6. Package Your CLI

Now you're ready to create the MSIX package:

```powershell
# Run from outside CLI folder
# Package with dev certificate (for local testing/distribution)
winapp pack .\MyCliPackage --cert path\to\devcert.pfx
```

This creates an `.msix` file in the current directory

### Tips:
1. Once you are ready for distribution, you can sign your MSIX with a code signing certificate from a Certificate Authority so your users don't have to install a self-signed certificate
2. The Microsoft Store will sign the MSIX for you, no need to sign before submission.
3. You might need to create multiple MSIX packages, one for each architecture you support (x64, Arm64)