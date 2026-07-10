# Agent Decisions

Record durable repo decisions here. Do not use this as a chat log. Each entry should make
sense without conversation history and should name superseded guidance when relevant.

Keep this file to what is currently in force or still open. When a decision is
closed - superseded, or settled and retained only as the rationale for a rule that
now lives in its canonical home elsewhere - move it verbatim, in that same change,
to an archive under `docs/history/` (for example `docs/history/decisions-archive.md`);
never summarize or drop wording, the exact text is the record. Keep a single
pointer to the archive at the top of this file, not a stub per entry. The archive
is the provenance log; this file is what is in force or still open.

**Archive:** `docs/history/decisions-archive.md`

## Decision lifecycle

A decision moves through these states:

- **Open** - a finding has been assessed but not yet acted on. It lives in the
  `## Open Decisions` queue below, with the verified evidence, the options, and a
  standing recommendation. The process is unchanged until it is adopted; an agent
  records it rather than implementing on the spot.
- **Active** - a decision that is in force now.
- **Adopted YYYY-MM-DD** - an Open finding that has been acted on: its rule now
  lives in its canonical home (a procedure, template, or invariant). Note where the
  rule landed; the finding is retained in place as the rationale that led to it,
  until it is archived.
- **Superseded** - replaced by a later decision; name the replacement.

When an entry becomes purely historical rationale - Adopted or Superseded, with the
live rule now owned elsewhere - archive it per the rule above: move it verbatim to
`docs/history/`, do not leave a stub.

## Decisions

### ACTIVE (2026-07-10): everything non-PowerShell routes through rtk (owner direction, in-session)

**Status:** Direction set by owner; implementation requires a plan (not yet
drafted). The current router hands rtk only scripts that are exactly one
bare native command with constant arguments (unified-shell-routing plan) —
deliberately narrow when routing first landed. The owner's direction: rtk
(v0.43.0 probed locally; per-tool filters for git/gh/docker/kubectl/
dotnet/pnpm/psql/aws/grep/rg/test-runners plus generic `err`/`test`/
`summary`/`log`/`read` wrappers and `gain` savings telemetry) already owns
robust compression routing, so ptk should classify ANY non-PowerShell work
onto it and let rtk's own dispatch pick the filter. PowerShell-native
output keeps ptk's object compression. Evidence base: the 2026-07-10
shaped-vs-raw measurement (object leg 3%, log leg 2%, rtk-git 46%,
plain-text passthrough 100% kept — the passthrough row never reached rtk
under the current shape check). Plan to define: chains/pipelines with
native segments, cmdlet-pattern mapping (e.g. file reads), refusal/dialect
interaction, and per-filter fidelity risks.

**Status:** Active — approved by owner in-session 2026-07-09. The plan's
decision points stand as recommended: D1 = (a) detected bash-only shapes get
a fast labeled refusal naming the construct and the platform-aware recovery
paths (no auto-translation; `route=pwsh` and `raw=true` bypass as consent);
D2 = non-breaking raw posture (reword every model-visible raw surface to
recovery-only, raw-usage visibility via server log line + `ptk_state`
counter at the user-call boundary only; gating/justification declined);
D3 = one dialect line in hook deny + ptk_init nudge + README routing
section.

**#4 reconciliation at approval:** the cross-model comment's four
acceptance suggestions were folded into D2 — adopted: no-preemptive-raw
(the recovery-only rewording), teaching `route=pwsh` + `raw=false` as
"exact execution, shaped output" (joins the reword inventory with slice-3
assertions), raw telemetry in `ptk_state`; declined: reason/cost gate on
unjustified raw (friction on a deliberate escape hatch; revisit only with
evidence that rewording fails). Slice 0 probe results freeze into the plan
before implementation; slices 1-4 land one commit + battery + codex loop
each.

**Amendment (2026-07-09, owner unparked sd1-4 in-session):** the slice-0
`set` exemption is narrowed — `set -e/-u/-x/-o pipefail` is flagged only
while `set` still resolves to the stock `Set-Variable` alias; an ambient
re-pointing or a preceding script-local `Set-Alias`/definition suppresses
the finding (fix `c43360c`). Rationale: the exemption predated the
detector's resolution-guard machinery and had become the lone exception to
the uniform "a name that works in this session is never bash evidence"
rule, in conflict with the plan's precision-first principle.

**Amendment (2026-07-10, sd3-1 adjudication — owner delegated the call
in-session):** D2's "every reworded surface that describes raw also names
the route=pwsh + raw=false pairing" is scoped to surfaces serving the
FIDELITY motive (tool and parameter descriptions, the nudge block, the
README/server-README override sections — all of which now teach it). The
in-band elision markers and the sentences that describe them advise only
`raw=true`, because elision applies on every shaped route: the pairing
cannot recover an elided middle, and teaching it at that moment would be
false advice (live proof in `.agents/review/findings/sd3-1.md`). Decided
under the owner's agent-experience principle, recorded in
`.agents/repo-guidance.md` (Earned Practices).

### ACTIVE (2026-07-08): Greenfield design adopted — `.agents/plans/greenfield-design.md`

**Status:** Active — approved by owner in-session 2026-07-08 after the codex
review loop on the plan text closed (gfd-1..gfd-4 fixed, re-grade RESOLVED
x4 / NO NEW FINDINGS; `.agents/review/index.md`). The plan's three
decision-point calls stand unoverridden: passthrough bounds 400 lines /
40 KB with `raw=true` unbounded; background jobs are child `pwsh` processes
(no warm session state); D5 (CLI-face retirement) executes after the
go/no-go window.

**What it changes, durably:**

- **Amends the Phase 2 passthrough contract** (2026-07-03 amendment in the
  continuation entry below): plain-text output of `ptk_invoke` is no longer
  "full passthrough, never truncated" — every text leg is bounded by a
  generous labeled head+tail window; completeness moves to the explicit
  `raw=true` escape hatch. Rationale: boundedness outranks
  completeness-by-default in a tool whose one job is protecting context
  (plan, principle P3).
- **Closes the universal-wrapper open decision by dissolution** —
  `ptk_invoke` is the universal surface, so "should the CLI dispatch any
  cmdlet" no longer has an object. The CLI face itself is retired by D5
  (deferred post-window). The entry moved verbatim to
  `docs/history/decisions-archive.md` in this change.
- **Execution scope:** D1 (ANSI strip at text ingest), D2 (bounded
  passthrough), D4 (`ptk_state` drift report subsuming
  `ptk_modules`/`ptk_ping`), D3 (background jobs), in that order, each
  slice committed and codex-looped; D5 deferred.

**Unchanged:** the go/no-go gate itself (decided GO 2026-07-08 and archived
to `docs/history/decisions-archive.md`), the release-distribution plan
(slice 3 still queued), the destructive-cmdlet pause, and the
not-a-security-boundary threat model.

## Open Decisions (deferred - not yet adopted)

### OPEN (2026-07-08): Destructive-cmdlet policy gate (carried out of the archived go/no-go entry)

**Status:** Open — parked on its own criterion, which survives the
2026-07-08 GO decision. The full three-iteration design record (two
rejected variants and the tentatively acceptable declarative policy file:
outside-workspace config, default read-only, classification via
SupportsShouldProcess/ConfirmImpact + alias resolution, fail-closed on
unknowns/natives) lives verbatim inside the archived continuation entry in
`docs/history/decisions-archive.md`.

**Criterion (unchanged):** keep `ptk_invoke` on ask-per-call in the
harness; build the policy gate only if real usage creates the desire to
blanket-allow `ptk_invoke`. All variants are guardrails against model
sloppiness, NOT security boundaries (recorded threat model).

### OPEN (2026-07-08): Whether to build a shared multi-client warm host (+ shared signals)

**Status:** Open — recorded from owner-shared design notes
(`F:\notes\PTK\shared-warm-runspace.md` and `shared-warm-runspace-plan.md`,
machine-local; decision-relevant core captured here). No code authorized;
the notes' own slice 0 is "approval and probe".

**Question:** Should ptk grow an optional long-lived host with a local
multi-client transport (named pipe / Unix socket), so multiple harness
sessions attach to ONE warm PowerShell session — modules, connections, cwd,
env shared across clients — plus a structured ephemeral signal store
(`ptk_signal`: add/list/update/close with actor, kind, scope, TTL) for
agent-to-agent coordination that does not abuse PowerShell variables?

**What it would bring:** heavy imports and unattended connects (AD, EXO
cert, Graph, implicit remoting) paid once per machine, reused by every
attached agent; warm state survives harness lifecycle (attach/detach, not
cold-start per chat); one place for drift/reset hygiene; fast
reviewer/implementer/verifier handoff via signals.

**Dominant gotchas (the notes' own analysis):** runspaces stay
single-pipeline, so sharing serializes agents behind each other — shared is
not faster; one timeout recycle evicts warm state for EVERY client (the
biggest operational hazard); not-a-security-boundary becomes cross-agent
lateral movement (any client reads/mutates every other's state — same OS
user, local-only, explicit opt-in are hard prerequisites); cwd/env/PATH are
one namespace, so unrelated-project agents mostly want isolation, not
share; reset semantics change from "fix my mess" to "evict everyone".

**The notes' recommendation, adopted as this entry's standing
recommendation:** do NOT make shared mode the default; build it only if
real use shows repeated reauth/reimport across sessions on one box (or
real multi-agent handoff need) — measured pain, not anticipated. If built:
attach-only hard share first (one host, one serialized runspace, full
shared state, loud shared timeout/reset messages, client identity in
ptk_state), signals in that same first version, private mode unchanged as
default. Named sessions and any private-variable multi-tenancy only if the
narrow form earns it.

**AMENDED 2026-07-10 (owner adjudication, in-session): the staging above
is superseded.** The owner explicitly set the notes' attach-only-first
preference aside: the enabler (standing host + attach-by-key) is the same
for both features, so persistence ships FIRST — GUID-keyed sessions,
process-per-key, ONE client per key — and sharing (a second client on an
existing key, opt-in) ships second as the increment that adds the
between-calls contract. Staged sketch and hard-problem mapping live in
`.agents/plans/shared-persistent-runspace.md`. Unchanged: private mode
stays the default, the measured-pain criterion still gates any build, and
no build is approved yet.

**Gate interaction:** behind the go/no-go like everything else, and behind
its own measured-pain criterion even after a go. The v2 greenfield design
(2026-07-08 adoption entry above) is the private-session product this
would extend; nothing in it blocks or presumes sharing.

### OPEN (2026-06-27): Whether to give ptk a session-persistent warm-runspace backend

**Status:** Open - selected as active work by owner 2026-07-02 and BUILT the same
day: slices 1-6 of `.agents/plans/warm-runspace-mcp-server.md` are complete,
verified, and pushed (server in `server/`). Slice 7 (Windows AD/EMS/EXO module
validation) was paused behind the go/no-go gate; that gate was decided **GO
2026-07-08** (owner, in-session — entry archived to
`docs/history/decisions-archive.md`), so slice 7 is unblocked open work.
This is the **substrate** counterpart to
the universal-wrapper decision
above: that entry settled that the universal path MUST run in-process to preserve a
warm host; this entry asks where that warm host should come from when the harness does
not happen to provide one.

**Question:** Should ptk own a persistent PowerShell host - a single long-lived
runspace that loads heavy modules (`ActiveDirectory`, `ExchangeOnlineManagement`) and
establishes their authenticated connections **once**, then serves many agent tool
calls from that warm state for the life of a coding session? And if so, in what form?

**What triggered it:** The universal-wrapper evidence showed the owner's on-prem
`Get-Queue` workflow only works because the agent happens to be running *inside* an
already-open EMS host whose implicit-remoting PSSession persists in that process. That
is incidental, not architectural: it is not portable, not reproducible from a cold
harness, and covers only the modules/connections that ambient host already loaded.
Cost driver is concrete - a cold per-call `pwsh` reloads modules and re-authenticates
every call (on-prem EMS connect is 30s+; EXO/Graph connects cost auth round-trips). The
question is whether ptk can provide that warm host *deterministically* instead of
depending on an ambient one.

**Verified evidence gathered this session (keep - expensive to re-establish):**

- **A stdio MCP server is the one Claude Code mechanism that gives a session-scoped
  warm process.** It is launched once and runs as a single long-lived child process
  for the whole session; tool calls are JSON-RPC to that same process, so an in-memory
  .NET object / PowerShell `Runspace` it creates persists across calls. (claude-code-guide
  agent, citing `code.claude.com/docs/en/mcp.md`.)
- **Per-tool-call timeout is generous.** `MCP_TOOL_TIMEOUT` default is ~28h; a per-server
  `timeout` (ms) in `.mcp.json` overrides it. There is a hard wall-clock cap per call
  and progress notifications do not extend it, but there is ample headroom for module
  load / connection setup. The 5-minute idle timeout applies to remote HTTP/SSE servers,
  not stdio. (`mcp.md`.)
- **The Bash-daemon alternative fights the harness.** The Bash tool is not a persistent
  shell - each call is a separate process, env vars do not persist, and background
  processes started via Bash are killed on session end or orphaned (open Claude Code
  issues #25188, #43944). `SessionEnd` hooks are non-blocking and not guaranteed to run
  on crash / Ctrl-C, so they cannot be relied on to tear a daemon down. The dedicated
  PowerShell tool (`CLAUDE_CODE_USE_POWERSHELL_TOOL=1`) has the same per-call-process
  model and does not help. (tools-reference.md, hooks-guide.md, the two issues.)
- **No official PowerShell MCP SDK.** The practical path is a .NET stdio server hosting
  `System.Management.Automation` and owning the `Runspace` in-process (tightest fit), or
  a Node/Python stdio server shelling into a persistent `pwsh`. (claude-code-guide.)
- **Headless EXO auth must be certificate-based app-only.** `Connect-ExchangeOnline`
  with MFA is interactive and cannot run inside a non-interactive server process;
  app-registration + certificate (`-CertificateThumbprint -AppId -Organization`) is the
  supported unattended path and the direction Microsoft is steering tenants toward.

**Settled sub-decisions (conditional on building it at all):**

- **Transport = stdio MCP server, not a Bash-spawned daemon.** Claude Code owns the
  lifecycle (start at session start, kill at session end), which sidesteps the
  background-process persistence/teardown bugs above. The Bash-daemon option is rejected
  on reliability grounds, not just cleanliness.
- **Implementation = .NET stdio server hosting `System.Management.Automation`**, owning a
  single `Runspace` in-process. The `PwshTokenCompressor` module loads once in that same
  runspace.
- **Core requirement = modules load once with no reload tax across calls.** Heavy
  modules (`ActiveDirectory`, `ExchangeOnlineManagement`, etc.) import into the warm
  runspace on first use and stay loaded. For connection-bearing modules, unattended
  auth (e.g. app-registration + certificate for EXO) is the supported pattern — no
  interactive `Connect-*` in the server. EXO is an example, not the defining case.
  (Corrected 2026-07-02: an earlier version of this entry recorded cert-based EXO
  auth itself as the hard requirement; owner clarified the requirement is warm module
  load generally.)
- **One serial runspace, not a pool.** Cmdlets and implicit-remoting PSSessions are not
  thread-safe; serialize calls. A per-call timeout recycles the runspace on a wedge
  rather than hanging the session. Reach for a `RunspacePool` only if real parallelism is
  ever proven necessary.
- **Module strategy = enumerate `Get-Module -ListAvailable` at startup, lazy-load + cache
  on first use.** Expose `ptk_modules` (available/loaded) and `ptk_reset` (recycle the
  runspace / clear leaked `$global:` / cwd / `$PSDefaultParameterValues` state).
- **Substrate vs shaping stay separate.** The runspace is *where* a command runs; output
  still flows through `Compress-PtcObject` (objects, lossless) before return. The
  `experiment/ptk-router` branch (rtk for logs, ollama for prose, deterministic text
  filter otherwise) is the *shaping* layer behind a `ptk_invoke { <scriptblock> }` tool -
  it is complementary, not an alternative.
- **Lifetime is managed inside the server** (idle self-timeout + idempotent
  startup cleanup), never via `SessionEnd`, which is not guaranteed to fire.

**Relationship to the universal-wrapper decision:** complementary, not competing
(and see the 2026-07-02 continuation decision below, which now gates all further
work on both entries). The universal wrapper is the *surface* (`ptk <cmdlet>`); the persistent runspace is the
*substrate* (a deterministic warm host). The MCP tool is the portable replacement for
"the agent happens to live in a warm EMS host." If both are built, `ptk_invoke` runs the
cmdlet inside the owned runspace.

**Why deferred (owner's framing):** unchanged from the universal-wrapper entry - ptk is
a personal/team tool complementing `headroom`; the build trigger is measured benefit on
real daily Windows work, not faith. This is a *larger* build than the universal wrapper
(a whole MCP server + .NET hosting + app-reg/cert setup), so the bar is at least as high.

**Standing recommendation (for whoever picks this up):** Do not build the server first.
(Superseded in practice 2026-07-02: the owner chose to build the server without the
step-1 measurement; it is built. Retained for the record.)
(1) Quantify the pain - count cold `Import-Module` / `Connect-*` invocations and their
latency over a week of real sessions; if the ambient-warm-host accident already covers
the daily workflow, the deterministic host may not pay for itself yet. (2) If material,
prototype the smallest possible .NET stdio MCP server exposing one tool
`ptk_invoke { <scriptblock> }` against a single warm `Runspace` with cert-based EXO
preconnect, returning `Compress-PtcObject` output. (3) Only then add the module map,
`ptk_reset`, and the router shaping layer. Each step is a separate authorized change
requiring its own go.
