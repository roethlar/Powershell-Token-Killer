# mhi-6: dev-install -Hook installs the blocking hook even when Claude registration was skipped

**Severity**: MEDIUM — can break every Bash/PowerShell tool call for that
install path (hook denies toward an unregistered tool); PTK_DIRECT remains
the escape hatch.
**Status**: Verified
**Branch**: master (direct, per the recorded codex-loop precedent)
**Commit**: (filled in at commit)

## Evidence
`scripts/dev-install.ps1` — `Register-PtkServer` returns without failing
when the `claude` CLI is not on PATH (prints manual-registration guidance),
but the `-Hook` block still invokes `ptk_init.ps1 -Agent claude`, whose
registration gate checks only `$PtkHome/bin` — which dev-install itself
just created.

## Predicted observable failure
`pwsh -File scripts/dev-install.ps1 -Hook` on a machine without the claude
CLI installs the payload, skips registration, and still installs the
blocking PreToolUse hook: from the next Claude Code session every
Bash/PowerShell tool call is denied and steered toward a ptk_invoke tool
the harness has no registration for.

## What
The slice-1 principle — enforce only where the steered-to tool can answer —
was applied to ptk_init's payload gate but not to dev-install's `-Hook`
chaining: registration outcome was not consulted.

## Approach
`Register-PtkServer` now returns `$true`/`$false` (registered vs left to
the user; the mcp-add failure path still throws). The `-Hook` block gates
on that value: registered → chain `ptk_init -Agent claude` as before; not
registered → warn, skip the hook, and print the exact ptk_init command to
run after manual registration.

## Files changed
- `scripts/dev-install.ps1` — Register-PtkServer return contract; gated
  `-Hook` block.

## Guard proof
Not automated: dev-install has no test harness (it runs a full
self-contained publish and mutates the real `~/.ptk`, live registration,
and ARP state; the recorded verification for it is the manual
install/uninstall round-trip). The no-CLI branch was NOT executed on this
box (claude CLI present, live install in use); verified by review of the
two touched code paths. The codex re-grade covers the fix.

## Coder dispute (if any)
None.

## Known gaps
The reverse skew (claude CLI present, registration succeeds, user later
unregisters) is out of scope — the hook's liveness wording (slice 1) covers
runtime unavailability.

## Reviewer comments
Raised by codex (Codex v0.143.0, gpt-5.5, read-only) reviewing 057a5ee,
2026-07-09. Re-grade: see index.
