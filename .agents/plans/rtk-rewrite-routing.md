# Plan: route all native work through `rtk rewrite` (DRAFT — not approved)

**Status:** DRAFT 2026-07-10 from the owner's direction (in-session):
everything non-PowerShell routes through rtk — rtk already owns robust
compression routing, so ptk stops reimplementing it. Validation performed
before drafting: rtk 0.43.0 (`../rtk` checkout, `src/discover/README.md`)
exposes `rtk rewrite "<command line>"` — a lexer-based engine that splits
compounds on `&&`/`||`/`;`/pipes, rewrites each segment against 60+ rules,
re-attaches redirects and env prefixes, and carries fidelity guards
(`gh --json` skip, `find`-before-pipe skip, write-redirect skip,
`RTK_DISABLED`). Live probes: `cargo fmt --all && cargo test 2>&1 |
tail -20` → per-segment rewrite; `head -20 README.md` → `rtk read
README.md --max-lines 20`; a `pwsh -Command` line passed through
untouched. This replaces ptk's deliberately narrow
single-bare-native-command shape check (unified-shell-routing plan) with
a call into rtk's own dispatch.

## Division of labor (the design in one sentence)

ptk stays the PowerShell expert — dialect refusal, object compression,
warm session; `rtk rewrite` becomes the sole authority on what native
work rides which rtk filter.

## Slices

0. **Probe and freeze `rtk rewrite` semantics** (no code): exit codes
   (a passthrough probe returned exit 3 with empty output — the
   contract must be pinned, not assumed); stdout contract (rewritten
   line vs empty); behavior on PowerShell-syntax input, quoting edge
   cases, env prefixes, `RTK_DISABLED`; guard cases verified against
   the documented list; version sensitivity noted (probed on 0.43.0).
   Results freeze into this plan before implementation (shell-dialect
   slice-0 precedent).
1. **PS7 execution-compatibility matrix** (no code): rtk emits
   POSIX-shaped lines; the warm runspace executes PowerShell 7. Probe
   each shape rtk can emit (`&&`, `||`, `;`, `|`, `2>&1`,
   `>/dev/null`, quoted args) in PS7 on macOS AND Windows —
   `>/dev/null` and friends are the suspected Windows breakers. Any
   shape PS7 cannot execute faithfully is either excluded from routing
   or transformed; the matrix freezes into this plan.
2. **Integration:** the module's native-routing leg
   (`Resolve-PtcInvokeScript`) calls `rtk rewrite` (honoring
   `PTK_RTK_PATH`) instead of the single-command shape check, executes
   the returned line, and falls back to the unchanged script on any
   rewrite failure (routing must never fail a call — existing
   invariant). **Rebinding requirement (rrp-1):** rtk emits literal
   `rtk ...` tokens (probed live: `head -20 README.md` → `rtk read
   README.md --max-lines 20`; prefixed: `sudo find ... | wc -l` →
   `sudo rtk find ...`), while the current resolver invokes the
   resolved executable path directly. Every emitted `rtk` token — in
   every compound segment, including after env/`sudo` prefixes — must
   be rebound to the `PTK_RTK_PATH`-resolved executable before
   execution; otherwise a pinned binary outside PATH fails
   command-not-found, or a different PATH copy runs despite the pin.
   Tests: pinned binary off PATH; conflicting PATH copy present.
   Order unchanged: dialect refusal FIRST (a bash-only
   script is refused or bash-wrapped before routing; the bash-wrapped
   path may hand the inner script to rtk rewrite too — decided by the
   slice-1 matrix). `route=pwsh`/`raw=true` consent bypasses unchanged.
3. **Measure and record:** re-run the 2026-07-10 shaped-vs-raw
   measurement plus `rtk gain` on a realistic mixed workload; record
   the numbers in this plan. This is the evidence for the owner's
   token-savings justification question — if widening routing does not
   move the native-workload number materially, that finding is recorded
   just as loudly.

## Risks / notes

- rtk rule changes ride rtk releases; ptk inherits them without code
  changes (that is the point), but a probe-pinned contract means a
  breaking `rtk rewrite` CLI change surfaces in tests, not in live use.
- The existing per-command rewrite (`& '<rtk>' <cmd>`) executes rtk as
  a PowerShell native call; multi-segment rewritten lines execute as a
  PS7 statement — the slice-1 matrix is the safety net.
- Windows behavior must be probed on the real Windows box (slice-1
  matrix includes it; the dev-install refresh is a prerequisite there).

## Verification

Battery (Pester + dotnet + handshake) with guard proofs per slice;
live MCP-stdio checks for representative rewritten shapes; codex
reviewloop per the recorded process.
