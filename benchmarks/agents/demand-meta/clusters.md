# Windows desktop developer demand clusters
Quality pass kept 300 current Windows desktop developer problems and moved 354 invalid/obsolete/generic previously sampled problems to `dropped.jsonl`. Each kept problem has exactly one `cluster_id`.
## Method
I re-read the prior problem titles and summaries, dropped Windows 8/Metro/Silverlight-era questions, generic C#/async questions, non-Windows platform items, and AI-skill/plugin requests, then rebuilt the kept set from the already collected raw GitHub/Stack Overflow records with the same criteria. Each problem was assigned the single best cluster by developer job, not by benchmark/tool command names. Frequency shares use the same three-signal method: 50% corrected sample share, 30% log-scaled search-count signal, and 20% corrected engagement.
## Cluster table
| ID | Cluster | Share | Problems | Example links |
|---|---|---:|---:|---|
| c04 | Diagnose crashes, hangs, runtime exceptions, and performance | 14.9% | 53 | [P0001](https://github.com/dotnet/maui/issues/15533)<br>[P0008](https://github.com/tauri-apps/tauri/issues/6322)<br>[P0010](https://github.com/flutter/flutter/issues/178916) |
| c07 | Sign packages and executables with certificates | 6.7% | 20 | [P0015](https://github.com/electron-userland/electron-builder/issues/7652)<br>[P0038](https://github.com/electron/electron/issues/44264)<br>[P0046](https://github.com/electron-userland/electron-builder/issues/8854) |
| c01 | Set up Windows desktop SDKs and toolchains | 6.1% | 19 | [P0013](https://github.com/dotnet/maui/issues/25160)<br>[P0040](https://github.com/microsoft/WindowsAppSDK/issues/3548)<br>[P0067](https://github.com/microsoft/WindowsAppSDK/issues/3594) |
| c25 | Handle WebView2 and embedded web content | 5.0% | 14 | [P0138](https://github.com/tauri-apps/tauri/issues/8196)<br>[P0262](https://github.com/microsoft/microsoft-ui-xaml/issues/9650)<br>[P0032](https://stackoverflow.com/questions/70246403/how-to-fix-msedgewebview2-error-in-vs2022-when-admin) |
| c05 | Package apps as MSIX, AppX, or app bundles | 4.7% | 15 | [P0003](https://github.com/dotnet/maui/issues/25832)<br>[P0200](https://github.com/microsoft/WindowsAppSDK/issues/4881)<br>[P0020](https://github.com/dotnet/maui/issues/16975) |
| c24 | Package, sign, update, and debug Electron Windows apps | 4.6% | 13 | [P0002](https://github.com/electron-userland/electron-builder/issues/8149)<br>[P0007](https://github.com/electron-userland/electron-builder/issues/8970)<br>[P0025](https://github.com/electron/electron/issues/38184) |
| c02 | Build and compile Windows desktop apps | 4.2% | 10 | [P0050](https://github.com/dotnet/maui/issues/25132)<br>[P0062](https://github.com/tauri-apps/tauri/issues/6746)<br>[P0085](https://github.com/tauri-apps/tauri/issues/10013) |
| c06 | Choose or configure MSI, NSIS, WiX, and setup installers | 4.0% | 12 | [P0029](https://github.com/electron-userland/electron-builder/issues/7973)<br>[P0033](https://github.com/electron-userland/electron-builder/issues/7574)<br>[P0036](https://github.com/electron-userland/electron-builder/issues/8131) |
| c10 | Install, upgrade, uninstall, and version apps correctly | 4.0% | 16 | [P0006](https://github.com/flutter/flutter/issues/117890)<br>[P0016](https://github.com/microsoft/WindowsAppSDK/issues/5730)<br>[P0017](https://github.com/flutter/flutter/issues/124883) |
| c21 | Build and deploy .NET MAUI Windows apps | 3.9% | 6 | [P0108](https://github.com/dotnet/maui/issues/15718)<br>[P0112](https://github.com/dotnet/maui/issues/21559)<br>[P0215](https://github.com/dotnet/maui/issues/19763) |
| c12 | Build and ship ARM64 or multi-architecture apps | 3.8% | 8 | [P0009](https://github.com/microsoft/WindowsAppSDK/issues/3842)<br>[P0026](https://github.com/dotnet/maui/issues/17330)<br>[P0055](https://github.com/electron-userland/electron-builder/issues/8687) |
| c17 | Get XAML layout, binding, controls, and resources right | 3.8% | 13 | [P0110](https://github.com/dotnet/maui/issues/14537)<br>[P0111](https://github.com/microsoft/microsoft-ui-xaml/issues/10970)<br>[P0209](https://github.com/electron/electron/issues/41928) |
| c11 | Deliver auto-updates and app installer feeds | 3.3% | 12 | [P0039](https://github.com/electron-userland/electron-builder/issues/7807)<br>[P0058](https://github.com/electron-userland/electron-builder/issues/7398)<br>[P0064](https://github.com/electron-userland/electron-builder/issues/7784) |
| c13 | Integrate Win32, HWND, COM, and native APIs | 3.1% | 11 | [P0023](https://github.com/microsoft/WindowsAppSDK/issues/3439)<br>[P0071](https://github.com/microsoft/WindowsAppSDK/issues/4651)<br>[P0088](https://github.com/microsoft/microsoft-ui-xaml/issues/11068) |
| c23 | Bundle, sign, and debug Tauri Windows apps | 3.1% | 8 | [P0035](https://github.com/tauri-apps/tauri/issues/9045)<br>[P0056](https://github.com/tauri-apps/tauri/issues/11546)<br>[P0068](https://github.com/tauri-apps/tauri/issues/10834) |
| c22 | Build, package, and publish Flutter Windows apps | 3.0% | 9 | [P0004](https://github.com/flutter/flutter/issues/121366)<br>[P0011](https://github.com/flutter/flutter/issues/150546)<br>[P0018](https://github.com/flutter/flutter/issues/119415) |
| c14 | Use file pickers, dialogs, and storage APIs | 2.6% | 8 | [P0051](https://github.com/microsoft/microsoft-ui-xaml/issues/8476)<br>[P0060](https://github.com/electron/electron/issues/37239)<br>[P0086](https://github.com/microsoft/WindowsAppSDK/issues/3489) |
| c20 | Migrate WPF, UWP, WinForms, or desktop code to newer stacks | 2.6% | 8 | [P0063](https://github.com/microsoft/WindowsAppSDK/issues/5684)<br>[P0072](https://github.com/electron-userland/electron-builder/issues/7946)<br>[P0073](https://github.com/electron/electron/issues/39278) |
| c03 | Run, deploy, and debug packaged or unpackaged apps | 2.5% | 10 | [P0059](https://github.com/microsoft/WindowsAppSDK/issues/3339)<br>[P0076](https://github.com/electron/electron/issues/44881)<br>[P0077](https://github.com/microsoft/WindowsAppSDK/issues/5832) |
| c09 | Publish or pass certification in Microsoft Store | 2.5% | 6 | [P0005](https://github.com/microsoft/WindowsAppSDK/issues/4480)<br>[P0078](https://github.com/microsoft/WindowsAppSDK/issues/6564)<br>[P0092](https://stackoverflow.com/questions/73940053/how-do-i-publish-a-winui3-msix-bundle-to-the-microsoft-store) |
| c26 | Manage dependencies, native libraries, and redistributables | 2.3% | 4 | [P0090](https://stackoverflow.com/questions/74466154/packaging-project-fails-with-mismatch-between-the-processor-architecture-etc)<br>[P0094](https://stackoverflow.com/questions/70843636/how-to-call-powershell-functions-from-c-in-winui-3)<br>[P0104](https://stackoverflow.com/questions/73051142/packaged-shell-extension-killing-application) |
| c28 | Build and package Windows apps in CI/CD | 2.3% | 4 | [P0093](https://github.com/electron-userland/electron-builder/issues/8613)<br>[P0096](https://github.com/tauri-apps/tauri/issues/7024)<br>[P0043](https://stackoverflow.com/questions/77644744/net-8-in-azure-devops-gives-error-netsdk1112-the-runtime-pack-for-microsoft-n) |
| c08 | Fix package identity, manifest, capabilities, and activation | 2.0% | 4 | [P0027](https://github.com/microsoft/WindowsAppSDK/issues/5820)<br>[P0080](https://github.com/microsoft/winappCli/issues/151)<br>[P0177](https://github.com/microsoft/WindowsAppSDK/issues/5636) |
| c15 | Send notifications, toasts, badges, and background tasks | 1.9% | 8 | [P0065](https://github.com/microsoft/WindowsAppSDK/issues/6071)<br>[P0075](https://github.com/microsoft/WindowsAppSDK/issues/3815)<br>[P0081](https://github.com/microsoft/winappCli/issues/352) |
| c18 | Apply dark mode, Mica, Fluent, and theming | 1.4% | 4 | [P0184](https://github.com/microsoft/microsoft-ui-xaml/issues/9895)<br>[P0191](https://github.com/tauri-apps/tauri/issues/14643)<br>[P0193](https://github.com/microsoft/microsoft-ui-xaml/issues/10766) |
| c16 | Implement windows, title bars, DPI, and display behavior | 0.9% | 1 | [P0257](https://stackoverflow.com/questions/69097246/how-to-support-for-windows-11-snap-layout-to-the-custom-maximize-restore-butto) |
| c27 | Distribute through enterprise or non-Store channels | 0.8% | 4 | [P0099](https://github.com/microsoft/WindowsAppSDK/issues/6639)<br>[P0097](https://stackoverflow.com/questions/67060229/open-a-windows-app-programmatically-when-it-was-installed-with-msix-application)<br>[P0098](https://stackoverflow.com/questions/67271306/why-does-the-msix-not-automatically-check-for-updates-every-time-the-application) |

## Cluster details
### c04 — Diagnose crashes, hangs, runtime exceptions, and performance (14.9%, n=53)
Crashes, hangs, runtime exceptions, rendering/GPU failures, and performance regressions in desktop apps.
Framework mix: {"WinUI3": 24, "Flutter": 11, "MAUI": 8, "Tauri": 5, "Electron": 2, "WPF": 1, "WinForms": 1, "other/unknown": 1}. Signals: sample_share=17.7%, engagement_weight=1559.3, search_count=713.
- [P0001](https://github.com/dotnet/maui/issues/15533)
- [P0008](https://github.com/tauri-apps/tauri/issues/6322)
- [P0010](https://github.com/flutter/flutter/issues/178916)

### c07 — Sign packages and executables with certificates (6.7%, n=20)
Certificate creation, trust, timestamping, SignTool, EV/OV signing, publisher mismatches, and unsigned package failures.
Framework mix: {"other/unknown": 12, "Electron": 4, "Tauri": 2, "MAUI": 1, "WPF": 1}. Signals: sample_share=6.7%, engagement_weight=608.2, search_count=119.
- [P0015](https://github.com/electron-userland/electron-builder/issues/7652)
- [P0038](https://github.com/electron/electron/issues/44264)
- [P0046](https://github.com/electron-userland/electron-builder/issues/8854)

### c01 — Set up Windows desktop SDKs and toolchains (6.1%, n=19)
Installing/selecting Windows SDKs, Visual Studio workloads, .NET workloads, package feeds, and generated tooling needed before a desktop project can build.
Framework mix: {"WinUI3": 12, "other/unknown": 3, ".NET console": 2, "C++/Win32": 1, "MAUI": 1}. Signals: sample_share=6.3%, engagement_weight=434.9, search_count=119.
- [P0013](https://github.com/dotnet/maui/issues/25160)
- [P0040](https://github.com/microsoft/WindowsAppSDK/issues/3548)
- [P0067](https://github.com/microsoft/WindowsAppSDK/issues/3594)

### c25 — Handle WebView2 and embedded web content (5.0%, n=14)
WebView2 runtime installation, initialization, user-data folders, navigation failures, and embedded web content behavior.
Framework mix: {"WPF": 6, "WinForms": 3, "other/unknown": 2, ".NET console": 1, "MAUI": 1, "Tauri": 1}. Signals: sample_share=4.7%, engagement_weight=395.7, search_count=88.
- [P0138](https://github.com/tauri-apps/tauri/issues/8196)
- [P0262](https://github.com/microsoft/microsoft-ui-xaml/issues/9650)
- [P0032](https://stackoverflow.com/questions/70246403/how-to-fix-msedgewebview2-error-in-vs2022-when-admin)

### c05 — Package apps as MSIX, AppX, or app bundles (4.7%, n=15)
Creating MSIX/AppX packages or bundles, package layout, MakeAppx, PRI/resource indexing, and package validation.
Framework mix: {"MAUI": 5, "WinUI3": 4, "Electron": 2, "WPF": 2, ".NET console": 1, "other/unknown": 1}. Signals: sample_share=5.0%, engagement_weight=540.8, search_count=7.
- [P0003](https://github.com/dotnet/maui/issues/25832)
- [P0200](https://github.com/microsoft/WindowsAppSDK/issues/4881)
- [P0020](https://github.com/dotnet/maui/issues/16975)

### c24 — Package, sign, update, and debug Electron Windows apps (4.6%, n=13)
Electron/electron-builder Windows installers, package contents, signing, updater metadata, native modules, and Windows runtime failures.
Framework mix: {"Electron": 13}. Signals: sample_share=4.3%, engagement_weight=497, search_count=21.
- [P0002](https://github.com/electron-userland/electron-builder/issues/8149)
- [P0007](https://github.com/electron-userland/electron-builder/issues/8970)
- [P0025](https://github.com/electron/electron/issues/38184)

### c02 — Build and compile Windows desktop apps (4.2%, n=10)
MSBuild, compiler, linker, resource, XAML compiler, NuGet restore, and generated-code failures that block a Windows desktop build.
Framework mix: {"WinUI3": 5, "Flutter": 2, "Tauri": 2, "MAUI": 1}. Signals: sample_share=3.3%, engagement_weight=190.3, search_count=209.
- [P0050](https://github.com/dotnet/maui/issues/25132)
- [P0062](https://github.com/tauri-apps/tauri/issues/6746)
- [P0085](https://github.com/tauri-apps/tauri/issues/10013)

### c06 — Choose or configure MSI, NSIS, WiX, and setup installers (4.0%, n=12)
Selecting/configuring non-MSIX installers such as MSI, NSIS, WiX, setup.exe, and ClickOnce-style deployment.
Framework mix: {"Electron": 10, "Tauri": 2}. Signals: sample_share=4.0%, engagement_weight=274, search_count=31.
- [P0029](https://github.com/electron-userland/electron-builder/issues/7973)
- [P0033](https://github.com/electron-userland/electron-builder/issues/7574)
- [P0036](https://github.com/electron-userland/electron-builder/issues/8131)

### c10 — Install, upgrade, uninstall, and version apps correctly (4.0%, n=16)
End-user install failures, version rules, upgrade/downgrade behavior, uninstall cleanup, dependency packages, and app data preservation.
Framework mix: {"WinUI3": 5, "MAUI": 3, "Electron": 2, "Flutter": 2, "WPF": 2, "other/unknown": 2}. Signals: sample_share=5.3%, engagement_weight=528.5, search_count=0.
- [P0006](https://github.com/flutter/flutter/issues/117890)
- [P0016](https://github.com/microsoft/WindowsAppSDK/issues/5730)
- [P0017](https://github.com/flutter/flutter/issues/124883)

### c21 — Build and deploy .NET MAUI Windows apps (3.9%, n=6)
MAUI Windows target build, packaging, WinUI host behavior, single-project assets, and Windows-specific MAUI failures.
Framework mix: {"MAUI": 6}. Signals: sample_share=2.0%, engagement_weight=187.8, search_count=609.
- [P0108](https://github.com/dotnet/maui/issues/15718)
- [P0112](https://github.com/dotnet/maui/issues/21559)
- [P0215](https://github.com/dotnet/maui/issues/19763)

### c12 — Build and ship ARM64 or multi-architecture apps (3.8%, n=8)
Targeting x64/x86/ARM64, native dependencies, runtime identifiers, platform targets, and multi-architecture bundles.
Framework mix: {"WinUI3": 3, "Electron": 2, "Flutter": 2, "MAUI": 1}. Signals: sample_share=2.7%, engagement_weight=218, search_count=143.
- [P0009](https://github.com/microsoft/WindowsAppSDK/issues/3842)
- [P0026](https://github.com/dotnet/maui/issues/17330)
- [P0055](https://github.com/electron-userland/electron-builder/issues/8687)

### c17 — Get XAML layout, binding, controls, and resources right (3.8%, n=13)
XAML layout, x:Bind/data binding, styles, resources, templates, NavigationView/ListView/Grid, and runtime XAML errors.
Framework mix: {"WinUI3": 10, "MAUI": 2, "Electron": 1}. Signals: sample_share=4.3%, engagement_weight=351.4, search_count=6.
- [P0110](https://github.com/dotnet/maui/issues/14537)
- [P0111](https://github.com/microsoft/microsoft-ui-xaml/issues/10970)
- [P0209](https://github.com/electron/electron/issues/41928)

### c11 — Deliver auto-updates and app installer feeds (3.3%, n=12)
App Installer files, update feeds, updater metadata, update intervals, silent/background updates, and update channel behavior.
Framework mix: {"Electron": 4, "other/unknown": 4, "Tauri": 2, "MAUI": 1, "WPF": 1}. Signals: sample_share=4.0%, engagement_weight=246.6, search_count=5.
- [P0039](https://github.com/electron-userland/electron-builder/issues/7807)
- [P0058](https://github.com/electron-userland/electron-builder/issues/7398)
- [P0064](https://github.com/electron-userland/electron-builder/issues/7784)

### c13 — Integrate Win32, HWND, COM, and native APIs (3.1%, n=11)
HWND ownership, P/Invoke, COM, C++/WinRT, message loops, window handles, HRESULTs, and other desktop interop.
Framework mix: {"WinUI3": 7, "C++/Win32": 2, "MAUI": 1, "WPF": 1}. Signals: sample_share=3.7%, engagement_weight=239.8, search_count=4.
- [P0023](https://github.com/microsoft/WindowsAppSDK/issues/3439)
- [P0071](https://github.com/microsoft/WindowsAppSDK/issues/4651)
- [P0088](https://github.com/microsoft/microsoft-ui-xaml/issues/11068)

### c23 — Bundle, sign, and debug Tauri Windows apps (3.1%, n=8)
Tauri Windows bundle formats, WebView2/runtime dependencies, MSI/NSIS/WiX config, signing, updater, and Windows runtime failures.
Framework mix: {"Tauri": 8}. Signals: sample_share=2.7%, engagement_weight=171, search_count=33.
- [P0035](https://github.com/tauri-apps/tauri/issues/9045)
- [P0056](https://github.com/tauri-apps/tauri/issues/11546)
- [P0068](https://github.com/tauri-apps/tauri/issues/10834)

### c22 — Build, package, and publish Flutter Windows apps (3.0%, n=9)
Flutter Windows desktop build/run/package/sign/publish problems, including Windows runner and desktop plugin issues.
Framework mix: {"Flutter": 9}. Signals: sample_share=3.0%, engagement_weight=341, search_count=5.
- [P0004](https://github.com/flutter/flutter/issues/121366)
- [P0011](https://github.com/flutter/flutter/issues/150546)
- [P0018](https://github.com/flutter/flutter/issues/119415)

### c14 — Use file pickers, dialogs, and storage APIs (2.6%, n=8)
File/folder pickers, save dialogs, ContentDialog, hwnd initialization for dialogs, StorageFile, and packaged storage access.
Framework mix: {"WinUI3": 3, "other/unknown": 3, "Electron": 1, "Tauri": 1}. Signals: sample_share=2.7%, engagement_weight=127.9, search_count=12.
- [P0051](https://github.com/microsoft/microsoft-ui-xaml/issues/8476)
- [P0060](https://github.com/electron/electron/issues/37239)
- [P0086](https://github.com/microsoft/WindowsAppSDK/issues/3489)

### c20 — Migrate WPF, UWP, WinForms, or desktop code to newer stacks (2.6%, n=8)
Porting between WPF/UWP/WinForms/WinUI/MAUI, replacing APIs, project migration, and desktop bridge modernization.
Framework mix: {"WPF": 5, "Electron": 2, "Tauri": 1}. Signals: sample_share=2.7%, engagement_weight=170.0, search_count=7.
- [P0063](https://github.com/microsoft/WindowsAppSDK/issues/5684)
- [P0072](https://github.com/electron-userland/electron-builder/issues/7946)
- [P0073](https://github.com/electron/electron/issues/39278)

### c03 — Run, deploy, and debug packaged or unpackaged apps (2.5%, n=10)
Local registration, launch, F5 deployment, unpackaged activation, debugging, and startup failures.
Framework mix: {"other/unknown": 4, "Electron": 2, "MAUI": 2, "WinUI3": 2}. Signals: sample_share=3.3%, engagement_weight=208.7, search_count=1.
- [P0059](https://github.com/microsoft/WindowsAppSDK/issues/3339)
- [P0076](https://github.com/electron/electron/issues/44881)
- [P0077](https://github.com/microsoft/WindowsAppSDK/issues/5832)

### c09 — Publish or pass certification in Microsoft Store (2.5%, n=6)
Partner Center, Store submission/certification, package flighting, ingestion, Store policy, and Store-only failures.
Framework mix: {"other/unknown": 3, "WinUI3": 2, "MAUI": 1}. Signals: sample_share=2.0%, engagement_weight=170.1, search_count=14.
- [P0005](https://github.com/microsoft/WindowsAppSDK/issues/4480)
- [P0078](https://github.com/microsoft/WindowsAppSDK/issues/6564)
- [P0092](https://stackoverflow.com/questions/73940053/how-do-i-publish-a-winui3-msix-bundle-to-the-microsoft-store)

### c26 — Manage dependencies, native libraries, and redistributables (2.3%, n=4)
Shipping VC++ runtimes, DLLs, framework packages, native assets, and missing dependency errors on user machines.
Framework mix: {"WPF": 2, "WinUI3": 2}. Signals: sample_share=1.3%, engagement_weight=43.4, search_count=51.
- [P0090](https://stackoverflow.com/questions/74466154/packaging-project-fails-with-mismatch-between-the-processor-architecture-etc)
- [P0094](https://stackoverflow.com/questions/70843636/how-to-call-powershell-functions-from-c-in-winui-3)
- [P0104](https://stackoverflow.com/questions/73051142/packaged-shell-extension-killing-application)

### c28 — Build and package Windows apps in CI/CD (2.3%, n=4)
GitHub Actions/Azure DevOps/CI build, packaging, signing, and environment failures specific to Windows desktop apps.
Framework mix: {"Electron": 1, "Tauri": 1, "WPF": 1, "other/unknown": 1}. Signals: sample_share=1.3%, engagement_weight=77.4, search_count=40.
- [P0093](https://github.com/electron-userland/electron-builder/issues/8613)
- [P0096](https://github.com/tauri-apps/tauri/issues/7024)
- [P0043](https://stackoverflow.com/questions/77644744/net-8-in-azure-devops-gives-error-netsdk1112-the-runtime-pack-for-microsoft-n)

### c08 — Fix package identity, manifest, capabilities, and activation (2.0%, n=4)
Package identity, AppxManifest schema, capabilities, protocol/file associations, app IDs, publisher IDs, and identity-dependent APIs.
Framework mix: {"other/unknown": 3, "Electron": 1}. Signals: sample_share=1.3%, engagement_weight=69, search_count=18.
- [P0027](https://github.com/microsoft/WindowsAppSDK/issues/5820)
- [P0080](https://github.com/microsoft/winappCli/issues/151)
- [P0177](https://github.com/microsoft/WindowsAppSDK/issues/5636)

### c15 — Send notifications, toasts, badges, and background tasks (1.9%, n=8)
Toast/local notifications, badges, push/activation, identity requirements, and background task activation.
Framework mix: {"other/unknown": 5, "WinUI3": 2, "Electron": 1}. Signals: sample_share=2.7%, engagement_weight=105.7, search_count=1.
- [P0065](https://github.com/microsoft/WindowsAppSDK/issues/6071)
- [P0075](https://github.com/microsoft/WindowsAppSDK/issues/3815)
- [P0081](https://github.com/microsoft/winappCli/issues/352)

### c18 — Apply dark mode, Mica, Fluent, and theming (1.4%, n=4)
Dark/light/high-contrast theme, Mica/Acrylic/Fluent backdrops, brushes, materials, and theme resources.
Framework mix: {"WinUI3": 3, "Tauri": 1}. Signals: sample_share=1.3%, engagement_weight=36.5, search_count=4.
- [P0184](https://github.com/microsoft/microsoft-ui-xaml/issues/9895)
- [P0191](https://github.com/tauri-apps/tauri/issues/14643)
- [P0193](https://github.com/microsoft/microsoft-ui-xaml/issues/10766)

### c16 — Implement windows, title bars, DPI, and display behavior (0.9%, n=1)
AppWindow, title bars, multiple windows, tray/window icons, DPI scaling, fullscreen, resize, monitor, and display behavior.
Framework mix: {"WPF": 1}. Signals: sample_share=0.3%, engagement_weight=25.0, search_count=5.
- [P0257](https://stackoverflow.com/questions/69097246/how-to-support-for-windows-11-snap-layout-to-the-custom-maximize-restore-butto)

### c27 — Distribute through enterprise or non-Store channels (0.8%, n=4)
Winget, Intune, sideloading, enterprise/private deployment, policy, app attach, and download distribution.
Framework mix: {"other/unknown": 3, "WPF": 1}. Signals: sample_share=1.3%, engagement_weight=42.0, search_count=0.
- [P0099](https://github.com/microsoft/WindowsAppSDK/issues/6639)
- [P0097](https://stackoverflow.com/questions/67060229/open-a-windows-app-programmatically-when-it-was-installed-with-msix-application)
- [P0098](https://stackoverflow.com/questions/67271306/why-does-the-msix-not-automatically-check-for-updates-every-time-the-application)

## Caveats
- Public GitHub and Stack Overflow are not representative of all Windows desktop developers; private enterprise packaging/signing issues are likely undercounted.
- Search counts are query-dependent; they are log-scaled and weighted less than corrected membership.
- Resolution summaries are issue-state/accepted-answer level notes, not independently validated fixes.
