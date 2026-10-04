# WinUI layout and responsive review reference

Load this when reviewing responsive behaviour, breakpoints, or state coverage on a data-driven page. SKILL.md already covers theming, control choice, and XAML landmines — don't duplicate.

## Page planning template

Fill these in before writing XAML or reviewing a page:

- Primary user task:
- Secondary tasks:
- Content type / density:
- Navigation structure: flat / hierarchical / hybrid:
- App silhouette: shell (left/top nav) / document / canvas-hero / dense-grid / list-detail / single-task:
- Breakpoint behaviour at small (`<640`), medium (`641–1007`), large (`≥1008`) epx:
- Input modes covered: keyboard, mouse, touch, pen:

## Responsive techniques (least to most disruptive)

Pick the lightest change that preserves the task:

| Technique | Use when | Example |
|---|---|---|
| **Reposition** | Same content fits better elsewhere | Side details move below main on narrow widths |
| **Resize** | Same content needs different space | Wider reading column at large widths |
| **Reflow** | Sequence can wrap or change columns | One column becomes two at `≥1008` epx |
| **Show/hide** | Secondary metadata is optional at small widths | Hide avatar/details, keep a route to full info |
| **Re-architect** | Width changes the task model | Single-pane list-detail becomes side-by-side |

Breakpoints are **app-window effective pixels**, not physical screen pixels. The window can be `<640` epx on a 4K monitor.

## State coverage for any data-driven page

Every collection, fetch, or async-bound surface should explicitly handle:

- **Loading** — progress text or skeleton; not just a spinner with no context
- **Empty** — what happened and what the user can do (call-to-action, not just "no items")
- **Error** — cause if known + a retry/repair affordance; never colour-only
- **Offline / permission denied** — separate from generic error if the recovery path differs
- **Selection** — including keyboard arrow-key behaviour and multi-select where relevant

If any of these aren't represented in the view model, the page isn't done.
