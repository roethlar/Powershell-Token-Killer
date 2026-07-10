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
   policy file = today's behavior exactly (the gate is opt-in, so
   nothing changes under an owner who never writes one). Refusals are
   labeled, name the matched rule, and teach the recovery path (edit
   the policy — a human act outside the agent's reach; that asymmetry
   is the point).
3. **Read-only preset:** a shipped policy preset expressing issue #3's
   read-only/dry-run install, as a starting template rather than a
   hardcoded mode.

## Open decisions the owner makes at approval

- Slice-1 default (recommended ON) and whether script text or hash+head
  lands in the log (privacy vs debuggability).
- Whether an ACTIVE policy also gates `background` jobs and `ptk_reset`
  (recommended: yes, same gate, one enforcement point).

## Verification

Battery + guard proofs per slice (red-leg proven); live checks: the
issue-#3 bypass scenario replayed against an active deny rule; a
sandboxed reviewer session demonstrating refusal + audit trail; codex
reviewloop per the recorded process.
