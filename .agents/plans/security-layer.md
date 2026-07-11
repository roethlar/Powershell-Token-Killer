# Plan: second security layer — policy gate + audit log (DRAFT — not approved)

**Status:** DRAFT 2026-07-10 on the owner's instruction (in-session). The
recorded build criterion for the policy gate — "build only if real usage
creates the desire to blanket-allow `ptk_invoke`" (`.agents/decisions.md`,
OPEN destructive-cmdlet entry) — has fired: the owner runs ~10 agents on
blanket approvals, and this week codex invoked `ptk_invoke` from inside a
read-only review sandbox (MCP tools sit outside harness command
classifiers; GitHub issue #3 documents the same bypass from live use).

**Honesty line carried from the threat model:** these are guardrails
against model sloppiness and unreviewed reach, NOT a security boundary —
same OS user, no privilege separation. The recorded threat model stands;
this plan narrows blast radius and adds visibility, it does not make ptk
safe against a hostile local process.

## Prior art this plan assembles (nothing here is new thinking)

- **Archived policy-file design** (three iterations, verbatim in
  `docs/history/decisions-archive.md`): outside-workspace config,
  default read-only, cmdlet classification via
  `SupportsShouldProcess`/`ConfirmImpact` + alias resolution,
  fail-closed on unknowns and natives.
- **Issue #3's proposals** (agent-authored): deny/allow regex patterns
  evaluated pre-execution (deny wins) with a refusal message the model
  can relay; append-only audit log (the job system already being "80% of
  this"); optional read-only/dry-run mode for locked-down installs.
- The shared-runspace idea plan's admin story (audit tail in the CLI)
  should consume the same log format — one design, recorded there.

## Slices

1. **Audit log first** (visibility before enforcement): the server
   appends one line per user call — UTC timestamp, tool, script (or a
   hash + head when oversized), route taken, raw flag, exit code,
   duration, outcome (ok / refused / timed out / recycled) — to an
   append-only file under `~/.ptk/logs/`, rotated by size. On by
   default: it is the cheapest artifact with immediate value to an
   owner managing many agents, and it makes every later enforcement
   decision reviewable. No execution behavior changes.
2. **Policy gate:** a declarative policy file OUTSIDE any workspace
   (`~/.ptk/policy.*` — format decided at implementation from the
   archived design), evaluated server-side before execution. Two tiers,
   reconciling the two prior designs: regex deny/allow for native
   command lines (deny wins), cmdlet classification
   (`SupportsShouldProcess`/`ConfirmImpact` + alias resolution) for
   PowerShell. Fail-closed on unknowns while a policy is active; no
   policy file = today's behavior exactly. **This opt-in default is a
   DEPARTURE from the recorded baseline, flagged, not slipped in
   (slp-1):** the archived tentatively-accepted design was DEFAULT
   READ-ONLY — destructive commands refused unless pre-authorized,
   protection present without any file. Opt-in means an owner who
   blanket-allows `ptk_invoke` believing the archived behavior shipped
   is unprotected. The choice (opt-in as drafted vs. archived
   default-read-only) is an explicit owner decision at approval, and
   whichever wins amends the open decision entry so the record
   matches. Either way both edges are defined exactly: policy active +
   command matching no rule → refused (fail-closed, including
   known-destructive cmdlets); no policy file → whichever default the
   owner picked, stated in the model-visible tool description.
   Refusals are labeled, name the matched rule, and teach the recovery
   path (edit the policy file).
   **Classification runs in the execution context it judges (slp-3):**
   the server already resolves foreground commands against the mutable
   WARM session and background commands against a pristine COLD table,
   because warm aliases can shadow anything. The policy classifier
   inherits that split: a foreground call is classified with warm
   resolution (what will actually run), a background call with cold
   resolution (jobs run `pwsh -NoProfile`). One shared evaluator that
   uses warm state for both is a hole: repoint `ri` at `Get-Item` in
   the warm session, get it classified read-only, then run
   `background=true` where stock `ri` = `Remove-Item` deletes data.
   Regression test: a repointed built-in alias classified per-path.
3. **Read-only preset:** a shipped policy preset expressing issue #3's
   read-only/dry-run install, as a starting template rather than a
   hardcoded mode.

## Open decisions the owner makes at approval

- Slice-1 default (recommended ON) and whether script text or hash+head
  lands in the log (privacy vs debuggability).
- Gate default while a policy is active vs. absent (slp-1 above):
  opt-in as drafted, or the archived default-read-only.
- Whether an ACTIVE policy also gates `ptk_reset` and `ptk_job kill`
  (recommended: yes — see the control-actions requirement below).

**Background jobs are NOT an owner decision (slp-2):** an active policy
MUST gate `background=true` identically to foreground, evaluated before
any job preflight or process start. The background flag hands the same
arbitrary script to a child process; leaving it optionally ungated
makes every deny rule and the read-only preset defeatable by adding one
parameter to the refused call. Verification must run the identical
denied script down both paths and see two refusals.

## Verification

Battery + guard proofs per slice (red-leg proven); live checks: the
issue-#3 bypass scenario replayed against an active deny rule; a
sandboxed reviewer session demonstrating refusal + audit trail; codex
reviewloop per the recorded process.
