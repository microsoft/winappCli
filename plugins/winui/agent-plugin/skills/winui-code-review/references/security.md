# Security rules

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
