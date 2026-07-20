# rbc-10: SIEM receiver Kestrel MaxRequestBodySize disabled (defense-in-depth gap)

**Severity**: MAJOR
**Status**: Triaged 2026-07-19 — confirmed at `ec4d292` (line anchor now `ReceiverApplication.cs:115`). Fix committed 2026-07-19 at `27511b1` on `fix/rbc-batch2-scheduler-kestrel-admission`: `kestrel.Limits.MaxRequestBodySize = MaxRequestBytes + 1`; sandwich guards pin the bound exactly (bound+1 still yields endpoint-owned `request_too_large`; bound+2 refused by the transport with 413 before commit/quarantine).
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `siem/PtkSiemReceiver/Ingest/ReceiverApplication.cs:40`

## Evidence

`kestrel.Limits.MaxRequestBodySize = null` removes Kestrel's built-in
body-size limit entirely. The custom `ReadBoundedAsync` enforces
`options.MaxRequestBytes` for the `/v1/logs` endpoint, but this removes
a layer of defense.

If a future endpoint is added that reads the request body differently
(or via model binding), it would have no size limit at all, creating a
memory-exhaustion vector.

## Predicted observable failure

A future SIEM endpoint (e.g., a query API or a health-check that
deserializes a body) accepts an unbounded request, exhausting memory
under load. The custom `ReadBoundedAsync` only protects `/v1/logs`.

## What

Set `MaxRequestBodySize` to `options.MaxRequestBytes` (or a slightly
higher bound) at the Kestrel level. This provides defense-in-depth
without changing current behavior for the `/v1/logs` endpoint.

## Scope of fix

One line in `ReceiverApplication.cs`. No architectural change.

## Guard proof

Written at `27511b1` (sandwich guards: bound+1 endpoint-owned
`request_too_large`; bound+2 refused 413 before commit/quarantine).
External review noted the bound+2 test used `ByteArrayContent`
(declared Content-Length), leaving the streamed/chunked path unpinned.
At `90b97b3` the comment names the declared-length path explicitly and
`Chunked_body_beyond_kestrel_backstop_fails_closed_mid_stream` pins
the undeclared/chunked bound+2 path (413 or transport abort; no
commit/quarantine) (`OtlpIngestIntegrationTests.cs`).

## Reviewer comments

Read-only review by Hermes subagent (SIEM receiver pass). External
fixed-SHA codex review of `27511b1`, turn 1: test-framing finding
(declared vs. chunked path). Adjudicated TECHNICALLY CORRECT, MARGINAL
— chunked enforcement is Kestrel framework behavior, so the added test
pins Kestrel's contract, not ours; accepted as cheap belt-and-
suspenders at `90b97b3`. Further review turns halted per operator
instruction (no turn 2/3 dispatched).