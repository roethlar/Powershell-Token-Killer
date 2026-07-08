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

**Unchanged:** the go/no-go gate itself ("Whether ptk continues at all",
below), the release-distribution plan (slice 3 still queued), the
destructive-cmdlet pause, and the not-a-security-boundary threat model.

## Open Decisions (deferred - not yet adopted)

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

**Gate interaction:** behind the go/no-go like everything else, and behind
its own measured-pain criterion even after a go. The v2 greenfield design
(2026-07-08 adoption entry above) is the private-session product this
would extend; nothing in it blocks or presumes sharing.

### OPEN (2026-06-27): Whether to give ptk a session-persistent warm-runspace backend

**Status:** Open - selected as active work by owner 2026-07-02 and BUILT the same
day: slices 1-6 of `.agents/plans/warm-runspace-mcp-server.md` are complete,
verified, and pushed (server in `server/`, registered in `.mcp.json`). Slice 7
(Windows validation) and any Phase 2 are paused behind the go/no-go test in the
2026-07-02 continuation decision below. This is the **substrate** counterpart to
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

### OPEN (2026-07-02): Whether ptk continues at all — substrate go/no-go after owner vacation

**Status:** Open, AMENDED 2026-07-03 (owner): Phase 2 compression is UNPAUSED —
the owner chose to build it ahead of the go/no-go so the test evaluates the full
product (warm runspace + compression together). Scope set by owner the same day:
`ptk_invoke` output routes objects → `Compress-PtcObject`, log-shaped text → rtk
when an rtk binary is present, all other text → full passthrough; the ollama /
local-model leg of the router experiment is dropped. Plan:
`.agents/plans/phase2-compression.md`. The universal wrapper and the
destructive-cmdlet gate REMAIN paused behind the test. Owner return date is now
~2026-07-20 (was ~2026-07-16). The go/no-go itself is unchanged: unprompted
adoption + experienced benefit on the real Windows box remain the criteria.

**AMENDED 2026-07-04 (owner):** unified shell routing is UNPAUSED — the owner
chose to make ptk the single tool surface for all shell work before the go/no-go,
plus a harness hook that enforces it. Scope set by owner the same day:

- **One tool for everything shell-shaped.** PowerShell scripts run in the warm
  runspace (the existing `ptk_invoke` path); log-shaped output routes to rtk
  (the existing `Compress-PtcOutput` leg); simple native command lines (git,
  npm, docker, ...) route to rtk so its per-command filters apply — compression
  happens for everything that supports it and the model has one tool to reach
  for.
- **A harness hook forces the redirect.** A PreToolUse hook on the harness's
  Bash and PowerShell tools redirects shell work to ptk, so adoption does not
  depend on model discipline — the direct answer to the 2026-07-02 headless
  dry-run (0/13 unprompted MCP usage; MCP tools hidden behind ToolSearch) and
  the rtk instruction-decay evidence.

**AMENDED 2026-07-04 (owner, later the same day):** a release/distribution
track is authorized ahead of the go/no-go. The owner judged the current
install story (running the MCP server out of a repo checkout via `dotnet run`)
unacceptable for anyone else to use, and set a **first public release target
of 2026-07-25**: prebuilt self-contained per-platform binaries on GitHub
Releases plus a one-line installer. A publish-and-register script and .NET
tool packaging are dev-only paths, explicitly not the public install story.
Plan (APPROVED by owner 2026-07-04, same day):
`.agents/plans/release-distribution.md`. Owner resolved the plan's open
questions the same day (5 RIDs incl. Windows/Linux ARM, version v0.2.0, one
`~/.ptk` home for payload+config on every platform and install method, winget
as the eventual primary Windows path with readiness — ARP entry, hostable
install logic — built into v0.2.0; resolutions recorded in the plan). One
question stays deliberately open: the public installer's hook default
("decision for later"; must close before the installer slice ships).
Interaction with the go/no-go is deliberate: CI produces only **draft**
releases; the `v0.2.0` tag and the publish click are owner actions after the
~2026-07-20 test window, so a no-go can still end the project with nothing
public shipped.

Consequence for the test: the ~2026-07-20 go/no-go now evaluates the routed
one-tool product with the hook installed; the criteria shift accordingly —
"unprompted adoption" is satisfied by construction on hooked sessions, so the
operative criterion becomes experienced benefit (real time/aggravation saved,
never a tool-reported metric) plus absence of friction that makes the owner
disable the hook. The universal PowerShell wrapper CLI face and the
destructive-cmdlet gate REMAIN paused. Plan (requires its own approval before
code): `.agents/plans/unified-shell-routing.md`. The zero-code alternative
(installing rtk's own Bash-rewrite hook and leaving ptk as-is) was considered
and declined in favor of the routed tool; rtk's hook may still complement the
bash leg if the probe slice finds it cheaper.

**Original status (2026-07-02):** Open - all further building is PAUSED by owner
decision until the test below runs. This gates Phase 2 (compression), the
universal wrapper, and the destructive-cmdlet gate. The warm-runspace server
itself (slices 1-6) is built, verified, and stays registered.

**What triggered it:** the owner stepped back and asked whether ptk is worth it,
citing two pieces of evidence from sibling tools:

- **headroom is stopped, net negative.** Its compression rewrote existing context,
  causing prompt-cache rewrites whose re-billing exceeded the savings. Its
  ~39.5M-tokens/day figure was the tool's own metric, not net benefit. (This
  corrects the claim recorded in the universal-wrapper entry above.)
- **rtk does not get used reliably** when instructed via AGENTS.md; usage is
  model-dependent and decays.

**The generalized finding (owner-confirmed):** tools whose benefit requires the
model's ongoing *discipline* - remembering a compression step whose payoff the
model never experiences - do not get adopted. ptk's CLI face and any
instruction-driven compression inherit this failure mode. Two distinctions keep
ptk from being dead on this evidence alone:

1. **Cache mechanism does not transfer:** ptk compresses fresh tool output before
   it enters context; unlike headroom it does not rewrite cached prompt prefixes,
   so the specific net-negative mechanism is headroom's, not ptk's.
2. **The warm-runspace server is a capability, not a discipline:** on the owner's
   Windows box, ptk_invoke is the only deterministic warm path to EMS/EXO (2s vs
   30s+ or failure). The model experiences that difference directly, which is an
   adoption story the rtk evidence does not already contradict - but it is a
   hypothesis, not a result.

**The test (after owner returns ~2026-07-16):** work normal days on the Windows box
with the server registered and observe two things:

- **Adoption:** does the model reach for ptk_invoke *unprompted* for Exchange/AD
  work?
- **Benefit:** does it save real time and aggravation (not a tool-reported metric -
  see the headroom trap)?

Both yes → Phase 2 (compression riding on an already-adopted tool) earns a second
look. Model ignores it like rtk → archive the project with the finding recorded.

**Also parked behind this gate - destructive-cmdlet security layer.** Design was
explored 2026-07-02 through three iterations, recorded so it need not be re-derived:
(1) two-tool split with harness permissions (ptk_invoke / ptk_invoke_unsafe) -
REJECTED by owner: a sticky "always allow" grant on the unsafe tool silently
removes the gate; (2) server-enforced per-call OS approval dialog - REJECTED by
owner: too cumbersome, unworkable headless/automation; (3) declarative policy file
outside the workspace, default read-only, destructive commands refused unless
pre-authorized, classification via each cmdlet's own SupportsShouldProcess /
ConfirmImpact metadata plus alias resolution, fail-closed on unknowns/natives -
tentatively acceptable to owner. All variants are guardrails against model
sloppiness, NOT security boundaries (the model has raw shell access; recorded
threat model unchanged). Interim posture: keep ptk_invoke on ask-per-call in the
harness; build the policy gate only if real usage creates the desire to
blanket-allow ptk_invoke.
