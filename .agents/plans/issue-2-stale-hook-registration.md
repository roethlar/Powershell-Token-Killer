# Plan: issue #2 — stale hook registrations must not fail open silently

**Status:** APPROVED by owner 2026-07-09 (mid-session instruction: "new gh
issue reported. pick that up when done with this one" — the issue text is
the spec). Follows the issue-1 loop; multi-harness slice 3 queues behind it.

## Problem (verified live on this box)

`~/.claude/settings.json` carries a ptk hook entry pointing at
`F:\dev\Powershell-Token-Killer\src\ptk-hook.ps1` — a path that has never
existed in this repo's history (the hook was born at
`scripts/ptk-hook.ps1`), so the entry came from a hand-edit or sync, not a
layout move; the failure class is the same either way. Every Bash/
PowerShell call logs a non-blocking PreToolUse error and proceeds raw: the
redirect fails open with no detection anywhere. Sessions that already use
ptk_invoke never execute the hook, so the breakage is invisible exactly
until the case the hook exists for.

## Slices

1. **Stable registration target + healing in ptk_init (claude leg).**
   - The hook command targets the INSTALLED copy
     (`<PtkHome>/scripts/ptk-hook.ps1`) whenever it exists — the payload
     gate already requires `~/.ptk` for installs, and the installed
     payload survives checkout moves/renames (issue direction 2). Fallback
     to the checkout sibling (`$PSScriptRoot`) only when the payload lacks
     the script (test seams).
   - Install names what it replaced when the stripped entry pointed at a
     missing file ("replaced STALE registration ...").
   - `-Show` validates the REGISTERED command's `-File` target (not just
     the computed source path) and flags `STALE`.
2. **dev-install detects and heals.** Install mode (even without `-Hook`):
   a marker-matched hook entry pointing at a missing file is repaired by
   invoking the installed `ptk_init -Agent claude` — consent is evidenced
   by the existing entry; a missing target has no legitimate working
   configuration. Plus the layout-move note in the READMEs (direction 3).

Out of scope: server-side (`ptk_state`) validation of harness settings —
crosses the component boundary; reconsider only if init/dev-install
healing proves insufficient in practice.

## Verification

Pester with guard tests (stable-target selection; stale detection message;
-Show STALE flag), guard proofs by sabotaged revert, battery; dev-install
heal path is thin orchestration over the tested ptk_init logic (no
harness; stated, codex-reviewed). Live heal on this box's real stale entry
as the end-to-end check. Codex loop per the recorded process; issue #2
gets the fix reference after the owner pushes.
