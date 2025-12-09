# Spec-let for moving global .winapp folder to .winappglobal

## Problem Statement

In a previous change, we moved the default location for WinAppCLI's global folder from:

```$UserProfile/.winapp``` (WinAppCLI 0.1.7)

To:

```$UserProfile/.winappglobal``` (WinAppCLI 0.1.8)

There's a few problems with this:
1. The .winapp folder can be large, we want to delete it from user's hard drives.
2. It's possible users will be using multiple versions of WinAppCLI.  For example, the user may
be switching between working on a Node project that's using WinAppCLI 0.1.7 and another that's using
WinAppCLI 0.1.8.

## Proposed solution

The proposed solution allows us to move to ".winappglobal" over time, and make sure the user never has an active ".winapp"
and ".winappglobal" dir at the same time (unless the user specifically configures it that way).

| Phase | Version | Behavior |
|-------|---------|----------|
| Phase 1 | v0.1.8 | If `.winappglobal` does not exist but `.winapp` does, just use `.winapp` and print a message "falling back to legacy location". |
| Phase 2 | When everyone is using 0.1.8 or later | When the fallback happens, change the logic to: "Found legacy .winapp folder, would you like to migrate this to the new location .winappglobal?" |
| Phase 3 | Later | Drop fallback logic completely |


## Other Potential Solutions
1. Print a warning message during "init" and "restore" if the old-style .winapp folder exists.
Prompt the user: "The old-style $UserProfile/.winapp folder exists, would you like to delete it? [y/N]".
In some future release we'll remove this logic.
2. Automatically delete the old-style .winapp folder when we detect it.  If the user later uses
a previous release of WinAppCLI (0.1.7 or prior), it will create the old-style .winapp folder again.

## Open Issues
- Do we want to have any utility for cleaning the .winappglobal folder?  It will get large over time.