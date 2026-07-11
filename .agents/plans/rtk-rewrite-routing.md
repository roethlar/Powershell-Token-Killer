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
warm session, and **deciding what is native in this session**; `rtk
rewrite` becomes the sole authority on which rtk filter confirmed-native
work rides. **Boundary requirement (rrp-2):** rtk maps names lexically —
probed live, bare `ls` rewrites to `rtk ls` — but in PS7 `ls` can be the
`Get-ChildItem` alias (Windows always), and warm or script-local
functions can shadow `git`. The current resolver's session-aware
classification (only commands resolving to `Application`; `.cmd`/`.bat`
shims excluded for argument-quoting fidelity) must survive per segment:
only segments whose head command resolves to a real native application
in the live runspace are handed to rtk; alias/function/cmdlet/shim
segments stay untouched. Tests: Windows `ls` alias; warm-shadowed and
script-local-shadowed `git`; `.cmd`/`.bat` shim.

## Scope (rrp-5)

This plan routes the FOREGROUND leg only: `Resolve-PtcInvokeScript`
runs in the warm runspace, and that is the only place slice 2 touches.
`background=true` jobs hand the original script to the job manager
before any module routing and are NOT rewritten by this plan — even
though builds/watchers are a major native workload. Whether background
jobs also route through `rtk rewrite` (in the cold child, with its own
fallback story) is a recorded follow-up decision for the owner, not an
implied part of "all native work"; the slice-3 measurement is labeled
foreground-only so the savings number does not claim coverage it does
not have.

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
   **Data semantics, not just syntax (rrp-3):** rtk rewrites the
   PRODUCER side of a pipe and leaves consumers in place — probed live:
   `git log --oneline | Measure-Object -Line` → `rtk git log --oneline
   | Measure-Object -Line` (the count silently measures rtk-filtered
   output), and `git diff | Set-Content patch.diff` → `rtk git diff |
   Set-Content patch.diff` (a compressed non-applyable "patch" written
   while the call reports success). The current design keeps pipelines
   unrouted for exactly this fidelity reason. The matrix must therefore
   cover filtered-producer → consumer SEMANTICS, and pipelines whose
   downstream consumes the data (redirection to file, `Set-Content`,
   counts, parsing, control flow) are excluded from routing unless
   their fidelity is proven case-by-case. Whether any pipeline routing
   ships at all is an explicit owner decision at approval — "parses and
   runs" is not "same data".
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
- **A hung rewrite must not eat the call (rrp-4):** routing runs inside
  the call's total wall-clock budget; a slow or wedged `rtk rewrite`
  process would consume the budget so the user's original command never
  executes — and an executing-timeout abort recycles warm state. That
  contradicts the fail-open invariant, so slice 2 must invoke `rtk
  rewrite` with its own short bound (seconds, not the call budget) and
  kill it on expiry, falling back to the unchanged script with enough
  budget left to actually run it. Seams to test: rewrite timeout →
  fallback executes; rewrite failure → fallback executes; warm state
  preserved in both.
- Windows behavior must be probed on the real Windows box (slice-1
  matrix includes it; the dev-install refresh is a prerequisite there).

## Verification

Battery (Pester + dotnet + handshake) with guard proofs per slice;
live MCP-stdio checks for representative rewritten shapes; codex
reviewloop per the recorded process.
