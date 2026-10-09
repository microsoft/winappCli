# Code quality rules

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
