# rbc-15: process-tree containment for background jobs — WIP finish (impl + tests + review)

**Severity:** MAJOR (containment gap: escaped orphans / recycled-pid kills)
**Status:** Remedies committed on `fix/rbc-15-process-tree-containment`; awaiting external fixed-SHA review.

## Commits (in order)
- `b4432dc` — rbc-15: process-tree containment for background jobs (initial impl: ProcessTreeContainment, BackgroundJobContainment, tests).
- `c17c1f9` — rbc-15: containment sweep + doc remedies for codex turn-1 findings.
- `08da8f5` — rbc-15: turn-2 remedies — eager exclusive-group init, terminal-release sweep, start-time incarnation guard.
- `a216734` — rbc-15: pre-start EnsureExclusiveGroup hooks + ReleaseAsync conversion (turn-2 finding 1 completion; turn-3 remedies).

## Review threads (codex MCP)
- Turn 1: `019f7d82-f482-7611-8141-b3afc19c27e8` — findings on exclusive-group first-launch degradation, escaped-orphan reaping, docs.
- Turns 2–3: `019f7df3-4f05-7b30-95ed-2332570fcdf1` — VERDICT: ACCEPT on remedy commit (turn 2/3 adjudication); remaining items folded into remedies now committed.

## Key findings and remedies
1. **First-launch fallback degradation** (turn-2 finding 1): the one-shot exclusive-group acquisition happened lazily inside the containment attach path, so the first root launched before acquisition degraded to fallback polling. Remedy: `ProcessTreeContainment.EnsureExclusiveGroup()` invoked pre-`Process.Start` in `BashProcessRunner` (both sites) and `RtkProcessRunner` (`a216734`).
2. **Escaped-orphan reaping on terminal release**: `BackgroundJobContainment.Release` → `ReleaseAsync`; terminal release now performs a deterministic sweep that reaps orphans that escaped the kill-on-close path (`08da8f5`, test updated in `a216734`).
3. **Pid/pgid recycling** (turn-2 finding 3): pid + pgid alone can be recycled; start-time incarnation guard (`StartIdentityToleranceTicks`) makes fallback-survivor matching incarnation-sensitive (`08da8f5`).

## Verification
- Focused suites (ProcessTreeContainmentTests + BashProcessRunner): 26/26 green at `a216734`.
- Full server suite: 1587/1587 green at `a216734` (2 m 14 s).

## Next
- External fixed-SHA review of `a216734` on the branch (per repo convention), then queue for accumulated master push (OWNER task #13).
