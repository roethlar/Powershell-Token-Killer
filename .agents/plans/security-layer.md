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
safe against a hostile local process. **That applies to the policy file
itself (slp-5):** an agent with any raw-shell path (harness Bash, a
second tool) runs as the same user and can edit or delete
`~/.ptk/policy.psd1`; outside-workspace placement buys resistance to
ACCIDENTAL workspace-scoped edits and keeps the file out of repo
checkouts — it is not an agent-proof control plane, and no wording in
this plan may claim otherwise. Concretely: once a server process has
loaded a policy, that file going missing or unreadable mid-session is
fail-closed with a labeled diagnostic (slp-4 rule) — deleting the file
under a running gated server is loud, not a silent return to
unrestricted; only a server that STARTS with no file gets the no-policy
default.

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
   **Background calls are events, not a line (slp-7):** a
   `background=true` invoke returns before the exit code exists, so
   "one line with exit + duration" cannot describe it, and appending
   the exit later would mean mutating an append-only record. The log
   is therefore event-shaped with correlation: `call` (every tool
   call, fields known at accept time), `job-start` (job id joined to
   its originating call), `job-end` (exit, duration) — foreground
   calls collapse to one `call` event carrying everything. The job
   system is an execution SOURCE feeding these events, not the audit
   store: issue #3's "80% of this" overstates it — `JobManager`
   persists only child output; script, start, and exit metadata live
   in process memory, and foreground/control calls never touch it.
   **Format (slp-8):** versioned JSONL, one event per line. Fields
   beyond the list above, each required by a named consumer: `cwd`
   (issue #3's own field — a relative destructive command is
   meaningless without it), tool-specific arguments for control calls
   (`ptk_job` action/id — status and kill must not look alike),
   requested vs effective route, event/call/job ids for correlation,
   policy file digest + matched rule on refused/allowed-by-rule events
   (decisions stay reconstructable after the policy is edited), and
   NULLABLE session-key/client fields reserved for the
   shared-runspace admin CLI so that consumer extends rather than
   breaks the format.
   **Many writers, and writes can fail (slp-9):** the motivating
   deployment is ~10 agents = ~10 concurrent server processes under
   one `~/.ptk/logs/`. Layout: one file PER PROCESS
   (`audit-<pid>-<startstamp>.jsonl`) — no cross-process locking, no
   torn lines, rotation is process-local by size; readers merge on
   timestamp (the admin-CLI consumer already needs a merge step).
   Write failure (disk full, permissions): the call still executes —
   slice 1 promises no execution behavior change — but the failure is
   NOT silent: a labeled warning line in the server's stderr log and a
   degraded-audit flag in `ptk_state`. Verification covers concurrent
   writers, rotation under load, and a forced write failure.
2. **Policy gate:** a declarative policy file OUTSIDE any workspace,
   evaluated server-side before execution. The file is
   `~/.ptk/policy.psd1` — not open: the release-distribution plan
   already reserves that name and location as user-owned config
   (slp-4). **A cold agent cannot implement "decided at
   implementation", so slice 2 begins with a design-freeze step whose
   output amends THIS plan before any code:** a versioned psd1 schema
   (schema version, deny/allow lists, tool/action rules, preset
   identity); the complete evaluation algorithm — whole-script AST
   walk, not a top-level glance (every command element in every nested
   block), explicit verdicts for dynamic invocation (`&`/`.`
   with expressions, `Invoke-Expression`, `Invoke-Command`,
   `Start-Process` — fail-closed while active), native vs unknown
   handling, deny-beats-allow ordering; malformed-or-unreadable file =
   fail-closed with a labeled diagnostic, never silently unrestricted;
   reload semantics (per-call stat or explicit reload — pick one);
   and the exact preset contents slice 3 ships. The freeze is reviewed
   in the loop like any slice before implementation continues. Two tiers,
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
   **Control actions need their own vocabulary and check placement
   (slp-6):** regex-over-natives and cmdlet classification cannot
   express "may this client recycle the runspace" — `ptk_reset` has no
   script, and it kills all background jobs BEFORE the host reset
   (`ResetTool.cs`), so a gate placed inside `ResetAsync` refuses after
   the destructive half already ran. `ptk_job kill` is a second
   scriptless destructive action. The schema freeze (slp-4) therefore
   includes tool/action rules, every check sits at the MCP tool
   boundary BEFORE any side effect, and the read-only preset states
   its verdict for reset and job-kill explicitly.
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
