# Plan: second security layer (policy-gate approach REJECTED 2026-07-11; problem OPEN)

## STOP — read this before the rest of the file

**Status of the security question: OPEN, under active owner-led
consultation. Nothing here is settled.**

### The problem statement (owner's framing for an outreach consultation, 2026-07-11)

The owner drafted this as the FRAMING he is taking to outside help — it
is the argument to be tested, **NOT a delivered verdict and NOT a
recorded decision**. Recorded here because the next agent must not
re-derive it, and must not mistake it for a settled call:

- ptk would get "zero consideration" in an architecture review: it is
  **a low-friction bypass gated on one careless, persistent "yes."**
  The install question is "allow ptk?" and the honest human answer is
  always "of course, I installed it, I need you to use it." That single
  grant then covers every subsequent command forever.
- **The missing control is a per-ACTION sanity check:** there is no
  `Allow Remove-Mailbox CEO@company.com  Y/n?` moment. A harness would
  impose one per destructive command; routing through an MCP tool
  erases it.
- **"The agent could do it anyway with direct tools" is not a defense** —
  the objection is precisely that ptk removes friction the harness would
  otherwise apply. Equivalent worst-case reachability is not equivalent
  risk when one path asks and the other never does.

Its author's standing (30 years IT, security event response, ARB
interviews) is why the framing deserves weight — it is not why it is
true. Treat it as the strongest available statement of the problem, and
the thing any proposed shape must answer.

### What the owner HAS decided (his own words, in-session 2026-07-10/11)

The declarative policy-file gate is rejected as the answer: "brittle
nonsense — no allow/deny list survives an even half-competent agent's
attempt to work around a friction point. Can't call `rm -rf`? Make an
alias. Use python to delete. Edit the rules file." The slices below are
retained as PRIOR ART ONLY. **Do not implement them.**

That rejection is a real owner call. The problem framing above is not.

**The live question:** how to restore a per-destructive-action human
check to a tool whose whole value is unattended, low-friction execution —
without the per-call prompts already dead in practice at ~10 agents.

**Candidate shape, UNVERIFIED — next session's first job:** MCP
**elicitation** (server-initiated request for user input mid-call). If
the protocol and the owner's harnesses support it, ptk can raise its own
confirmation for a classified-destructive action — the tool grant stops
being a blanket grant, and the `Remove-Mailbox CEO@company.com  Y/n?`
moment exists again, sourced by the server rather than the harness
classifier that never sees it. Verify: (1) MCP spec support for
elicitation and its current status; (2) which of Claude Code / codex /
grok / agy actually implement it (a server-side prompt nobody renders is
worse than none — it fails open silently); (3) what happens headless.
This is the only shape found that answers the owner's objection on its
own terms; it was NOT proposed by any reviewer — it comes from the
owner's critique.

## Cross-harness consultation record (2026-07-10/11)

Two rounds, both recorded because the first was methodologically
botched and the failure is instructive.

**Round 1 — LEADING PROMPT (discard the verdict, keep the additions).**
Three harnesses (codex, grok, agy) were asked "what is the shape of the
security layer, if any?" — but the prompt contained the sentence *"the
tool should build nothing; the layer belongs elsewhere' is a fully valid
recommendation"* plus a pointer to where the blast radius lives. All
three returned `build_nothing_is_the_answer: true`. **That unanimity is
an echo of the prompt, not evidence** (owner caught this: "you handed
them the answer"). Salvageable, because it required knowledge rather
than agreement: (a) credential scoping collapses if the broad secret is
readable on disk by the agent's OS identity — a denied agent simply
re-authenticates as the owner from any exec path; (b) where remote roles
are too coarse (legacy AD), the surviving alternative is a typed
operation broker, never arbitrary PowerShell; (c) a keyed pre-authenticated
shared session (the parked shared-runspace idea) is a bearer-token risk
concentrator and must not be built without this settled.

**Round 2 — OWNER'S FRAMING ("how do we secure this at the app layer").**
Neutral prompt, no escape hatch, no bypass list. All three produced
ranked app-layer measures. Convergent items worth keeping regardless of
what the gate question resolves to:

- **Secret redaction on every output path** (all three, independently —
  and NEW to this repo's thinking): tokens, PATs, JWTs, connection
  strings, credential dumps currently flow through the compressor into
  model context, harness transcripts, and job logs. codex's framing:
  *"raw should mean uncompressed, not unredacted."* This is a live leak
  in a tool whose job is to shape output, and it is independent of the
  policy debate.
- **ConstrainedLanguage mode** (agy) — a real PowerShell primitive, not
  a hand-rolled filter: blocks arbitrary .NET/`Add-Type`. Cost is high
  (breaks admin modules and dev work), so it is a profile, not a
  default — but it is app-layer, OS-backed, and was never considered
  here.
- **Authenticated-session lifecycle** (codex, grok): TTL and hard
  teardown for admin sessions; on privileged reset, replace the WORKER
  PROCESS, not just the runspace, because auth state is process-scoped.
- **Control-action gating placement** (grok, codex): whatever gate
  exists must sit ABOVE `ptk_reset`/`ptk_job kill` — today ResetTool
  kills jobs before the host reset, so a check inside `ResetAsync`
  fires after the destructive half already ran.
- Every one of them ranked the in-tool command policy LAST and marked it
  advisory-only, naming the owner's own bypasses. Independent of the
  round-1 leak, that is three-for-three against the gate as a control.

Raw verdicts: `shape-{codex,grok,agy}.json` and
`applayer-{codex,grok,agy}.json` in the session scratchpad (not durable —
the substance is recorded above).

## Process failure, recorded so it is not repeated

The policy-gate design was driven through **seven adversarial review
rounds** (20 findings, all fixed, all graded) without any round being
able to ask *"is this mechanism capable of achieving the goal?"* — a
review loop grades text against its own premise. The owner's five
questions demolished the premise that ten agent-hours of reviewing had
polished. **Lesson (owner, verbatim): "what you should have said is,
'this is the goal. what's the shape?' then iterated on that."** Shape
review BEFORE plan review; a plan loop cannot save a wrong shape.

---

## PRIOR ART BELOW — DO NOT IMPLEMENT (premise rejected above)

**Original status:** DRAFT 2026-07-10 on the owner's instruction
(in-session). The recorded build criterion for the policy gate — "build
only if real usage creates the desire to blanket-allow `ptk_invoke`"
(`.agents/decisions.md`, OPEN destructive-cmdlet entry) — had fired: the
owner runs ~10 agents on blanket approvals, and codex invoked
`ptk_invoke` from inside a read-only review sandbox (MCP tools sit
outside harness command classifiers; GitHub issue #3 documents the same
bypass from live use). The criterion firing is still true; the RESPONSE
recorded below is what the owner rejected.

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
   **Retention (slp-11):** rotation without retention just shards the
   growth — ~10 writers accumulate rotated files until the disk fills
   and audit itself enters the degraded path (`JobManager` already
   sweeps aged job logs for exactly this reason). The slp-4 design
   freeze sets an age AND total-size cap for `~/.ptk/logs/` (defaults
   to propose: 30 days / 200 MB, owner-tunable), enforced at server
   start AND at every rotation event — a long-lived server that keeps
   rotating enforces the cap itself; startup-only enforcement would
   let it fill the disk between restarts. A sweep never touches
   another LIVE process's active file (liveness checked via the pid in
   the filename). Acceptance cases: aged rotations provably bounded,
   and a rotation loop in one long-lived process stays under the
   total-size cap without any restart.
   **Crash truth (slp-12):** a hard-killed server leaves background
   children running and its `job-end` unwritten forever; a merged
   reader must not render that as "still running". The pid+startstamp
   in the audit filename makes it decidable: a `job-start` with no
   `job-end` whose writer pid is dead reads as TERMINAL-UNKNOWN
   (server died; child outcome unrecorded) — distinct from running
   and from every recorded outcome. Acceptance case: hard-kill the
   server mid-job and prove the merged reading.
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

## Verification (slp-10 — an acceptance matrix, not a happy path)

Battery + guard proofs per slice (red-leg proven), plus a per-slice
acceptance matrix; a single foreground deny replay proves almost
nothing about the properties this plan claims.

- **Slice 1 (audit):** every tool (`ptk_invoke` fg/bg, `ptk_job`
  status/output/kill, `ptk_state`, `ptk_reset`) produces its event(s);
  job events correlate call→start→end; refused/timed-out/recycled
  outcomes logged; concurrent writers; rotation under load; forced
  write failure = call succeeds + loud degraded-audit signal; log-off
  configuration produces zero writes.
- **Slice 2 (gate):** policy states — absent, active, malformed,
  deleted-after-load (fail-closed per slp-5); the SAME denied script
  refused on foreground AND background (slp-2); deny-beats-allow;
  unknown-command fail-closed; repointed-alias context test (slp-3);
  dynamic invocation (`Invoke-Expression`, `& $cmd`) fail-closed;
  native regex deny/allow incl. `raw=true` and each `route` value;
  control actions checked before side effects — a refused reset kills
  no jobs (slp-6); refusal text names the rule.
- **Slice 3 (preset):** the shipped preset loads cleanly, its
  read-only verdicts hold for a destructive-cmdlet sample and both
  scriptless control actions, and a documented edit (allowing one
  command) behaves.
- **Live checks:** the issue-#3 bypass scenario replayed against an
  active deny rule end-to-end over MCP stdio; a sandboxed reviewer
  session demonstrating refusal + audit trail; codex reviewloop per
  the recorded process.
