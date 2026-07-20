# rbc-13: ColdCommandResolution MatchesCurrentResolution PATH race (safe but racy)

**Severity**: Note (refuted as defect at 2026-07-19 triage; was MAJOR)
**Status**: Refuted as defect 2026-07-19 — live-PATH re-resolution at commit with fail-closed matching is the intended integrity gate (PATH hijack is in-threat-model); a snapshot compare would weaken it, and non-shadowing PATH appends do not change resolution, so the benign-penalty window is narrower than described. Docs-only follow-up complete (2026-07-19, rbc-batch branch): design requirement recorded as a comment at `ColdCommandResolution.MatchesCurrentResolution`. Contested by maintaining agent (owner-delegated triage); codex consensus AGREE (thread `019f7cb9-c587-79c1-994b-a28e8d7b1ba1`, turn 1/3).
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/Execution/ColdCommandResolution.cs:238-246`

## Evidence

`MatchesCurrentResolution` re-resolves the command name against the
live process PATH at commit time via `ColdPathCommandResolver.Resolve`.
The original capture also read PATH at capture time.

`RunspaceHost.RestoreEnvironmentBaseline()` and user scripts can mutate
PATH between plan creation and `CommitStart`. A PATH change can cause
`MatchesCurrentResolution` to either (a) find a different executable
and fail the match (safe), or (b) find the same path string but the
file at that path was replaced — caught by the `ExecutableFileIdentity`
digest check.

The behavior is safe (fails closed), but the race window is the entire
prepare→commit gap. A user who legitimately extends PATH after plan
creation but before commit sees their job rejected with
`RtkTargetResolutionChanged`. `ColdPathCommandResolver.Resolve` uses
`Environment.GetEnvironmentVariable("PATH")` — a process-wide mutable
variable — with no snapshot.

Downgraded from blocker (the subagent's initial rating) to major
because the failure mode is safe — it's a correctness/operability
concern, not a containment or integrity breach.

## Predicted observable failure

A user who extends PATH with a new tool directory between job prepare
and commit sees their cold RTK job rejected even though the tool they
wanted is still resolvable. The rejection is honest (PATH changed) but
penalizes a benign action.

## What

Either snapshot PATH at plan creation and compare against the snapshot
at commit (reject only if the resolving entry changed, not if PATH
grew), or document that any PATH mutation between prepare and commit
is a proved no-start by design and the user must re-prepare after PATH
changes.

## Scope of fix

One method in `ColdCommandResolution.cs`, plus possibly a PATH
snapshot in `ColdPathCommandResolver`. No architectural change; the
`ExecutableFileIdentity` digest check is the real integrity gate.

## Guard proof

Not yet written. A guard should mutate PATH (add a new entry that does
not shadow the resolving one) between prepare and commit and assert the
job is still admitted (if the snapshot approach is taken) or rejected
with the documented code (if the documented-behavior approach is taken).

## Reviewer comments

Read-only review by Hermes subagent (execution/worker subsystem pass),
downgraded from blocker to major by the orchestrating session after
verifying the failure mode is safe (fails closed). No external
fixed-SHA review has been dispatched.