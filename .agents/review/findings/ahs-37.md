# ahs-37: Slice 0 review closure is not durably evidenced

**Severity**: MEDIUM — a cold implementation or drift pass can trust an
unsupported closure claim in the canonical current-state entry point and skip
required re-verification.
**Status**: In progress
**Branch**: `fix/ahs-37-record-slice0-review`
**Commit**: `62d32eb` (fix; intake record `46563c2`)

## Evidence

At reviewed head `a9fe9ecae75b712f5ab48fd7636613a3e0ffb35a`,
`.agents/state.md:17-19` says focused RTK, audit/export, and platform/schema
reviews of the Slice 0 freeze closed with no remaining medium-or-higher issue.
`git grep -n -E 'SLICE 0 CLAUDE REVIEW|a9fe9ec|focused RTK, audit/export'
a9fe9ec -- .agents/review .agents/state.md` finds only that state claim: the
committed tree has no finding, verdict, reviewer identity/version, fixed SHA,
or timestamp supporting it.

## Predicted observable failure

A cold Slice 1 session treats the frozen Slice 0 contracts as independently
cleared and skips re-verification, while a drift pass cannot reconstruct or
verify the claimed reviews from the repo. The canonical state entry point
therefore overstates what the committed evidence proves.

## What

Slice 0 recorded a broad focused-review closure claim only in state, without
the durable evidence required by the repo-is-memory invariant. The claim must
be removed or scoped, and the actual fixed-SHA Claude verdict must be recorded.

## Approach

Record Claude's fixed-SHA reopened verdict in this finding and the review
index, then remove the unsupported focused-review closure sentence from
`.agents/state.md`. Preserve the independently evidenced pre-implementation
dual-review statement and point cold sessions to the canonical review index.

## Files changed

- `.agents/state.md` — remove the unsupported closure claim and retain only
  evidence-backed Slice 0 status.
- `.agents/review/index.md` — record the fixed-SHA Slice 0 verdict and status.
- `.agents/review/findings/ahs-37.md` — preserve intake, proof, and re-grade
  evidence.

## Guard proof

- Manual docs guard at the reviewed head: the fixed-commit `git grep` above
  finds the closure claim but no supporting review record.
- After the fix: the unsupported claim must be absent from `.agents/state.md`,
  the index and this finding must contain the fixed-SHA verdict, and
  `git diff --check` must pass. Product-code sabotage is inapplicable to this
  docs-only evidence correction.

## Coder dispute (if any)

None. The cited claim and missing committed evidence are independently
reproducible, and the predicted cold-session/drift failure follows directly.

## Known gaps

None.

## Reviewer comments

Claude Code 2.1.207 (model reported as `claude-fable-5`, read-only), reviewed
head `a9fe9ecae75b712f5ab48fd7636613a3e0ffb35a` against base
`2a83723369e2752b4d930fd57c3ae4b5f484bad9`, `guard_confirmed=true`,
`verdict=reopened`, recorded 2026-07-11T16:53:29Z.

> MEDIUM | `.agents/state.md:17-19` | The committed focused-review closure
> claim has no recorded evidence anywhere in the head tree; the review index
> ends at plan approval, and no Slice 0 finding, archive, or verdict record
> exists. A cold Slice 1 session can trust that ungrounded closure and skip
> re-verification, while a drift pass cannot verify it. Record the focused
> review evidence durably or drop/scope the claim as unrecorded pending the
> synchronous review of `a9fe9ec`.
