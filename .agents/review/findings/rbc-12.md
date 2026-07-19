# rbc-12: SIEM receiver has no rate limiting or backpressure

**Severity**: MAJOR
**Status**: Triaged 2026-07-19 — confirmed (only the `SqliteIngestStore` writer gate `SemaphoreSlim(1,1)` exists; no admission cap). Fix approved: global admission concurrency cap rejecting 503 when saturated; per-client rate limiting deferred to SIEM ingest hardening. Queued in fix batch 2.
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

Not yet written. A guard should open more concurrent connections than
the cap and assert excess connections are rejected with 503 before
memory grows unboundedly.

## Reviewer comments

Read-only review by Hermes subagent (SIEM receiver pass). No external
fixed-SHA review has been dispatched.