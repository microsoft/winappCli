# Quality Rules — Detailed Reference

Consolidated detailed rules from performance, security, accessibility, globalization, and code quality.

---

## Performance

### x:Bind vs {Binding}

Always prefer `x:Bind` (compiled bindings) over `{Binding}` (runtime reflection). `x:Bind` resolves at compile time, generates strongly typed code, and avoids the reflection overhead of `{Binding}`.

| Feature | `x:Bind` | `{Binding}` |
|---|---|---|
| Resolution | Compile-time | Runtime (reflection) |
| Type safety | ✅ Yes | ❌ No |
| Default mode | OneTime | OneWay |
| Performance | Faster | Slower |

Reserve `{Binding}` only where `x:Bind` cannot be used (e.g., `Style` setters).

### Deferred Loading with x:Load

Use `x:Load` to defer creation of UI subtrees that aren't immediately visible (e.g., dialogs, secondary tabs, collapsed panels). The element is created only when `x:Load` evaluates to `true`.

```xml
<StackPanel x:Name="SettingsPanel" x:Load="{x:Bind ViewModel.IsSettingsOpen, Mode=OneWay}">
    <TextBlock Text="Settings content here" />
</StackPanel>
```

### Incremental Rendering with x:Phase

Use `x:Phase` inside `DataTemplate` to prioritize which parts of each list item render first. Phase 0 (default) renders immediately; higher phases render in subsequent passes.

```xml
<DataTemplate x:DataType="vm:ItemViewModel">
    <StackPanel>
        <TextBlock Text="{x:Bind Title}" />
        <TextBlock Text="{x:Bind Description}" x:Phase="1" />
        <Image Source="{x:Bind ThumbnailUrl}" x:Phase="2" />
    </StackPanel>
</DataTemplate>
```

### Collection Virtualization

Use `ListView`, `GridView`, or `ItemsRepeater` for any list that may exceed ~20 items. These controls create UI elements only for visible items and recycle them on scroll.

```xml
<ScrollViewer>
    <ItemsRepeater ItemsSource="{x:Bind ViewModel.Items}">
        <ItemsRepeater.Layout>
            <StackLayout Spacing="4" />
        </ItemsRepeater.Layout>
    </ItemsRepeater>
</ScrollViewer>
```

For large datasets, implement `ISupportIncrementalLoading` so the `ListView` fetches pages of data as the user scrolls.

### DispatcherQueue for UI-Thread Management

```csharp
public async Task LoadDataAsync()
{
    var data = await Task.Run(() => _service.GetExpensiveData());

    DispatcherQueue.TryEnqueue(() =>
    {
        ViewModel.Items.Clear();
        foreach (var item in data)
            ViewModel.Items.Add(item);
    });
}
```

**Do not flood the queue.** Batch updates into a single `TryEnqueue` call rather than enqueuing per item.

### Async Patterns

- Use `async/await` for I/O-bound work (file access, HTTP calls, database queries).
- Use `Task.Run` for CPU-bound work (parsing, compression, image processing).
- Never block the UI thread with `.Result`, `.Wait()`, or `.GetAwaiter().GetResult()`.

### Layout and Visual Tree

- Minimize XAML visual tree depth — deep nesting compounds layout-pass cost.
- Prefer `Grid` over nested `StackPanel` layouts when you need rows and columns.
- Cache expensive computations and HTTP responses when appropriate.

---

## Security

### Secrets Management with PasswordVault

Use the Windows Credential Locker (`PasswordVault`) to store secrets. Credentials are encrypted per-user, per-app.

```csharp
using Windows.Security.Credentials;

var vault = new PasswordVault();
vault.Add(new PasswordCredential("MyApp", username, accessToken));

var credential = vault.Retrieve("MyApp", username);
credential.RetrievePassword();
string token = credential.Password;

vault.Remove(credential);
```

### DPAPI Encryption for Data at Rest

For encrypting arbitrary data at rest (e.g., local cache files), use `DataProtectionProvider`:

```csharp
using Windows.Security.Cryptography.DataProtection;
using Windows.Storage.Streams;

// Encrypt
var provider = new DataProtectionProvider("LOCAL=user");
IBuffer encrypted = await provider.ProtectAsync(dataBuffer);

// Decrypt
var unprotectProvider = new DataProtectionProvider();
IBuffer decrypted = await unprotectProvider.UnprotectAsync(encrypted);
```

### Input Validation

Validate and sanitize all external input before processing. Use XAML input constraints and C# validation together:

```xml
<TextBox x:Name="AgeInput"
         InputScope="Number"
         MaxLength="3"
         BeforeTextChanging="AgeInput_BeforeTextChanging" />
```

```csharp
private void AgeInput_BeforeTextChanging(TextBox sender,
    TextBoxBeforeTextChangingEventArgs args)
{
    args.Cancel = !args.NewText.All(char.IsDigit);
}
```

For file paths and process execution, never pass unsanitized user input:

```csharp
// BAD — command injection risk
Process.Start("cmd.exe", $"/c {userInput}");

// GOOD — validate and use typed APIs
if (Path.GetExtension(filePath) == ".txt" && Path.IsPathFullyQualified(filePath))
{
    var content = await File.ReadAllTextAsync(filePath);
}
```

### Secure WebView2 Configuration

```csharp
async Task InitializeWebView()
{
    await webView.EnsureCoreWebView2Async();
    var settings = webView.CoreWebView2.Settings;

    settings.IsScriptEnabled = false;
    settings.AreDefaultScriptDialogsEnabled = false;
    settings.IsWebMessageEnabled = false;
    settings.AreDevToolsEnabled = false;

    webView.CoreWebView2.NavigationStarting += (s, e) =>
    {
        var uri = new Uri(e.Uri);
        if (uri.Host != "trusted.example.com")
            e.Cancel = true;
    };
}
```

### Network Security

- Always use HTTPS. Never disable TLS certificate validation.
- Use `HttpClient` with default certificate validation — do not override `ServerCertificateCustomValidationCallback` to return `true`.
- Pin certificates for high-security scenarios using a custom `HttpClientHandler`.

### Package Identity and Secure Storage

- Packaged apps run inside an MSIX container with isolated `ApplicationData` storage.
- Follow the principle of least privilege in `Package.appxmanifest`.
- Keep NuGet packages up to date — run `dotnet list package --outdated` regularly.
- Never log sensitive data (PII, tokens, passwords).

---

## Accessibility

### AutomationProperties

- **Every interactive control** must have an `AutomationProperties.Name` or `AutomationProperties.LabeledBy`.
- Add a stable, unique `AutomationProperties.AutomationId` for controls targeted by UI automation tests.
- Use semantic XAML controls — prefer `Button`, `HyperlinkButton`, `ListView` over styled `Border`/`Grid` with click handlers.
- Images must have `AutomationProperties.Name` describing the image purpose (or `AutomationProperties.AccessibilityView="Raw"` for decorative images).

### Keyboard Navigation

- Logical tab order via `TabIndex`.
- `AccessKey` bindings for frequently used actions.
- `KeyboardAccelerator` for shortcut keys.

### Screen Readers

- Support Narrator / NVDA: test that all content is announced correctly.
- Do not rely on colour alone to convey meaning — add icons, text, or patterns.
- Do not use `Visibility.Collapsed` to "hide" content from screen readers (use `AccessibilityView` instead).

### Contrast

- Maintain minimum contrast ratios: 4.5:1 for normal text, 3:1 for large text.
- Test in High Contrast mode.

### Verification Checklist

- [ ] All interactive controls have `AutomationProperties.Name`
- [ ] Keyboard navigation works for the changed area
- [ ] Tested with High Contrast theme enabled
- [ ] Tab through the entire UI with keyboard only
- [ ] Key interactive controls have stable `AutomationProperties.AutomationId` values
- [ ] Switch to Windows High Contrast theme and verify readability
- [ ] Run Narrator and verify all controls are announced correctly
- [ ] Run Accessibility Insights for Windows on the app

---

## Code Quality

### Roslyn Analyzer Setup

```xml
<ItemGroup>
  <PackageReference Include="Microsoft.CodeAnalysis.NetAnalyzers" Version="*" />
</ItemGroup>
```

```xml
<PropertyGroup>
  <EnableNETAnalyzers>true</EnableNETAnalyzers>
  <AnalysisLevel>latest-recommended</AnalysisLevel>
  <EnforceCodeStyleInBuild>true</EnforceCodeStyleInBuild>
  <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  <Nullable>enable</Nullable>
</PropertyGroup>
```

Follow **all** CA* (quality) and IDE* (code style) analyzer rules at their configured severity.

### .editorconfig

The project's `.editorconfig` is the source of truth for code style:
- Private fields use `_camelCase` prefix.
- File-scoped namespaces are required.
- `this.` qualification is not used.

### Code Cleanup Rules (After Every Edit)

1. Remove unused `using` statements.
2. Remove commented-out code.
3. Remove unused variables and fields.
4. Remove empty methods.
5. Apply IDE suggestions (IDE0001–IDE0090).

### Naming Conventions

| Element | Convention | Example |
|---|---|---|
| Class / Struct | PascalCase | `MainViewModel` |
| Interface | I + PascalCase | `INavigationService` |
| Public method | PascalCase | `LoadDataAsync()` |
| Private method | PascalCase | `ValidateInput()` |
| Public property | PascalCase | `CurrentPage` |
| Private field | _camelCase | `_settingsService` |
| Parameter | camelCase | `userName` |
| Local variable | camelCase | `itemCount` |
| Constant | PascalCase | `MaxRetryCount` |
| Async method | Suffix `Async` | `FetchDataAsync()` |
| Boolean | Prefix `Is/Has/Can` | `IsLoading`, `HasAccess` |

### File Organization

Each `.cs` file should follow this order:
1. `using` directives (System first, then others, alphabetically)
2. Namespace declaration (file-scoped)
3. Class/struct/interface declaration
4. Inside the type: Constants → Static fields → Instance fields → Constructors → Properties → Public methods → Private methods → Event handlers → Nested types

---

## Globalization

### .resw File Structure

Resource files live under `Strings/{language-tag}/` in the project:

```
MyApp/
├── Strings/
│   ├── en-us/
│   │   └── Resources.resw
│   ├── de-de/
│   │   └── Resources.resw
│   └── ja-jp/
│       └── Resources.resw
```

Each `.resw` file is an XML table of name–value pairs with dot notation for property targeting:

| Name | Value |
|---|---|
| `SaveButton.Content` | Save |
| `WelcomeMessage.Text` | Welcome! |
| `SearchBox.PlaceholderText` | Search… |
| `NameInput.Header` | Full Name |
| `ErrorFileNotFound` | The file could not be found. |

### x:Uid Binding Patterns

```xml
<Button x:Uid="SaveButton" />
<TextBlock x:Uid="WelcomeMessage" />
<TextBox x:Uid="SearchBox" />
<ContentDialog x:Uid="DeleteConfirmDialog" />
```

### x:Uid Property Suffix Table

| Suffix | XAML Property | Controls |
|---|---|---|
| `.Text` | `TextBlock.Text` | `TextBlock` |
| `.Content` | `ContentControl.Content` | `Button`, `CheckBox`, `RadioButton` |
| `.PlaceholderText` | `TextBox.PlaceholderText` | `TextBox`, `AutoSuggestBox` |
| `.Header` | `HeaderedContentControl.Header` | `TextBox`, `ComboBox`, `Slider` |
| `.Title` | `ContentDialog.Title` | `ContentDialog` |
| `.Description` | `SettingsCard.Description` | `SettingsCard` |

### ResourceLoader Patterns

```csharp
using Microsoft.Windows.ApplicationModel.Resources;

public class MainViewModel
{
    private readonly ResourceLoader _resourceLoader = new();

    public string GetErrorMessage(string fileName)
    {
        string template = _resourceLoader.GetString("ErrorFileNotFound");
        return string.Format(template, fileName);
    }
}
```

For strings with format placeholders, define the `.resw` value with `{0}`, `{1}`, etc.:

```csharp
string message = string.Format(_resourceLoader.GetString("ItemCount"), count);
```

### Culture-Aware Formatting

```csharp
using System.Globalization;

// GOOD — respects user's regional settings
string date = DateTime.Now.ToString("d", CultureInfo.CurrentCulture);
string price = cost.ToString("C", CultureInfo.CurrentCulture);
string number = value.ToString("N2", CultureInfo.CurrentCulture);

// BAD — assumes US format
string date = DateTime.Now.ToString("MM/dd/yyyy");
string price = $"${cost:F2}";
```

### RTL Layout Support

```xml
<Grid FlowDirection="{x:Bind ViewModel.AppFlowDirection, Mode=OneTime}">
    <!-- All child controls inherit the flow direction -->
</Grid>
```

Use `Start`/`End` alignment, not `Left`/`Right`. Avoid hard-coding `Margin` or `Padding` that assumes LTR layout.

### Pluralization Handling

```csharp
string key = count == 1 ? "ItemCount_One" : "ItemCount_Other";
string message = string.Format(_resourceLoader.GetString(key), count);
```

### Testing Localization

```csharp
// In App.xaml.cs — set before any UI loads
Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = "de-de";
```
