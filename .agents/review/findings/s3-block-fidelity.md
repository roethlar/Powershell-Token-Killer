# s3-block-fidelity: clean and dynamicparam blocks can be dropped by RTK routing

**Severity**: MEDIUM — an eligible-looking end block can cause other submitted
PowerShell blocks, including cleanup, to be silently omitted.
**Status**: Open
**Branch**: `fix/s3-block-fidelity`
**Commit**: pending

## Evidence

`server/PtkMcpServer/Execution/ExecutionPlanner.cs:260` and
`server/PtkMcpServer/Execution/ExecutionPlanner.cs:416` reject
`ParamBlock`/`BeginBlock`/`ProcessBlock`, but not `CleanBlock` or
`DynamicParamBlock`. The same file's mixed-guidance classifier already rejects
both omitted blocks.

Claude parsed and executed
`clean { ... } end { git status }`, confirmed both blocks normally run, then
confirmed the planner at `669ce6e` constructs only the RTK-wrapped `git status`
command. The clean block is absent from the execution script.

## Predicted observable failure

Cleanup or dynamic-parameter logic can be skipped without a label, including
lock release, temporary-file deletion, or state restoration. Audit records the
submission as native/RTK even though the submitted PowerShell semantics were
not executed.

## What

The new structured planner does not include all named PowerShell script-block
forms in its fidelity exclusions, so its end-block extraction can discard
other executable blocks.

## Approach

Pending implementation. Reject `CleanBlock` and `DynamicParamBlock` in RTK
eligibility and classify either as `MixedDataflow`, preserving the exact
original PowerShell text.

## Files changed

- Pending.

## Guard proof

- Pending: planner guards for clean and dynamicparam submissions must assert
  `PowerShellDirect`, `MixedDataflow`, and byte-exact original execution text.
  Removing either production exclusion must fail its case.

## Coder dispute (if any)

None. The finding is independently admitted.

## Known gaps

The older trusted preflight parser has a related block-set omission, but this
finding is scoped to Slice 3's execution planner and its observable routing
failure.

## Reviewer comments

Claude Code 2.1.207 (`claude-opus-4-8`) reviewed
`0c08379a02c796b8ea0e1779c196840c6a9b1269..669ce6ea47c520a9c3bb73411192630d56ed519b`
with `guard_confirmed=true` and verdict `reopened`, recorded
2026-07-13T01:48:24Z.
