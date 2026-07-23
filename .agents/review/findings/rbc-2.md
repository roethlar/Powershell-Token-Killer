# rbc-2: AuditRuntimeGate StopCoreAsync does not guarantee server.stopped on session/exporter failure

**Severity**: MAJOR
**Status**: Fixed, merged to `master` at `a6c4a17` (guard tests green; owner-approved merge)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/Audit/AuditRuntimeGate.cs:350-362`

## Evidence

`StopCoreAsync` awaits `sessionLifetime.ShutdownAsync()` and
`resources.StopExporterAsync()` without any try/catch. If either throws
during graceful drain, the entire `StopAsync` task faults, the host
records a faulted shutdown, and `Dispose` (line 320) captures the same
exception and rethrows — but `lifecycle?.Stop()` on line 361 never runs,
so `server.stopped` is never appended to the journal.

This violates the lifecycle contract that the stop record is the final
record in the audit journal.

## Predicted observable failure

A session-lifetime or exporter drain failure during shutdown leaves
the journal without a terminal `server.stopped` event. An operator or a
later startup-recovery pass cannot distinguish a clean shutdown from a
crash, and the chain appears truncated.

## What

Wrap the `sessionLifetime.ShutdownAsync()` and
`resources.StopExporterAsync()` calls in try/catch (downgrade to logged
warnings or a health marker), so `lifecycle?.Stop()` always runs and
`server.stopped` is appended as the final record even when the
session/exporter drain faults.

## Scope of fix

One method in `AuditRuntimeGate.cs`. No architectural change.

## Guard proof

Two guards in `AuditRuntimeGateTests`
(`Failed_runtime_drain_still_records_a_degraded_server_stopped`,
`Failed_owned_runspace_cleanup_still_records_a_degraded_server_stopped`)
inject a throwing session-lifetime drain and assert, via
`AssertDegradedServerStopped`, that `server.stopped` is still appended
as the final journal record and that its health snapshot carries the
`session.shutdown` degradation.

Fix: `StopCoreAsync` wraps `sessionLifetime.ShutdownAsync()` in a
non-fatal try/catch that calls `MarkDrainDegraded("session.shutdown")`
before `lifecycle?.Stop()`, so the terminal record always lands and
durably carries the drain failure. `MarkDrainDegraded` never softens an
existing unhealthy record (its failure class is the recovery key
consumed by `TryRecoverExternal` on restart) and tolerates a failing
health surface.

Deliberate deviation from the original "What": `StopExporterAsync`
stays fail-closed (NOT wrapped). A faulted export pipeline is an
audit-integrity failure that must not be papered over with a clean
terminal record; the pre-existing guard
`Export_loop_failure_prevents_false_server_stopped` still passes
unchanged. Full `AuditRuntimeGateTests` class: 17 passed, 0 failed.
Full suite: 1533 passed, 0 failed.

## Reviewer comments

Read-only review by Hermes subagent (audit subsystem pass). No external
fixed-SHA review has been dispatched.