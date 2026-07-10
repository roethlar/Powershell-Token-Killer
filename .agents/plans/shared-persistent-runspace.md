# Plan: shared / persistent runspaces (IDEA — not approved)

**Status:** IDEA recorded 2026-07-10 from the owner's gist, in the owner's
words: "a shared runspace that multiple agents can access concurrently"
and "a persistent runspace that will survive a harness restart — maybe
keyed to a guid or something that the model can save and check back out."
NOT approved for implementation. Like all further building, it sits
behind the go/no-go adoption test in `.agents/decisions.md` (Open
Decisions); it also supersedes-on-approval the greenfield design's
recorded non-goal "runspace pools / parallel foreground calls"
(`.agents/plans/greenfield-design.md`), which stands until the owner says
otherwise.

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
2. **Security.** A standing daemon executing arbitrary shell is a
   standing exec service, and a GUID becomes a bearer token to an
   authenticated (possibly Exchange-admin) session. This amplifies the
   issue-#3 permission-bypass concern and likely lands behind the
   policy-file gate design recorded in `.agents/decisions.md`. Socket
   permissions, key handling, and audit logging are design inputs, not
   afterthoughts.
3. **Lifecycle.** Per-key idle expiry (an abandoned GUID must not hold
   an authenticated session forever), daemon start/stop ergonomics,
   crash recovery semantics ("your key survived but its runspace did
   not" needs an honest, machine-readable answer — the issue-6
   `Recovering`/`WarmStateLost` plumbing is the precedent).
4. **Transport.** MCP stdio-proxy vs. streamable HTTP; what each harness
   on the owner's machines can actually register.

## Trigger

Per the recorded build discipline: experienced benefit on real daily
usage, not anticipated need. The natural evidence would be the Windows
go/no-go week showing repeated cold-import pain across session restarts
(the slice-7 Exchange/AD latency numbers speak directly to this).
