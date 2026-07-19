# rbc-14: OutputStore retention deletes artifact files while holding the store gate

**Severity**: MAJOR
**Status**: Triaged 2026-07-19 — confirmed; fix approved (move unlink/dispose off `_gate`, preserving delete-failure retry and cap-accounting semantics). First in fix queue; own branch + external fixed-SHA review per rbc-7 precedent.
**Source**: external fixed-SHA review (codex, turn 1) of `fix/rbc-7-outputstore-read-wedge` at `bb2df34`
**File**: `server/PtkMcpServer/Execution/OutputStore.cs:1178,1233` (line anchors at `db23ec4`)

## Evidence

`RetainLocked` (line 1178) is the inline retention sweep and always runs
under `_gate`. For tombstoned entries it calls
`TryDeleteStoredArtifactLocked` (line 1233), which performs
`SecureAuditStorage.DeleteRetainedProtectedFile` (an unlink, ~line 1241)
and `stream.Dispose()` (a handle close, ~line 1255) while the lock is
held. `Read`, `Search`, `Status`, and `Seal` all invoke `RetainLocked`
on entry.

The rbc-7 fix moved reader file I/O off `_gate`, but on a hung
filesystem (the rbc-7 threat model) an unlink or close blocks the same
way a read does, so a retention pass triggered by any caller can still
wedge every other store operation behind the gate.

## Predicted observable failure

With an NFS stall / disk hang, the first store call after an artifact's
TTL elapses runs the retention sweep, blocks in unlink/close under
`_gate`, and every subsequent `ptk_output` / `ptk_job` call queues
behind it — the same observable failure as rbc-7, now confined to the
delete path.

## What

Restructure deletion so only bookkeeping happens under `_gate`: detach
`entry.Stream`/`entry.Path` and update byte accounting inside the lock,
then unlink and dispose outside it. The design must preserve the
current delete-failure retry semantics (`TryDeleteStoredArtifactLocked`
returns `false` and keeps the entry for a later pass) and must not
reorder accounting relative to deletion in a way that lets caps be
exceeded on a failed delete.

## Why this is a separate finding

Deferred from the rbc-7 fix on purpose: moving deletion I/O changes
retry/accounting atomicity and deserves its own fix and its own
external review cycle rather than widening an already-reviewed diff.
