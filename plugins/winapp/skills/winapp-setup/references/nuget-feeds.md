# Private or custom NuGet feeds

### Private or custom NuGet feeds

`init`, `restore`, and `update` download the SDK packages through NuGet, honoring your standard `nuget.config` hierarchy. Private feeds and mirrors, feed credentials (including credential providers), and a custom `globalPackagesFolder` all work as they do for `dotnet restore`. To use only your feed, `<clear />` the inherited sources first:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="contoso" value="https://pkgs.dev.azure.com/contoso/_packaging/winsdk-mirror/nuget/v3/index.json" />
  </packageSources>
</configuration>
```

> **Security note:** For native projects winapp resolves `nuget.config` from the directory it operates on: the `init`/`restore` directory argument, `--config-dir` when given, otherwise the current directory. For **.NET projects** the sources come from the project's own `nuget.config` hierarchy instead, because that is what `dotnet add package` and `dotnet restore` use — so put a private feed's config in the project directory or an ancestor, not in a sibling passed via `--config-dir` (that is reported and ignored). Run these commands only against directories you trust, the same as `dotnet restore`. Use `<packageSourceMapping>` to pin packages to specific feeds when more than one source is configured.
