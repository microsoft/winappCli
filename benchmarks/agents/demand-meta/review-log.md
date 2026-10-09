### c11-appinstaller-version-digit-dosvc
- Broadened the root-cause and solved criteria from only the version digit-count workaround to the documented Delivery Optimization stale range/file-size behavior, so answers that fix the same source bug by padding the App Installer file or moving to a new feed URI are not unfairly rejected.
- Added the constant-size App Installer XML workaround to acceptable alternatives and clarified the note tying source evidence to the Stack Overflow answers.

### c11-package-appinstaller-template-settings
- Softened the template-location requirement so Visual Studio/MSBuild answers still require `Package.appinstaller`, while a checked-in CI template generator is also accepted as a solution-agnostic route.
- Updated the solved definition to match that broader but still source-faithful answer.

### c12-electron-arm64-signtool-cache
- OK

### c12-winui-net8-portable-rids-arm64
- OK

### c13-dispatcherqueue-viewmodel-thread
- OK

### c13-winui-page-hwnd-filesavepicker
- OK

### c14-contentdialog-mvvm-xamlroot
- OK

### c14-folderpicker-selfcontained-element-not-found
- OK

### c15-rust-toast-debug-identity-elevated
- OK

### c16-wpf-custom-titlebar-snap-layout
- OK

### c17-winui3-no-xaml-designer
- OK

### c17-wpf-xamlcontrolsresources-winui2
- OK

### c18-winui3-acrylic-sdk10
- OK

### c20-wpf-desktopbridge-settings-localsettings
- OK

### c20-wpf-textrecognizer-keep-wpf
- OK

### c21-maui-self-contained-runtime-prompt
- Added the winapp CLI project-packaging route as an acceptable alternative because `docs\usage.md` documents `winapp pack <project>.csproj --self-contained` for bundling the Windows App SDK runtime, while still requiring the .NET self-contained RID/property separately.

### c21-maui-winui-xaml-no-main
- Softened the single-project item-glob requirement: the source issue remained nuanced, so the rubric now accepts restoring default MAUI items, explicitly classifying manifest/XAML items, or using the multi-project Windows head recommended in the thread.
- Updated solved/partial to avoid requiring a hand-maintained MSBuild item setup when the multi-project template is the lower-friction documented alternative.

### c22-flutter-black-screen-old-gpu
- OK

### c22-flutter-release-window-hidden
- OK

### c23-tauri-plugin-permissions-cache
- OK

### c23-tauri-windows-stack-overflow
- OK

### c24-electron-ts-config-colon-cache
- OK

### c24-electron-versioned-output-icon
- OK

### c24-electron-wincodesign-symlink-cache
- OK

### c25-clickonce-webview2loader-dll
- OK

### c25-winforms-webview2-new-window
- OK

### c25-wpf-webview2-added-to-visual-tree
- OK

### c26-wpf-wapproj-minversion-mismatch
- OK at freeze.
- After the freeze, in PR review: added the source thread's second reported fix (removing `SkipGetTargetFrameworkProperties` from the app's `ProjectReference`) as an acceptable alternative, and let `solved` accept it. The accepted answer's `TargetPlatformMinVersion` fix is unchanged.

### c27-appinstaller-onlaunch-schema
- Removed trivia pressure from the Windows-version criterion by focusing it on the launch-check settings used by this fixture, with prompt/blocking support left as a caveat only if those settings are added later.
- Updated notes to explain that the source resolution is the 2018 App Installer namespace and the OS-version check is supporting context.

### c28-azure-runtime-pack-not-restored
- OK

### c01-console-wasdk-dotnet-msb4062
- OK

### c01-maui-webview2-type-conflict
- OK

### c01-wasdk-auto-initializer-duplicate-obj
- OK

### c01-winui-template-workload-missing
- OK

### c02-maui-cswinrt-partial-analyzer
- OK

### c02-tauri-linux-windows-icon-resource
- OK

### c02-winui-packaged-anycpu-msix
- Added an acceptable alternative for the winapp CLI project packaging path (`winapp pack ... --arch x64`) so architecture-specific packaging answers are not penalized.
- Added `docs\usage.md#pack` to `verified_with` for that alternative.

### c03-maui-class-not-registered-unpackaged
- Added an acceptable alternative for the winapp CLI local unpackaged run path (`winapp run ... -p WindowsPackageType=None`) so equivalent debug-launch answers are not penalized.
- Added `docs\usage.md#project-mode-net-sdk-projects` to `verified_with` for that alternative.

### c04-electron-builder-v27-custom-nsis-toolset
- OK

### c04-flutter-ceil-native-crash
- OK

### c04-maui-flexlayout-image-layout-cycle
- OK

### c04-maui-hybridwebview-arm64-anycpu
- OK

### c04-tauri-resize-lag-webview2
- OK

### c04-winforms-webview2-anycpu-crash
- OK

### c04-winui-hot-reload-toolbar-av
- OK

### c04-winui-single-file-renamed-resources
- OK

### c04-wpf-webview2-loader-missing
- OK

### c05-electron-appx-unplated-icons
- Added an acceptable alternative for using `winapp manifest update-assets` to generate equivalent MSIX target-size/unplated icon assets outside electron-builder.
- Added the source issue and `docs\usage.md#manifest-update-assets` to `verified_with`.

### c05-maui-appxrecipe-wrong-targetframework
- Added the source issue to `verified_with`; the target-framework-selection workaround was already reflected in the rubric and fixture.

### c05-winui-appx1101-trimmed-pdb
- Added the source issue to `verified_with`; the trimming workaround and affected SDK behavior were already reflected in the rubric and fixture.

### c06-installer-tech-for-no-uac
- Added the source issue to `verified_with`; the rubric already matched the no-UAC per-user installer tradeoff and MSI wrapper evidence.

### c06-nsis-split-arch-installers
- Added the source issue to `verified_with`; the rubric already matched the universal-installer default, `buildUniversalInstaller`, and older separate-build workaround.

### c07-electron-azure-appx-store-signing
- Added the source issue to `verified_with`; the rubric already matched the Store AppX final-signing failure and `signExts`/hook workaround.

### c07-electron-osslsigncode-node-prebuilds
- Added the source issue to `verified_with`; the rubric already matched the non-Windows native prebuild signing failure and fixed-version workaround.

### c07-signtool-private-key-admin
- Added an acceptable alternative for using `winapp sign` with a protected PFX instead of the certificate-store private key path.
- Added `docs\usage.md#sign` to `verified_with` for that alternative.

### c07-tauri-resource-text-signing
- Added the source issue to `verified_with`; the rubric already matched the Tauri 2.5.0 non-binary resource signing regression.

### c08-manifest-en-uk-language
- Added the source issue to `verified_with`; the rubric already matched the invalid `en-UK` language tag and `en-GB`/neutral-language fixes.

### c09-winui-store-packaging-project
- Added the Stack Exchange source API URL to `verified_with`; the rubric already matched the packaging-project/Store upload flow.

### c10-msix-package-version-caption
- Added an acceptable alternative for using `winapp run` to test the packaged identity path for `Package.Current`.
- Added `docs\usage.md#run` to `verified_with` for that alternative.

### c10-windows-app-runtime-registered-user
- Added the source issue to `verified_with`; the rubric already matched the per-user/all-users runtime registration diagnostics.
