# Inspect a Reactor app with DevTools

An app built with [Microsoft.UI.Reactor](https://github.com/microsoft/microsoft-ui-reactor)
needs no extra flag. `winapp run` starts DevTools for it like any WinUI project, and
everything in [Inspect a WinUI app with DevTools](devtools.md) applies. This page covers
what DevTools adds for Reactor's C# components.

```powershell
winapp run . --detach
winapp devtools inspect
```

```text
Window "TaskBoard" -w 43913168 Reactor win-1
  [stackpanel-9bec7a8478] <App> root
    [stackpanel-9bec7a8478] Stack (StackPanel) App.cs:34
      [textblock-a5d0630467] TextBlock App.cs:35 Text="TaskBoard"
      [border-a5e0b88a83] <Counter> App.cs:36
        [button-baacfbdb21] Button #incrementButton App.cs:67 Content="+1"
Window "Settings" -w 173605090 Reactor win-2 key=Settings
  [stackpanel-e0f3dea4c9] <SettingsPage> root
    ...
```

The tree shows the elements your code created, whether it is XAML or Reactor C#.
A Reactor line shows the element, or `<Name>` for a component, then its C# name, the
WinUI type it produced when that differs, its `key`, and the C# call site. The C# name
is the variable or member the element was assigned to, shown like a XAML `x:Name`:

```csharp
var incrementButton = Button("+1", () => setCount(count + 1));   // Button #incrementButton
Element Header => TextBlock("Tasks");                             // TextBlock #Header
```

An element built inline in a tree has no name, like a XAML element without `x:Name`.
With `--json`, a named element has `"name"` and `"nameSource"`: `csharp-variable` when
Reactor recorded the name, or `csharp-source-text` when DevTools read it from the
call site's line because your Reactor version records none. A name read from the
line can be out of date if you edited the file after the build. Each window's root
component is the `root` line above the control it rendered; a `ReactorHostControl`
inside a XAML page shows its root as a `Reactor island root` under the XAML that
hosts it. When the app has several windows, the tree is grouped by window, and
each header shows the `-w` value that limits any command to that window.

The bracketed selector is the WinUI element, so `get-property`, `get-layout`,
`set-property` and `get-source` accept it. Reactor elements can also be selected by
what your C# says about them:

| Selector | Selects |
|---|---|
| `"<Counter>"` | the control a `Counter` component rendered |
| `incrementButton` or `#incrementButton` | the element assigned to that C# variable, when only one is |
| `"<Counter>/Button"` | a `Button` element inside `Counter` (steps can repeat: `"<SettingsPage>/<Counter>/Button"`) |
| `key=t1` | the element with that Reactor key |
| `App.cs:67` | the element created on that line (`App.cs:67:13` adds the column; `..\Shared\Badge.cs:14` or `Panels/Footer.cs:6` picks one of several same-named files; a file a `#line` directive names, such as `Widget.rxt:3`, works too) |

```powershell
winapp devtools get-source "<Counter>/Button"
winapp devtools search --component Counter
winapp devtools search --of-type Stack --with 'key==t2'
```

A selector that matches more than one element fails and lists each match with its
selector, key, call site and window; pass `-w` or one of the listed selectors.
`search` matches component names, C# names (`#incrementButton`), element kinds, keys
and call sites. A query written as a selector (`key=t1`, `App.cs:67` or `"<Counter>/Button"`)
lists exactly the elements that selector selects, in
every window, so `key=t1` does not also list `key=t10`. `search --component Counter` lists every rendered `Counter`, and adds a
text query to search only inside them. `--of-type` accepts Reactor element kinds
(`Button`, `Stack`) as well as WinUI types, and `--with 'key==t1'` filters by key.
`--all` shows the full WinUI tree with the Reactor element after each line.

## Find the C# behind an element

```powershell
winapp devtools get-source "<Counter>/Button"
```

```text
[button-baacfbdb21] Button
  <Counter> · App.cs:67 (C#, Reactor Button)
  C:\src\TaskBoard\App.cs

  Button("+1", () => setCount(count + 1)));
```

`get-source` reports the component that rendered the element, the C# call site and
the statement there. For a template part, such as a button's inner TextBlock, it
reports the nearest Reactor element that contains it. With `--json` the answer has
`"kind": "csharp"` (XAML answers have `"kind": "xaml"`). Passwords are hidden as
described in [DevTools](devtools.md#find-and-inspect-an-element).

The in-app overlay and the inspector window show the same thing. Picking an
element in the app selects the Reactor element you wrote rather than a template
part (turn off **Just my code** to pick raw elements). The selection reads
`Button #incrementButton · <Counter> · App.cs:67`, and the comment panel shows the
same call site where a XAML element shows its file and line. The inspector's tree
shows `<Counter>` rows and Reactor element kinds, and its properties pane shows the
C# statement with an **Open** link that hands the `.cs` file to your default editor.

C# paths that DevTools shows are relative to your project. A Native AOT or attached app
records each call site relative to the project that compiled it, so DevTools looks for
the file in your project and the projects it references (`../` paths for linked files,
inside your repository), then in the folders above your project, and uses the build's
`PathMap`. When that path names a file in more than one of those projects, DevTools
cannot tell which one created the element: `get-source` lists the files and shows no
code (`"authoredState": "ambiguous"` with `--json`). Open the one in the project that
defines the component. After `winapp devtools attach`, run `get-source` from your app's
project folder so DevTools knows the projects.

If you edit a file after building, the recorded line may now hold other code.
`get-source` says the file changed since the app was built (`"authoredState": "stale"`
with `--json`); rebuild and run the app, or apply the edit with Hot Reload, to see the
call site again. A `#line` directive can point a call site past the end of its file;
`get-source` says so instead of showing nothing. Code a source generator produced is
not on disk unless you set `<EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>`
in the project and rebuild.

## Properties your C# sets

```powershell
winapp devtools get-property "<Counter>/Button"
winapp devtools set-property "<Counter>/Button" Content "Plus one"
```

`get-property` marks each value the element's C# sets (its factory arguments,
initializers and modifiers such as `.Width(120)`) with `set in C# · App.cs:67`; with
`--json` those properties have `"setBy": "reactor"`. For a component, it also lists
the props the component was rendered with.

`set-property` changes such a property live and warns that it is set in C#: Reactor
writes its own value again when the component that rendered the element re-renders
with a changed value (`"reactorOwned": true` with `--json`). Edit the C# to keep a
change.

A property that follows a theme resource through a `ThemeRef`, such as
`.Foreground(Theme.AccentText)`, shows `← ThemeRef AccentTextFillColorPrimaryBrush`
(`"themeResource"` with `--json`). `get-property` also lists the element's event
handlers and the method each one runs:

```text
  Handlers:
    OnClick → lambda in Counter.Render App.cs:76
```

## Read and change component state

```powershell
winapp devtools get-state "<Counter>"
```

```text
[border-a5e0b88a83] Border
  <Counter> class component · defined App.cs:71 (Counter.Render) · declared App.cs:44
    Props (CounterProps):
      Label = Done today
    Hooks:
      [0] count state int = 3 settable
      [1] density context string = "Compact" (Density)
    Context Density = "Compact"
```

`get-state` shows the component that renders the element you name: its props, its
hooks in call order with their values and the C# variable each is assigned to, and the
contexts it reads. When Reactor recorded no hook names (for example after
`winapp devtools attach`), DevTools reads them from the render method's C#, as long as
its hook calls run in a fixed order: not in a branch, a loop, a lambda or a custom
hook. Those names are marked as read from the file (`"nameSource": "csharp-source-text"`
with `--json`) and can be out of date if you edited it after the build. Otherwise
hooks show their index only. Pass a component
(`"<Counter>"`) or any element inside it. `defined` is where the component's render
method is, read from the app's PDB; when that file changed after the build, the
output says lines may have moved. Passwords show as `<redacted>` and cannot be set;
see [DevTools](devtools.md#find-and-inspect-an-element).

```powershell
winapp devtools set-state "<Counter>" count 42
winapp devtools rerender "<Counter>"
winapp devtools invoke-handler "<Counter>/Button" OnClick
```

- `set-state <selector> <hook> <value>` writes one `state`, `reducer` or
  `persisted` hook, as if the component called its own setter, and reports the value
  before and after the re-render. `<hook>` is the hook's name or its index, as
  `get-state` lists it. A name listed as `[?]` belongs to a hook after one in a branch, a
  loop or a composite hook, so DevTools cannot tell which live hook it is; set that
  hook by its index. The value is parsed to the hook's type: a number,
  text, `true`/`false`, an enum name, or `null` for a nullable. Hooks holding other
  types (records, lists) are refused; change them through the app or its handlers.
- `rerender <selector>` renders the component again. A render reapplies only the C#
  values that changed, so a property you edited with `set-property` keeps its edit
  until its C# value changes.
- `invoke-handler <selector> <handler>` runs a handler the element's C# set, such as
  `OnClick`, on the app's UI thread. Pass `--value` when the handler takes one text,
  number, `true`/`false` or enum argument. Handlers that need other arguments are
  refused; trigger them with `winapp ui` instead.

These are live, in-memory changes; your C# is unchanged and the app starts from its
initial state on the next launch. Each waits until Reactor finishes rendering before
it reads or writes. If the app does not answer in time, the command fails with
`indeterminate`: the change may or may not have happened, so read it back with
`get-state` before retrying.

In the app, select a Reactor element with **Select element**: the inspector's
properties pane has a **Component** section with the same props, hooks and handlers.
Type a new value into a state hook and press Enter, or select **Run** next to a
handler.

## Why does this element show this value?

```powershell
winapp devtools diagnose-binding "<Counter>/TextBlock" Text
```

```text
[textblock-394288607b] TextBlock.Text
  kind: reactor (set in C#, not a binding)
  shows: Done today: 3 [Local]
  ← TextBlock Content = Done today: 3 (the element's C#)
  ← created at App.cs:75 in <Counter>
  ← <Counter> as it last rendered (green: values the shown value contains):
    ...
      [0] state int = 3 settable
```

For a property a Reactor element sets, `diagnose-binding` follows the value back:
the WinUI value, the element record value, modifier or `ThemeRef` that set it, the
line that created the element, and the props, state and contexts of the component
that rendered it. Values that appear in the shown value are highlighted, and
`--json` lists them as `likelySources` (`"kind": "reactor"`). XAML bindings are
diagnosed as before.

## Drive Reactor elements with `winapp ui`

`winapp ui` commands accept the same Reactor selectors when the app runs with
DevTools:

```powershell
winapp ui invoke "<Counter>/Button" -a TaskBoard
winapp ui get-value key=t2 -a TaskBoard
```

DevTools finds the element, and `winapp ui` acts on the UI Automation element at the
same place on screen. Without DevTools attached, the text keeps its usual `winapp ui`
meaning (a name search).

## Leave comments on Reactor code

A comment left in the overlay on a Reactor element, or added from a terminal, is
anchored to the C# call site that created the element:

```powershell
winapp devtools comments add "<Counter>/Button" -a TaskBoard --text "Make this larger"
winapp devtools comments add --file App.cs --line 35 --text "Make this larger"
```

`--file App.cs --line 35` needs exactly one element call, such as `Button(...)`, on
that line; if the line has several, the error lists them and `--column` picks one.
A call site in a project your app references, or in a linked file, is anchored too when
the file is in the same repository: pass its path from your project
(`--file ..\MyLibrary\Panels\Footer.cs --line 6`) or its full path. When DevTools cannot
link a comment to source, for example for an element with no C# call site, `comments add`
still saves it and says why, with how to link it (`"unanchored"` with `--json`).
To move an existing comment to the element picked in the app, keep its text:

```powershell
winapp devtools comments add --id cmt_example --from-selection -a TaskBoard
```

`comments list` and `get` find the call site again in the current source. The stored
line still making the same call, or the same line moved elsewhere in the component
that rendered it, is confirmed. An edited or duplicated line, or a renamed file,
gives ranked candidates with `requiresConfirmation`; confirm the intended one before
editing. An element with no call site of its own, such as the text inside a
`Button`, uses the nearest Reactor element's call site, which you confirm with
`--confirm-likely-source` (or the **Use this likely source for my comment** box in
the app).

Markers appear on the live element in every window that shows it and follow edits
after hot reload or a rebuild.

## See Reactor warnings and errors

```powershell
winapp devtools logs -a TaskBoard
winapp devtools logs -a TaskBoard --level error --follow
```

`logs` lists what Reactor reported since the app started: render errors, errors Reactor
caught and continued past, framework warnings, failed Windows calls, keyed lists with
null or duplicate keys, hot reload and windows opening or closing. Entries name
components and exception types; they never carry exception messages or app data.
The app keeps the latest 500. `--follow` keeps printing new entries until you press
Ctrl+C or the app exits, and `--json` returns the entries (one JSON object per line
with `--follow`).

A keyed list with a null or duplicate key re-creates its rows instead of moving them,
which loses their state. `logs` reports each such list once, and again when it keeps
happening, with how many keys were affected but never the keys themselves:

```text
14:02:11.483 warning Keyed list in ListView of <TaskList> at TaskList.cs:42 has duplicate keys (x3); it re-creates its rows instead of moving them
```

The component and the C# location appear when your Reactor version reports them. Each
list counts once toward the toolbar's red badge. These warnings replace the keyed-list
dialog in Reactor's in-app developer menu.

After a .NET Hot Reload edit, DevTools reads the tree again, so sources, comment
markers and the inspector follow the new code; component state is kept.

When a window's root component throws while rendering, or Reactor fails to apply a
render to the window's controls, Reactor replaces the window's content with its error
screen. `inspect` marks that window's root `showing Reactor's error
screen` and prints the error's headline, and `get-state`, `set-state` and `rerender` on
it fail with `reactor-error-screen`. Fix the error in your C#, then use Hot Reload or
restart the app.

## Launch and limits

Call sites need a Debug build, or `<ReactorSourceMap>true</ReactorSourceMap>` in
your project. `winapp run` with DevTools turns them on by setting `REACTOR_SOURCEMAP=1`
unless you already set that variable, and warns when the project sets
`<ReactorSourceMap>false</ReactorSourceMap>` or the configuration compiles no source
mapping. It also notes that `Microsoft.UI.Reactor.Devtools` is deprecated when your
project references it: winapp DevTools replaces it.

### Native AOT, trimmed and attached apps

```powershell
winapp run . --aot --detach
winapp devtools inspect
```

`winapp run` with DevTools also sets `REACTOR_DIAGNOSTICS=1`, and with `--aot` (or any
publish) it builds with `-p:ReactorDiagnostics=true -p:ReactorSourceMap=true` unless you
pass those properties yourself. A Reactor version with a diagnostics mode then records
each control's component, call site, key and C# name on the control itself, and
DevTools reads them without loading any .NET code into the app. So the tree,
`get-source`, Reactor selectors, `search`, C# names and comments work in Native AOT
apps, trimmed builds and apps you `attach` to, as long as the app was launched with
`REACTOR_DIAGNOSTICS=1`.

State, handlers, re-renders and logs (`get-state`, `set-state`, `rerender`,
`invoke-handler`, `logs`) need the managed DevTools agent, which cannot load into a
Native AOT app or a trimmed build. `inspect` says so in one line, and those commands
fail with `reactor-unavailable`. With `--json`, the tree's `reactor` object then has
`"agent": "absent"` and `"annotations": "published"`.

If the Reactor version you use has no diagnostics mode, `inspect` shows the WinUI tree
with one note saying the components cannot be read. `winapp devtools attach` loads the
agent into a running .NET 8 or later app; C# call sites still need the
`winapp run` launch with DevTools.

Other error tokens are `reactor-absent` (the app does not use Reactor),
`reactor-no-sourcemap` (no C# call sites were recorded) and `reactor-error-screen` (the
window shows Reactor's error screen instead of its root).

`winapp run --on sandbox --devtools on` starts a Reactor app with or without XAML
files. The launch copies the project's `.cs` files with its XAML, read-only, so
comments left in the Sandbox anchor to C# call sites and are saved in the host
project.

## Preview one component

Reactor's component preview is an app argument, so pass it after `--`:

```powershell
winapp run . -- --preview Counter
```

The app receives `--preview Counter` and DevTools attaches as usual. Which component
the app mounts is up to Reactor's preview host; if the app exits right away with
*built without Reactor.DevtoolsSupport*, the Reactor version you use still needs
`Microsoft.UI.Reactor.Devtools` for previews. To capture a preview, use
`winapp ui screenshot -a <pid> --output preview.png`.

## Move from Reactor DevTools

`winapp run` with DevTools replaces `mur devtools` and the
`Microsoft.UI.Reactor.Devtools` package. You don't need the package or the
`Reactor.DevtoolsSupport` switch: remove them when you no longer use their MCP tools.
Agents use these commands with `--json` and the `winui-devtools` skill instead of the
MCP server.

| Instead of | Run |
|---|---|
| `mur devtools` | `winapp run .` (`--detach` to get the terminal back) |
| `mur devtools reload`, the restart loop | `winapp run . --watch` |
| `session list` | `winapp devtools list` |
| `tree`, `ancestors <selector>` | `winapp devtools inspect`, `inspect <selector> --ancestors` |
| `properties`, `styles`, `set-property` | `winapp devtools get-property <selector>`, `set-property <selector> <prop> <value>` |
| `resources`, `set-resource` | `winapp devtools get-resource <key>`, `set-resource <key> <value> --scope app` |
| `state` | `winapp devtools get-state <selector>` (any component), then `set-state`, `rerender` |
| `fire <Component>.<event>` | `winapp devtools invoke-handler <selector> OnClick` |
| `logs` | `winapp devtools logs --follow` |
| `click`, `type`, `focus`, `invoke`, `scroll`, `wait` | `winapp ui click`, `set-value`, `focus`, `invoke`, `scroll`, `wait-for` |
| `toggle`, `select`, `expand`, `collapse` | `winapp ui invoke <selector> --action toggle` (or `select`, `expand`, `collapse`) |
| `screenshot`, `windows` | `winapp ui screenshot`, `winapp ui list-windows` |
| `--component <Name>` | `winapp run . -- --preview <Name>` (see [Preview one component](#preview-one-component)) |
| references overlay | `winapp devtools get-property <selector>` (References) or the inspector |

`winapp ui` commands accept Reactor selectors such as `"<Counter>/Button"` while
DevTools is attached. `switch`, `windows.open` and `windows.close` change what the app
shows, so drive the app's own UI with `winapp ui` or relaunch with another
`--preview`.
