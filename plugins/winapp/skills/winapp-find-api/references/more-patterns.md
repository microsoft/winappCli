# More find-api patterns and the CLI reference

### Inspect a large type without dumping it
```powershell
winapp find-api members Button --filter background
winapp find-api members NavigationView --filter selection
```

### See what the project references
```powershell
winapp find-api packages
winapp find-api stats
```

### Manage the index
```powershell
# Force a re-index (usually automatic after restore); --scan indexes every project under the dir
winapp find-api refresh
winapp find-api refresh --scan
```

### Explore the SDK with no project
```powershell
# From a directory with no project, results come from the machine-wide Windows SDK
# scope (reported as scope: sdk) — useful before an app has been scaffolded
winapp find-api "acrylic brush"
winapp find-api members Button --project sdk

# Rebuild the SDK scope after installing a new Windows SDK
winapp find-api refresh --project sdk
```

### Script against it with --json
```powershell
# Every verb supports --json for a clean, machine-readable payload on stdout
winapp find-api NavigationView --json
winapp find-api check-property Button Backgruond --json   # exits 1, JSON reports found:false

# Payloads say which index answered: scope, projectName, and projectDir
winapp find-api enums Symbol --json
# { "scope": "project", "projectName": "MyApp", "projectDir": "C:\\src\\MyApp",
#   "fullName": "Microsoft.UI.Xaml.Controls.Symbol",
#   "totalValues": 197, "values": [ "Accept", "Add", ... ] }

# A batch wraps the same per-subject payloads in an envelope
winapp find-api check-property InfoBar Severity Backgruond --json
# { "count": 2, "missingCount": 1, "results": [ { ...found:true... }, { ...found:false... } ] }
```

## CLI reference
- `winapp find-api "<query>" [<query>...] [--max N]` — search across type and member names, then their summaries (bare form). Each hit carries its owning package and a one-line purpose. Exits non-zero on no hits; with no query at all it prints usage and exits `0`.
- `winapp find-api members <type> [<type>...] [--filter <text>] [--all]` — properties, events, and methods of a type. An unfiltered listing shows declared members with signatures, summarizes inherited members by declaring type (names only), and omits dependency-property statics and descriptions; `--filter` and `--all` see everything with full signatures.
- `winapp find-api check-property <type> <property> [<property>...]` — validate properties exist; exits non-zero if any is missing. Read-only properties are flagged (`writable: false`) but still exit `0`.
- `winapp find-api enums <type> [<type>...] [--filter <text>]` — enum values; exits non-zero when the type is not an enum.
- `winapp find-api packages` — indexed NuGet/SDK packages with per-package counts.
- `winapp find-api stats` — aggregate index statistics for the project.
- `winapp find-api refresh [--scan]` — force a re-index; `--scan` walks all projects under the directory.

Common options (all verbs): `--json` for machine-readable output, `--project <Name>` / `--project-dir <path>` to select a project, `--project sdk` to query the machine-wide Windows SDK scope.

`--filter` means **case-insensitive substring** on `members` and `enums`. Filtered payloads also report the unfiltered totals (`totalValues`, `totalProperties`/`totalEvents`/`totalMethods`). Prefer it on large member lists; prefer dumping enums whole.

Every `--json` query payload identifies the index that answered: `scope` (`project` or `sdk`), `projectName`, and `projectDir` (omitted for the SDK scope). Because project names are not unique across directories, `projectDir` is the reliable identity when you need to confirm *which* project a result came from.
