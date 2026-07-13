# s3-wrapper-context: context-changing native wrappers remain RTK eligible

**Severity**: MEDIUM — a wrapper submission the approved plan requires to stay
exact-original is instead handed to host RTK.
**Status**: Open
**Branch**: `fix/s3-wrapper-context`
**Commit**: pending

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

Pending implementation. Freeze the narrow wrapper shapes that are excluded by
the approved Slice 3 contract, classify them as `MixedDataflow`/fidelity
excluded as appropriate, and preserve exact original execution without
inventing broad compound routing.

## Files changed

- Pending.

## Guard proof

- Pending: an Application-backed `docker exec ...`-shaped submission must
  plan `PowerShellDirect`, retain exact original text, and never invoke the RTK
  stub. Removing the exclusion must fail the focused guard.

## Coder dispute (if any)

None. The finding is admitted from the explicit approved wrapper-context
contract.

## Known gaps

The correction must be narrow and evidence-based; it must not revive the
unapproved broad `rtk rewrite` design.

## Reviewer comments

Coder integrated RTK audit against fixed head `669ce6e`, recorded
2026-07-13T01:48:24Z. This finding is distinct from output bounds, preference
isolation, and the trailing background operator.
