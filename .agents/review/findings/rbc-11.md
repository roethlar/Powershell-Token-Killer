# rbc-11: SIEM receiver has no retention enforcement on master

**Severity**: MAJOR
**Status**: Triaged 2026-07-19 — confirmed (retention options parsed in `SiemReceiverConfiguration.cs:59-60,93-95`, referenced nowhere else on master). Deferred: gated on the S3H `plan/mini-siem-storage-hardening` land/park decision. Interim deployment warning landed in `siem/PtkSiemReceiver/README.md` and the gate recorded in `.agents/decisions.md` (2026-07-19, rbc-batch branch); docs follow-up complete.
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**Files**: `siem/PtkSiemReceiver/Configuration/SiemReceiverConfiguration.cs:271-284`,
`siem/PtkSiemReceiver/Storage/SqliteIngestStore.cs` (no enforcement)

## Evidence

`RetentionMaxAgeDays` and `RetentionMaxTotalBytes` are parsed,
validated, and stored in `SiemReceiverOptions` but are never referenced
by any code on master. The `events`, `quarantine`, and `custody` tables
(which store full `raw_request` BLOBs up to `MaxRequestBytes` each)
grow without bound.

The repo's `.agents/state.md` records that S3H (storage hardening) is
on an isolated branch `plan/mini-siem-storage-hardening` and is not on
master. This finding is expected for the current scope — but a deployed
master build has no protection against disk exhaustion.

## Predicted observable failure

A malicious or misbehaving authenticated client fills the disk by
sending many large OTLP requests. The `events` table grows without
bound; once the disk is full, SQLite writes fail and the receiver
cannot accept new records (it must reject, losing audit custody).

## What

Either merge the S3H retention enforcement from
`plan/mini-siem-storage-hardening` into `master`, or document that
master is not deployable without S3H and gate the deployment guidance
on the S3H branch landing. The owner's pending S3H land/park/review
decision (`.agents/state.md` Next item 1) is the relevant gate.

## Scope of fix

Depends on the S3H decision. If merged, the retention enforcement
code on the isolated branch is the fix. If parked, add a deployment
warning to the SIEM receiver README and record the dependency in
`.agents/decisions.md`.

## Guard proof

Not yet written. A guard should assert that after inserting more than
`RetentionMaxTotalBytes` of records, a retention sweep brings the
store under the limit, and that records older than
`RetentionMaxAgeDays` are purged.

## Reviewer comments

Read-only review by Hermes subagent (SIEM receiver pass). No external
fixed-SHA review has been dispatched.