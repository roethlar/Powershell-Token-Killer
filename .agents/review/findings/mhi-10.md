# mhi-10: dev-install uninstall reverses only currently-detected legs

**Severity**: MEDIUM — uninstall is asymmetric with install: a harness CLI removed from PATH after install leaves stale ptk state (nudge blocks, config entries pointing at a deleted binary) behind forever.
**Status**: Fixed; re-grade round 1 NOT RESOLVED; completion landed (awaiting re-grade round 2)
**Branch**: master (direct; repo precedent)
**Commit**: `fa3620a` (fix); `e8363f3` (re-grade completion)
Reviewer intake id: `mhi-dev-uninstall-detected-only` (codex, codex-cli 0.142.5).

## Evidence
`scripts/dev-install.ps1:308` — Uninstall calls `& $init -Uninstall` with no `-Agent`. `scripts/ptk_init.ps1:623-630` — with no explicit agent, only currently *detected* agents run (non-Claude detection is `Get-Command` only, `scripts/ptk_init.ps1:130-135`). Every leg's uninstall already no-ops safely without its CLI (`scripts/ptk_init.ps1:363-375` codex, `550-563` agy), so running all legs is safe; the plan requires "`-Uninstall` reverses every leg".

## Predicted observable failure
Install with codex/grok/agy present, later remove one CLI from PATH, then `scripts/dev-install.ps1 -Uninstall`: that harness leg is skipped, `~/.ptk` is removed, and stale state survives — e.g. a ptk block in `~/.codex/AGENTS.md`, a `~/.grok/config.toml` entry, or `~/.gemini/config/plugins/ptk/mcp_config.json` pointing at the deleted binary.

## What
Uninstall scope is derived from live CLI detection instead of the installed-artifact set, so it cannot reverse legs whose CLI has gone missing, violating install/uninstall symmetry.

## Approach
Root cause is the agent-resolution block, not any leg: with no explicit `-Agent`/seams, resolution used the *detected* set for every verb. Fix: when `-Uninstall` is passed with no explicit agent, resolve to `$supportedAgents` (all legs) instead of the detected set; install-path behavior is unchanged (detected set, claude fallback). This was verified safe leg-by-leg before the change: codex uninstall without its CLI prints "no registration to remove" and returns `$true`; grok uninstall with a registration but no CLI warns to remove manually and returns `$true`; agy uninstall is pure file removal; the claude leg's uninstall never needed the CLI (mhi-9 gate excludes uninstall). dev-install's uninstall arm inherits the fix for free (`& $init -Uninstall`, no `-Agent`); its comment and the `-Agent` param doc were corrected to state the new contract. Docs already promised this behavior (`.agents/plans/multi-harness-init.md:101,228` "reverses every leg") — the code now matches the plan.

## Files changed
- `scripts/ptk_init.ps1` — resolution block: bare `-Uninstall` → `$supportedAgents`; `-Agent` param comment updated.
- `scripts/dev-install.ps1` — uninstall-arm comment: "every SUPPORTED leg - not just detected ones".
- `tests/PwshTokenCompressor.Tests.ps1` — guard test `bare -Uninstall reverses every leg, not just detected ones`: child pwsh with gutted PATH (nothing detectable) runs `ptk_init.ps1 -Uninstall -DryRun`; asserts exit 0 plus codex (`codex mcp remove ptk`), grok (`grok mcp remove -s user ptk`), and agy (`plugins/ptk`) dry-run lines all appear. `-DryRun` keeps the run read-only — both nudge helpers early-return under it (`scripts/ptk_init.ps1:176-180,194-197`), and each CLI leg prints its would-run command before touching anything.

## Guard proof
Stashed `scripts/ptk_init.ps1`, re-ran the guard test: FailedCount=1 — output contained only the claude leg's dry-run ("DRY RUN - would write /Users/michael/.claude/settings.json", guidance-block removal) and no codex/grok/agy lines; `'codex mcp remove ptk'` failed to match, exactly the predicted skip. Stash popped; test passes with the fix. Full battery: 82 tests, 81 passed, 0 failed, 1 skipped (pre-existing), ~13.6s.

## Coder dispute (if any)
None — admitted as graded.

## Known gaps
grok registration removal genuinely needs the grok CLI (`grok mcp remove`); for that leg "run and report honestly" is the best achievable without the CLI. File/dir artifacts (nudge blocks, plugin dir) need no CLI.

## Reviewer comments
**Re-grade round 1 (codex, read-only): NOT RESOLVED.** Held at the codex
leg's no-CLI uninstall branch (then `scripts/ptk_init.ps1:390`): with the
codex CLI gone the leg printed `[codex] codex CLI not found - no
registration to remove.` without ever reading the config, so a stale
`[mcp_servers.ptk]` entry survived behind a false report — the finding's
core (stale cross-harness state) reproduced under the new runs-every-leg
resolution.

## Re-grade completion (`e8363f3`)
Grok-leg parity (`scripts/ptk_init.ps1:542`): the no-CLI branch now reads
the config and, when a base `[mcp_servers.ptk]` entry exists, warns with
the exact manual removal (`scripts/ptk_init.ps1:437`). Warn, not edit —
the base entry is valid config a reinstalled CLI can manage; only
unloadable orphaned subtables are swept (mhi-12, `9d00c6e`). New guard
`codex leg uninstall warns about a stale registration when the CLI left
PATH (mhi-10)` (`tests/PwshTokenCompressor.Tests.ps1:961`): temp
config.toml with a base entry, gutted PATH, `-Agent codex -Uninstall
-CodexConfigPath`; asserts exit 0, the manual-removal warning, and that
the entry itself is untouched.

**Guard proof (completion)**: stashed `scripts/ptk_init.ps1` (reverting
to `9d00c6e`), re-ran the guard: FailedCount=1 — Expected regular
expression 'remove the \[mcp_servers\.ptk\] entry' to match '[codex]
codex CLI not found - no registration to remove.' — exactly the
predicted false report. Popped; passes. Full battery at `e8363f3`: 85
tests, 84 passed, 0 failed, 1 skipped (pre-existing), ~14.7s.

(awaiting re-grade round 2)
