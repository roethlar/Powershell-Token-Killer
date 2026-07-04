# gov-doc-2: contradictory PTK_MODULE_PATH registration requirements

**Severity**: LOW — two passages give opposite registration contracts; no runtime impact
**Status**: Verified
**Branch**: master (direct commit per repo codex-loop precedent)
**Commit**: `2704cb7`

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
codex (codex-cli 0.142.5), reviewed f77b3537999e196a32605c7f9210db288f7c4f41
against base a43897a66db016d31eeec993ff52466761cd1d39, 2026-07-04T15:11Z.
Verdict: **accepted**. guard_confirmed: n/a (prose; manual-consistency check
in lieu, per the finding's Guard proof). "The module-discovery facts now
describe cwd-first probing as current-but-wrong, defer the intended fix to
the binary-dir-first design commitment, and name Registration as the single
contract. `PTK_MODULE_PATH` remains only an explicit override; I found no
contradictory registration guidance."
