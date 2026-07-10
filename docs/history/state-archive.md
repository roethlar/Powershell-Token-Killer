# State Archive

Rotated verbatim from `.agents/state.md` by the `handoff` operator; current
state lives there. Entries below are historical record, newest rotation first.

## Rotated 2026-07-09 (handoff at d352e66)

### From `## Now`

- **2026-07-09 (night, latest): shell-dialect slice-1 codex re-grade
  round 1 RECORDED.** At head `acb0f39` (codex, Codex v0.144.0,
  gpt-5.6-sol, read-only): sd1-2 RESOLVED; sd1-1 and sd1-3 held NOT
  RESOLVED — each fix covered one half of its finding (ambient-only
  resolution; comment-only blanking). 4 new findings, every claim
  independently verified in-session before triage (repros re-run at
  head): sd1-6 (MEDIUM, space-filler blanking SYNTHESIZES bash shapes —
  new FP class, disproves sd1-3's "never an over-match" claim) and
  sd1-7 (LOW, error IDs pair with shape evidence globally) ADMITTED;
  sd1-5 DECLINED (miss inside sd1-3's recorded known gap); sd1-4
  CONTESTED (alias-shadowed `set` — contests the frozen slice-0 `set`
  exemption, OWNER CALL). Reviewer battery at head: Pester 119/1
  skipped, clean tree. FIX ROUND 2 LANDED same night: `bc5638d`
  (sd1-1 script-local definitions), `4e6a223` (sd1-3 Generic-fragment
  blanking), `5f4b3fa` (sd1-6 non-bridging blank filler), `ef9f3ed`
  (sd1-7 error/evidence locality) — one commit + red/green guard each;
  battery at head: Pester 123/1 skipped, dotnet 59/59. RE-GRADE ROUND 2
  (head `293eda6`): sd1-6 RESOLVED; sd1-1/sd1-3/sd1-7 held on residual
  tails (no new IDs; every tail master-verified at head). FIX ROUND 3
  LANDED: `9b5e326` (sd1-1 Set-Alias tracking + lexical ordering),
  `20ba7fd` (sd1-3 escape-aware fragments), `f30ddde` (sd1-7
  command-position keywords). Battery: Pester 127/1 skipped, dotnet
  59/59. RE-GRADE ROUND 3 (head `374666b`): all three held on strictly
  more crafted tails (no new IDs, third consecutive round; all
  master-verified). FIX ROUND 4 LANDED: `eb5e193` (recursion counts),
  `f5229a7` (fragments span newlines), `0c43b05` (defined/resolving
  keywords are not evidence). **LOOP CLOSED CONVERGED** per the ccc9686
  contrived-tail precedent — every named repro across four rounds is
  guarded. **Canonical counts: Pester 130 (+1 skip), dotnet 59.**
  SLICE 1 DONE. NEXT: slice 2 (server wiring — labeled refusal result on
  both execution paths). OWNER: (a) ~~sd1-4 adjudication~~ DONE — owner
  unparked in-session, fixed at `c43360c` (set -e flags only while set
  still means stock Set-Variable; Pester 132/1 skip, dotnet 59/59;
  decision amendment recorded), (b) ratify the convergence close or
  order re-grade round 4, (c) push go. Records:
  `.agents/review/index.md`, `.agents/review/findings/sd1-{1..7}.md`.
- **2026-07-09 (night): shell-dialect plan APPROVED — owner,
  in-session.** D1 = (a) refuse-fast with platform-aware guidance; D2 =
  non-breaking raw-posture subset; D3 = dialect line. The #4 comment's 4
  acceptance suggestions reconciled into D2 at approval (adopted:
  no-preemptive-raw rewording, `route=pwsh`+`raw=false` taught as "exact
  execution, shaped output", `ptk_state` raw telemetry; declined:
  reason/cost gate — attribution recorded in the plan). Decision entry
  in `.agents/decisions.md`. Slice 0 probes RAN same night, results
  frozen into the plan: the #3 repro does not reproduce on this build
  (the resolver never rtk-wraps a `&&` chain — pwsh leg, runs fine);
  detection list pinned at 12 constructs IN, trailing-`\` OUT
  (Windows-path false-positive risk); `bash -lc` recovery verified end
  to end (cwd anchor, `[exit] N`, `[ptk:log via rtk]` compression, both
  legs); D2/D3 wording baseline snapshotted in the plan. NEXT: slice 1
  (token-aware detector in the module), then 2-4 — one commit + battery
  + codex loop each. #5/#6 remain UNTRIAGED (owner call).
- **2026-07-09 (evening refresh): OWNER PUSHED master through
  `5e3cd70`; GitHub issues #1 and #2 CLOSED (~15:35Z). THREE NEW GitHub
  items since (all `roethlar`, ~19:28-19:33Z):** issue **#5** (ptk_invoke
  labels successful exit-0 native stderr as `[errors]` — medium; asks
  for a neutral stderr label, PowerShell Write-Error kept
  distinguishable, 4-case regression matrix), issue **#6**
  (`timeoutSeconds` excludes queue wait behind the serialized runspace —
  a 1s-budget call can wait arbitrarily and still run; `ptk_state`
  blocks behind the busy runspace it should diagnose — medium; asks for
  wall-clock budget semantics + prompt busy/active-call-age/waiter-count
  reporting), and a **comment on issue #4**: cross-model confirmation
  (GPT-5.6-Sol/Codex governance audit, 2026-07-09) of the
  `raw=true`-as-habit problem — raw set preemptively on most inspection
  calls, bought nothing (README byte-identical raw vs shaped; 86%/95.6%
  reduction forfeited) — with 4 acceptance suggestions (no preemptive
  raw; teach route=pwsh+raw=false as "exact execution, shaped output";
  reason/cost gate on unjustified raw; raw-usage telemetry in
  ptk_state). **#5/#6 are UNTRIAGED — no plan, no code; owner
  prioritization needed.** The #4 comment bears directly on the DRAFT
  shell-dialect plan's raw-posture leg — reconcile before approval.
  Local state: working tree clean, 4 unpushed commits, all
  shell-dialect plan text (`3227607` draft, sources = issue #3 item 1 +
  issue #4; `1d7f38b` sd-1..sd-10 fixes; `13599a6` sd2-1..sd2-5;
  `809e0d0` sd3-1). Plan status: DRAFT awaiting owner approval; slice 0
  (probes) runs first; no code before approval.
- **2026-07-09: slices 3-6 review loop CLOSED (converged).**
  Re-grade round 1 (codex read-only, head `3ec608b`) cleared mhi-9 and
  mhi-11 and held mhi-10; round 2 (head `d58be68`, base `3ec608b`)
  graded the mhi-10 completion and mhi-12 RESOLVED, guard_confirmed,
  NO NEW FINDINGS (codex-cli 0.142.5). Verdicts recorded in
  `.agents/review/findings/mhi-{9,10,11,12}.md`; index updated. ~~All
  slice 3-6 + review-loop commits remain unpushed pending the owner's
  master push go.~~ RESOLVED same day: owner pushed master through
  `5e3cd70` (origin confirmed at that head, 2026-07-09 evening).
- **2026-07-09: slices 3-6 review loop — fixes landed; re-grade closed
  (see latest bullet).** Codex loop over `86b51ae..6134a2f` produced
  mhi-9/10/11 (fixed: `ce0caf2`, `fa3620a`, `6c1d025`); re-grade round 1
  held mhi-10 NOT RESOLVED — completion `e8363f3` (no-CLI codex uninstall
  now reads the config and names the manual removal). mhi-12 self-found
  live (HIGH): `codex mcp remove ptk` orphans `[mcp_servers.ptk.tools.*]`
  subtables and bricks the codex CLI — this box's config repaired
  in-session; sweep fix `9d00c6e`. **Canonical counts: Pester 85, dotnet
  59.** Master is local-ahead of origin; push is the owner's call. NEXT:
  codex re-grade round 2 at `e8363f3` (mhi-10 completion + mhi-12), then
  record the verdicts.
- **2026-07-09 (latest): revert miscommunication resolved; end-state
  build resumed.** The harness-file writes stay undone; the
  nudge-standard-layer script change (60cd9f3) is RESTORED by reverting
  82b8c51. Operative decisions live in the plan's two 2026-07-09
  amendments (`.agents/plans/multi-harness-init.md`): nudge is a
  standard layer, machine changes only through the complete owner-run
  install process, slice 5 = default dev-install chaining, agy hook
  deferred, no live installer runs during development. Building slices
  3-6 to completion. Owner pushed through a0dceb8; everything after is
  local. **Canonical counts: Pester 75, dotnet 59.**
- **2026-07-09 (latest): GITHUB ISSUE #2 FIXED and codex-closed** (plan:
  `.agents/plans/issue-2-stale-hook-registration.md`; owner mid-session
  go). ptk_init's claude leg registers the INSTALLED hook copy
  (`~/.ptk/scripts/ptk-hook.ps1`) so checkout moves cannot strand
  registrations; `-Show` flags STALE entries (missing file OR directory);
  installs name what they replaced; dev-install refreshes an existing
  hook entry when it also registered the server. Loop: i2-1 (MEDIUM,
  consent via parsed PreToolUse entry, 665d99a), i2-2 (LOW docs,
  ea95d48), i2-3 (LOW -PathType Leaf, 69a2b13) — all re-graded RESOLVED.
  **This box's live stale entry (src\ path — fail-open since unknown) was
  HEALED; hook effective next Claude Code session.** NOTE: ~/.ptk's
  payload is the owner's 2026-07-08 install — a dev-install re-run picks
  up the new hook text (neutral naming + liveness) and installer.
  **Canonical counts: Pester 75, dotnet 59.** ~~Comment/close issues
  #1+#2 after the owner pushes.~~ DONE 2026-07-09: both CLOSED on
  GitHub (~15:35Z). NEXT: multi-harness slice 3 (grok leg).
- **2026-07-09 (later): GITHUB ISSUE #1 FIXED and codex-closed** (plan:
  `.agents/plans/issue-1-mixed-stream-shaping.md`, owner-approved "go";
  taken ahead of multi-harness slice 3 by the same go). Shipped, one
  commit each: caa7714 (string-bearing mixed streams render as text —
  the repro class), 66a53df (heterogeneous header + ToString samples),
  c2d8a4a (i1-1: header type-list bounded), 86f990e (Select-Object
  -First stamps Selected.* into live TypeNames — generic path de-mutated,
  self-caught), d3f4569 (i1-3: MaxItems 0 wraparound), 1e1ab99 (i1-2:
  Select-PtcFirst helper, no object-row Select-Object anywhere — the
  deserialized/remoting exposure). Loop record: `.agents/review/index.md`
  (two rounds, final NO NEW FINDINGS). **Canonical counts: Pester 71,
  dotnet 59; handshake PASSED.** Comment/close issue #1 after the owner
  pushes. NEXT (owner instruction mid-session): GitHub issue #2 — stale
  global hook registration fails open silently — then multi-harness
  slice 3 (grok leg).
- **2026-07-09: CONCURRENT GOVERNANCE REFRESH interleaved with the
  slice-2 build session** (602ee45/03d9162/719c200/bd6ff02, toolkit
  ce0db15, owner identity, ~00:17-00:18): AGENTS.md + skills + CLAUDE.md
  + repo .claude/settings.json refreshed mid-build. bd6ff02 ("reset
  governance") swept one in-flight, comment-only `scripts/ptk_init.ps1`
  edit from the build session's working tree into its commit — content
  verified intact at HEAD (Pester 66/66 at 3caa78f; no governance file
  touched by the build commits, no build file damaged by the refresh).
  REFRESH FLAGS awaiting owner: `.agents/repo-map.json` and
  `.agents/artifact-manifest.json` are retired-but-locally-modified
  ("remove by hand if intended"). Process hazard worth remembering: a
  refresh that commits working-tree sweeps must not run while a build
  session has in-flight edits — a guard-proof sabotage state could get
  committed under a refresh message.
- **2026-07-09: MULTI-HARNESS SLICE 2 EXECUTED — codex leg.**
  `ptk_init.ps1 -Agent codex`: idempotent registration (existing entry
  left as-is via `codex mcp get`; else `codex mcp add ptk -- <installed
  exe>`, payload-gated), nudge block in `~/.codex/AGENTS.md`
  (`-NudgePath` now binds to the single selected leg). LIVE ON THIS BOX:
  real run hit the already-registered short-circuit, nudge installed
  after the owner's `@RTK.md` include, and a fresh `codex exec` quoted
  the block verbatim — codex nudge home VERIFIED
  (`docs/harness-support.md`). Codex loop on 7a068b9 CLOSED 2026-07-09:
  mhi-8 (MEDIUM — registration probe now runs before the payload gate,
  preserving leave-as-is for existing/custom entries; guard test uses a
  fake codex shim, 3caa78f) fixed and re-graded RESOLVED, no new
  findings. **Canonical counts: Pester 66, dotnet 59.** Details in the
  plan. NEXT: slice 3 (grok leg).
- **2026-07-08 (later night): MULTI-HARNESS SLICE 1 EXECUTED — installer
  framework + Claude leg.** `ptk_init.ps1` is the per-agent framework
  (`-Agent claude|codex|grok|agy|all`, detected-set default, stub legs for
  2-4), user-level by default with loud flip note, `-Local` warned opt-in,
  `-Nudge` block in `~/.claude/CLAUDE.md`, registration gate (refuses the
  hook without a `~/.ptk` payload), harness-neutral hook text, and the
  mhi-2 liveness check (down-server wording in the deny; wording only).
  Details + in-slice decisions in the plan
  (`.agents/plans/multi-harness-init.md`). **Canonical counts now: Pester
  62, dotnet 59** (11 new Pester tests, all guard-proven). README +
  server/README hook sections updated. OWNER NOTE: the flip means a bare
  `ptk_init.ps1` run now patches `~/.claude/settings.json`, and
  `dev-install.ps1 -Hook` prints a Claude-leg-only note. Codex loop on
  057a5ee CLOSED 2026-07-09: mhi-6 (MEDIUM, dev-install -Hook now gated
  on actual registration, 1e06351) and mhi-7 (LOW, surgical byte-exact
  nudge strip, ec6e094) both fixed and re-graded RESOLVED, no new
  findings (`.agents/review/index.md`). NEXT: slice 2 (codex leg).
- **2026-07-08 (night): v2 LIVE ON THIS BOX; MULTI-HARNESS PLAN APPROVED;
  SLICE 0 EXECUTED — ALL PROBES GREEN.** Owner completed dev-install +
  global hook + push; the session's own tool calls then hit the redirect
  and re-issued via ptk_invoke (both matchers verified live). Fresh
  headless Claude session: deny quoted verbatim → unprompted re-issue →
  task done — the standing hooked-check gate is CLOSED. grok and agy
  registered and live-verified (grok: ptk__ naming, no Claude-hook
  spillover, nudge home = ~/.claude/CLAUDE.md VERIFIED by marker probe;
  agy: mcp_config.json entry, unprefixed names, headless auth worked);
  codex entry was missing, re-added, tools list, headless auto-deny
  re-confirmed (interactive is codex's path). Durable table:
  `docs/harness-support.md`. Slice-0 probes ran THROUGH ptk background
  jobs. NEXT: slice 1 (installer framework + Claude leg), codex loop per
  slice. **Dogfood findings for the backlog:** (1) mixed string/object
  streams hit the generic table and LOSE the string lines (twice in live
  use — real shaping gap); (2) same class: string+MatchInfo mix rendered
  a Length-only table.
- **2026-07-08 (evening): SETUP DOCS UPDATED; MULTI-HARNESS INIT PLAN
  DRAFTED, codex-closed, AWAITING owner approval + the manual agy
  interview.** `.agents/plans/multi-harness-init.md`: per-harness
  registration/enforcement/nudge legs modeled on rtk init; evidence
  frozen (codex/grok CLI surfaces verified, self-reports marked as
  probe targets; agy headless interview failed — owner is asking it
  interactively with a prompt that demands VERIFIED vs RECALLED
  labeling). Slice 0 = live probes, headlined by the STANDING GATE both
  repos want: the live Claude hooked deny-and-reissue check. README/
  server-README now carry the cross-repo findings (global-first hook,
  content-tracking warning, truthful hook failure semantics — a down
  server still denies, PTK_DIRECT escapes; liveness-aware hook is a
  recorded slice-1 candidate). Review loop mhi-1..5 closed
  (`.agents/review/index.md`; two HIGH docs claims corrected). Commits
  local from 419503c onward; push owner-gated as always.
  in-session).** ptk continues as an active product; the continuation-gate
  entry is archived (`docs/history/decisions-archive.md`), the
  destructive-cmdlet parking survives as its own Open entry, and
  warm-runspace slice 7 (AD/EMS/EXO validation) plus greenfield D5
  (CLI-face retirement) are unblocked by the gate's closure. The shared
  multi-client host stays parked on its own measured-pain criterion (not
  part of this go).
- **2026-07-08 (post-GO build): v2-FEEDBACK FIXES + D5 RETIREMENT BUILT
  and codex-closed** (`.agents/plans/v2-feedback-fixes.md`; loop record
  in `.agents/review/index.md`). What shipped:
  - **Slice 1 (56b1af3): the os-error-6 class is FIXED.** Probe-diagnosed
    root cause: ChildStdinGuard's NUL handle was NON-INHERITABLE
    (File.OpenHandle default), so children got a stdin handle value
    absent from their handle table — rustup shims (cargo/rustc/codex)
    died duplicating it. SetHandleInformation(HANDLE_FLAG_INHERIT) fixes
    it; live-verified: cargo/rustc work on route=pwsh, auto, raw, jobs.
    The e2e now asserts stdin-reading natives SUCCEED (the missing
    assertion that hid the bug) and spawns CreateNoWindow.
  - **Slice 2 (9cc74de): UTF-8 native output decoding** — Console
    OutputEncoding pinned BOM-less UTF-8 in RunspaceHost (mojibake class
    dead; OEM-emitting tools now mojibake instead, escape hatch = job
    logs, NOT raw=true).
  - **Slice 3 (4f957ab): timeout message warns resolution can differ
    after recycle and points at ptk_state; rtk install nag filtered at
    error collection (specific banner only); stderr-swallow report
    probed NULL** (both routes return identical real stderr — details
    frozen in the plan).
  - **Slice 4 / greenfield D5 (bfc6323): CLI face RETIRED.** Module =
    server shaping library (exports: Compress-PtcObject,
    Compress-PtcOutput, Resolve-PtcInvokeScript; 1374→622 lines);
    ptk.ps1, docs/usage.md, CLI tests and fixtures removed; README
    single-surface story + setup corrected (d5-1: .mcp.json is empty,
    dev-install or explicit registration are the paths).
  - Codex loops: v2fb-1, v2fb-2, d5-1 (all LOW) fixed one commit each;
    v2fb re-grades RESOLVED; loop closed converged.
  - **Canonical counts now: Pester 51, dotnet 59.** Owner pushed master
    through d881d37 on 2026-07-08 and CI run 28985350456 is GREEN on all
    three OSes at that head. The installed 0.2.0 binary still serves the
    OLD surface — stop the ~/.ptk servers, rerun scripts/dev-install.ps1
    (it refuses while one runs, by design), then /mcp reconnect to go
    live on v2. Owner fixed the codex config (now points at the
    installed binary), closing the repo-bin exe-lock annoyance.
- **2026-07-08 (after the build): SECOND LIVE-USE FEEDBACK BATCH recorded
  (owner-shared notes from heavy real use of the CURRENT installed v1,
  `F:\notes\PTK\vela_session_notes.md` — machine-local, essentials
  captured here). Assessment against the just-built v2, candidate work
  awaiting owner prioritization (no code authorized):**
  1. **HIGH — "The handle is invalid (os error 6)" for rustup-shimmed
     binaries (cargo, rustc, codex) via route=auto; the session's single
     biggest time sink.** Workaround used live: `cmd /c "... < nul"` under
     route=pwsh. Code fact: `ChildStdinGuard` NUL-backs STDIN only
     (handle -10); stdout/stderr (-11/-12) of the console-less server
     were never guarded, so console-handle-probing shims can still hit
     invalid handles. NEEDS A PROBE on this box (which handle, foreground
     vs rtk-routed vs jobs — job children get a closed-pipe stdin, not
     NUL) before any fix. This failure class is invisible to the current
     test suite.
  2. **HIGH value/effort — mojibake in native output (`ΓÇö` for em-dash):
     OEM-codepage (cp437/850) vs UTF-8 mismatch on the capture side.**
     Candidate: pin `[Console]::OutputEncoding`/native decoding to UTF-8
     in the server/runspace. Pollutes all native tool output today.
  3. **Timeout recycle surprised live use with changed command/PATH
     resolution in the fresh runspace.** v2 already improves this
     (ptk_state drift; teach-at-timeout), and env restore was
     deliberately reset-only — but the timeout message should also say
     command resolution may differ and point at ptk_state. Small.
  4. **Minor:** rtk's "no hook installed" nag rides along in routed
     output (candidate strip); route=auto sometimes swallowed a failed
     native's stderr where route=pwsh showed it (probe).
  5. **Validation, not work:** the note's #3 ("long work has no story",
     pattern hand-rolled 6-7 times) is exactly what D3 built; warm-state
     reliability and shaping fidelity got explicit praise.
- **2026-07-08 (latest): GREENFIELD SLICES D1/D2/D4/D3 BUILT and
  codex-closed.** `.agents/plans/greenfield-design.md` (approved same day,
  adoption entry in `.agents/decisions.md`) executed in full except D5
  (CLI-face retirement — deferred until after the go/no-go window by the
  plan's own call). What shipped:
  - **D1** (dfcc4f0): ANSI/control sequences stripped at text ingest in
    `Compress-PtcOutput`, before log-shape classification.
  - **D2** (c573a08): every text leg bounded — `Limit-PtcPassthrough`,
    400 lines / 40 KB head+tail with explicit elision markers naming
    raw=true; the old never-truncate contract test reconciled under the
    adoption decision.
  - **D4** (247fe72): `ptk_state` (engine/PID/uptime/cwd/modules/jobs +
    env DRIFT vs post-priming baseline, PATH as entry diff, variable
    count; `listAvailable` cached only on clean probes) SUBSUMES
    `ptk_modules` + `ptk_ping`, which are REMOVED. `ptk_reset` now
    restores the process environment to its server-start baseline
    (factory-state semantics; timeout recycles deliberately do not).
  - **D3** (d3efc2d): background jobs — `ptk_invoke background=true`
    (child pwsh, self-redirected log under `~/.ptk/jobs/`, session cwd,
    -ExecutionPolicy Bypass, parse errors land in the log, exit 64),
    `ptk_job` (status/output/kill/list; shaped bounded offset-paged
    polls), per-call `timeoutSeconds` capped by new
    `PTK_MAX_CALL_TIMEOUT_SECONDS` (default 3600), teach-at-timeout
    error naming both recovery paths, reset/graceful-shutdown kill jobs.
  - Codex loops: 7 findings (d1-1, d2-1, d2-2, d4-1(+b), d3-1..d3-3 —
    two MEDIUM), all fixed one commit each with guard proofs, final
    re-grade RESOLVED x4 / NO NEW FINDINGS (`.agents/review/index.md`).
  - **Canonical counts now: Pester 76, dotnet 57.** Handshake asserts the
    four-tool surface (ptk_invoke, ptk_job, ptk_state, ptk_reset) and
    calls ptk_state instead of the removed ptk_ping.
  - **Tool-surface break, owner action on next release/install:** the
    installed 0.2.0 binary still serves the old tools; a rebuild/
    dev-install is needed for the new surface. CI ci.yml runs the same
    battery unchanged.
  - **Environment finding (owner action):** `~/.codex/config.toml` still
    registers ptk as `dotnet run` on this repo — every `codex exec`
    (including this session's review loops) spawns/builds it and can
    leave a repo-bin `PtkMcpServer.exe` running, locking the build. The
    Claude Code registration already points at `~/.ptk/bin`; recommend
    updating codex's to match. Recovery that worked all session:
    `Get-Process PtkMcpServer | Where Path -like '*Powershell-Token-Killer*' | Stop-Process`.
  - All commits local/unpushed (master push stays owner-gated).
  Release-plan slice 3 unaffected, still queued. D5 execution note for
  the future session: closes `ptk` CLI verbs, `docs/usage.md`, their
  tests; README single-surface rewrite.
- **2026-07-08 (later): release-plan SLICE 2 DONE and codex-closed.**
  `.github/workflows/ci.yml` landed on master (74a2604): ubuntu/windows/
  macos matrix, current action majors (checkout@v7, setup-dotnet@v5),
  Pester `-CI` + `dotnet test` + default-mode handshake. Its first run
  caught a REAL product bug: the hosted runspace honors Windows execution
  policy and the server never set one, so any Windows box with no policy
  configured (CI runners, fresh user installs) got the hosted default
  (Restricted), which blocked the module import and silently degraded
  shaping/routing to Out-String — owner boxes had always passed only
  because they have a policy configured; Linux/macOS unaffected (SMA
  hardcodes Unrestricted off-Windows). Owner-approved fix (dddbb6b, a
  recorded scope addition to the release plan): pin
  `InitialSessionState.ExecutionPolicy = Bypass` on Windows in
  `RunspaceHost.CreateRunspace` — rationale: ptk_invoke runs script text,
  it replaces a harness tool that itself runs `pwsh -ExecutionPolicy
  Bypass`, and ptk is not a security boundary. Guard proven: the new
  regression test (forces Restricted process policy) fails without the
  fix, 37/37 with it, including under the Restricted repro
  (`$env:PSExecutionPolicyPreference='Restricted'; dotnet test ...`).
  Server suite canonical count is now 37. CI green on all three OSes at
  the branch head (run 28971482704); codex loop closed NO FINDINGS first
  pass on both commits (`.agents/review/index.md`). Branch bookkeeping:
  commits were cherry-picked from `ci/slice-2` (831bcc3/30f283d) to
  master; local branch deleted; REMOTE `ci/slice-2` deletion was blocked
  by the harness permission classifier and awaits the owner confirming
  (`git push origin --delete ci/slice-2`) — the no-lingering-branches
  condition is not yet satisfied for the remote. Master push (now 7 local
  commits) stays owner-gated. Next: slice 3 (release workflow).
- **2026-07-08: Windows battery GREEN; dev-install verified on this box
  (handoff items 1-2).** Pester 70/70 (0 skipped — the shim test runs here,
  ls stays unrouted), dotnet test 36/36, handshake passes in all three modes
  (default, `-UseRegistrationCommand`, and `-ServerCommand` against
  `~\.ptk\bin\PtkMcpServer.exe` from a neutral cwd; installed binary reports
  0.2.0.0). dev-install had already been run on this box (VERSION
  `0.2.0-dev.g9ec73fe`): ARP entry present and `winget list` surfaces it
  (`ARP\User\X64\ptk`), user-scope registration live. NOT verified:
  `-Uninstall` round-trip and the elevated/running-server refusals.
  Notes: `claude mcp list` flagged ptk defined in BOTH user scope (installed
  exe) and project scope (`.mcp.json` dotnet run) with different endpoints —
  both servers were live. RESOLVED 2026-07-08: owner removed the
  project-scope registration and the emptied `.mcp.json` is committed; the
  installed user-scope binary is the endpoint, and a checkout needs
  `scripts/dev-install.ps1` to get a server (the Mac has no install after
  the slice-1 round-trip test). Both instances were killed to unblock
  `dotnet test` (precedented exe lock); `/mcp` respawned on the installed
  binary.
- **2026-07-08: ptk MCP server live-use feedback recorded.**
  After ~10 calls in a real session, the owner reported the MCP server was the
  right tool and that warm runspace/state persistence is the standout feature,
  but also the main isolation hazard: variables and `$env:PATH` persist across
  calls, including test shims such as a fake `npm` prepended to PATH. Long work
  should use the background process + redirected output + polling pattern so
  each MCP call stays under timeout and preserves the live server. Output shaping
  preserved useful signal, including compile warnings, final artifact lines, and
  stderr as `[errors]`; minor polish gap: raw ANSI color sequences from tools
  such as vite surfaced unfiltered. Native command routing through rtk was
  transparent. Treat this as adoption evidence and open feedback items.
- **HANDOFF 2026-07-04 (end of day): owner moving to the WINDOWS box for
  testing; master pushed through the handoff commit (explicit owner go).**
  Everything below in this entry's sibling bullets is the day's context;
  the Windows session starts with `git pull`, then:
  1. **Battery:** Pester suite (70 tests; the 1 Unix skip — the .cmd/.bat
     shim test — RUNS on Windows, and the new ls platform test takes its
     `$IsWindows` branch: `ls` must stay unrouted there), `dotnet test`
     (36/36 expected), handshake default + `-UseRegistrationCommand`.
  2. **dev-install on Windows (the paths this Mac could not verify):**
     `pwsh -File scripts/dev-install.ps1` → check the Add/Remove Programs
     entry appears (HKCU uninstall key; `winget list` should surface it),
     user-scope registration works, handshake `-ServerCommand
     "$HOME\.ptk\bin\PtkMcpServer.exe"` passes from a neutral cwd, then
     `-Uninstall` removes payload+registration+ARP and leaves user files.
     The install refuses elevated shells and refuses while a `~/.ptk`
     server is running (clear message with the PID) — both by design.
  3. **Owner items for the go/no-go window:** install the hook
     (`scripts/ptk_init.ps1 -Global` or `dev-install.ps1 -Hook`), run the
     live hooked check in a fresh session (Bash + PowerShell tool calls
     should come back denied with ptk guidance), start the friction log.
  4. **Next build item:** slice 2 (`.github/workflows/ci.yml`, three-OS
     matrix; iterate on a `ci/*` branch — granted scope, delete after —
     master push per-go). Slice 3 after. Hook-default decision still open,
     needed before slice 4.
- **2026-07-04 (latest): release-plan SLICE 1 DONE and codex-closed; slice
  0 fully closed (CI probe ran — see the plan's probe results).** Slice 1:
  module discovery flipped to binary-dir-first (35fd472 + dc26c30, guard
  tests prove both the order and the cwd-fallback through the real
  composition) and `scripts/dev-install.ps1` landed (10d4a1a + b11eb66 +
  719fd85): publish→`~/.ptk` install, user-scope registration
  (remove-then-add), Codex snippet, Windows ARP entry, `-Hook`,
  `-Uninstall`, `-LayoutOnly -OutputDir` for release CI. Process: 3-lens
  pre-commit subagent review (10 findings fixed in-tree) + codex loop
  (rel1-1 tag-version normalization, rel1-2 TOML escaping — both fixed,
  re-grade NO FINDINGS; `.agents/review/index.md`). Full battery green on
  this Mac: Pester 69/0/1, dotnet 36/36, handshake all modes, install/
  uninstall round-trip leaves the machine in its pre-test state
  (`~/.ptk` removed, no user-scope registration, settings.json md5
  unchanged). NOT verified here: Windows ARP paths and a live `-Hook`
  install (slice 7 / owner's Windows box). Next slice: 2 (CI workflow) —
  iterate on `ci/*` (granted scope), the workflow file itself lands on
  master locally; the master push stays owner-gated.
- **2026-07-04 (later): release-plan slice 0 is DONE except the CI probe.**
  `-ServerCommand` mode landed in `server/test-handshake.ps1` (a8553dc,
  pre-commit multi-lens review fixes folded in) and the osx-arm64 probe
  results are frozen into the plan (8161af2): 45 MB tar.gz asset (129 MB
  unpacked), apphost ad-hoc signed, curl download quarantine-free and runs
  clean, quarantined-fresh-copy contrast SIGKILLed + `spctl` rejected
  (this box), published binary removes the dotnet-run build check from
  session start, module loads position-independently from the canonical
  layout via the BaseDirectory upward probe, `claude mcp add`/`remove`
  syntax confirmed on Claude Code CLI 2.1.201. Codex review loop on the
  slice: see `.agents/review/index.md`. PUSH GO GRANTED 2026-07-04 for
  `ci/*` + `v0.2.0-rc.*`, with the owner's hard condition: NO branches
  may linger once the coding is done — the agent deletes every `ci/*`
  branch (local and remote) as soon as its facts/workflows land on
  master; the owner never has to handle branches. Probe branches fork
  from origin/master (last pushed commit), NOT local HEAD, so unpushed
  master work is not published through a side branch. `master` pushes and
  the final `v0.2.0` tag stay per-explicit-go.
- **2026-07-04 (later): master's Pester battery was RED on this Mac —
  pre-existing, NOT slice 0 — now FIXED (owner go same day).** Clean
  master had 65 passed / 3 failed / 1 skipped: two "read README"
  assertions expected `pwsh_token_compressor` in the LIVE README (removed
  by the a43897a docs-pass rewrite; broke on every platform), and "leaves
  aliases and cmdlets on the PowerShell path" asserted `ls` stays
  unrouted, which only holds on Windows — on macOS/Linux `ls` is the
  native Application and the resolver routes it to rtk BY DESIGN
  (owner-ratified 2026-07-04: ls IS the shell command where a native one
  exists; Get-ChildItem is the PowerShell way; models shouldn't lean on
  aliases). Fixes: 849081d (deterministic temp fixtures) and d0e34d6
  (gci for the cross-platform alias case + a new test pinning the ls
  platform split via $IsWindows). Codex loop: NO FINDINGS first pass.
  Suite on this Mac: 69 passed / 0 failed / 1 skipped (70 tests — the
  canonical count going forward; the earlier "69/69" phrasing predates
  the added test). Lesson recorded: the old 69/69 record had not
  reproduced on identical code — suite greenness was machine-dependent
  until these fixtures were made deterministic.
- **2026-07-04 (late): RELEASE-DISTRIBUTION PLAN APPROVED — next action is
  slice 0.** `.agents/plans/release-distribution.md`, approved by owner
  in-session 2026-07-04 after question resolution and a codex review loop on
  the plan text (3 LOW doc fixes, all accepted — `.agents/review/index.md`).
  All commits from 7494edf onward are UNPUSHED (push needs owner go), and
  the plan's requested standing push scope (`ci/*` + `v0.2.0-rc.*`) was NOT
  separately confirmed — ask explicitly before the first CI push. Slice 0 =
  local osx-arm64 publish probe, `test-handshake.ps1 -ServerCommand` mode,
  CI runner probe (needs that push go). Owner set a first public release
  target of **2026-07-25**: prebuilt self-contained per-RID binaries on
  GitHub Releases + `install.ps1`/`install.sh` one-liners (tier 3);
  publish-and-register script and .NET-tool packaging are dev-only. Decision
  amendment recorded in `.agents/decisions.md` (continuation entry). Owner
  resolved the plan's open questions in-session the same day: **5 RIDs**
  (win-x64, win-arm64, linux-x64, linux-arm64, osx-arm64 — owner has
  hardware for all five), **v0.2.0**, **one `~/.ptk` home** (payload+config,
  every platform and install method, no `--dir`), **winget = eventual
  primary Windows path** (installer-type only; v0.2.0 builds readiness: ARP
  uninstall entry, binary-hostable install logic, binary-relative module
  discovery — probe-order flip is an approved scope addition). STILL OPEN
  (owner: "decision for later", must close before slice 4): the public
  installer's hook default — tension recorded in the plan's Resolutions.
  Resume point: formal plan approval + the scoped push go (`ci/*` branch,
  `v0.2.0-rc.*` tags), then slice 0 (local publish probe, handshake
  `-ServerCommand` mode, CI runner probe incl. ARM runners). No code before
  approval.
- **2026-07-04 (earlier): docs pass PUSHED through a43897a.** README now
  leads with the MCP server as the primary use and documents rtk routing
  (four shaping legs, install-rtk encouragement, credits); server/README
  introduces rtk with the in-runspace rewrite detail; usage.md documents the
  `[exit] N` and PTC_TEMP-concurrency facts of `Invoke-PtcRun`. App name
  corrected everywhere: **PowerShell Token Killer** (`ptk`), named after
  rtk (Rust Token Killer); `PwshTokenCompressor` is only the module's
  on-disk name (repo-guidance mission line aligned).
- **UNIFIED SHELL ROUTING: BUILT 2026-07-04** — all five slices of the
  approved plan are committed and verified, each through the codex review
  loop (details in the plan and the commit messages):
  - Slice 0 probe results are recorded in the plan (rtk fidelity, cwd,
    chains, the Windows `rtk ls` gap, hook mechanics, latency).
  - Slice 1 (aa0ff12 + fixes): `Resolve-PtcInvokeScript` rewrites a single
    bare native-Application command with constant args to run through rtk;
    everything else runs as PowerShell unchanged; `route=auto|pwsh|rtk`
    argument on ptk_invoke; `raw=true` skips routing. Codex loop closed NO
    FINDINGS after fixes: $Error pollution (twice — resolver and
    Get-PtcRtkCommand), .cmd/.bat shim exclusion, Unix test stub, NUL handle
    lifetime.
  - **Load-bearing discovery (0a31364): natives that read stdin hung forever
    over stdio.** PowerShell hands a native command with no pipeline input
    the process stdin — the idle-but-open MCP JSON-RPC pipe — so bare git
    (any MSYS binary, sort, ssh) blocked until session end. Predates
    routing; never seen because no one had run a bare MSYS binary through
    ptk_invoke over stdio (live checks used cmdlets; rtk-routed calls mask
    it by wiring their own child stdio). Fix: ChildStdinGuard captures the
    transport streams, then points process stdin at NUL so children inherit
    EOF. Regression e2e spawns the real server over idle pipes and runs a
    stdin-reading native (60s hang without the guard, ~600ms with).
  - Slices 2-3 (e8ff3d7, 0f84988 + fixes): ptk_invoke repositioned as the
    single shell tool; `scripts/ptk-hook.ps1` (PreToolUse deny-with-guidance
    redirect for Bash+PowerShell, PTK_DIRECT escape hatch, fail-open,
    cwd-anchoring advice with apostrophe escaping) and `scripts/ptk_init.ps1`
    (rtk-init-style installer: local default/-Global/-Show/-Uninstall/
    -DryRun, idempotent, preserves foreign hooks surgically). Codex loop:
    cwd drop (High), shared-entry deletion, param-description mismatch,
    apostrophe escaping — all fixed with guard proofs; final one-line escape
    fix declared converged (replicates a codex-cleared pattern).
  - Verified 2026-07-04 (final battery): Pester 69/69, dotnet 34/34, both
    handshake variants, live stdio spot-check with real rtk (`[ptk:log via
    rtk]` + `[exit] 7` together), live in-harness routed `git status`.
  - **NOT DONE / OWNER ACTIONS:** (a) the hook is NOT installed anywhere —
    install with `pwsh -File scripts/ptk_init.ps1 -Global` (next session
    start), then run the live hooked check: a Bash and a PowerShell tool
    call should come back denied with ptk guidance and the model should
    re-issue via ptk_invoke; start the friction log the amended go/no-go
    needs. (b) ~~28 local commits unpushed~~ RESOLVED 2026-07-04: owner
    pushed everything through a43897a; only the plan commit 7494edf remains
    unpushed (see the release-plan entry above). (c) `/mcp` restart to
    respawn the live server on the final build (the last live instance was
    killed for the final rebuild) — not verifiable from the 2026-07-04 docs
    session; check whether it happened before reading a quiet ptk day as
    non-adoption.
  - Process notes for the record: two review-fix tests rode along in
    earlier commits instead of their own (5202756 carried the shim-test
    skip; 58990b1 carried the shared-entry test) — content correct, history
    not rewritten. Claude Code auto-respawns the ptk server after kills and
    each /mcp reconnect can leave an extra instance; every server rebuild
    this session needed a `Stop-Process -Name PtkMcpServer` first.
    PROPOSED (no go yet): a shadow-copy launcher so builds never collide
    with live servers.
- (COMPLETE — superseded by the BUILT entry above; kept as the approval
  record) Unified shell routing was the active work item:
  Owner-approved plan `.agents/plans/unified-shell-routing.md` (2026-07-04):
  ptk becomes the single shell tool — PowerShell → warm runspace, ANY
  non-PowerShell command line → rtk unconditionally (rtk passes through what
  it doesn't filter), log-shaped output → rtk log (exists) — plus a PreToolUse
  redirect hook on the harness Bash AND PowerShell tools, shipped via a
  `ptk_init.ps1` installer mirroring `rtk init` semantics. Decision basis: the
  2026-07-04 amendment in `.agents/decisions.md` (go/no-go now evaluates this
  product with the hook installed; operative criterion is experienced benefit
  + owner not disabling the hook). All plan open questions are resolved; the
  next action is slice 0 (rtk fidelity + hook mechanics probe, which freezes
  the design). Process: codex review loop after each slice (owner-set,
  2026-07-04): `codex exec --sandbox read-only` reviews each commit, real
  findings get fixed one-commit-each with guard proofs, iterate to NO
  FINDINGS or convergence (contrived-only Lows). Verified rtk facts for slice
  0: rtk's own global hook is installed on this box (`~/.claude/settings.json`
  PreToolUse, matcher `Bash` only — the PowerShell tool is uncovered),
  `rtk hook claude` is a native binary reading tool-call JSON from stdin,
  `rtk init --show` / `--dry-run` are safe read-only probes.
- 2026-07-04: the round-2 review (two findings on a8d3d02..HEAD) was FIXED
  under the approved plan `.agents/plans/review-fixes-2026-07-round2.md` with
  the codex-review loop per slice: (1) High — `Invoke-PtcRtkLog` snapshots and
  restores the caller's `$LASTEXITCODE` around the native rtk leg (the rtk
  invocation was clobbering the user script's exit code before the server read
  it; snapshot the VALUE, not the live PSVariable) — commit a798094, codex:
  NO FINDINGS; (2) Medium — dispatch guards completed in three codex rounds:
  every route guards all properties its compressor dereferences (ae1b9d6),
  files must have a KNOWN Length (null value or missing → generic; only
  directories legitimately lack it) (f86da5a), and every item must match the
  route's type name — one genuine FileInfo can no longer drag look-alike
  shapes of other types onto the fs route (ccc9686). All fixes guard-proven
  (revert → predicted failure → restore). Verified 2026-07-04: Pester 49/49,
  dotnet 30/30, both handshake variants pass, and a live stdio spot-check
  against the real winget rtk shows a log-shaped `exit 7` script rendering
  BOTH `[ptk:log via rtk]` and `[exit] 7`. The final codex pass on ccc9686
  landed after the handoff commit: one Low, self-labeled adversarial
  (calculated property returning a non-numeric `Length`/`WorkingSet64` value
  passes the guards, and direct `Compress-PtcObject` throws on the numeric
  conversion; the server path is already contained by `Compress-PtcOutput`'s
  never-throw shape-error fallback). Per the convergence rule the loop is
  CONVERGED and closed with this finding consciously not fixed — value-TYPE
  validation of every guarded property is out of proportion for a display
  heuristic with a raw escape hatch. Reopen only if a real (non-crafted)
  stream ever hits it.
- NOTE (2026-07-04): the live ptk MCP server was killed again this session to
  unblock `dotnet test` (PID 7696 held the exe lock — same precedented
  recovery); the owner needs an `/mcp` restart to respawn it on the current
  build. ~~Local commits through efe94c1 UNPUSHED~~ RESOLVED: owner pushed
  through a43897a on 2026-07-04.
- Owner pushed the day's work (a0a4819..4f943ea, including Phase 2) to origin on
  2026-07-03; only docs commits after 4f943ea may be local. On the push, the remote
  reported the repo MOVED to `AlsoBeltrix/PowerShell-Token-Killer` (capital S — the
  URL already recorded in `.agents/repo-guidance.md`); owner updated the local
  `origin` URL to match the same day. Pester: 31/31 on the Mac (2026-07-02); 29/31
  on the Windows box (2026-07-03) — the 2 failures were a pre-existing test-fixture
  sensitivity, FIXED later the same day (see the review-fixes bullet below).
- 2026-07-03 (night): five findings from an external (GPT-5.5) review of the Phase 2
  build were verified and FIXED under the approved plan
  `.agents/plans/review-fixes-2026-07.md`, one commit per finding: (1) ptk_invoke
  now surfaces nonzero native exit codes as an `[exit] N` block (with the CLI
  path's stale-guard, reset-before/read-after, mirrored into the server);
  (2) `Compress-PtcObject` dispatches to a specialized compressor only when EVERY
  item passes its property check, so mixed streams (FileInfo + string) compress
  via the generic path instead of degrading to `[ptk:shape ERROR]` raw fallback;
  (3) caller cancellation (user Esc) is now distinguished from timeout — the
  pipeline is stopped with a 5s grace and the warm runspace SURVIVES a clean
  stop (only a truly wedged pipeline still recycles), and the error says
  "canceled", not "timeout"; (4) `-MaxItems` + `+N more` now apply to
  property-less rows (scalar streams); (5) the two repo-root-sensitive Pester
  tests use deterministic temp-dir fixtures. Verified: Pester 43/43 (first fully
  clean run on this box), dotnet test 29/29, both handshake variants pass, plus
  an end-to-end stdio check of `[exit] 7` + stale-guard; guard proofs done for
  every behavior fix. Warm round-trip re-measured with the exit-code
  bookkeeping: avg 1.7 ms / max 3.5 ms over 20 stdio calls — no regression vs
  the ~3 ms baseline. Note: the live ptk servers were killed to unblock the
  rebuild (the exe was file-locked), so the session's MCP tools are down until
  an `/mcp` restart, which will respawn on the fixed build; a live Esc-abort
  spot-check in a real session is the one check not run headlessly (the
  mechanism is unit-tested).
- A 2026-06-27 design session explored a "universal PowerShell wrapper" rearchitecture
  (triggered by `ptk Get-ChildItem` printing help instead of running). No product code
  was written; the owner deferred the build decision. Recorded as an Open decision
  (b1e0550, docs-only). See `.agents/decisions.md`.
- A follow-on 2026-06-27 exploration looked at giving ptk a session-persistent
  warm-runspace backend (a stdio MCP server owning a `Runspace` that loads heavy
  modules / authenticated connections once). Recorded as a second Open decision in
  `.agents/decisions.md`. The core requirement is warm module load with no reload
  tax; unattended (cert-based) auth is the pattern for connection-bearing modules
  like EXO, not itself the requirement (owner correction 2026-07-02).
- 2026-07-02: owner selected the warm-runspace MCP server as the active work item and
  approved `.agents/plans/warm-runspace-mcp-server.md`. Slices 1-6 are built and
  verified: `server/` holds a net10.0 stdio MCP server (ModelContextProtocol 1.4.0 +
  Microsoft.PowerShell.SDK 7.6.3) with ptk_ping / ptk_invoke / ptk_modules /
  ptk_reset over a single warm runspace (serialized calls, timeout recycle, idle
  self-exit), registered in `.mcp.json` as `dotnet run -v q --project
  server/PtkMcpServer` (verified byte-clean stdout on cold build).
- Measured reload tax on this machine (Pester as the heavy module): cold per-call
  pwsh ≈ 460-500 ms every call; warm server pays 402 ms once, then re-import and
  module use round-trip at ~3 ms per tool call.
- Owner intent that frames future work: ptk is a personal/team tool complementing the
  owner's `headroom` PoC on Windows/PowerShell work, not an org-wide tool. The build
  trigger is measured benefit on real daily Windows usage, not faith. See
  `.agents/repo-guidance.md` for the generalized framing.
- 2026-07-02: governance refreshed from the AgentGovernanceBootstrap toolkit
  (`AGENTS.md` reconciled to the current template; repo-specific content carved into
  the new `.agents/repo-guidance.md` and `.agents/push-policy.md`).

- 2026-07-02 (late): owner stepped back and PAUSED all further building pending a
  go/no-go test. Evidence: headroom stopped (its context rewrites caused prompt-cache
  re-billing, net negative); rtk not adopted reliably via AGENTS.md instructions.
  Full reasoning and the test definition live in `.agents/decisions.md` ("Whether ptk
  continues at all"). Phase 2 compression, the universal wrapper, and the
  destructive-cmdlet gate are all behind that gate. No new plans until the test runs.
- 2026-07-03: owner UNPAUSED Phase 2 compression (amendment recorded in the
  continuation decision entry): build compression on ptk_invoke before the go/no-go
  so the test evaluates the full product. Scope: objects → Compress-PtcObject;
  log-shaped text → rtk when an rtk binary is present; all other text full
  passthrough; ollama leg dropped. ACTIVE plan (approved 2026-07-03):
  `.agents/plans/phase2-compression.md`. The universal wrapper and the
  destructive-cmdlet gate stay paused. Owner back at work ~2026-07-20 (was ~07-16).
- 2026-07-03 (later): Phase 2 slices 0-2 BUILT and committed. Module:
  `Compress-PtcOutput` (objects → Compress-PtcObject; log-shaped text → rtk with
  labeled raw fallback; all other text verbatim passthrough, never truncated;
  never-throw contract). Server: every runspace (create/reset/recycle) is primed
  with the module (`PTK_MODULE_PATH` override, else upward probe; import failure →
  stderr log + Out-String fallback) and ptk_invoke output is shaped unless the new
  `raw=true` argument is set. Verified: Pester 38 passing (+ the 2 known
  repo-root-fixture failures), dotnet test 25/25, both handshake variants pass;
  guard proofs done for module and server tests. Measured savings (chars/4
  estimate, TOOL-REPORTED — explicitly not the go/no-go benefit metric):
  Get-Process 2096→492 tok (76.5%), Get-Service 3689→861 (76.7%), Get-ChildItem
  -Recurse repo 47139→530 (98.9%), Get-Command 1737→1034 (40.5%); README.md text
  passthrough byte-identical. Object savings are summary-by-truncation (top-N +
  count) by design; `raw=true` is the escape hatch.
- Slice 3 (rtk binary) DONE 2026-07-03: owner installed rtk 0.43.0 via winget
  (resolves to `%LOCALAPPDATA%\Microsoft\WinGet\Links\rtk.exe` on the user PATH;
  an earlier agent-downloaded copy was refused by the permission classifier and
  discarded — owner install superseded it). Verified: `rtk log <file>` matches the
  interface the module uses (dedup summary + errors/warnings); rtk's "no hook
  installed" nag goes to stderr, which `Invoke-PtcRtkLog` already suppresses; the
  module log leg verified end-to-end with real rtk (`[ptk:log via rtk]` output).
  The SERVER discovers rtk via `Get-Command rtk` on its own PATH — true once
  Claude Code restarts from an environment that has the post-install user PATH;
  if it ever isn't, the leg degrades to its labeled raw fallback (visible, not
  silent), and `PTK_RTK_PATH` is the explicit override.
- Failure-mode drill result (observed during the rebuild): killing the server
  process mid-session bricks the ptk tools for the rest of the session — the
  harness marks them disconnected and does NOT respawn the server; only a session
  restart brings them back. Interpretation rule for the go/no-go weeks: a quiet
  ptk day needs a server-alive check before it is read as non-adoption. This
  session's live server was killed for the rebuild; the owner's `/mcp` restart
  respawned it on the new build without a full session restart (a second, gentler
  recovery path worth remembering alongside the drill finding above).
- 2026-07-03 (evening): Phase 2 LIVE CHECK PASSED in a real Claude Code session
  on the new build: objects → compact `fs:` summary; strings → verbatim
  passthrough; log-shaped output → `[ptk:log via rtk]` through the real winget
  rtk (the live server's PATH sees it — no PTK_RTK_PATH override needed);
  `raw=true` → plain Out-String table. Phase 2 is fully operational end to end.

- 2026-07-03 session findings (recorded here so they survive without chat):
  - PowerShell 5.1 compatibility, measured on this box: the module BODY imports and
    runs under Windows PowerShell 5.1.26100 — zero parse errors; fs, text, and
    MatchInfo paths work; only the process-object path fails ("Exception getting
    CPU": 5.1's ETS computes CPU from TotalProcessorTime, which throws on protected
    processes where 7 returns null). The manifest floor (`PowerShellVersion = '7.2'`)
    is the only import gate. So a 5.1 CLI backport is bounded (lower floor + one
    defensive access + dual-engine test runs; Pester 5.8.0 already sits in this
    box's 5.1 user scope). A 5.1 WARM SUBSTRATE would be new architecture — no
    NuGet package embeds 5.1; it would need a .NET Framework host or a persistent
    powershell.exe child. Parked, and capped by the decom horizon below. Note: a
    self-contained publish of the existing server xcopy-deploys to boxes with no
    .NET installed (engine is still 7.6).
  - Module-compat map for the go/no-go environment: on-prem Exchange management
    tools/EMS are 5.1-only through 2019/SE and will never load in the 7.6 runspace
    (owner-confirmed); the viable route is implicit remoting held inside the warm
    runspace (`New-PSSession -ConfigurationName Microsoft.Exchange` +
    `Import-PSSession` — plain WSMan/Kerberos, unofficial on 7, UNTESTED in this
    environment and the single most valuable slice-7 check). ActiveDirectory module:
    assumed PS7-native with current RSAT — unverified, no RSAT on this box. EXO and
    MSGraph: 7-native (owner-confirmed).
  - Decom horizon (owner, 2026-07-03): Exchange 2019 decommissions next year; the
    on-prem need lasts roughly 6-12 more months. This caps any 5.1-specific build —
    post-decom the workload is EXO+Graph, fully 7-native.


### From `## Next`

- **NEXT ACTION (2026-07-10): shell-dialect slice 2 — server wiring.**
  Slice 1 (detector) is DONE and its codex loop CLOSED CONVERGED
  (`.agents/review/index.md`). Slices 2-4 land one commit + battery +
  codex loop each.
- Review loop: shell-dialect slice-1 loop CLOSED CONVERGED 2026-07-09
  after four rounds (`.agents/review/index.md`); sd1-4 owner-unparked
  and fixed (`c43360c`) — all seven sd1 findings closed.
  Owner decisions still open: (a) prioritize new issues **#5**
  (`[errors]` mislabels exit-0 native stderr) and **#6** (queue-wait
  excluded from `timeoutSeconds`; `ptk_state` blocks behind a busy
  runspace) — both medium, untriaged, adjacent to but outside the
  shell-dialect plan's scope, (b) push go for the local commits
  (`3227607..809e0d0` plus the approval-recording commit).
- Slice 2 DONE (top Now entry). Next: slice 3 (release workflow,
  `.github/workflows/release.yml` on `v*` tags — per-RID publish on native
  runners, draft release; iterate on `ci/*`, granted scope). Still needed
  along the way: the hook-default decision before slice 4. Loose end from
  slice 2: the REMOTE `ci/slice-2` branch still exists pending owner
  confirmation of the delete. ~~test fixes~~ DONE 2026-07-04
  (owner go; battery green). The other questions (RIDs, version, install
  root, winget posture) are RESOLVED — do not re-raise them.
- ~~Execute unified-shell-routing slices~~ DONE 2026-07-04 (see Now). Next
  actions are the OWNER items in the routing entry: install the hook
  (`scripts/ptk_init.ps1 -Global`), run the live hooked check in a fresh
  session, start the friction log, push on go, `/mcp` restart.
- ~~Codex verdict on ccc9686~~ CLOSED 2026-07-04 (converged; disposition
  recorded in the round-2 entry above).
- 2026-07-02 headless adoption dry-run on this Mac (Sonnet, 19 trials, neutral cwd,
  ptk pre-approved via --allowedTools, tasks PowerShell-shaped but never mentioning
  ptk): **0/13 unprompted ptk usage**. The model used the harness's native PowerShell
  tool every time; the 6 control trials correctly used Bash. Even when the permission
  system blocked `Import-Module` in the native tool, it retried rather than discover
  ptk. Structural finding: the harness defers MCP tools behind ToolSearch (they are
  not in the upfront tool list — confirmed reachable when explicitly requested), so
  ptk's descriptions are invisible unless the model actively searches; against a
  native PowerShell tool it never does. This reproduces the rtk non-adoption pattern
  and is directly relevant to the 7/16 go/no-go: on the Windows box, expect adoption
  only if the native tool path is painful (cold EXO/AD auth per call) or the tools
  are surfaced/allowlisted prominently. Harness artifacts (runner + transcripts) were
  session-scratch, not kept in-repo.
- 2026-07-02 Codex condition: ptk registered in `~/.codex/config.toml` (dotnet run,
  absolute project path; Headroom proxy config removed the same day). Unlike Claude
  Code, Codex loads MCP tool descriptions upfront — gpt-5.5 found and called ptk_ping
  correctly. Headless `codex exec` auto-denies MCP tool calls ("user cancelled", ~1ms
  pre-flight; not overridable via --full-auto or approval_policy=never), so automated
  trials were not possible; interactive verification by owner succeeded (pong, after
  one-time allow). Asked directly, gpt-5.5 self-reported it would prefer ptk for
  multi-step/heavy-module PowerShell work (75-90%) but not one-offs (30-40%) —
  recorded as a self-report only: per the continuation decision, self-reported
  benefit is explicitly not evidence; observed unprompted usage on the Windows box
  remains the go/no-go criterion. No fresh-session behavioral trial run on Codex yet.
- 2026-07-03: Windows box brought up and the server validated there (the
  prerequisite for the go/no-go, NOT slice 7 itself — no AD/EMS/EXO modules were
  exercised). Two machine gaps found and fixed: the user-level NuGet.Config had an
  EMPTY packageSources list, so every restore failed NU1100 instantly — registered
  the default nuget.org feed; Pester 5 was not visible to pwsh (only inbox 3.4.0) —
  installed 5.8.0 CurrentUser via Install-PSResource. With dotnet SDK 10.0.301 and
  pwsh 7.6.3: `dotnet test` 19/19; stdio handshake PASSED both via the built dll and
  via the exact `.mcp.json` registration command from a cold build (stdout
  byte-clean); warm cross-call state verified. Module Pester suite 29/31: the two
  failures ("compresses filesystem objects before formatting" and "still routes real
  (deserialized) filesystem objects to the fs compressor") assert README.md survives
  `Get-ChildItem <repo root> | Compress-PtcObject -MaxItems 10`, but this box's root
  has 12 entries (a machine-local `.claude/` dir among them), so README.md falls
  past the cap. The fixture is the live repo root — environment-sensitive by design,
  pre-existing, not a Windows defect. FIXED 2026-07-03 (review-fixes plan, slice
  5): both tests now use deterministic temp-dir fixtures; suite 43/43 on this box.
- 2026-07-03 (later): owner enabled the MCP server on this box and the live-session
  check PASSED in Claude Code on Windows — full parity with the Mac check:
  ptk_ping → pong; ptk_invoke shares state across calls (same PID, variable set in
  call 1 read back in call 2, engine PS 7.6.3); warm module load confirmed (Pester
  cold import 545.7 ms, warm re-import 2 ms); ptk_modules lists loaded modules;
  script errors surface in an `[errors]` block; ptk_reset clears variables and
  modules without restarting the process. This box is now a fully working ptk
  environment; the remaining pre-07-16 items are the AWAITING OWNER GO list above.
- AWAITING OWNER GO, proposed 2026-07-03: ~~(a) push the local commits~~ DONE —
  owner pushed a8d3d02..9804eed (the review fixes and docs) to origin
  2026-07-03; ~~(b) deterministic temp-dir fixture~~ DONE 2026-07-03 via the
  review-fixes plan (slice 5); (c) fold the slice-7 test matrix below into the
  continuation decision entry; (d) pre-07-16 tests on this box (bring-up + warm-load
  measurement DONE 2026-07-03 — see the live-check bullet below): ToolSearch
  discoverability probe (do the ptk tool descriptions rank for
  "powershell"-shaped queries?); failure-mode drills (kill
  the server mid-session — do the tools respawn or brick? wedge a call past a
  short `PTK_CALL_TIMEOUT_SECONDS` — does the recycle keep the session usable?);
  and a headless "nudge ladder" adoption experiment (bare registration →
  allowlisted → one neutral CLAUDE.md line → explicit rule; small N,
  PowerShell-shaped tasks that never name ptk; burns API tokens). Related protocol
  proposal: run the go/no-go two-phase — week 1 pure/unprompted (the recorded
  criterion), then if adoption is zero add the minimal effective nudge and watch
  PERSISTENCE (this tests the experienced-benefit hypothesis); adopting it means
  amending the continuation decision entry, not quietly moving the goalpost.
- ~~Live-session check~~ DONE 2026-07-02: all four tools appeared in a live Claude
  Code session on this Mac. Verified: ptk_ping → pong; ptk_invoke shares state across
  calls (same PID, variable set in call 1 read back in call 2); module warmth matches
  the recorded reload tax (Pester cold import 448 ms, warm re-import 6.9 ms);
  ptk_modules lists loaded modules; script errors surface in an `[errors]` block;
  ptk_reset clears variables and modules without restarting the process. The go/no-go
  test on the real Windows box (below) is still the open item.
