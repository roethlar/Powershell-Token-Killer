# Plan: Fix the verified findings from the 2026-07-03 round-2 review

**Status:** Draft — awaiting owner approval.
**Decision basis:** A second external review (of a8d3d02..HEAD, the round-1 review
fixes) surfaced two findings; a 2026-07-03 verification session confirmed both
against the code. Both are defects in already-shipped, owner-approved work (round-1
slices 2 and 3 of `.agents/plans/review-fixes-2026-07.md`): the exit-code fix and
the all-items dispatch guard each closed the reported case but left a gap. This
plan fixes those gaps only; nothing paused behind the go/no-go is touched.

## Goal

The two round-1 fixes hold under the inputs that currently defeat them:

- A nonzero native exit code survives output shaping: a user script that exits N
  and emits log-shaped output still reports `[exit] N`, even when the rtk leg runs
  (rtk's own exit code must never replace or mask the user script's).
- Every specialized dispatch route guards (or accesses null-safely) **all** the
  properties its compressor dereferences, so no projection (`Select-Object` subset)
  can pass a guard and then throw under `Set-StrictMode -Version Latest`.

## Findings, verified

1. **High — rtk leg clobbers `$LASTEXITCODE`.** The server pipeline is
   `AddScript(script).AddCommand("Compress-PtcOutput")` (RunspaceHost.cs:162-163)
   and `ReadExitCode()` runs after the whole pipeline (RunspaceHost.cs:211). When
   output is log-shaped and rtk is present, `Invoke-PtcRtkLog` runs `& $rtk log`
   (psm1:812) — a native command in the same runspace — overwriting the user
   script's `$LASTEXITCODE` before the server reads it. Reviewer repro (confirmed
   mechanism): cmd script emitting timestamped ERROR lines, `exit 7` → response is
   `[ptk:log via rtk]` with no `[exit] 7`. If rtk itself failed, its nonzero code
   would be misattributed to the user's script — same root cause.

2. **Medium — dispatch guards are incomplete.** Round 1 made the guards check
   every item, but each guard lists only a subset of the properties its compressor
   directly dereferences under strict mode:
   - fs guards `PSIsContainer` (psm1:751-752); compressor dereferences `Name`,
     `LastWriteTime` (psm1:596-597, 604-607) and `Length` on files (psm1:605).
     Reviewer repro: `Get-ChildItem .\README.md | Select-Object PSIsContainer |
     Compress-PtcObject` → routes fs, throws PropertyNotFoundException.
   - MatchInfo guards `LineNumber, Path`; compressor dereferences `Line` (psm1:634).
   - Process guards `Id, ProcessName`; compressor dereferences `CPU`,
     `WorkingSet64` (psm1:659-660).
   - Service guards `Status, Name`; compressor dereferences `DisplayName` (psm1:679).
   On the server path `Compress-PtcOutput`'s never-throw contract degrades this to
   labeled raw output (lost compression, not a failed call); the direct
   `Compress-PtcObject` path throws.

## Slices

One finding per slice, one commit per slice (Git Safety). High first.

1. **Module: preserve `$LASTEXITCODE` across the rtk leg** (finding 1, High).
   In `Invoke-PtcRtkLog` (psm1:803-821), snapshot `$global:LASTEXITCODE` before
   invoking rtk and restore it in the existing `finally` alongside the temp-file
   cleanup (after `$?`/result capture — rtk's own success signal is still read
   first). The fix lives in the module, next to the only native command the
   shaping layer runs, and honors `Compress-PtcOutput`'s stated contract that
   shaping must not affect the call (psm1:826-827). No server change needed.
   *Verify:* new Pester case: point `PTK_RTK_PATH` at a stub `.cmd` that emits
   output and exits 0, set `$global:LASTEXITCODE = 7`, run log-shaped text through
   `Compress-PtcOutput`, assert `$global:LASTEXITCODE` is still 7 (and the rtk
   label is present, proving the leg actually ran). New dotnet test mirroring the
   reviewer's repro end-to-end (stub rtk via `PTK_RTK_PATH`): log-shaped output +
   `exit 7` → response contains both `[ptk:log via rtk]` and `[exit] 7`. Guard
   proofs (revert fix → each new test fails); full Pester + dotnet suites.

2. **Module: complete the specialized-dispatch guards** (finding 2, Medium).
   Principle: a guard lists every property its compressor directly dereferences;
   where a property is legitimately absent on real objects, the compressor uses
   null-safe access instead of the guard requiring it.
   - fs: guard `PSIsContainer, Name, LastWriteTime` on all items. Do **not** add
     `Length` to the guard — real `DirectoryInfo` has no `Length` property, so
     requiring it would kick every directory listing off the fs route. Instead
     switch psm1:605 to the null-safe `Get-PtcPropertyValue` (psm1:588 already
     uses it for the same property).
   - MatchInfo: add `Line` to the guard.
   - Process: add `CPU, WorkingSet64` to the guard.
   - Service: add `DisplayName` to the guard.
   *Verify:* new Pester cases: the reviewer's repro (fs projection with only
   `PSIsContainer`) → no throw, compresses via the generic path; analogous
   projection case for at least one other route; regressions — a real mixed
   file+directory listing still routes fs (proving `Length` is not over-guarded)
   and the existing deserialized-fs test still routes fs. Guard proofs; full
   suite.

3. **End-to-end + docs.** Full battery (Pester, `dotnet test`, handshake both
   variants) plus a live `ptk_invoke` spot-check of finding 1 against the real
   winget rtk (log-shaped failing command shows both the rtk label and `[exit]`).
   Update `state.md`; note the round-2 review disposition in the continuation
   decision entry if the owner wants it on record.
   *Verify:* all suites green; live check observed; docs updated.

## Risks

- **Slice 1:** restoring a *zero* snapshot also erases any nonzero exit code rtk
  itself produced — intended (shaping must be invisible), but it removes one
  diagnostic signal for a broken rtk; the existing labeled
  `[ptk:log rtk failed …]` fallback remains the visible failure channel.
- **Slice 2:** widening the guards moves more projections to the generic path.
  That is the designed behavior (the dispatch comment at psm1:730-737 states it),
  but deserialized objects are the regression to watch: Clixml round-trips keep
  properties as note properties, so real deserialized streams should still pass —
  the existing deserialized-fs Pester case is the canary, and slice 2 keeps it
  green.
- **Slice 1 test plumbing:** `PTK_RTK_PATH` must be restored after each test
  (`AfterEach`/`finally`) so a leaked override never redirects other tests — the
  suite already runs against a real rtk on this box.

## Non-goals

- No change to `Success` semantics or to the `[exit]` rendering contract
  (visibility only, established in round 1).
- No restructuring of the server pipeline (e.g. splitting script and shaping into
  two invocations) — the module-side fix is sufficient and smaller.
- No blanket null-safety rewrite of the compressors; only the one `Length` access
  the guard cannot cover.
- Nothing behind the go/no-go gate (universal wrapper, destructive-cmdlet gate),
  and no change to the go/no-go criteria or timeline.
