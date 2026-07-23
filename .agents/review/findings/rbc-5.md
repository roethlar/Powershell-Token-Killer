# rbc-5: Background jobs lack Job Object containment on Windows

**Severity**: MAJOR
**Status**: Open (disposition decided 2026-07-19: close via resilience R7)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**Files**: `server/PtkMcpServer/JobManager.cs:1820, 1962`

## Evidence

Both `TryRequestContainment` (line 1820) and `RequestKill` (line 1962) use
`Process.Kill(entireProcessTree: true)`. On Windows, this relies on the
.NET runtime's tree-walk (`CreateToolhelp32Snapshot` + kill descendants),
not a Job Object.

The foreground worker path uses `WindowsProcessTreeSupervisor` with a
Job Object and `KillOnJobClose` (`WindowsProcessTreeSupervisor.cs:44, 101`),
which the OS enforces even if the supervisor is hard-killed. Background
`JobManager` jobs have no Job Object containment. If the supervisor
process is hard-killed, background job trees are orphaned and keep
running.

The code acknowledges this via `RootTerminationConfirmed` tracking
(line 1426) and `ShutdownCoreAsync` throwing if any root is unconfirmed
(line 2100), but these only help on graceful shutdown — a supervisor
crash leaves the tree running with no one to kill it.

## Predicted observable failure

A supervisor crash (or `kill -9`) leaves a cold background job's process
tree running on Windows indefinitely, consuming resources and
potentially holding locks or network connections. No OS-level mechanism
reaps the tree because no Job Object owns it.

## What

Apply the same `WindowsProcessTreeSupervisor` Job-Object containment to
background jobs, or document that background jobs accept weaker
containment than foreground workers by design. If the latter, record
the decision and the operational mitigation (e.g., the shutdown
unconfirmed-root throw) in `.agents/decisions.md`.

## Scope of fix

Either wire `WindowsProcessTreeSupervisor` into the `JobManager`
background start path (larger), or record the accepted risk (smaller).
Depends on the owner's containment posture decision.

## Guard proof

Not yet written. If wired, a guard should assert that a hard-killed
supervisor leaves no orphaned background job tree on Windows
(verified via a Job-Object close callback or a post-crash process
scan).

## Disposition (owner, 2026-07-19)

Owner ruling: close rbc-5 through resilience R7's already-planned
creation-time worker containment, adding a Windows guard that a
background descendant dies on hard supervisor termination
(hard-supervisor-death background-descendant guard). A separate
creation-time contained launcher for the current in-process runtime
was offered and not chosen.

The saved spawn-then-assign post-start attach WIP (preserved
uncommitted on `fix/rbc-6-unix-sigkill-escalation` at `2b3ce1a`)
remains rejected: it has an admitted escape race and conflicts with
the approved creation-time containment contract. Do not continue or
commit it.

The finding stays Open until R7 lands with its guard proof; closure is
gated on R7's own external fixed-SHA review and green suite, not on
this record.

## Reviewer comments

Read-only review by Hermes subagent (execution/worker subsystem pass).
No external fixed-SHA review has been dispatched.