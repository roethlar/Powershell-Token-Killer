# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- Module is in a clean, reviewed state; `master` tracked `origin/master` with nothing
  unpushed when checked 2026-07-03. Pester: 31/31 on the Mac (2026-07-02); 29/31 on
  the Windows box (2026-07-03) — the 2 failures are a pre-existing test-fixture
  sensitivity, not a module defect (see the Windows bring-up bullet under Next).
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

## Next

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
  pre-existing, not a Windows defect. Proposed fix (needs a go): deterministic
  temp-dir fixture for those two tests. Remaining to go live on this box: restart
  the Claude Code session so `.mcp.json` spawns the server (it could not start
  pre-SDK); expect the per-machine approval prompt for the project MCP server.
- (~2026-07-16, owner back at work) Run the go/no-go test on the real Windows box:
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
