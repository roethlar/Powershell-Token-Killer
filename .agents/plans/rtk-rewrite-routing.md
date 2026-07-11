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

0. **Probe and freeze `rtk rewrite` semantics** (no code). The exit
   protocol is now source- and probe-verified on 0.43.0
   (`../rtk/src/hooks/rewrite_cmd.rs`) — an earlier in-session
   observation recorded passthrough as "exit 3, empty output"; that was
   WRONG (rrp-7). The real contract:

   | Exit | Stdout    | Meaning                                    |
   |------|-----------|--------------------------------------------|
   | 0    | rewritten | rewrite, allow-tier permission verdict     |
   | 1    | (none)    | passthrough — no rtk equivalent            |
   | 2    | (none)    | deny rule matched                          |
   | 3    | rewritten | rewrite, ask/default-tier verdict          |

   Probe-confirmed: `pwsh -NoProfile -Command Get-Date` → exit 1, no
   output; `head -20 README.md` → exit 3 + rewrite. Success for ptk =
   exit 0 OR 3 AND non-empty stdout (treating exit 3 as failure would
   silently unroute nearly everything — default-tier verdicts are the
   common case). What ptk does with exit 2 (rtk-side deny: honor as a
   refusal, or ignore — rtk permission rules are a separate config
   surface from ptk's own planned policy gate) is pinned here in slice
   0 and decided by the owner at approval. Remaining slice-0 work:
   quoting edge cases, PowerShell-syntax inputs, env prefixes,
   `RTK_DISABLED`, guard cases verified against the documented list;
   version sensitivity noted. Results freeze into this plan before
   implementation (shell-dialect slice-0 precedent).
   **Known guard hole, probed (rrp-8):** the find-before-pipe guard
   checks the raw producer for a LITERAL leading `find`/`fd`, but
   env/`sudo` prefixes are stripped and reattached later — so
   `sudo find . -name "*.rs" | wc -l` rewrites to `sudo rtk find ... |
   wc -l`, feeding grouped output to the pipe consumer the guard exists
   to protect (wrong counts, broken `xargs`). Slice 0 must probe
   prefixed `find`/`fd` pipelines explicitly, and integration must
   exclude them ptk-side (or an upstream rtk fix must land) before any
   pipeline routing — do not treat the documented guard list as
   established fidelity.
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
   **route=rtk gets a defined meaning (rrp-6):** today `route=rtk`
   force-routes a safe-shaped script through the configured binary,
   skipping the Application check (pinned by an existing Pester test
   and the tool description). Under rewrite-based routing a forced
   script that rtk passes through (exit 1) would silently execute as
   plain PowerShell — indistinguishable from `auto`. **Selected
   contract (the owner may veto at approval): `route=rtk` is
   rewrite-mandatory-with-labeled-outcome.** rtk rewrite is always
   attempted; exit 0/3 with stdout executes the rebound rewritten
   line; exit 1/2 executes the script UNCHANGED and prepends a labeled
   `[rtk] no rewrite (passthrough|deny) — executed unrouted` line, so
   a forced call is always distinguishable from `auto` (which stays
   silent). Force-wrapping arbitrary text as `rtk <script>` is retired
   with the shape check that made it safe. The existing pinned Pester
   test and the model-visible tool description change to exactly this
   contract in the same slice.
3. **Measure and record (protocol frozen here — rrp-9):** the
   measurement is self-contained in this plan, not a reference to a
   session artifact. Method: drive the BUILT server over real MCP
   stdio (initialize handshake, then `tools/call` on `ptk_invoke`);
   for each case call once with `raw=true` and once shaped
   (`raw=false`), compare response-text character counts. Fixed case
   set (the 2026-07-10 baseline workload, from the repo root):
   - objects: `Get-ChildItem server -Recurse -File | Select-Object
     Name,Length,LastWriteTime` (baseline ratio 3%)
   - objects: `Get-Process | Select-Object -First 50` (81%)
   - native: `git log --stat -30` (46%)
   - logs: 80 synthetic `INFO worker[n]: step n completed in nms`
     lines (2%)
   - passthrough: `Get-Content README.md -Raw` (100%)
   plus these FROZEN native cases added for this slice (exact
   commands, all runnable from the repo root with only git/coreutils —
   no cargo/npm dependence):
   - compound: `git status && git log --oneline -20`
   - file-read class: `head -40 README.md`
   - rtk git-filter class: `git show --stat HEAD`
   One run per case (output is deterministic enough at this size;
   re-run twice only if a ratio moves >5 points between runs).
   `rtk gain` accumulates HISTORICAL tracking data — snapshot or reset
   its counter before the run and report only the delta, never the
   accumulated total. **Baseline and aggregation:** before integration
   lands (slice-1 exit), run this exact protocol at the
   pre-integration HEAD and record every case's raw/shaped character
   counts in this plan; the post-integration run re-measures the same
   list. The native subtotal is `sum(shaped chars) / sum(raw chars)`
   across the native cases — sums, never a mean of per-case ratios.
   Materiality bar for the owner's justification question: the
   post-integration native subtotal must improve on the recorded
   pre-integration native subtotal by ≥10 percentage points, or the
   finding "routing widened, savings did not move" is recorded just
   as loudly.

4. **Reconcile the durable contracts (rrp-10):** the repo's standing
   record says the opposite of this plan — the greenfield decision and
   its adopted plan deliberately preserve single-command routing, and
   the `ptk_invoke` tool description plus both READMEs advertise it.
   Implementing slices 0-3 without touching those leaves code and
   canon contradicting each other: models read a stale contract, and a
   future cold agent could "correct" the code back to the narrow
   check. On approval, a final slice records the superseding owner
   decision in `.agents/decisions.md`, amends the greenfield plan's
   routing paragraph with a pointer here, and updates every surface
   that advertises the old contract in the same slice: BOTH InvokeTool
   descriptions — the top-level tool `[Description]` (which
   independently advertises single-command routing) AND the `route`
   parameter text — `README.md`, `server/README.md`, and the frozen
   single-bare-native-command decision inside
   `.agents/plans/unified-shell-routing.md` (marked superseded with a
   pointer here, not silently left standing). Handshake/schema
   assertions updated to the new wording. (The decisions.md entry itself waits for the owner's
   explicit go, per the standing in-session instruction.)

## Risks / notes

- rtk rule changes ride rtk releases; ptk inherits them without code
  changes (that is the point). The exit/stdout protocol gets its own
  Pester tests against the real binary, but note honestly: CI installs
  no rtk, so those protocol tests only run where a binary is present
  (locally, and rtk-absent seams elsewhere) — a breaking upstream CLI
  change surfaces on the first LOCAL battery run after an rtk upgrade,
  not in CI (rrp-7).
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
