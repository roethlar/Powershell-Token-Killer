# mhi-7: nudge-block writer trims user-owned whitespace outside the marker block

**Severity**: LOW — edge-case content mutation of the user's guidance file;
no effect on hook execution.
**Status**: Verified
**Branch**: master (direct, per the recorded codex-loop precedent)
**Commit**: (filled in at commit)

## Evidence
`scripts/ptk_init.ps1` — `Get-PtkNudgeStripped` called `.Trim()` on the
ENTIRE file text, and both `Install-PtkNudgeBlock` and
`Uninstall-PtkNudgeBlock` write that trimmed remainder back.

## Predicted observable failure
A `~/.claude/CLAUDE.md` whose user content starts with significant leading
whitespace (e.g. an indented Markdown code block at the top of the file)
loses that whitespace on `-Nudge` install and on `-Uninstall`, changing the
file's Markdown semantics. Install-then-uninstall does not round-trip to
the original bytes.

## What
The block writer was surgical about the marker block but not about the
surrounding user content: whole-file Trim() was used to keep repeated
cycles from accreting blank lines, at the cost of mutating user bytes.

## Approach
`Get-PtkNudgeStripped` now removes exactly the shape the installer writes —
an optional blank-line separator pair, the marker block, one trailing
newline — and nothing else; the whole-file Trim() is gone. The installer
appends `separator + block` to the stripped text as-is. Repeated
install/remove cycles are stable (the strip removes exactly what the
previous install added), and uninstall returns the pre-install bytes.

## Files changed
- `scripts/ptk_init.ps1` — Get-PtkNudgeStripped surgical pattern;
  Install-PtkNudgeBlock separator construction.

## Guard proof
- `tests/PwshTokenCompressor.Tests.ps1::round-trips the nudge file
  byte-exactly, preserving user whitespace` — writes a nudge file whose
  content has leading whitespace and no trailing newline, runs -Nudge
  install then -Uninstall, asserts the final bytes equal the original.
  FAILS with the whole-file Trim() reverted in; PASSES with the fix.

## Coder dispute (if any)
None.

## Known gaps
A HAND-placed marker block mid-file with single (not blank-line)
separation is removed cleanly but its single separating newline collapses;
out of contract — the installer never writes that shape.

## Reviewer comments
Raised by codex (Codex v0.143.0, gpt-5.5, read-only) reviewing 057a5ee,
2026-07-09. Re-grade: see index.
