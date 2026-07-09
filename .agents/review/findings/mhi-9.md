# mhi-9: dev-install still Claude-gates the entire per-agent init

**Severity**: MEDIUM — on a codex/grok/agy machine without the Claude CLI, the advertised one-command install silently produces none of the non-Claude harness state.
**Status**: Fixed (pending reviewer re-check)
**Branch**: master (direct; repo precedent)
**Commit**: `ce0caf2`
Reviewer intake id: `mhi-dev-install-claude-gate` (codex, codex-cli 0.142.5).

## Evidence
`scripts/dev-install.ps1:195-200` — `Register-PtkServer` returns `$false` when `claude` is not on PATH. `scripts/dev-install.ps1:340-354` — the default arm chains `ptk_init.ps1` only inside `if ($registered)`; the `else` path skips per-agent init entirely. `README.md:156-160` documents dev-install chaining as "one command per machine produces the whole state"; the plan (`.agents/plans/multi-harness-init.md`, "A bare `ptk_init.ps1` — or dev-install's chaining — must produce the full correct state for every detected harness") states the same invariant.

## Predicted observable failure
On a machine with codex, grok, or agy installed but no Claude Code CLI, `pwsh -File scripts/dev-install.ps1` publishes `~/.ptk` and warns, but never runs the codex/grok/agy legs: no registrations, no guidance files. ptk stays unavailable in those harnesses until the user manually runs `ptk_init.ps1` — contradicting the README claim.

## What
The mhi-6 gate (never ship the blocking hook while the steered-to tool is unregistered) only justifies gating the **claude hook**, not the whole init: the codex/grok/agy legs register the just-installed binary directly with their own harnesses and are independent of Claude registration. Gating all of init on `$registered` is over-broad.

## Approach
Move the mhi-6 invariant to the layer that owns it. The claude leg now
gates **its own hook** on `Get-Command claude` (the same registration
proxy `Register-PtkServer` uses): with the CLI absent it warns, skips
only the settings write, still installs the conditionally-worded nudge
block (grok's single layer), and returns `$false` so the leg lands in
ptk_init's failed-legs nonzero exit. `-DryRun` honors the gate (a dry
run previews what a real run would do); `-Uninstall` is ungated
(removing a hook needs no CLI). dev-install's default arm then chains
`ptk_init.ps1` unconditionally — the old skip-warning narrowed to a
registration-only notice — so codex/grok/agy legs run on machines
without Claude. Side benefit: a bare `ptk_init.ps1` on a
`~/.claude`-but-no-CLI machine (previously reachable mhi-6 hazard,
undetected) now takes the same safe path.

## Files changed
- `scripts/ptk_init.ps1` — claude leg: `$skipHook` CLI gate before the
  hook write; degraded return `(-not $skipHook)`.
- `scripts/dev-install.ps1` — default arm chains init unconditionally,
  warning first when `-not $registered`; `Register-PtkServer` header
  comment updated (the `-Hook` reference was stale).
- `tests/PwshTokenCompressor.Tests.ps1` — Context-level fake `claude`
  shim on PATH (keeps the existing hook-install tests hermetic on
  CLI-less machines, mirroring the mhi-8 codex shim) + new guard test.

## Guard proof
New test: `claude leg skips the hook but keeps the nudge when the
claude CLI is absent` — resolves pwsh's full path, guts `PATH`, runs
the leg against seam targets; asserts nonzero exit, **no** settings
file written, nudge block present. Reverted run (fix stashed from
`scripts/ptk_init.ps1`): 0 passed / 1 failed — old leg exits 0 and
installs the hook ("Expected 0 to be different from the actual
value"). Fix restored: full suite 80 passed / 1 skipped (Windows-only
batch-shim test on darwin) / 0 failed, 12.5s.

## Coder dispute (if any)
None — admitted as graded.

## Known gaps
Interacts with mhi-10 (both touch dev-install chaining); fixed in separate commits.
CLI presence — not live registration — remains the proxy: a machine with the
claude CLI present but ptk never registered still gets the hook from a
standalone `ptk_init.ps1` run (pre-existing exposure, unchanged by this fix;
dev-install itself registers before chaining). Candidate follow-up: probe
`claude mcp get ptk` the way the codex leg does, once that surface is
live-verified per docs/harness-support.md discipline.

## Reviewer comments
(pending re-review)
