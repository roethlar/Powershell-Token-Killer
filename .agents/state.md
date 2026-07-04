# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

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
    needs. (b) ~25 local commits are unpushed; push needs owner go.
    (c) `/mcp` restart to respawn the live server on the final build.
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
  build. Local commits through efe94c1 are UNPUSHED; push needs owner go per
  `.agents/push-policy.md`.
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

## Next

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
- ~~Live-session check~~ DONE 2026-07-02: all four tools appeared in a live Claude
  Code session on this Mac. Verified: ptk_ping → pong; ptk_invoke shares state across
  calls (same PID, variable set in call 1 read back in call 2); module warmth matches
  the recorded reload tax (Pester cold import 448 ms, warm re-import 6.9 ms);
  ptk_modules lists loaded modules; script errors surface in an `[errors]` block;
  ptk_reset clears variables and modules without restarting the process. The go/no-go
  test on the real Windows box (below) is still the open item.
- Interim security posture: keep ptk_invoke on ask-per-call in the harness; the
  policy-file gate design is recorded in the continuation decision, build only if
  real usage creates blanket-allow pressure.

## Blockers

- None.

## Verification

- `pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"`
  (Pester). See `.agents/repo-map.json` for the recorded command.

## Active Sources

- `AGENTS.md`
- `.agents/repo-guidance.md`
- `.agents/repo-map.json`
- `.agents/decisions.md`

## Unrecorded Repo Memory

- None known.
