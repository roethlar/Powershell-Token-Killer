# Plan: persistent then shared runspaces (staged — build not approved)

**Status:** IDEA recorded 2026-07-10 from the owner's gist ("a shared
runspace that multiple agents can access concurrently"; "a persistent
runspace that will survive a harness restart — maybe keyed to a guid or
something that the model can save and check back out"). **Staging
adjudicated by the owner in-session 2026-07-10:** persistence ships
first, sharing second (rationale below); this supersedes the earlier
notes' attach-only-first recommendation — the owner explicitly set the
notes' preference aside. Build is NOT approved: what gates it is (a) an
explicit owner build go and (b) the measured-pain criterion of the
canonical shared-host decision entry in `.agents/decisions.md` (which
carries the matching 2026-07-10 amendment). The product go/no-go is not
a blocker — decided GO 2026-07-08, archived. On a build go this also
supersedes the greenfield non-goal "runspace pools / parallel foreground
calls" (`.agents/plans/greenfield-design.md`).

## Why this staging (owner adjudication, 2026-07-10)

The expensive enabler is identical for both features: a standing host
plus "attach to a session by key." One shared session versus many keyed
ones is only how many keys exist, so splitting sharing out as a
separate first build (the old notes' attach-only staging) is artificial
— it pays for the daemon while dodging the isolation design. Reversed,
each stage ships standalone value:

- **Stage 1 — persistence** (keyed sessions, ONE client per key,
  process-per-key): "Exchange stays logged in across restarts." No
  cross-client hazards exist yet by construction, and process-per-key
  makes the env/auth isolation problems disappear structurally instead
  of being mitigated.
- **Stage 2 — sharing** (allow a second client on an existing key): a
  small increment on stage 1 that adds exactly one hard thing — the
  state-changed-under-me contract between calls.

## What exists today (for contrast)

- One stdio server per harness session, spawned and killed by the
  harness; the warm runspace dies with it. The `IdleWatchdog` (4h
  backstop) exists to kill servers that outlive their session — this idea
  deliberately inverts that philosophy for a designated daemon.
- One runspace per server, calls serialized. A PowerShell runspace runs
  one pipeline at a time regardless, so "concurrent" access to a single
  shared runspace is always serialized access; the issue-6 work (total
  wall-clock budgets, never-queueing ptk_state busy reports with
  active-call age and waiter count) is the contention story that would
  make shared access livable.

## Sketch (to be designed properly if approved)

**Stage 1 — persistence.** A standing local registry/broker; each key
maps to its own worker PROCESS hosting one warm runspace
(process-per-key: env vars and process-global module/auth state are
isolated by construction — hard problems 1-2 dissolve rather than get
mitigated). Per-session MCP stdio servers become thin proxies (local
socket / named pipe), or the harness registers a long-lived MCP
endpoint. `ptk_invoke` gains a session key: the model saves the GUID
(the harness-visible tool result is durable enough) and checks the same
session back out after a harness restart — imported Exchange/AD/Graph
modules and authenticated connections survive. One client per key,
enforced. On Windows this multiplies the warm-runspace value prop:
import Exchange once per day, not once per session.

**Stage 2 — sharing.** Permit a second client on an existing key
(explicit opt-in). Serialized as ever, busy-reported per the issue-6
plumbing; the new work is the between-calls contract (hard problem 5):
client identity, a generation/change signal so a client can tell "same
session I left" from "changed under me," and loud shared
timeout/reset messages.

## Interaction sketch (owner-shaped, 2026-07-10)

There is no mode switch — you hand out a key, per conversation,
reversibly. No key = today's private per-session behavior, unchanged;
the daemon starts lazily on the first keyed request and never otherwise.
"Keep this session" → the agent calls a `ptk_session` tool, gets a GUID
(visible in the conversation, so the human and the model can both save
it), and passes it on subsequent `ptk_invoke` calls. A fresh chat checks
the key back out. Sharing (stage 2) requires the key to have been
CREATED shareable — opt-in at creation, never something that happens to
an existing private key. `ptk_state` always names the mode in effect.

## Admin visibility and control — CLI, no model in the loop (owner requirement, 2026-07-10)

Humans must be able to see and control sessions without asking an agent:

- **For free from process-per-key:** every session is a real OS process
  — visible in Task Manager/`ps`, killable with stock OS tools. Sessions
  are never invisible state inside one opaque daemon.
- **Proper admin CLI** against the same daemon socket: `ptk sessions
  list` (key, age, client count, busy/idle, last activity, shareable
  flag), `ptk sessions kill <key>`, `ptk daemon status|stop`, and an
  audit-log tail. The daemon is the natural home for the append-only
  audit log (timestamp, session key, client, script, exit) already
  proposed in GitHub issue #3 — this requirement and that proposal
  should land as one design.
- **Does not reopen D5:** the retired CLI face was a shell-dispatch
  surface the MCP tools replaced; this is an admin/observability face
  for a daemon, a different job.
- **Web interface — recorded as an option (owner musing 2026-07-10),
  NOT v1.** Glanceable sessions + browsable audit log is real value,
  but a listening web port adds an auth/attack surface on exactly the
  security-sensitive component; the CLI is required for control either
  way and inherits OS permissions via the local socket. If built: an
  EXTERNAL companion (e.g. a `ptk dashboard` command serving a
  localhost read-only page fed by the same socket/CLI data the admin
  commands use) — the daemon itself grows no HTTP surface. Read-only,
  localhost-only, no control actions without a real auth story; control
  stays in the CLI. `ptk sessions list --json` ships in v1 to stabilize
  the data contract a dashboard would consume — it does not by itself
  make one free.

## Hard problems to solve before any build

1. **Environment variables are process-wide** — RESOLVED BY STAGING:
   process-per-key (stage 1) gives each session its own env by
   construction; the current drift/reset design carries over per
   worker unchanged. (The runspace-per-key alternative would need env
   virtualization and is recorded as rejected for this reason.)
2. **Module and authentication state can be process-global, not
   per-runspace** — RESOLVED BY STAGING for the same reason: e.g.
   Microsoft.Graph's `Connect-MgGraph -ContextScope Process` scopes
   auth to the PROCESS, so runspaces-in-one-process could switch or
   kill each other's tenant context; process-per-key contains it. Any
   future densification (many runspaces in one worker) must re-open
   this with per-module isolation probes.
3. **Security.** A standing daemon executing arbitrary shell is a
   standing exec service, and a GUID becomes a bearer token to an
   authenticated (possibly Exchange-admin) session. This amplifies the
   issue-#3 permission-bypass concern and likely lands behind the
   policy-file gate design recorded in `.agents/decisions.md`. Socket
   permissions, key handling, and audit logging are design inputs, not
   afterthoughts.
4. **Lifecycle.** Per-key idle expiry (an abandoned GUID must not hold
   an authenticated session forever), daemon start/stop ergonomics,
   crash recovery semantics ("your key survived but its runspace did
   not" needs an honest, machine-readable answer — the issue-6
   `Recovering`/`WarmStateLost` plumbing is the precedent).
5. **Semantic interleaving between calls** — STAGE 2 ONLY (stage 1's
   one-client-per-key rule excludes it by construction). Serialization
   and busy telemetry protect a single call, not the state BETWEEN
   calls: with one key shared, agent B can change
   cwd/variables/connections — or trigger a timeout recycle or
   ptk_reset that evicts everything — in the gap between agent A's
   calls (the canonical entry already names shared timeout/reset blast
   radius the biggest operational hazard). Stage 2 needs an explicit
   shared-state/lease contract, client identity in ptk_state, and a
   generation counter or change notification so a client can tell
   "same session I left" from "changed under me"; waiter counts alone
   are insufficient.
6. **Transport.** MCP stdio-proxy vs. streamable HTTP; what each harness
   on the owner's machines can actually register.

## Trigger

Per the recorded build discipline and the canonical entry's own
criterion: measured pain, not anticipated need — real use showing
repeated reauth/reimport across sessions on one box, or a real
multi-agent handoff need. The slice-7 Exchange/AD cold-import latency
numbers, when run, speak directly to this.
