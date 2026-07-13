# s3-rtk-output-bounds: RTK output bypasses PTK text bounds and ANSI stripping

**Severity**: HIGH — the default terminal-native route can return unbounded,
ANSI-bearing text and contradict the tool's core bounded-output contract.
**Status**: Open
**Branch**: `fix/s3-rtk-output-bounds`
**Commit**: pending

## Evidence

`server/PtkMcpServer/RunspaceHost.cs:2008` runs `Compress-PtcOutput` only when
the dispatch provenance is `PowerShellObjects`. Every RTK plan is required to
carry `RtkUnknown`, so RTK output receives only `Out-String`.

Claude reproduced the regression at fixed head `669ce6e` with a fake RTK that
emitted 1,000 lines plus ANSI color. The reviewed head returned 1,002 lines /
8,903 characters with the escape sequence intact and no elision marker. Base
`0c08379` returned 401 lines / 3,559 characters, stripped ANSI, and included
the labeled elision marker.

## Predicted observable failure

A common RTK-routed command can flood model context beyond the adopted 400-line
/ 40-KB bound and retain terminal control sequences even though the tool
description promises both cleanup and labeled bounding.

## What

Slice 3 correctly prevents RTK-origin output from entering a second generic
`rtk log` pass, but does so by bypassing the entire PTK output module. The
approved plan requires PTK text cleanup/bounding to remain while provenance
suppresses only the lossy second RTK log optimization.

## Approach

Pending implementation. Pass execution provenance into `Compress-PtcOutput`,
run every non-raw result through the module, and use RTK provenance to skip
only `Invoke-PtcRtkLog`. Preserve the existing audited pinned identity for
direct PowerShell log shaping.

## Files changed

- Pending.

## Guard proof

- Pending: an end-to-end fake RTK must emit oversized ANSI text; the result
  must be ANSI-free and bounded with a labeled marker while the RTK invocation
  log proves no second `rtk log` call. Reverting the production correction
  must fail this guard.

## Coder dispute (if any)

None. The finding is independently admitted.

## Known gaps

None currently.

## Reviewer comments

Claude Code 2.1.207 (`claude-opus-4-8`) reviewed
`0c08379a02c796b8ea0e1779c196840c6a9b1269..669ce6ea47c520a9c3bb73411192630d56ed519b`
with `guard_confirmed=true` and verdict `reopened`, recorded
2026-07-13T01:48:24Z. The required correction above is the reviewer's
evidence-backed disposition.
