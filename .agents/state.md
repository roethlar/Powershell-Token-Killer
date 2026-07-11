# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- **SECURITY: question OPEN, owner-led consultation in progress — read
  `.agents/plans/security-layer.md` top section BEFORE any security
  work.** Two distinct things, do not conflate: (1) **DECIDED** — the
  declarative policy-file gate is rejected as the answer (owner: "brittle
  nonsense — alias it, use python, edit the rules file"); its slices are
  prior art, do not implement. (2) **NOT a decision** — the "ptk gets
  zero consideration / low-friction bypass gated on one careless yes /
  missing the `Remove-Mailbox CEO@company.com Y/n?` check" statement is
  the owner's FRAMING for an outreach consultation he is running
  himself, recorded verbatim in the plan. It is the problem any shape
  must answer, not a verdict already delivered. Do not cite it as a
  settled call. **Live candidate, UNVERIFIED: MCP elicitation**
  (server-initiated user confirmation mid-call) — verify spec support,
  per-harness client support, headless behavior (a prompt no client
  renders fails OPEN — worse than none). Also banked from the neutral
  cross-harness consultation and independent of the gate question:
  **secret redaction on output paths** (tokens/creds currently flow
  through the compressor into model context — a real leak, new to this
  repo's thinking), ConstrainedLanguage profile, authenticated-session
  TTL/process-teardown, and control-action gating placement above
  `ptk_reset`/`ptk_job kill`. Process lesson recorded in the plan: shape
  review BEFORE plan review — a review loop cannot question its own
  premise.
- **Two DRAFT plans, codex-reviewed, NEITHER APPROVED — no code written
  for either.** (a) `.agents/plans/rtk-rewrite-routing.md` — route all
  native work through `rtk rewrite` (owner direction 2026-07-10:
  "everything that isn't powershell should route through rtk"); loop
  CLOSED CONVERGED after 7 rounds, 15 findings, rounds 20→7→6→2→1→1→1.
  One item (rrp-15) closed by coder disposition, not reviewer grade —
  **flagged for owner ratification**: routing by substitution means
  name-keyed session hooks (a `git` breakpoint) never fire on a routed
  segment; inherent to substitution, already true of shipped
  single-command routing, disclosed in docs with `route=pwsh` as escape.
  (b) `.agents/plans/security-layer.md` — see the rejection above; its
  12 findings were all resolved before the premise fell.
- **`.agents/decisions.md` is UNDER HOLD** (owner, in-session 2026-07-10:
  "don't update decisions until we're done talking"; a premature entry
  was reverted at `6ee6f9e` — reverted, never rewritten). The rtk-routing
  direction and the security reframe both still need decision entries
  when the owner releases the hold.
- **~30 commits local-ahead of `origin/master`** (both plan drafts, all
  loop fixes, the loop records). Owner pushes himself.
- **issue-5/6 batch COMPLETE, loop CLOSED (2026-07-10)**: the
  implementation review ran three rounds (10 findings → 3 reopened + 5
  new → clean NO-FINDINGS close at `9a894e1`); all 25 findings fixed one
  per commit, record in `.agents/review/index.md`. Battery at head:
  dotnet 100/100, Pester 133/1 skipped, handshake PASSED, live stdio
  issue-repro checks 11/11 (canonical counts). Owner PUSHED 2026-07-10
  (`master` == `origin/master` at `f12dd46`); issues #5 and #6 got fix
  references and were CLOSED the same day. GitHub reports the remote
  renamed to `PowerShell-Token-Killer` (capital W) — the configured URL
  still works via redirect. Owner grant (2026-07-10, in-session):
  persistent approval to handle GitHub issues on this repo as
  appropriate (comment/close/triage without per-action asks).
  Machine-local note: this session's ptk server died during the slice-0
  incident and the installed payload predates the whole batch — a
  dev-install re-run is needed for live sessions to pick it up.
- Batch details (`.agents/plans/issue-5-6-invoke-semantics.md`, APPROVED
  in-session): slice 0 root cause — the "900s call never answered"
  incident was system sleep vs monotonic timers (harness MCP log +
  pmset evidence, recorded in the plan), no code defect; slice 1 neutral
  `[stderr]` by invocation provenance; slice 2 total wall-clock budget
  (queue + preflight + execution, sleep-safe deadline re-checks, fast
  queue expiry without executing); slice 3 never-queueing ptk_state.
  Plan loop i56p-1..11 CLOSED CONVERGED the same day. Also banked:
  codex calls ptk_invoke from inside its read-only sandbox (issue-3
  permission-surface datum). Process note (owner, in-session): approval
  requests now go to the owner as ≤50-word plain-English summaries; the
  broader plan-length fix will come via the governance toolkit, not this
  repo.
- **shell-dialect plan COMPLETE 2026-07-10**
  (`.agents/plans/shell-dialect.md`; decision entry + the sd1-4 and
  sd3-1 amendments in `.agents/decisions.md`). All slices done and
  codex-loop-closed: slice 0 (probes frozen), slice 1 (detector;
  sd1-1..7, owner-ratified), slice 2 (server wiring `8c234e8`;
  sd2-1..6), slice 3 (raw posture `fa1b23c` + elision-hint redesign
  `0840d13`; sd3-1 owner-adjudicated, sd3-2..4 RESOLVED), slice 4 (D3
  texts `8bb96b1`; sd4-1..2). The plan's live end-to-end Verification
  pass ran over real MCP stdio against the built server: **11/11**
  (refusals verbatim on both paths, bash -lc recovery with compression,
  raw counter + log line positive and negative, rtk-absent seam).
  Battery as of `e576962`: dotnet 80/80, Pester 133 passed / 1 skipped
  (canonical counts), handshake PASSED. Loop records:
  `.agents/review/index.md`; findings: `.agents/review/findings/sd*-*.md`.
  Plan tail EXECUTED 2026-07-10: the owner pushed mid-session (remote
  `master` reached `8bb96b1`), so the approved fix references were
  posted — issue #3 got the item-1 comment (stays open for items 2-4),
  issue #4 got its comment and was CLOSED. Only the sd4-fix tail
  (`428ac82`, `e576962`, the loop-close records) remains local-ahead.
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
- GitHub status as of 2026-07-10: issues #1, #2, and #4 CLOSED (#4
  closed this date with its fix reference); #3 open with the item-1 fix
  comment posted (items 2-4 remain a candidate small follow-up batch;
  the MCP permission-bypass ask is its own future owner-gated plan);
  #5/#6 open, triaged after-current-work.
- Standing flags carried forward: the release-distribution plan's
  slice 3 (`release.yml`) is now unblocked (shell-dialect complete), and
  its hook-default decision must close before its slice 4
  (`.agents/plans/release-distribution.md`); the remote `ci/slice-2`
  branch was DELETED 2026-07-09 (owner go in-session) — flag retired;
  the machine-local dev-install note lives in `## Next`.

## Next

1. **Security (highest, owner-driven — but the owner is running his own
   outreach on the framing; do not pre-empt his consultation with a
   plan).** Useful agent work while that runs: verify the
   MCP-elicitation candidate as FACT-FINDING only (spec support; which
   harnesses render server-initiated prompts; headless failure mode — a
   prompt nobody renders fails OPEN and is worse than none). Report
   findings; do not draft a plan or a shape until the owner's
   consultation returns. Do NOT re-derive the policy file.
2. **rtk-routing plan:** owner approval + ratification of the rrp-15
   disposition (see `## Now`). No code until then.
3. Owner push go for the local-ahead tail (~30 commits).
4. Owner releases the decisions.md hold, then record: the rtk-routing
   direction and the security reframe.
- Owner push go for the local-ahead docs tail (shared-runspace idea
  plan + its two review loops' records + drift fixes; ask-first
  policy). The issue-5/6 approval/push bullet that lived here is DONE —
  approved, built, pushed, issues closed (see `## Now`); caught stale by
  the grok reviewloop pass 2026-07-10.
- Remaining owner decision: hook-default (blocks release slice 4 only;
  owner chose "decide later" 2026-07-09 — re-present before that slice).
- Machine-local (owner's Mac + Windows box): the installed `~/.ptk`
  payload and the ptk_init-written nudges/hook predate the whole
  shell-dialect plan — a dev-install re-run (+ ptk_init) is needed for
  live sessions to pick up slices 1-4 and the new texts.
- Slice-7 test matrix (proposed, not yet in the decision entry): (1) AD module
  native import inside ptk_invoke + warm reuse across calls; (2) build and HOLD an
  Exchange implicit-remoting session in the warm runspace, Get-Queue latency call
  1 vs call N; (3) EXO/Graph via unattended cert auth (plan constraint: no
  interactive Connect-*). Server knobs for these tests (Program.cs): per-call
  timeout default 300s (`PTK_CALL_TIMEOUT_SECONDS`), idle self-exit default 4h
  orphan backstop (`PTK_IDLE_EXIT_SECONDS`).
- ~~go/no-go test (~2026-07-20)~~ — DECIDED: unqualified GO 2026-07-08,
  ahead of the window (`docs/history/decisions-archive.md`); this bullet
  was stale (caught by the shared-runspace plan review, spr-1). The
  Windows-box real-usage evaluation intent lives on only as the slice-7
  test matrix above and the shared-host measured-pain criterion in
  `.agents/decisions.md`.
- ~~Interim security posture: keep ptk_invoke on ask-per-call in the
  harness; build the policy-file gate if blanket-allow pressure
  appears.~~ — SUPERSEDED 2026-07-11: the criterion fired AND the
  response was rejected. Current posture: ask-per-call remains the only
  control, the owner considers that insufficient (`## Now`), and the
  replacement shape is unresolved. The decision entry stays OPEN until
  the hold lifts.

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
