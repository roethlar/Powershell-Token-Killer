# mhi-12: uninstall orphans codex tool-approval subtables, bricking the codex CLI

**Severity**: HIGH — after an uninstall, every codex command fails at config load ("invalid transport"); the CLI cannot self-repair because each command begins by loading the config.
**Status**: Fixed (awaiting reviewer re-review)
**Branch**: master (direct; repo precedent)
**Commit**: `9d00c6e`
Reviewer intake id: none — self-discovered live on this box mid review-loop (the codex CLI bricked), not a reviewer intake.

## Evidence
codex writes `[mcp_servers.ptk.tools.*]` approval subtables into `~/.codex/config.toml` when the user approves ptk tools. `codex mcp remove ptk` strips only the base `[mcp_servers.ptk]` table. Orphaned, a `mcp_servers.ptk.*` subtable makes the whole config unloadable — codex parses it as a server table with no command and refuses to start: "Error loading config.toml: invalid transport in mcp_servers.ptk". Observed live on this machine mid review-loop. Every codex command loads the config first, so the CLI cannot repair itself.

## Predicted observable failure
Install on codex, approve a ptk tool (codex persists the approval subtable), later `scripts/dev-install.ps1 -Uninstall`: the base table is removed, the subtables orphan, and every subsequent `codex` invocation exits at config load. The uninstaller bricks the harness it uninstalls from — and codex itself cannot be used to undo it.

## Live repair
This box's `~/.codex/config.toml` was repaired in-session — the orphaned `[mcp_servers.ptk.*]` subtables removed, nothing else touched — so the slice 3-6 review loop could resume.

## What
The codex uninstall path delegated entirely to `codex mcp remove`, which does not own the approval subtables codex writes under the ptk server key; removing the base table converts them into config-breaking orphans.

## Approach
Sweep exactly the ptk-scoped subtables — never the base table, never any non-ptk key. New helpers `Test-PtkCodexOrphanTable`/`Remove-PtkCodexOrphanTable` (`scripts/ptk_init.ps1:367,373`) match `[mcp_servers.ptk.` table headers line-wise and drop only those tables; they are the ONE place the leg touches `~/.codex/config.toml` directly. The sweep runs after the CLI remove and also when the CLI is absent — healing fresh uninstalls and machines an earlier uninstall already bricked. `-DryRun` discloses the pending sweep without writing (`scripts/ptk_init.ps1:420`). New `-CodexConfigPath` test seam mirrors `-GrokConfigPath` (`scripts/ptk_init.ps1:416`).

## Files changed
- `scripts/ptk_init.ps1` — orphan-table helpers; uninstall-arm sweep + dry-run disclosure; `-CodexConfigPath` seam.
- `tests/PwshTokenCompressor.Tests.ps1` — guard `codex leg uninstall sweeps orphaned tool-approval subtables (mhi-12)` (`:903`): fake codex shim accepting only `mcp remove ptk` (precedent: the mhi-8 leave-as-is test) plus a temp config.toml carrying `model = "keep-me"`, `[mcp_servers.other]`, two `[mcp_servers.ptk.tools.*]` subtables, and `[hooks.state]`. Asserts: dry run discloses the sweep and writes nothing; real run reports `registration removed` + the sweep, every `mcp_servers.ptk` line is gone, and `model = "keep-me"`, `[mcp_servers.other]`, `[hooks.state]` all survive.

## Guard proof
Stashed `scripts/ptk_init.ps1`, re-ran the guard: failed — the orphaned subtables survived the uninstall (the exact brick precursor). Popped; passes. Full battery at `9d00c6e`: 84 tests, 83 passed, 0 failed, 1 skipped (pre-existing).

## Coder dispute (if any)
None — self-found; admitted directly.

## Known gaps
The sweep is header-scoped line surgery: a ptk subtable expressed as an inline table on some other line would not be caught — not a shape codex writes. Other harnesses: grok stores the registration flat (no observed subtables); agy is file-scoped (plugin dir), nothing shared to orphan.

## Reviewer comments
(pending re-review — queued for re-grade round 2 alongside the mhi-10 completion)