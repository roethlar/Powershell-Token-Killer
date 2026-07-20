# rbc-9: WorkerOperationScheduler ignores injected TaskScheduler for outer admit dispatch

**Severity**: MAJOR
**Status**: Triaged 2026-07-19 — confirmed at `ec4d292` (outer admit hop `WorkerOperationScheduler.cs:145-149` on `TaskScheduler.Default`; inner hop `:255-259` on `_taskScheduler`). Fix committed 2026-07-19 at `27511b1` on `fix/rbc-batch2-scheduler-kestrel-admission`: outer admit hop routed through `_taskScheduler`; scheduler suite 20/20 green (deterministic scheduler now controls both hops).
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/Worker/WorkerOperationScheduler.cs:145-149`

## Evidence

`AdmitRequest` (line 145-149) schedules `RunScheduledRequestAsync` via
`Task.Factory.StartNew(..., TaskScheduler.Default)`, but the constructor
accepts a `_taskScheduler` parameter (line 65).
`RunScheduledRequestAsync` (line 251-259) then does a *second*
`Task.Factory.StartNew(..., _taskScheduler)` for the actual
`RunRequestAsync`.

The outer scheduling ignores the injected scheduler; only the inner
one respects it. This means the scheduler-injection test seam does not
control the first hop, so tests that inject a custom `TaskScheduler`
to force deterministic ordering only control the inner dispatch, not
the outer admit.

## Predicted observable failure

A test that injects a custom `TaskScheduler` to serialize execution
masks a concurrency bug that only manifests on the real
`TaskScheduler.Default` outer hop, because the outer hop always runs
on the thread pool regardless of the injected scheduler.

## What

Use `_taskScheduler` instead of `TaskScheduler.Default` in the outer
`Task.Factory.StartNew` at line 145-149, so the injected scheduler
controls both hops. Or document that the outer hop is intentionally
not under test control and only the inner hop is.

## Scope of fix

One line in `WorkerOperationScheduler.cs`. No architectural change.

## Guard proof

Written at `27511b1` (counting scheduler asserts injected-scheduler
dispatch). External review found the assertion (`QueueCount > 0`) too
weak: it stays green if the outer admit hop regresses to
`TaskScheduler.Default`. Hardened at `90b97b3`: assertion now requires
`QueueCount >= 2` with a failure message naming both hops
(`WorkerOperationSchedulerTests.cs`).

## Reviewer comments

Read-only review by Hermes subagent (execution/worker subsystem pass).
External fixed-SHA codex review of `27511b1`, turn 1: guard-weakness
finding. Adjudicated VALID — the guard as committed did not fail on
the exact mutation it was built to catch. Hardened at `90b97b3`.
Further review turns halted per operator instruction (no turn 2/3
dispatched).