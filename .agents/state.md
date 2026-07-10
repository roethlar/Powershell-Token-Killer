# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- **ACTIVE WORK: shell-dialect plan** (`.agents/plans/shell-dialect.md`,
  owner-approved 2026-07-09; decision entry + the sd1-4 amendment in
  `.agents/decisions.md`). Slice 0 (probes, results frozen in the plan)
  and slice 1 (the token-aware detector `Get-PtcShellDialectFinding` in
  the module) are DONE. Slice-1 loop CLOSED CONVERGED + owner-ratified
  2026-07-09 (sd1-1..sd1-7). **Slice 2 (server wiring) DONE, loop CLOSED
  2026-07-10** (`8c234e8` + sd2-1..sd2-6 all RESOLVED). **Slice 3 (raw
  posture per D2) DONE, loop CLOSED 2026-07-10 round 4**: reword +
  raw-visibility landed at `fa1b23c`; findings sd3-1 (CONTESTED →
  owner-delegated adjudication, D2 amendment in `.agents/decisions.md`,
  agent-experience principle recorded as an Earned Practice in
  `.agents/repo-guidance.md`), sd3-2, sd3-3, sd3-4 (the elision-hint
  redesign `0840d13` — marker advice is composed by the elision itself)
  all closed (`.agents/review/index.md`;
  `.agents/review/findings/sd3-*.md`). Battery as of `0840d13`: dotnet
  80/80, Pester 133 passed / 1 skipped (canonical counts), handshake
  PASSED. NEXT BUILD: slice 4 (D3 texts — dialect line in hook deny +
  ptk_init nudge + README routing section; docs slice), then the plan's
  live end-to-end Verification pass, then fix references on issues #3
  (item 1) and #4 after the owner's next push.
- **Owner decisions recorded 2026-07-09 (in-session, post-handoff):**
  (a) slice-1 convergence close RATIFIED (above); (b) the push
  happened — `master` == `origin/master` == remote HEAD at `c71ea70`,
  so the "29 commits local-ahead / push go" item is CLOSED; (c) issues
  **#5** and **#6** triaged **after current work** — finish shell-dialect
  slices 2-4 first, then take #5/#6 as the next batch (not parked, not
  preempting); (d) the release-plan hook-default question stays
  DELIBERATELY OPEN by owner choice ("decide later") — re-present it
  before release slice 4 (installers) starts; (e) owner approved posting
  fix references on issue #3 (item 1) and issue #4 — and closing #4 —
  but execution is DEFERRED until the fixing slices land and are pushed
  (per the plan's Verification section: #3 item 1 needs slice 2; #4
  needs slices 3-4). Do not post before then.
- GitHub status as of 2026-07-09: issues #1 and #2 CLOSED; #3 open
  (item 1 is in the shell-dialect plan; items 2-4 recorded there as a
  candidate small follow-up batch; the MCP permission-bypass ask is its
  own future owner-gated plan); #4 open (addressed by the plan; close
  after slices 3-4 land + push, per the deferred-execution decision
  above); #5/#6 open, triaged after-current-work.
- Standing flags carried forward: the release-distribution plan's
  slice 3 (`release.yml`) is queued behind the shell-dialect work, and
  its hook-default decision must close before its slice 4
  (`.agents/plans/release-distribution.md`); the remote `ci/slice-2`
  branch was DELETED 2026-07-09 (owner go in-session;
  `git push origin --delete ci/slice-2` confirmed `[deleted]`) — flag
  retired; machine-local (owner's Mac + Windows box): the installed
  `~/.ptk` payload predates the detector work — a dev-install re-run is
  needed for live sessions to pick up slices 1-4 once they land.

## Next

- Shell-dialect slice 4 (dialect line in hook deny + nudge + README
  routing section — docs slice per D3), then the plan's live end-to-end
  Verification pass. Battery baseline: see the counts `as of 0840d13` in
  `## Now`.
- After slices 2-4 land + push: post the approved fix references
  (issue #3 item 1, issue #4) and close #4; then the #5/#6 batch.
- Remaining owner decision: hook-default (blocks release slice 4 only;
  owner chose "decide later" 2026-07-09 — re-present before that slice).
- Slice-7 test matrix (proposed, not yet in the decision entry): (1) AD module
  native import inside ptk_invoke + warm reuse across calls; (2) build and HOLD an
  Exchange implicit-remoting session in the warm runspace, Get-Queue latency call
  1 vs call N; (3) EXO/Graph via unattended cert auth (plan constraint: no
  interactive Connect-*). Server knobs for these tests (Program.cs): per-call
  timeout default 300s (`PTK_CALL_TIMEOUT_SECONDS`), idle self-exit default 4h
  orphan backstop (`PTK_IDLE_EXIT_SECONDS`).
- (~2026-07-20, owner back at work) Run the go/no-go test on the real Windows box:
  does the model use ptk_invoke unprompted for Exchange/AD work, and does it save
  real time? Both yes → Phase 2 earns a second look. Ignored like rtk → archive the
  project with the finding. Definition in `.agents/decisions.md`.
- Interim security posture: keep ptk_invoke on ask-per-call in the harness; the
  policy-file gate design is recorded in the continuation decision, build only if
  real usage creates blanket-allow pressure.

## Blockers

- None. (Handoff re-verification note: the 2026-07-09 governance-refresh
  flag "`.agents/repo-map.json` / `.agents/artifact-manifest.json` are
  retired-but-locally-modified, remove by hand" is now stale — the tree
  is clean and both files are tracked unmodified; deleting them remains
  an owner option, not a blocker.)

## Verification

- Automated verification entry point: see `.agents/repo-guidance.md`
  (Verification) — Pester suite, `dotnet test server`, and the manual
  handshake script. Current counts are recorded with `as of <commit>` in
  `## Now`, never duplicated here.

## Active Sources

- `AGENTS.md`
- `.agents/repo-guidance.md`
- `.agents/decisions.md`
- `.agents/plans/shell-dialect.md`
- `.agents/review/index.md`

## Unrecorded Repo Memory

- None known.

History: rotated entries live verbatim in `docs/history/state-archive.md`.
