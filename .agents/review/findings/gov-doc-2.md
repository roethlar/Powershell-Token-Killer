# gov-doc-2: contradictory PTK_MODULE_PATH registration requirements

**Severity**: LOW — two passages give opposite registration contracts; no runtime impact
**Status**: In progress
**Branch**: master (direct commit per repo codex-loop precedent)
**Commit**: (filled after commit)

## Evidence
`.agents/plans/release-distribution.md` — the "Grounded facts" bullet (written
before the owner resolutions) says the installer registers with an explicit
`PTK_MODULE_PATH` rather than relying on the probe; the Design commitments
"Registration" bullet (written after the probe-order-flip decision) says
registration is the absolute binary path with no env block needed.

## Predicted observable failure
An implementer cannot derive a single registration contract; installer, tests,
and docs can diverge on whether binary-relative discovery or the env var is
the source of truth.

## What
The grounded-facts bullet became stale when the owner adopted binary-relative
discovery (probe-order flip); it was not updated to match.

## Approach
Rewrite the grounded-facts bullet to record the resolution: the probe exists
and currently checks cwd first; the plan flips it binary-dir-first, after
which registration needs no env block; `PTK_MODULE_PATH` remains the explicit
override only.

## Files changed
- `.agents/plans/release-distribution.md` — grounded-facts module-discovery bullet

## Guard proof
Prose-only change: no automatable guard. Manual check = the two passages agree;
reviewer re-grade on the corrected range is the verification.

## Coder dispute (if any)
None.

## Known gaps
None.

## Reviewer comments
(pending re-review)
