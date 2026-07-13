# s3-rtk-preference-isolation: routed RTK execution is controlled by warm native preferences

**Severity**: HIGH — ambient warm-session native error preferences can discard
valid routed stdout and pollute persistent `$Error` state.
**Status**: In progress
**Branch**: `fix/s3-rtk-preference-isolation`
**Commit**: `40923784601bf8063d9461188b04be3940374c7d` (reopened;
Windows correction pending)

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

The immutable RTK plan now carries a typed argument vector and audited working
directory instead of executable PowerShell text. Eligibility preserves exact
PowerShell argument behavior by retaining submitted numeric spellings and
attached-parameter forms while leaving unsupported native-argument-passing,
stop-parsing, tilde-expansion, and wildcard-shaped submissions on the exact
PowerShell route.

After the durable dispatch barrier, `RtkProcessRunner` starts the pinned RTK
identity directly with `ProcessStartInfo.ArgumentList`, independent of warm
PowerShell preferences. It captures and drains bounded UTF-8 stdout/stderr,
enforces the remaining deadline, preserves the direct exit code, and never
retries after `Process.Start` succeeds. A proven identity, preparation, or
start failure carries a typed pre-start reason; the host must authorize a
second durable dispatch before running the plan's exact-original fallback
once. Captured stdout enters only the already authorized trusted compression
function with `rtk_unknown` provenance, so it receives ANSI/bound cleanup
without a second RTK pass or ambient `$Error` mutation.

## Files changed

- `server/PtkMcpServer/Execution/ExecutionPlan.cs`
- `server/PtkMcpServer/Execution/ExecutionPlanner.cs`
- `server/PtkMcpServer/Execution/RtkProcessRunner.cs`
- `server/PtkMcpServer/RunspaceHost.cs`
- `server/PtkMcpServer.Tests/AuditCallContextTests.cs`
- `server/PtkMcpServer.Tests/ExecutionDispatchTests.cs`
- `server/PtkMcpServer.Tests/ExecutionPlannerTests.cs`
- `server/PtkMcpServer.Tests/InvokeToolTests.cs`
- `server/PtkMcpServer.Tests/RtkProcessRunnerTests.cs`
- `server/PtkMcpServer.Tests/PtkMcpServer.Tests.csproj`
- `server/PtkRtkTestFixture/Program.cs`
- `server/PtkRtkTestFixture/PtkRtkTestFixture.csproj`
- `server/PtkMcpServer.slnx`

## Guard proof

- The coder replaced the direct runner leg with the former warm native
  invocation. `Rtk_capture_ignores_warm_native_error_preferences_and_preserves_error_state`
  then failed with the expected terminating exit-7 preference error and
  passed after byte-exact restoration.
- Independently removing the typed start-failure handoff made
  `Proven_rtk_start_failure_uses_the_audited_exact_fallback_once` fail because
  the exact original never ran. Removing the wildcard/tilde exclusion failed
  the four corresponding planner cases, and substituting AST extent text for
  string values failed exact quote/empty-argument cases. Restoration passed
  the combined focused 27/27 guard.
- Claude independently repeated the direct-capture, proven-pre-start, and
  argument-expansion red-to-green mutations in a disposable exact-head
  worktree. The restored head passed 1,030/1,030 .NET tests, 139 Pester tests
  with two platform skips, and the zero-warning stdio handshake; both coder
  and reviewer trees were clean and the review worktree was removed.

## Coder dispute (if any)

None. The finding was independently reproduced and admitted.

## Known gaps

The immediate identity recheck cannot bind an OS executable across the final
check/start window, dynamically loaded code, Windows ACL changes, or macOS
xattrs/quarantine. The installed payload therefore remains an
administrator-protected dependency. Root-process kill and stream completion
also cannot prove every descendant or remote effect; terminal wording keeps
that coverage unknown. Eliminating the residual identity race requires a
future execution-bound Unix/Windows handle, not another path check.

The wildcard exclusion is deliberately conservative: PowerShell 7 passes
native wildcards literally, so those submissions retain exact behavior but
skip RTK compression. Raw RTK shaping is safe because the planner cannot
produce an RTK plan when `raw=true`; that coupling remains implicit. The new
Windows fixture and Windows/Legacy argument branches were statically reviewed
on macOS. The first exact-archive Windows checkout validation reopened the
finding with ten focused/integration failures; the correction and a fresh
exact-head Windows proof are required before the repeated integrated review.

## Reviewer comments

Coder integrated RTK audit against fixed head `669ce6e`, recorded
2026-07-13T01:48:24Z. This finding is non-duplicate of Claude's RTK output
bounding finding.

Claude Code 2.1.207 (`claude-opus-4-8`) reviewed
`e766866a65469dad93384a95d572288ba96e1381..40923784601bf8063d9461188b04be3940374c7d`
with `guard_confirmed=true` and verdict `accepted`, recorded
2026-07-13T04:55:03Z. Claude independently reproduced the exact warm
preference failure, the missing-fallback failure, and the tilde/wildcard
eligibility failures, then restored each mutation byte-exactly. It also
validated numeric, empty, quoted, and attached argument behavior against a
real PowerShell 7 native probe; confirmed second-dispatch-before-fallback,
single-use fallback, pinned-identity, deadline/stream bounds, truthful
`LASTEXITCODE`, and provenance-aware single-pass shaping; passed the restored
full battery; and removed its clean disposable worktree without touching the
clean coder tree.

One intermediate restored-head run timed out in four unchanged child-process
tests under load; the same clean tree passed 1,030/1,030 immediately before
and after. Claude classified this as existing timing flakiness rather than a
diff defect. Its conservative-wildcard and implicit-raw-coupling observations
remain recorded above as non-blocking residuals.

Windows exact-archive validation of code head
`40923784601bf8063d9461188b04be3940374c7d` reopened this finding, recorded
2026-07-13T05:03:51Z. The local archive and uploaded copy matched SHA-256
`CE5707231353BABCBA096E90076513B9532A7C0B1FE9C64F337A474D8110FF2E` before
expansion in a disposable `NETWATCH-01` checkout. The .NET battery passed
1,020/1,030: `Direct_capture_preserves_stdout_stderr_and_nonzero_exit` observed
two trailing spaces in captured stderr,
`Deadline_stops_the_started_root_without_retrying_it` found no start marker,
and eight `InvokeToolTests` RTK-route guards returned empty/no routed output.
Pester and handshake did not run after the required .NET gate failed. The
remote checkout, uploaded archive, and local archive were all confirmed
removed; installed payloads and persistent configuration were untouched. This
platform result supersedes branch readiness but does not erase the historical
macOS Claude acceptance above. Canonical machine-level failure evidence is in
`.agents/machines.md`.
