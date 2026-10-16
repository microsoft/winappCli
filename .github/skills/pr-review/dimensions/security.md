# Security

Apply `_shared-contract.md`. Set `Domain: security`.

You know the standard vulnerability classes. What matters here is **where this
particular CLI is exposed**.

## This tool's attack surface

It launches Windows SDK build tools (`makeappx`, `signtool`, `makepri`,
`cppwinrt`) via `Process.Start`; generates and installs code-signing certificates
and PFX files; reads and writes `appxmanifest.xml`; downloads NuGet packages and
SDK tools over the network; registers sparse and loose-layout packages with
Windows; drives UI Automation against arbitrary running apps; and ships an npm
wrapper that shells out to the native binary.

The recurring shape of a real bug here is **a value from a manifest, a config
file, or a CLI arg reaching a process invocation or a file path unvalidated.**

## Threat model

winapp is a local developer tool that runs as the current user. A finding is in
scope only when the attacker controls something the user did **not** author
and would reasonably trust: a downloaded package or tool, a network response, a
project or manifest cloned from someone else, or another app the UI commands
drive.

Out of scope: an attacker who can already write the user's files, environment
variables, PATH, registry, or NuGet config. They already run code as the user,
so winapp gives them nothing extra. Drop these findings; do not downgrade them.

**Exception: elevation.** Some steps run as administrator, such as installing a
certificate to the LocalMachine store or turning on Developer Mode through UAC.
If an elevated step reads anything the user can write (a file, PATH, a temp
folder, config), that *is* in scope. It turns user access into admin access.

Every security finding must name **who the attacker is, what they control, and
what they gain** in `Show me`. If you cannot, or they gain nothing new (see
below), it is not a finding.

## Is it worth fixing here?

Being in scope is not enough to block a PR. First ask what the attacker
**newly** gains. Drop the finding if:

- They could already do the same thing another way with the access the attack
  needs. Example: the attack needs a malicious app running on the user's
  machine, and that app could already read the same files directly. Be strict:
  it must be the *same* gain. "Building a cloned repo already runs its code"
  doesn't excuse a command users expect to be safe on that repo, such as
  inspecting or restoring it, if that command now runs the repo's code.
- The effect isn't harmful. Example: winapp crashes or prints a wrong message,
  and nothing leaks or runs. Signing, trusting, or installing the wrong thing
  *is* harmful.

For what survives, weigh three things:

- **Likelihood:** does the attack work in a normal setup, or does it need
  several unusual conditions at once?
- **Impact:** what the attacker gets: a signing key or secret, code running on
  other machines, or something minor.
- **Fix cost:** a small guard, or a redesign of the feature?

If the attack is unlikely **and** the fix means a redesign, the finding is
`medium`. Recommend a follow-up issue instead of changing this PR. Example: "a
malicious app the user is already automating could spoof a window title"
doesn't justify rebuilding `winapp ui` targeting.

Still block when the fix is small, or when the impact is severe: leaked signing
keys or secrets, or code running on other people's machines (for example,
tampered downloads or a published package). The escalation minimums below
still apply; those patterns are cheap to fix.

## Escalations (mandatory minimums)

| Pattern | Minimum severity |
|---|---|
| `BinaryFormatter` / `SoapFormatter`, or JSON with `TypeNameHandling != None` | critical |
| `Process.Start` with unsanitized external input | high |
| Hardcoded credential (anything but the documented dev default `password`) | high |
| New HTTP listener bound to anything but loopback | high |
| Download over non-HTTPS, or from a non-Microsoft host with no checksum | high |
| Structured manifest XML edited by regex in new code | medium |
| Missing elevation check on a path that requires admin | medium |

Regex on manifest XML is allowed **only** for pre-parse placeholder replacement
(`$targetnametoken$`) on raw text before the XML is valid. Structured edits go
through `AppxManifestDocument`.

## Required: name the red-team attempt

For the highest-risk item you find, describe one concrete attempt the
orchestrator can run in the Validate phase — e.g. *"pass a manifest whose
`Source` is `a b\" --flag` and confirm the extra flag reaches makeappx."*
Findings stay `static-only` until that phase reproduces or refutes them.

This is the most valuable thing you produce: a security finding nobody can
reproduce gets ignored, and one that gets reproduced gets fixed.

## Reminders

- In-scope security findings are **never** suppressed for low confidence. Emit
  them. Out-of-scope ones (see Threat model) are never emitted.
- If the dangerous sink is in the diff but the input source is not, use
  `Confidence: medium` and say so in `Show me`.
- Do not flag what the CA-series analyzers already catch.
