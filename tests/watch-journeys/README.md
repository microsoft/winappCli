# Watch journeys

Scripted editing sessions against `winapp run <app> --watch`. Each journey copies a small app, starts the watch, makes
one save per step, and checks what happened: what winapp or `dotnet watch` printed, whether the change is on screen, and,
at chosen steps, whether the app matches a clean build of the same source (the parity gate).

```powershell
# Build winapp first (.\scripts\build-cli.ps1 -SkipTests), then:
.\tests\watch-journeys\Run-WatchJourney.ps1 -Journey e2-libs,plain-rude2 -DotnetRoot C:\dotnet11
.\tests\watch-journeys\Run-WatchJourney.ps1            # every journey, on the SDK on PATH
```

The run prints one line per save and writes `scoreboard.md`, plus each journey's `watch.log` and `results.json`, under
`-OutputRoot` (default `%TEMP%\winapp-watch-journeys`). Journeys drive the app's UI, so don't use the desktop while they
run. On a machine that other sessions share, pass `-DesktopLock <file>`: each journey waits for that lock file and holds
it while it runs, and after a release the next journey waits 60 s so other sessions get a turn.

## Expectations and the scoreboard

Every step says what should happen in v1 (the design's target) and what happens today:

```json
"expect": { "v1": "live", "today": "unchanged" }
```

| v1 | Means |
|---|---|
| `live` | The save applied in the same process, and the step's checks see the change |
| `live+note` | The same, and winapp printed a `note:` saying what started over |
| `unchanged` | The save changed nothing that runs |
| `waiting` / `error` | The files don't agree yet / a build error; the app is untouched |
| `restart` | The app relaunched, winapp said why, and the checks pass in the new app |
| `untested` | Not decided yet; the step only records what happens |

`today` is the outcome the run observes now: `applied`, `unchanged`, `waiting`, `error`, `restart` (with a reason),
`restart-no-reason`, `stuck` (a restart whose rebuild failed, so no app), `crash`, `ignored` (nothing printed) or `timeout` (the save was picked up but no outcome followed, as when the app hangs). A `|` lists
outcomes that vary between runs.

| Status | Means |
|---|---|
| `pass` | v1 is met |
| `gap` | Today's recorded outcome, not v1 yet: an expected failure |
| `fail` | Neither: a regression, or an improvement whose `today` should be updated |
| `record` | A new step (`"today": "?"`): copy the observed outcome into `today` |
| `pending` | Not run; `pending` says what the step still needs |

When a change makes a step reach v1, its status turns to `pass`; update `today` in the same change. Never delete a step
to make the scoreboard green: change its expectation instead, and say why.

## Writing a step

```json
{
  "id": "r41-core-body", "row": 41, "title": "Library method body (Core)",
  "file": "Core/Greeter.cs", "op": "replace", "find": "\"core-v1|\"", "text": "\"core-v2|\"",
  "expect": { "v1": "live", "today": "applied" },
  "checks": [ { "assert": "invoke", "target": "LibButton" }, { "assert": "text-equals", "target": "Header", "value": "core-v2|data-v1" } ],
  "parity": true
}
```

- `row` is the row in the design's journey matrix. `expect.reason`, a regular expression, names the cause an `error` or
  `restart` must give, so the same outcome for another cause doesn't pass.
- Edits: `op` is `replace`, `insert-after`, `insert-before` (each needs exactly one match of `find`), `create-file`,
  `delete-file` or `rename-file` (`to`). Paths are relative to the app folder in `apps\`. Several files in one save go
  in `edits`; `delayMs` spaces them out, the way an agent or a rename writes them.
- `"op": "key", "key": "ctrl+r"` presses Ctrl+R instead of saving a file.
- Checks: `invoke`, `set-value`, `exists`, `absent`, `text-equals`, `text-contains`, `text-changes` (the text moves on
  within 2 s, so the UI thread is alive), all through UI Automation by `x:Name` or visible text, and `property-equals`
  (`"Property=Value"`, read through DevTools). Checks also run after a `timeout`, to show whether the app still responds.
- `parity: true` compares the app with a clean build after the step. The journey's `parity` object sets the `root` to
  read, `navigate` steps for the clean build, `probes` run on both apps, and `ignore` patterns.
- `setup` edits are made before the first build; `before` checks run after each launch and restart.
- `pending` keeps a step on the scoreboard without running it.

The apps: `apps\winui` is a WinUI app (`App`, with `Core` and `Data` class libraries beside it) and `apps\reactor` is a
Microsoft.UI.Reactor app. Both use the stock Windows App SDK 2.7.0 through `Microsoft.WindowsAppSDK.WinUI` and
`Microsoft.WindowsAppSDK.Runtime`, because the `Microsoft.WindowsAppSDK` 2.7.0 metapackage also needs
`Microsoft.WindowsAppSDK.AI` 2.7.0, which these runs' package feed doesn't have.
