# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- No active development work. Governance was bootstrapped onto an existing,
  working module.

## Next

- Open review findings recorded 2026-06-26 await a decision to act; see the Open
  Decisions queue in `.agents/decisions.md`.

## Blockers

- None recorded.

## Verification

- `pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"`
  (Pester). See `.agents/repo-map.json` for the recorded command.

## Active Sources

- `AGENTS.md`
- `.agents/repo-map.json`
- `.agents/decisions.md`

## Unrecorded Repo Memory

- None known.
