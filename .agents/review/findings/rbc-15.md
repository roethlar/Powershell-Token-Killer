# rbc-15: process-tree containment for background jobs — WIP finish (impl + tests + review)

**Severity:** MAJOR (containment gap: escaped orphans / recycled-pid kills)
**Status:** Review closed at turn 4 — diff-scoped findings: none; sole turn-4 finding adjudicated PRE-EXISTING → deferred (follow-up task below). Merged locally; queued for accumulated master push (OWNER).

## Commits (in order)
- `b4432dc` — rbc-15: process-tree containment for background jobs (initial impl: ProcessTreeContainment, BackgroundJobContainment, tests).
- `c17c1f9` — rbc-15: containment sweep + doc remedies for codex turn-1 findings.
- `08da8f5` — rbc-15: turn-2 remedies — eager exclusive-group init, terminal-release sweep, start-time incarnation guard.
- `a216734` — rbc-15: pre-start EnsureExclusiveGroup hooks + ReleaseAsync conversion (turn-2 finding 1 completion; turn-3 remedies).
- `3634fe7` — rbc-15: turn-4 remedy — tolerance-boundary regression test + wraparound rationale on `StartIdentityToleranceTicks`.

## Review threads (codex MCP)
- Turn 1: `019f7d82-f482-7611-8141-b3afc19c27e8` — findings on exclusive-group first-launch degradation, escaped-orphan reaping, docs.
- Turns 2–3: `019f7df3-4f05-7b30-95ed-2332570fcdf1` — VERDICT: ACCEPT on remedy commit (turn 2/3 adjudication); remaining items folded into remedies now committed.
- Turn 4 (same thread, final funded round): remedy `3634fe7` verified. VERDICT: FINDINGS — single MAJOR explicitly labeled **PRE-EXISTING; not remedied by `3634fe7`** (recycled process within the 100 ms `StartIdentityToleranceTicks` window remains kill-eligible on hosts with small PID space + high churn; `ProcessTreeContainment.cs:74-75`, `:397-411`). Diff-scoped findings: none.

## Adjudication (turn 4)
- The sole finding is outside the committed diff by the reviewer's own labeling; it restates the residual risk documented in Key finding 3 (tolerance-bounded incarnation guard). Accepting bounded 100 ms ambiguity was a deliberate trade against OS start-time clock granularity — exact matching would fail-open on legitimate survivors.
- Disposition: **deferred** — tracked as a follow-up hardening task (stronger incarnation identity, e.g. boot-id + /proc starttime composite) rather than a blocker. No further paid review rounds for rbc-15.

## Key findings and remedies
1. **First-launch fallback degradation** (turn-2 finding 1): the one-shot exclusive-group acquisition happened lazily inside the containment attach path, so the first root launched before acquisition degraded to fallback polling. Remedy: `ProcessTreeContainment.EnsureExclusiveGroup()` invoked pre-`Process.Start` in `BashProcessRunner` (both sites) and `RtkProcessRunner` (`a216734`).
2. **Escaped-orphan reaping on terminal release**: `BackgroundJobContainment.Release` → `ReleaseAsync`; terminal release now performs a deterministic sweep that reaps orphans that escaped the kill-on-close path (`08da8f5`, test updated in `a216734`).
3. **Pid/pgid recycling** (turn-2 finding 3): pid + pgid alone can be recycled; start-time incarnation guard (`StartIdentityToleranceTicks`) makes fallback-survivor matching incarnation-sensitive (`08da8f5`).

## Verification
- Focused suites (ProcessTreeContainmentTests + BashProcessRunner): 26/26 green at `a216734`.
- Full server suite: 1587/1587 green at `a216734` (2 m 14 s).
- Focused containment suite 6/6 green and full server suite 1587/1587 green at `3634fe7` (2 m 12 s).

## Next
- Merge `fix/rbc-15-process-tree-containment` into `master` locally with adjudication recorded (this file); master push remains owner-gated (task #13).
- Follow-up task: recycled-PID incarnation hardening (deferred turn-4 finding).
