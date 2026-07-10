# Plan: shared / persistent runspaces (IDEA — not approved)

**Status:** IDEA recorded 2026-07-10 from the owner's gist, in the owner's
words: "a shared runspace that multiple agents can access concurrently"
and "a persistent runspace that will survive a harness restart — maybe
keyed to a guid or something that the model can save and check back out."
NOT approved for implementation. The product go/no-go gate is NOT what
blocks it — that was decided **GO 2026-07-08** and archived
(`docs/history/decisions-archive.md`). What gates this idea is (a) an
explicit owner approval and (b) the measured-pain criterion of the
canonical shared-host decision entry (next paragraph). On approval it
would also supersede the greenfield design's recorded non-goal "runspace
pools / parallel foreground calls"
(`.agents/plans/greenfield-design.md`), which stands until the owner
says otherwise.

## Canonical decision record (read it first)

This idea is NOT the first durable record of shared warm hosting: the
**OPEN (2026-07-08) entry in `.agents/decisions.md` — "Whether to build a
shared multi-client warm host (+ shared signals)"** — already captures
the owner's design notes, the dominant gotchas, and a standing
recommendation: private mode stays the default; if built, an
**attach-only hard share ships first** (one host, ONE serialized
runspace, full shared state, loud shared timeout/reset messages, client
identity in ptk_state), and **named sessions / private multi-tenancy
come only if the narrow form earns it**. That entry owns the decision;
this file only adds the owner's 2026-07-10 gist.

What this gist adds beyond that record: **persistence across harness
restarts via a key the model saves and checks back out** (GUID
checkout). The N-keyed-runspaces sketch below is the "named sessions"
stage of the recorded staging — building it FIRST would override the
standing attach-only-first recommendation, which is an explicit owner
decision to make at approval time, not a default this file gets to set.

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

- A standing local daemon owns N runspaces keyed by GUID. Per-session
  MCP stdio servers become thin proxies to it (local socket / named
  pipe), or the harness connects to a long-lived MCP endpoint directly.
- `ptk_invoke` gains a session key: the model saves the GUID (the
  harness-visible tool result is durable enough) and checks the same
  runspace back out after a harness restart — imported Exchange/AD/Graph
  modules and authenticated connections survive. On Windows this
  multiplies the warm-runspace value prop: import Exchange once per
  day, not once per session.
- Same key shared by several agents = the shared runspace (serialized,
  busy-reported); distinct keys = isolated warm sessions in one daemon.

## Hard problems to solve before any build

1. **Environment variables are process-wide.** The current drift/reset
   design (baseline snapshot, `RestoreEnvironmentBaseline`) assumes one
   runspace per process. Keyed runspaces in one daemon would bleed env
   changes (PATH shims!) across sessions, and one session's ptk_reset
   would clobber another's env. Needs per-runspace env virtualization,
   or a documented shared-env contract, or process-per-key instead of
   runspace-per-key.
2. **Module and authentication state can be process-global, not
   per-runspace.** Distinct keyed runspaces are NOT a generic isolation
   boundary: e.g. Microsoft.Graph's `Connect-MgGraph -ContextScope
   Process` scopes the auth context to the PROCESS, so key B
   reconnecting or disconnecting can switch or kill key A's tenant
   context despite "isolated" runspaces — admin-state bleed no amount
   of env virtualization fixes. Each connection-bearing module needs an
   isolation/concurrency probe; **process-per-key is the generic
   strong-isolation option** unless in-process safety is proven per
   module.
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
5. **Semantic interleaving between calls.** Serialization and the busy
   telemetry protect a single call, not the state BETWEEN calls: with
   one key shared, agent B can change cwd/variables/connections — or
   trigger a timeout recycle or ptk_reset that evicts everything — in
   the gap between agent A's calls, and A's still-valid GUID silently
   reaches changed or fresh state (the canonical entry already names
   shared timeout/reset blast radius the biggest operational hazard).
   The design needs an explicit shared-state/lease contract, client
   identity in ptk_state, and a runspace generation counter or change
   notification so a checkout can tell "same session I left" from
   "changed under me"; waiter counts alone are insufficient.
6. **Transport.** MCP stdio-proxy vs. streamable HTTP; what each harness
   on the owner's machines can actually register.

## Trigger

Per the recorded build discipline and the canonical entry's own
criterion: measured pain, not anticipated need — real use showing
repeated reauth/reimport across sessions on one box, or a real
multi-agent handoff need. The slice-7 Exchange/AD cold-import latency
numbers, when run, speak directly to this.
