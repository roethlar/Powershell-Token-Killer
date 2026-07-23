# rbc-7: OutputStore Read/Search can wedge the store lock on a slow filesystem

**Severity**: MAJOR
**Status**: Merged to master 2026-07-19 (`a9b0476`, fix branch head `48f87ac`; external review via codex MCP converged within the 3-turn cap; owner-approved batch merge; retention-path residue tracked as rbc-14)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/Execution/OutputStore.cs:584-638`

## Evidence

After sealing, the artifact's directory entry is removed and reads go
through the retained `FileStream.SafeFileHandle`. This is correct on
Unix (open fd survives unlink) and Windows (delete-pending).

However, `Read` (line 597) and `Search` (line 663) call `IsUtf8Boundary`,
which does `ReadExact(handle, offset, 1)` — a synchronous
`RandomAccess.Read` while holding `_gate`. On a wedged filesystem (the
exact scenario `TryStartForegroundOperation` is designed to isolate),
these synchronous reads block the lock and stall every other
`Status` / `Read` / `Search` / `TryReserve` caller.

The foreground-operation lane only protects seal/reserve; reads bypass
it.

## Predicted observable failure

A slow or stuck filesystem (NFS stall, disk full, I/O hang) causes a
single `ptk_output` read to block the entire `OutputStore` lock
indefinitely. Every subsequent `ptk_output` or `ptk_job` call that
touches the store queues behind the wedged read.

## What

Move `Read`/`Search` off the `_gate` lock, or add a bounded timeout to
the `RandomAccess.Read` calls (e.g., via `CancellationToken` or a
`Task.Run` with `Task.WaitAsync`). The retained-handle read does not
need to serialize against seal/reserve operations since the artifact is
already sealed and unlinked.

## Scope of fix

One or two methods in `OutputStore.cs`. No architectural change; the
seal/unlink invariant is preserved.

## Guard proof

Not yet written. A guard should inject a stalled `RandomAccess.Read`
(or a filesystem that hangs) and assert that a concurrent `Status` call
completes within a bounded time instead of queueing behind the wedge.

## Reviewer comments

Read-only review by Hermes subagent (execution/worker subsystem pass).
No external fixed-SHA review has been dispatched.

## Disposition (owner, 2026-07-19)

Fix committed on `fix/rbc-7-outputstore-read-wedge` at `bb2df34`
(branch cut from master `e082d53`). `Read` and `Search` now snapshot
the artifact entry under `_gate` and perform all `RandomAccess` file
I/O against the retained `SafeFileHandle` outside the lock,
re-validating entry state afterward. The seal/unlink invariant and the
foreground-operation lane are unchanged.

Guard tests (in `OutputStoreTests.cs`):
- `Read_and_search_file_io_does_not_wedge_the_store_gate` — asserts
  concurrent store operations complete while a read's file I/O is
  stalled, instead of queueing behind the wedge.
- `Expiry_during_unlocked_read_reports_the_tombstone_state` — covers
  the entry-expiry race opened by moving I/O outside the lock.

Evidence: full suite green on the fix branch (1577/1577). The fix was
developed in the `fix/rbc-6-unix-sigkill-escalation` checkout and
ported file-identically (no committed drift for either file between
`1441858` and `e082d53`).

The finding stays Open until the branch passes its own external
fixed-SHA review and is merged to master; closure is gated on that
review, not on this record.