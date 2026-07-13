# s3-background-operator: RTK routing drops the PowerShell background operator

**Severity**: MEDIUM — an asynchronous native submission is silently changed
into synchronous RTK execution with different timing, output, job, and timeout
semantics.
**Status**: Open
**Branch**: `fix/s3-background-operator`
**Commit**: pending

## Evidence

`server/PtkMcpServer/Execution/ExecutionPlanner.cs:256` accepts a single-command
`PipelineAst` without checking `pipeline.Background`. The RTK execution script
at line 147 uses `command.Extent.Text`; PowerShell excludes the trailing `&`
from both the command and pipeline extents. `ClassifyDomain` at line 410 also
reports this shape as `NativeTerminal`.

The coder audit reproduced `bash -c 'sleep 1; printf done' &` at `669ce6e`.
Auto route took 1,085 ms and returned `done`, proving synchronous RTK
execution. `route=pwsh` returned in 123 ms with a running `PSRemotingJob`.

## Predicted observable failure

A command intended to run asynchronously blocks the tool call, returns the
wrong output/job state, and can consume the foreground deadline. Audit also
records a native/RTK route rather than the exact mixed/control-flow semantics.

## What

The structured planner omits the AST's background flag from its fidelity gate,
then constructs routed text from an extent that cannot preserve that flag.

## Approach

Pending implementation. Reject background pipelines from RTK eligibility and
classify them as `MixedDataflow`, so the exact original text including `&`
executes once in PowerShell.

## Files changed

- Pending.

## Guard proof

- Pending: an Application-backed background pipeline plan must be
  `PowerShellDirect`, `MixedDataflow`, carry the exact original including `&`,
  and have no RTK provenance. Removing the background check must fail the
  guard.

## Coder dispute (if any)

None. The finding was independently reproduced and admitted by the coder.

## Known gaps

None currently.

## Reviewer comments

Coder integrated audit against fixed head `669ce6e`, recorded
2026-07-13T01:48:24Z. This finding is independent of Claude's two integrated
findings and blocks Slice 3 acceptance under the same review loop.
