# rbc-9: WorkerOperationScheduler ignores injected TaskScheduler for outer admit dispatch

**Severity**: MAJOR
**Status**: Triaged 2026-07-19 — confirmed at `ec4d292` (outer admit hop `WorkerOperationScheduler.cs:145-149` on `TaskScheduler.Default`; inner hop `:255-259` on `_taskScheduler`). Fix approved: route the outer hop through `_taskScheduler` + hop-counting guard. Queued in fix batch 2.
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

Not yet written. A guard should inject a custom `TaskScheduler` that
counts hops and assert both the outer and inner hops run on it.

## Reviewer comments

Read-only review by Hermes subagent (execution/worker subsystem pass).
No external fixed-SHA review has been dispatched.