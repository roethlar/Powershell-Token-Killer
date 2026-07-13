# s3-rtk-preference-isolation: routed RTK execution is controlled by warm native preferences

**Severity**: HIGH — ambient warm-session native error preferences can discard
valid routed stdout and pollute persistent `$Error` state.
**Status**: Open
**Branch**: `fix/s3-rtk-preference-isolation`
**Commit**: pending

## Evidence

`server/PtkMcpServer/Execution/ExecutionPlanner.cs:144` constructs a PowerShell
native invocation of the pinned RTK path. `server/PtkMcpServer/RunspaceHost.cs:2003`
runs that text inside the persistent user pipeline.

The coder audit set `$PSNativeCommandUseErrorActionPreference=$true` and
`$ErrorActionPreference='Stop'`, cleared `$Error`, then routed a native command
that emitted stdout/stderr and exited 7. At exact head `669ce6e`, stdout was
discarded, a terminating `Program "rtk" ended...` error was returned, and the
follow-up warm session contained two `$Error` entries. The warm server was
reset after the probe.

## Predicted observable failure

The same terminal-native submission changes result and persistent session
state solely because unrelated warm PowerShell preference variables were set.
Valid stdout can disappear, and later calls observe new `$Error` entries.

## What

RTK execution still occurs as a PowerShell native command inside the mutable
warm runspace. This violates the plan's carried rrp-11 requirement for
preference-independent capture.

## Approach

Pending implementation. Move RTK execution to preference-independent host
process capture while preserving the audited plan/dispatch barrier, exact
argument semantics, startup-frozen identity, cwd, deadline, streams, exit
code, bounded output, and pre-start-only fallback rules.

## Files changed

- Pending.

## Guard proof

- Pending: set both native/error preferences in the warm session, route a
  controlled nonzero RTK result with stdout/stderr, and assert stdout/exit/
  stderr are preserved while `$Error` remains unchanged. Reverting the host
  capture correction must fail the guard.

## Coder dispute (if any)

None. The finding was independently reproduced and admitted.

## Known gaps

The fix must not weaken audit ordering, exact pinned identity, deadlines, or
the no-retry-after-start contract.

## Reviewer comments

Coder integrated RTK audit against fixed head `669ce6e`, recorded
2026-07-13T01:48:24Z. This finding is non-duplicate of Claude's RTK output
bounding finding.
