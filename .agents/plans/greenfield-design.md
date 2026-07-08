# Plan: ptk greenfield design — the agent shell, derived from the goal

**Status:** DRAFT for owner review. Owner-requested design exercise
(2026-07-08): distill the goal, design the product greenfield — the way the
designing agent would build it from scratch — plan only, codex-reviewed
before presentation. NOT approved; authorizes no code. Where this design
contradicts a recorded decision, the contradiction is flagged inline and
would be recorded in `.agents/decisions.md` at adoption, never silently.

## The goal

A coding agent does shell work through a cold, stateless pipe that renders
output for human eyes. That is wrong on three axes at once:

1. **Cold.** Every call pays process start, module import, and
   authentication again. On the estate this tool serves (Windows admin
   work: AD, Exchange, EXO, Graph), that is 30s+ per call — or impossible,
   because interactive auth cannot happen inside a per-call process.
2. **Stateless.** No variables, no cwd, no connections survive between
   calls. A human admin's entire productivity model — a warm terminal that
   accumulates context over a working session — is denied to the agent.
3. **Human-formatted.** Output is rendered as tables, colors, and repeated
   log lines for eyes, then billed by the token to a model that needs only
   the signal.

**Goal statement: give a coding agent the shell a senior Windows admin
actually has — one persistent session with warm modules, live connections,
and accumulated state — and make everything it returns cost as little of
the model's context as the signal allows. And make the agent use it without
having to remember to.**

The last clause is load-bearing and evidence-bound, not rhetorical: rtk
usage via AGENTS.md instructions decayed; the 2026-07-02 headless dry-run
measured 0/13 unprompted MCP adoption (harness hides MCP tools behind
ToolSearch); headroom was net negative. Tools whose benefit depends on the
model's ongoing discipline do not get adopted. Adoption must be structural.

## Design principles

Each principle traces to evidence already banked in this repo; none is
aesthetic.

- **P1 — The session is the product.** Warm state is the capability, not a
  cache to invalidate. (2s vs 30s+ EMS; 2026-07-08 live feedback: warm
  state "the standout feature".)
- **P2 — Compress at the source, never in place.** Shape fresh output
  before it enters context. Never rewrite context already sent — that is
  headroom's prompt-cache re-billing trap.
- **P3 — Everything returned is bounded and labeled.** No output path may
  be unbounded. Every elision is explicit (`+N more`, `[N lines elided]`),
  every side channel labeled (`[exit]`, `[errors]`, `[warnings]`), and the
  escape hatch (`raw=true`) is a deliberate model choice, not an accident.
  A compression tool with an unbounded worst case fails at its own job.
- **P4 — Adoption by construction, not discipline.** One tool surface, a
  harness hook that redirects to it, and tool descriptions plus error
  messages that teach the tool's own use patterns at the moment they
  matter. Never depend on the model remembering an instruction.
- **P5 — Shaping never fails a call.** Any internal shaping failure returns
  labeled raw output. (The existing never-throw contract — earned through
  the round-2 dispatch-guard loop; kept verbatim.)
- **P6 — Delegate what others do better.** Native-command filtering belongs
  to rtk (no maintained per-command list in ptk — the owner rejected
  "maintained debt list" designs). File reading and searching belong to the
  harness's own tools, which are better at them.
- **P7 — Not a security boundary.** Commands run with the caller's
  authority; the harness permission prompt is the gate. (Recorded threat
  model; the destructive-cmdlet gate stays out unless blanket-allow
  pressure materializes.)
- **P8 — Personal-tool scale.** Built and extended on experienced benefit
  on real daily usage, never a tool-reported metric. Graceful degradation
  over configuration. Smallest surface that serves the workflow.

## The design

### One component: the ptk server

A single self-contained per-RID binary; a stdio MCP server. Greenfield
re-derives this shape from the world, not from the existing code: a stdio
MCP server is the only mechanism an agent harness offers for a
session-scoped warm process (verified 2026-06-27/07-02); runspaces are not
thread-safe, so one serialized foreground runspace; `SessionEnd` cannot be
trusted, so lifetime is managed inside the server (per-call timeout with
recycle, idle self-exit backstop). The server owns three things:

- **The session** — one warm interactive runspace. Serialized foreground
  calls. A call that exceeds its timeout is aborted and the session
  recycled, with the loss labeled in the reply.
- **The job runner** — background execution for long work (below).
- **The shaper** — a PowerShell shaping library loaded into the session.
  Shaping must live in PowerShell: the entire point is catching objects
  before they are formatted to text, which requires ETS/PSObject access
  in-runspace. Greenfield, the library is an implementation detail of the
  server — not a user-facing module with exports, aliases, and a CLI face.

### Tool surface — four tools, small on purpose

Every tool costs context in every session's tool list; each additional
tool also dilutes the "one obvious tool" adoption story. Four is the
floor that covers the workflow.

**1. `ptk_invoke` — the one shell tool.**
Arguments: `script`; `raw=false`; `route=auto|pwsh|rtk`;
`background=false`; `timeoutSeconds` (per-call override, capped by the
server maximum).

- Foreground (default): runs in the warm session; output returns shaped.
- `background=true`: starts the script as a job and returns a job id
  immediately. For anything that could exceed the call timeout — builds,
  deploys, watchers, long queries.
- **Teach at the moment of failure:** the timeout error message itself
  teaches both recovery paths, because they differ by workload: stateless
  long work (builds, watchers, native-heavy commands) reruns with
  `background=true` and polls with `ptk_job`; work that NEEDS the warm
  session — a long EXO/AD query on a live connection, the workloads the
  session exists for — reruns foreground with a larger `timeoutSeconds`,
  because a background job is a cold process and would forfeit exactly
  the state that makes the call worth routing through ptk. The one place
  a model reliably reads documentation is the error it just received
  (P4). The 2026-07-08 live feedback showed the background+poll pattern
  works but currently lives only in a state file no model ever sees.

**2. `ptk_job` — poll the long work.**
Arguments: `action=status|output|kill|list`; `id`; `offset`.
`output` returns the job's new output since `offset`, run through the same
shaping pipeline, bounded per poll (P3).

Job mechanics — deliberate greenfield call: **a job is a child `pwsh`
process with output redirected to a file under `~/.ptk/jobs/`**, not a
second in-process runspace. Rationale: jobs are long by definition, so the
~500ms cold start is noise; a process gives free, robust kill/cleanup
semantics and zero thread-safety questions against the SDK; and it
formalizes exactly the pattern live usage already validated by hand.
The trade-off is explicit: a job does NOT see the warm session's state
(modules, connections, variables). Jobs are for builds and watchers —
native-heavy work that needs no warm state; stateful admin work belongs in
foreground calls. The tool description says so. Child processes inherit
the NUL-stdin guard so stdin-readers cannot hang a job.

**3. `ptk_state` — the session made observable.**
No arguments. Returns: engine version, server PID, uptime (subsumes
`ptk_ping`); current directory; loaded modules (subsumes `ptk_modules`;
`listAvailable` stays as its one argument); running jobs; and **drift** —
what this session has changed since birth: environment variables added or
modified (`PATH` shown as a diff, prominently), and the session variable
count. This is the designed answer to the recorded hazard that warm state
is both the standout feature and the main foot-gun (the fake-`npm`-shim
incident): persistence stays exactly as-is because it IS the product (P1);
it becomes diagnosable in one call instead of by mystery (P4).

**4. `ptk_reset` — the nuke.**
Recycles the session to factory state and kills running jobs. One
documented meaning: after reset, nothing survives. A job worth keeping
across a reset is a process the caller should have started as one.

### The shaping pipeline — one pipeline, every path

1. **Route (pre-execution).** A script that is exactly one bare
   native-application command with constant arguments is rewritten through
   rtk, whose per-command filters compress `git`/`npm`/`docker`-class
   output at the source and pass through commands they do not know.
   Everything else — pipelines, cmdlets, variables, chains, redirections,
   `.cmd`/`.bat` shims — executes as PowerShell in the session. This is
   the current routing design kept verbatim: it embodies P6 and survived a
   full adversarial probe series (alias shadowing, shim quoting, stdin
   hang, `$Error` pollution). No rtk → scripts run unchanged, unfiltered,
   never a failure.
2. **Classify output.**
   - **Objects** → typed summaries: guarded per-type compressors (today:
     filesystem, process, service, match-info; the set grows only when
     daily usage earns an entry — P8) with a null-safe generic table as
     the floor. The earned dispatch guards stay: every dereferenced
     property is checked AND every item's type name must match — shape
     alone or name alone never routes.
   - **Text** → **normalize first: strip ANSI/control sequences before
     anything else.** Terminal color codes are pure token waste to a
     model, and they defeat downstream classification — the log-shape
     regexes anchor on line start, so a `\e[32m`-prefixed log line dodges
     the dedup leg entirely (observed live: vite, 2026-07-08). Then
     classify: log-shaped → rtk dedup (labeled fallback to raw if rtk is
     absent or fails); otherwise bounded passthrough.
3. **Bound (P3).** Every text leg ends in the bounder: a generous
   head+tail window with an explicit `[N lines elided — raw=true for
   everything]` marker. Object legs are already bounded (`MaxItems` /
   `+N more`). Thresholds are sized so real command output virtually never
   hits them (proposed: 400 lines / 40 KB; tune at implementation) — the
   cap exists for the pathological case, a whole-file `Get-Content`
   landing in context through a compression tool. **This amends the
   Phase 2 "plain text passes through verbatim, never truncated"
   contract** — flagged per the plan header: greenfield, the boundedness
   invariant outranks completeness-by-default, because protecting context
   is the tool's one job; completeness is the escape hatch (`raw=true`,
   which stays unbounded because it is a deliberate choice), not the
   default.
4. **Label.** Output first, then `[exit] N`, `[errors]`, `[warnings]`
   sections when present. Earned surface, praised in live use — kept.

### The adoption kit

- **Hook.** PreToolUse deny-with-guidance on the harness Bash and
  PowerShell tools, `PTK_DIRECT` escape hatch, fail-open, installed by an
  `rtk init`-style installer (local default, `-Global`, `-Show`,
  `-Uninstall`, `-DryRun`, surgical preservation of foreign hooks).
  Current design kept: deny-with-guidance is the strongest redirect the
  harness supports, and the dry-run evidence says nothing weaker works.
- **Codex.** Registration snippet; Codex loads MCP descriptions upfront,
  so there the descriptions alone carry adoption weight.
- **Descriptions are the real documentation.** The model never reads
  README.md; it reads the tool list and the errors. Budget tokens there
  deliberately: the `ptk_invoke` description teaches warm state (import
  once, reuse), long work (`background=true`), and hygiene (`ptk_state`
  to diagnose, `ptk_reset` to recover); the timeout and failure messages
  repeat the relevant lesson in-line (P4).

### Distribution

Self-contained per-RID binaries on GitHub Releases, one-line installers,
one `~/.ptk` home for payload and config, winget readiness on Windows.
This is the approved release-distribution plan unchanged — greenfield
derives the same answer ("a tool fighting for adoption cannot ask its
user to build it from a checkout"), so the in-flight track continues as
scheduled.

### Explicitly not in this design

- **The CLI face** (`ptk ls|read|grep|smart|savings` verb dispatch, the
  `ptk` alias, `Invoke-Ptc`). File reading and searching compete with
  harness-native Read/Grep tools that are better at them, the hook does
  not route them, and live usage never touched them. Greenfield, this
  surface does not exist; the module's value is the shaping library
  inside the server. **Adopting this closes the open universal-wrapper
  decision by dissolution** — `ptk_invoke` IS the universal surface, so
  the "should the CLI dispatch any cmdlet" question no longer has an
  object; record the closure in `.agents/decisions.md` at adoption.
  `Measure-PtcSavings` goes with it: tool-reported savings are explicitly
  not this repo's success metric (P8); a dev may keep a scratch script.
- **Destructive-cmdlet policy gate** — out per P7 and the recorded
  posture; revisit only under real blanket-allow pressure.
- **A PowerShell 5.1 substrate** — the decom horizon (recorded 2026-07-03:
  on-prem Exchange gone in 6–12 months, then the estate is 7-native) caps
  its value below its cost. On-prem work in the meantime rides implicit
  remoting held inside the warm session (the slice-7 check).
- **Runspace pools / parallel foreground calls** — serialization is a
  correctness feature until real usage proves a parallel need.
- **The ollama/local-model shaping leg** — dropped by owner decision;
  stays dropped.

## Delta: greenfield vs. what is built

The honest conclusion first: **the built product is this design minus five
deltas.** The architecture — stdio server, single warm runspace,
in-runspace PowerShell shaping, rtk delegation, hook enforcement,
self-contained distribution — re-derives identically from the goal,
because the evidence chains that produced it (warm-process mechanics,
thread-safety, adoption failures, cache traps) are facts about the world,
not artifacts of the codebase. The deltas:

| # | Delta | Principle | Evidence |
|---|-------|-----------|----------|
| D1 | Strip ANSI/control sequences at text ingest, before classification and return | P3 | 2026-07-08 live feedback (vite noise); `Test-PtcLogShaped` misclassifies colored logs — `Remove-PtcAnsi` exists but the passthrough leg never applies it |
| D2 | Bound every output leg: labeled head+tail cap on the passthrough (amends the Phase 2 never-truncate contract) | P3 | design hole: unbounded worst case in a compression tool |
| D3 | First-class background jobs (`background=true` + `ptk_job`), teach-at-timeout error text, per-call `timeoutSeconds` | P1, P4 | 2026-07-08 live feedback: the background+poll pattern works but lives only in `.agents/state.md` |
| D4 | `ptk_state` with drift report (subsumes `ptk_modules` + `ptk_ping`) | P1, P4 | 2026-07-08 live feedback: PATH/shim pollution is the recorded hazard of the standout feature |
| D5 | Retire the CLI face; the module becomes the server's internal shaping library | P6, P8 | pre-server vestige; unrouted by the hook; competes with better harness tools; unused in live sessions |

### Sequencing sketch (if adopted — each slice separately gated)

1. **D1** — small, pure win, guard-tested (colored-log fixture must fail
   without the strip). No contract questions.
2. **D2** — thresholds proposed above; the contract amendment recorded in
   `.agents/decisions.md` when approved.
3. **D4** — `ptk_state`/drift; removes two tools, adds one.
4. **D3** — the largest new build (job runner, `~/.ptk/jobs` lifecycle,
   poll shaping); cleanly separable from everything else.
5. **D5** — mostly deletion: CLI dispatch, its tests, `docs/usage.md`;
   README rewrite to a single-surface story; decision-entry closure.

Interaction with in-flight work: none of this blocks release-plan slice 3
(the release workflow), which continues independently. D1/D2/D4 are small
enough to improve the product the ~2026-07-20 go/no-go evaluates; D3 can
land after the window if the owner prefers a stable test article. The
go/no-go gate itself is untouched — this design describes what the product
should be; the owner's experienced-benefit test still decides whether it
continues to exist.

### Decision points this plan puts to the owner

The design makes a call on each; the owner can override at approval.

1. **D2 thresholds and the contract amendment** (design: 400 lines /
   40 KB head+tail; `raw=true` stays unbounded).
2. **D3 job substrate** (design: child `pwsh` process + redirected file,
   accepting that jobs never see warm session state; alternative
   considered and rejected: per-job in-process runspaces — more machinery
   to share state jobs do not need).
3. **D5 timing** (design: after the go/no-go window — retirement is
   product hygiene, not test-article improvement).
