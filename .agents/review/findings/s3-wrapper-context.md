# s3-wrapper-context: context-changing native wrappers remain RTK eligible

**Severity**: MEDIUM — a wrapper submission the approved plan requires to stay
exact-original is instead handed to host RTK.
**Status**: Verified
**Branch**: `fix/s3-wrapper-context`
**Commit**: `bad4287b0a102904d0ed626c250c5a3fd8f15194`

## Evidence

`server/PtkMcpServer/Execution/ExecutionPlanner.cs:256` accepts every single
constant terminal Application; the route at lines 108-156 has no
wrapper-context exclusion. The approved plan explicitly requires wrapper
contexts such as `docker exec ... git ...` to execute the original and carries
rrp-12 as frozen regression evidence.

## Predicted observable failure

`docker exec app git status` and equivalent context-changing wrappers are
planned/audited as RTK instead of exact-original PowerShell execution. A host
RTK rule may reinterpret or filter a command whose actual target command runs
inside a different container/host context.

## What

The first structured planner preserves alias/function/batch exclusions but
does not implement its separately named wrapper-context fidelity exclusion.

## Approach

The first implementation freezes container `exec` wrappers for `docker`,
`podman`, `kubectl`, and `oc`. Their plans stay `NativeTerminal` but execute
`PowerShellDirect` with exact original text; forced RTK uses the truthful
`RtkFidelityExclusion` reason. Ordinary container commands remain RTK-eligible.

## Files changed

- `server/PtkMcpServer/Execution/ExecutionPlanner.cs` — detect the narrow
  context-changing container-exec boundary at the RTK planning chokepoint.
- `server/PtkMcpServer.Tests/ExecutionPlannerTests.cs` — guard all four wrapper
  families on auto and forced routes.

## Guard proof

- All four `Keeps_container_exec_wrappers_on_the_exact_PowerShell_path` cases
  failed before the production correction and passed afterward.
- Claude independently reverted the production exclusion and observed exactly
  those four failures while the remaining focused cases passed; restoration
  passed focused 44/44.

## Coder dispute (if any)

None. The finding is admitted from the explicit approved wrapper-context
contract.

## Known gaps

This closes the approved plan's named container-`exec` context only. `docker
run`, `ssh`, and `wsl` remain unchanged from the reviewed base; widening them
requires separate evidence and must not revive the unapproved broad rewrite
design. Matching any literal `exec` argument is intentionally conservative:
false positives lose compression only and preserve exact execution.

## Reviewer comments

Coder integrated RTK audit against fixed head `669ce6e`, recorded
2026-07-13T01:48:24Z. This finding is distinct from output bounds, preference
isolation, and the trailing background operator.

Claude Code 2.1.207 (`claude-opus-4-8`) reviewed
`4995bc02b776a90bcce1b268dd0e83078cd62a71..bad4287b0a102904d0ed626c250c5a3fd8f15194`
with `guard_confirmed=true` and verdict `accepted`, recorded
2026-07-13T03:35:21Z. It independently confirmed the plan's `docker exec`
example, option-prefixed forms, exact auto/forced behavior, and the
load-bearing four-case guard; its worktree was clean and removed.
