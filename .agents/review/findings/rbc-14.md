# rbc-14: OutputStore retention deletes artifact files while holding the store gate

**Severity**: MAJOR
**Status**: Fix committed 2026-07-19 on `fix/rbc-14-retention-delete-offgate` at `5fc84ad`; codex turn-1 MAJOR (settle-before-evict) remedied at `0f1a78b` — awaiting codex turn-2 re-review before merge.
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

## Fix record (2026-07-19, `5fc84ad`)

- `TombstoneLocked` now only claims the delete
  (`ClaimStoredArtifactDeleteLocked`: sets `DeleteClaimed`, bumps
  `_deletesInFlight`, enqueues into `_pendingDeletes`). All unlink and
  handle-dispose I/O runs in `DrainPendingArtifactDeletes` outside
  `_gate`.
- Accounting semantics preserved: byte caps are decremented under
  `_gate` only after the unlink succeeds; a failed unlink clears the
  claim so the entry is retried by the next retention pass. Reservers
  that need in-flight deletes to settle wait on `Monitor.PulseAll`
  bounded by `PendingDeleteSettleTimeout`.
- Seam correction found while writing the guard: sealed artifacts lose
  their directory entry at seal time (retained-handle design), so
  `entry.Path` is null and the old `ArtifactDeleteStartingForTests`
  invocation (inside the `Path is { }` branch) never fired on Unix —
  the real delete io there is the retained-handle `Stream.Dispose()`.
  The seam now fires for every drained tombstone (empty string when
  path-unlinked), so wedge guards cover the common sealed path.
- Guard: `Retention_delete_io_does_not_wedge_the_store_gate` wedges the
  delete seam mid-retention, proves `Status` and `Read` on a live
  artifact complete while wedged, then proves the released delete
  reclaimed the expired artifact's bytes (post-release reservation fits
  the aggregate cap without evicting the survivor).
- Full suite at `5fc84ad`: 1578 total, 1 unrelated flake
  (`Guardian_death_contains_every_creation_barrier`, passes 7/7 in
  isolation), all OutputStore tests green.

## External review, turn 1 (codex, thread `019f7ce6-d2df-7570-ab51-336d1aef4679`)

VERDICT: FINDINGS — one MAJOR, accepted.

- MAJOR — `TryReserveCore` checked capacity and tombstoned a live
  eviction candidate before considering `_deletesInFlight` /
  `_pendingDeletes`; it waited only when no candidate existed. Because
  the off-gate design defers byte/slot reclamation to
  `DrainPendingArtifactDeletes`, a reservation racing a settling delete
  (including the expired claim made by its own inline retention pass)
  could permanently evict a live artifact whose capacity the settling
  delete was about to reclaim. Pre-fix behavior deleted synchronously
  under `_gate`, so this unnecessary eviction could not happen — a
  genuine regression introduced by the off-gate move, not a
  pre-existing defect.

### Remedy (turn 1)

- Extracted pure predicates (`NeedsSessionCapacityLocked`,
  `NeedsAggregateCapacityLocked`, `NeedsArtifactSlotLocked`,
  `NeedsCapacityLocked`) shared by the step-makers and the new guard so
  the two views of "insufficient" cannot drift.
- `TryReserveCore` now defers eviction while claims are settling
  (`_pendingDeletes.Count > 0 || _deletesInFlight > 0`): it drains
  queued claims / waits (bounded by `PendingDeleteSettleTimeout`) and
  re-checks, tombstoning a live candidate only when nothing is settling
  and capacity is still short. On settle-timeout with a wedged delete
  it fails truthfully with `capacity` instead of evicting — evicting
  there would just queue more unlinks behind the same wedged
  filesystem, the rbc-14 anti-goal.
- Guards: `Reservation_settles_expired_deletes_before_evicting_live_artifacts`
  (aggregate-bytes shape) and
  `Reservation_settles_expired_deletes_before_evicting_for_artifact_slots`
  (retained-slot shape, which the byte-only view would have missed
  because tombstoned entries hold their retained stream — and therefore
  an artifact slot — until drained). Refutation run: both fail 2/2
  against the pre-remedy store, pass with the guard.
- Full suite post-remedy: 1580/1580 green (includes
  `Guardian_death_contains_every_creation_barrier`).

## Codex turn-2 follow-up: stale-pulse wait (remedied `f624796`)

- Codex turn-2 contested remedy shape: a reserver that observes
  "settling" and elects to wait can wait on a capacity pulse that a
  concurrent settle finalize had **already fired** before the reserver
  reached its wait — wedging the reservation until the next unrelated
  retention pass fires the gate again (or the settle timeout fails it
  with `capacity` despite freed room).
- Remedy (`f624796`): `TryReserveCore` snapshots `_settleGeneration`
  under `_gate` at claim time; settle finalize increments the
  generation and pulses under the same lock. After the settling seam
  the reserver re-locks and re-checks the generation — if a settle
  completed in the window it loops and re-evaluates capacity directly
  instead of waiting on a pulse that already happened.
- Guard: `Reservation_rechecks_capacity_when_settle_finalizes_before_wait`
  drives the race deterministically via `reservationSettlingForTests`
  (seam between the claim snapshot and the wait) with the drain wedged
  by `artifactDeleteStartingForTests` until the reserver sits in the
  window. Red-verified: with the generation re-check neutralized
  (pre-remedy logic) the test FAILs; with the fix it passes.
- Full suite post-remedy: 1581/1581 green.
- Codex turn-3 (final, conversation `019f7ce6-d2df-7570-ab51-336d1aef4679`):
  VERDICT: ACCEPT, guard_confirmed: true. Finding closed at `f624796`
  within the 3-turn cap.
