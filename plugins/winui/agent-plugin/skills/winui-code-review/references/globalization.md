# Globalization rules

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
