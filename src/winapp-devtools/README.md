# DevTools

DevTools provides the in-app toolbar and inspector window. Native UI code and
XAML templates live under `native\WinApp.DevTools.Native`.

## Text scaling source check

Run from the repository root, without building or changing Windows settings:

```powershell
.\scripts\validate-devtools-text-scaling.ps1
.\scripts\tests\Test-DevToolsTextScaling.ps1
```

The **DevTools Text Scaling** CI workflow runs both commands. The checker flags
fixed heights around text, fixed widths around icons, text in fixed-height grid
rows, disabled text scaling, unscaled button icons, chevron size overrides,
numeric sizing style setters, and constant C++ width/height setter calls.

Prefer minimum dimensions and content-sized rows. Intentional constraints belong
in `scripts\devtools-text-scaling-exceptions.json`, with the exact file, rule,
control name or expression, match count, and reason. Unused exceptions fail.

This is a source guardrail, not proof of correct rendering. XAML is checked as an
XML tree. C++ checks only complete tags in ordinary adjacent string literals and
direct numeric sizing calls. It does not resolve runtime string expressions,
raw strings, inherited styles, templates at runtime, or variable-driven setters.
C++ ancestor layout and command-icon relationships are not inferred. Keep the
geometry and rendered UI tests for behavior the source check cannot establish.
