# gov-doc-3: stale "four open questions" in state.md Next

**Severity**: LOW — handoff inconsistency; could make the next session re-open settled decisions
**Status**: In progress
**Branch**: master (direct commit per repo codex-loop precedent)
**Commit**: (filled after commit)

## Evidence
`.agents/state.md` — the Now entry (updated with the resolutions) says only
the hook default remains open, but the short Next bullet still says "the four
open questions are the resume actions" (written before the owner resolved
three of them on 2026-07-04).

## Predicted observable failure
A next session following the Next bullet re-raises RID set, version, and
install root with the owner even though the same file records them as
resolved — settled decisions get reopened, wasting the owner's time.

## What
The Next bullet was not updated when the Now entry absorbed the resolutions.

## Approach
Point the Next bullet at the one genuinely open item (hook default) plus the
approval/push-go actions, matching the Now entry.

## Files changed
- `.agents/state.md` — Next bullet for the release plan

## Guard proof
Prose-only change: no automatable guard. Manual check = Next and Now agree;
reviewer re-grade on the corrected range is the verification.

## Coder dispute (if any)
None.

## Known gaps
None.

## Reviewer comments
(pending re-review)
