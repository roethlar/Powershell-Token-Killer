# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- Module is in a clean, reviewed state. HEAD is 9d56fae (LOCAL, not yet pushed;
  origin/master is c7cc77a): on top of the reviewed master it fixes two bugs that
  survived the design session - the deserialized/projected-object routing crash in
  `Compress-PtcObject` (the README `ptk run "... | Select-Object ..."` example) and the
  line-based `Remove-PtcPowerShellComments` (now tokenizer-based, preserves `#requires`
  and here-string `#` lines). 31/31 Pester tests passing. Decide whether to push 9d56fae
  or land it via PR.
- A 2026-06-27 design session explored a "universal PowerShell wrapper" rearchitecture
  (triggered by `ptk Get-ChildItem` printing help instead of running). No product code
  was written; the owner deferred the build decision. Outcome recorded as an Open
  decision (b1e0550, docs-only). See `.agents/decisions.md`.
- A follow-on 2026-06-27 exploration looked at giving ptk a session-persistent
  warm-runspace backend (a stdio MCP server owning a `Runspace` that loads heavy
  modules / authenticated connections once). Recorded as a second Open decision in
  `.agents/decisions.md`; app-reg + certificate EXO auth is a settled hard requirement.
  No code authorized.
- Owner intent that frames future work: ptk is a personal/team tool complementing the
  owner's `headroom` PoC on Windows/PowerShell work, not an org-wide tool. The build
  trigger is measured benefit on real daily Windows usage, not faith.
- Untracked: a session-log `.txt` in the repo root (prior session transcript). Not
  ours to delete; ignore unless asked.

## Next

- Decide how to land HEAD 9d56fae (push to master vs PR); it is committed locally but
  unpushed. Direct push to master was blocked by the harness policy this session.
- Two open decisions deferred by owner (both 2026-06-27), in `.agents/decisions.md`
  (Open Decisions) - resume there, do not re-derive:
  1. whether/how to build a "universal PowerShell wrapper" rearchitecture (the surface);
  2. whether to give ptk a session-persistent warm-runspace backend via a stdio MCP
     server (the substrate). The two are complementary, not alternatives.

## Blockers

- None.

## Verification

- `pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"`
  (Pester). See `.agents/repo-map.json` for the recorded command.

## Active Sources

- `AGENTS.md`
- `.agents/repo-map.json`
- `.agents/decisions.md`

## Unrecorded Repo Memory

- None known.
