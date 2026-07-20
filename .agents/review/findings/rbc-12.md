# rbc-12: SIEM receiver has no rate limiting or backpressure

**Severity**: MAJOR
**Status**: Triaged 2026-07-19 — confirmed (only the `SqliteIngestStore` writer gate `SemaphoreSlim(1,1)` exists; no admission cap). Fix committed 2026-07-19 at `27511b1` on `fix/rbc-batch2-scheduler-kestrel-admission`: `IngestAdmissionGate` (`MaxConcurrentRequests`) refuses saturation before any buffering with transient 503 + Retry-After + `admission_capacity`; guard proves refusal under a parked commit and capacity recovery after release. Per-client rate limiting remains deferred to SIEM ingest hardening.
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `siem/PtkSiemReceiver/Ingest/ReceiverApplication.cs` (no rate-limiting middleware)

## Evidence

The receiver accepts unlimited concurrent connections and requests
from any holder of a valid client certificate. There is no per-client
rate limit, no global concurrency cap, and no queue-depth limit.

Combined with rbc-11 (no retention enforcement on master), a single
compromised client cert can exhaust disk space. The `SemaphoreSlim(1, 1)`
in `SqliteIngestStore` serializes writes but does not reject or shed
load — it just queues requests, which also consumes memory under
sustained load.

## Predicted observable failure

A single compromised client cert sends thousands of concurrent large
OTLP requests. The in-memory queue grows (each request holds up to
`MaxRequestBytes` in a `MemoryStream`), the SQLite write queue backs
up, and the receiver exhausts memory or disk before any limit fires.

## What

Add a global concurrency cap (e.g., a `SemaphoreSlim` around request
admission that rejects with 503 when full) and optionally a per-client
rate limit. The concurrency cap alone closes the memory-exhaustion
vector; the per-client limit closes the noisy-neighbor vector.

## Scope of fix

One middleware in `ReceiverApplication.cs`. No architectural change to
the storage or validation layers.

## Guard proof

Written at `27511b1` (integration guard: refusal under a parked commit
with transient 503 + Retry-After; capacity recovery after release).
External review noted no unit-level pin that refusal happens *before*
any body buffering. At `90b97b3`: `HandleIngestAsync` made internal
(`InternalsVisibleTo` already present) and
`ReceiverAdmissionRefusalTests` saturates the gate, passes a throwing
body stream plus `null!` options/committer, asserts 503 +
`Retry-After: 1` + non-empty status body, and asserts the refusal path
does not call `Exit()` (no over-admission under saturation).

## Reviewer comments

Read-only review by Hermes subagent (SIEM receiver pass). External
fixed-SHA codex review of `27511b1`, turn 1: missing unit pin on
refusal-before-buffering. Adjudicated PARTIALLY VALID — the
integration guard proves refusal under saturation but not body-read
timing; the unit pin adds real mutation-sensitivity (including
`Exit()` slot accounting), though the `null!` collaborator technique
is blunt. Unit pin added at `90b97b3`. Per-client rate limiting
remains deferred to SIEM ingest hardening. Further review turns halted
per operator instruction (no turn 2/3 dispatched).