# Plan: resilient MCP guardian and automatic backend recovery

**Status:** PLANNING BOUNDARY OWNER-APPROVED 2026-07-15;
IMPLEMENTATION NOT AUTHORIZED. The approved boundary is: add two recovery
layers so the MCP host automatically replaces failed session workers and a
minimal public-pipe guardian automatically restarts the host; neither layer
replays uncertain calls, every recovery changes generation, only declared
bootstrap state is recreated, and health reporting remains usable. This file
records the implementation contract to review before a separate code go.

The owner additionally accepted on 2026-07-15 that the guardian is a small
session-lifetime control-plane binary, not a general pipe relay. It owns audit
admission, sealed output, public IDs, frozen bootstrap metadata, and generation
allocation while loading no PowerShell. Its own crash ending the stdio MCP
connection is an acceptable residual boundary.

`.agents/decisions.md` remains under the owner's existing hold and is not
amended by this plan. This plan is the canonical source for MCP transport and
backend recovery. It narrowly supersedes the target topology and the
unexpected-worker-loss/manual-restart rules in
`.agents/plans/audited-harness-sessions.md`; it does not supersede that plan's
mandatory audit, containment, no-replay, prepared-operation, output-capability,
or harness/session-isolation contracts. Shipped behavior remains unchanged
until individually approved implementation slices land.

## Outcome

Keep one public MCP connection usable for the guardian's complete harness
lifetime while recovering, without human intervention, from either:

1. loss of one session worker inside a healthy host; or
2. loss of the private host and all of its session workers.

Recovery is automatic but never transparent about state loss. It creates new
process identities and generations, restores only frozen declared session
bindings/bootstrap, reports arbitrary warm state and jobs lost, and returns
exactly one truthful terminal result for every call that overlapped failure.

## Terms and identities

- **Guardian:** the harness-owned process launched by the MCP client. It alone
  owns public stdin/stdout, the public MCP initialization, mandatory audit
  admission, output capabilities, public IDs, the frozen session catalog and
  recovery manifest, and all generation allocation. It loads no PowerShell
  runtime and executes no user, bootstrap, RTK, Bash, or native command.
- **Host:** one private replaceable control-plane process. It owns live session
  slots, worker protocol clients, routing/execution coordination, and worker
  launch/containment machinery. It has no public MCP handles, no public tool
  schema authority, and no audit/SIEM credentials.
- **Worker:** one replaceable process containing exactly one `SessionRuntime`
  for one session alias, as defined by the audited-harness plan.
- **Harness lifetime:** the lifetime of one guardian boot and its original
  public stdin/stdout. A host restart is not a new harness. Guardian death or
  public EOF ends the harness.
- **Guardian boot ID:** one random UUIDv4 fixed for the public connection.
- **Host identity:** a random UUIDv4 boot ID plus a positive signed-64-bit host
  generation allocated by the guardian for each process-creation attempt.
- **Worker identity:** the existing random worker boot ID plus a positive
  signed-64-bit generation allocated per alias by the guardian. Worker
  generations never reset when the host restarts.

The guardian allocates a host generation immediately before its own host
process-create attempt. For a worker, the guardian advances and irreversibly
consumes the alias generation when it grants the host one single-use worker-
create capability, before the host can reach OS creation. A refusal before a
grant consumes nothing; a granted capability consumes its value even when the
host dies without using it or creation/initialization/bootstrap fails. Gaps are
therefore valid. No value is reused, duplicate death signals consume nothing,
and signed-64-bit exhaustion fails closed without wraparound.

## Explicit non-goals and hard boundary

- A stdio guardian cannot repair its own dead public pipes. Guardian crash,
  kill, upgrade, or replacement ends the current MCP connection; an OS service
  restart cannot attach a new process to those pipe endpoints. Client-side
  reconnect or a separately approved Streamable HTTP design would be required
  to cross that boundary.
- This is crash recovery, not hot upgrade. One guardian pins the exact host
  executable identity, content digest, private-protocol version, public tool
  contract digest, and frozen configuration for its lifetime. A changed binary
  or tool contract is refused until a new guardian/client session.
- No daemon, detached service, durable/shared runspace, reattachment, or
  cross-harness session is introduced. Guardian, hosts, workers, brokers, and
  managed jobs remain descendants/resources of one harness.
- Recovery never serializes, checkpoints, or reconstructs arbitrary variables,
  functions, aliases, imported modules, cwd/environment drift, credentials,
  live connections, pipelines, jobs, or unsealed output.
- Recovery never resends a request or commit whose delivery or effects are
  uncertain. No optimization, retry library, or client duplicate can weaken
  this rule.
- The guardian is not a general JSON-RPC byte proxy and never forwards public
  MCP frames directly to a host. It terminates public MCP semantically and uses
  a separate private host protocol.
- Streamable HTTP, remote MCP, multi-client use, code-update handoff, and live
  guardian replacement remain separate decisions.

## Current evidence and insertion point

At this plan base, `server/PtkMcpServer/Program.cs` captures public stdio and
constructs the MCP host, mandatory audit services, `OutputStore`, and an
in-process default `SessionRuntime` in one process. A crash therefore closes
the client pipes. The staged worker lifecycle, scheduler, operation codecs,
and prepared-operation codecs are still deliberately unwired.

The audited-harness target already separates risky session execution into
workers and freezes worker boot IDs, monotonic generations, containment,
prepared commit, stale-response rejection, and `outcome_unknown`. This plan
adds a stable public guardian above the private host and replaces only the
old rule that unexpected worker loss waits for an explicit restart.

The public Codex configuration continues to launch one stdio command. That
command becomes the guardian apphost. The current server body becomes a
private exact `--host` role, and the existing exact `--worker` role remains
private. Role classification is the first executable action and accepts only:

```text
PtkMcpGuardian                 # public stdio; no role argument
PtkMcpServer --host            # private guardian child
PtkMcpServer --worker          # private host-managed session worker
```

Direct `--host` or `--worker` use without valid inherited bootstrap channels
fails before configuration, logging, audit, output, PowerShell, or MCP startup.
The role list above is the post-cutover contract. During R4-R6 only, the
existing no-argument `PtkMcpServer` public mode remains intact for installed
registrations; R7 changes registration and removes that legacy public entry in
one atomic slice.

## Target topology and ownership

```text
MCP client / harness
  └─ PtkMcpGuardian (public stdio; stable guardian boot)
       ├─ mandatory audit journal/export and call admission
       ├─ output store, public job/output IDs, frozen catalog/recovery manifest
       ├─ host lifecycle, identity, backoff, and containment observation
       └─ PtkMcpServer --host (private host generation N)
            ├─ session lifecycle and worker-protocol coordination
            ├─ session default ─────► worker generation A ─► SessionRuntime
            ├─ session ad ──────────► worker generation B ─► SessionRuntime
            └─ session exo ─────────► worker generation C ─► SessionRuntime
```

On Unix the guardian's managed descendants are rooted through a required
single-threaded native `PtkGuardianBroker`. The guardian alone holds its
liveness writer. The broker forks the gated private host, proves
`host PGID == host PID` before host exec/release, and later reaps that direct
child. The guardian and outer broker never join the host-generation group;
the host, every ordinary descendant of the transitional in-host runtime, and
every per-worker broker remain in it. The outer broker also owns the
identity-fenced pending/armed registry for worker groups that leave it. It
survives guardian death only long enough to terminate and confirm the host
group and registry before exiting. Per-worker brokers remain children of the
private host; after forced host death they are reparented and reaped by the
platform system reaper, not by an invalid nonparent `waitpid`. Deliberate
detach/process-group escape retains the audited-harness plan's explicit
partial-coverage semantics. There is no managed or shell fallback.

The guardian owns `AuditRuntimeGate`, audit/export configuration, exact-script
evidence, `OutputStore`, the public nonreusing job-ID sequence, call IDs,
frozen catalog/profile/bootstrap bytes and digests, desired session bindings,
per-alias worker-generation high-water marks, and host-generation high-water
mark. Those resources survive host replacement but not guardian death.

The host receives only bounded per-call audit/output capabilities and frozen
nonsecret session data needed for its current generation. It cannot open the
audit spool, mint public IDs, reread catalog/bootstrap files, change a binding,
or acquire public MCP handles. The host never launches another host.

The guardian executable is a separate project and apphost. Its production
dependency graph may include the MCP SDK, hosting, strict shared contract
types, audit, output, and containment primitives. It must not reference
`Microsoft.PowerShell.SDK`, `RunspaceHost`, `SessionRuntime`, `JobManager`,
execution/routing runners, or the host/worker entry assembly. A shared
contracts project contains only immutable DTOs, strict codecs, tool schemas,
limits, and normalized failure codes; dependency and source guards enforce the
separation.

## Public MCP contract

The guardian owns the public MCP initialize/initialized lifecycle exactly once
per guardian boot. It serves `ping`, the frozen server identity/instructions,
and a deterministic byte-equivalent `tools/list` without consulting the host.
A host restart never asks the client to initialize again and never emits a
public tool-list change.

Before completing the initial public initialization, the guardian starts one
host, completes private initialization, and proves that its exact build and
contract digests match the guardian. Initial failure follows the configured
MCP startup timeout. After public initialization, host loss never closes public
stdout and never makes the public protocol malformed.

Guardian-local surfaces remain prompt during host startup, containment,
backoff, circuit-open, and session recovery:

- `ptk_state` returns guardian/audit/host recovery state and the last known
  session manifest without waiting on a host or runspace gate.
- `ptk_session list` returns desired bindings plus clearly labeled stale,
  recovering, faulted, or recovery-unknown observed state.
- `ptk_output` and sealed job output remain readable through the guardian-owned
  `OutputStore`; an artifact that was not sealed before loss is truthfully
  incomplete or unavailable and is never reconstructed by rerunning work.
- `tools/list` and `ping` remain guardian-local protocol operations.

Every backend-dependent tool call arriving while no healthy host generation is
ready is admitted and terminally audited by the guardian, starts no host/worker
effect, and returns one normalized tool error. Calls are not queued across a
host-generation boundary. The stable detail codes are:

```text
host_recovering
host_circuit_open
host_containment_unconfirmed
host_contract_mismatch
host_start_failed
session_recovering
session_recovery_unknown
session_bootstrap_failed
backend_lost_before_dispatch
outcome_unknown
```

The result includes only bounded guardian/host/session generation and recovery
facts. It contains no script, arguments, bootstrap content, paths, raw
environment, exception text, or secret. Existing expected-generation checks
remain pre-effect and reject a replacement generation.

`ptk_state(listAvailable=true)` during recovery returns a labeled partial
result and performs no module scan. State reads never launch a host/worker,
reset a delay, consume a retry attempt, or wait behind effectful work. If the
mandatory audit journal is unavailable, only the existing narrow labeled
unrecorded audit-health diagnostic remains available.

The idle watchdog moves to the guardian. Public-pipe liveness, not host
liveness, controls guardian shutdown. `PTK_IDLE_EXIT_SECONDS` may recycle an
idle host/workers behind the guardian but must never close a still-open public
MCP connection; intentional idle recycle is labeled and uses the same recovery
generation rules.

## Private guardian-host protocol

Use a separate bounded strict-UTF-8 NDJSON protocol; never tunnel public
JSON-RPC. Reuse the worker protocol's 1 MiB encoded-frame limit, JSON depth 32,
duplicate/unknown-field rejection, serialized writes, bounded diagnostics, and
pooled-buffer clearing rules. Every frame carries protocol version, guardian
boot ID, host boot ID, host generation, and a positive monotonic private
request ID where applicable.

Private kinds are closed to:

```text
hello | initialize | ready | request | cancel | event | response | shutdown
```

Private initialization is first and occurs exactly once for each host
generation. It proves the pinned executable digest, host/guardian protocol
versions, public contract digest, guardian boot ID, host identity, frozen
configuration identity, and inherited private-channel ownership before the
host receives session data. Mismatch is generation-fatal and non-retryable for
that guardian boot.

The host receives a fresh recovery manifest only after authenticated private
initialization. It reports worker containment identities to the guardian
before releasing a worker's creation gate. Late frames are rejected
independently on guardian boot ID, host boot ID, host generation, private
request ID, session transition version, worker boot ID/generation, plan ID,
and operation correlation.

Every worker launch first requests a capability from the guardian. The strict
capability binds one random opaque token to guardian boot ID, host boot ID/
generation, canonical alias, alias transition version, consumed worker
generation, frozen binding/profile digest, and absolute startup deadline. Only
that host generation may present it, exactly once, for that alias transition;
duplicate, stale, expired, mismatched, or already-consumed presentation starts
no process. Guardian grant is the authoritative harness-lifetime high-water
mark: host loss between grant and OS creation never permits reuse, and the
replacement host must ask for a later generation.

Only the guardian writes public stdout. Guardian diagnostics use stderr. Host,
worker, user, RTK, Bash, and native stdout/stderr are bounded private
diagnostics and can never be parsed or forwarded as public frames. No host or
descendant inherits either public MCP handle.

## Request delivery and failure truth

For each backend-dependent call, the guardian tracks a closed delivery state:

```text
not_dispatched | write_started | terminal_decoded | public_terminal_sent
```

- Host loss in `not_dispatched` returns `backend_lost_before_dispatch`, proves
  no backend effect, and never carries the call into the replacement host.
- Once any private request byte may have been written, loss before one complete
  validated terminal response returns exactly one `outcome_unknown` and never
  resends that request.
- A complete validated terminal decoded before loss remains authoritative and
  is delivered exactly once even if the host exits before public delivery.
- Cancellation before dispatch produces zero effects. Cancellation after
  `write_started` may signal only the original host generation and never causes
  replay.
- Partial, malformed, oversized, wrong-generation, or duplicate private
  responses are never public results. The affected generation fails closed.
- A late old-generation response, progress event, diagnostic, or worker event
  is observed/discarded and cannot mutate replacement state.

For prepared worker operations, the stronger existing proof applies. Worker
loss after prepare but before any commit byte proves no execution; the
supervisor may replan against a recovered worker only within the original
absolute deadline. Once any commit byte may have reached the old worker, the
call is `outcome_unknown`; no commit or original request reaches a replacement.
Explicit `replan_required`, abort, expiry, or another typed proved-no-start
result may replan only as already authorized by the prepared-operation
contract.

## Host loss and recovery

Host EOF, process exit, protocol-fatal input, writer failure, and containment
notification race through one exactly-once loss
transition. The guardian:

1. blocks backend-dependent admission and freezes the old generation's request
   terminals;
2. using the containment authority already committed by host/worker launch,
   immediately closes the old control channels and initiates containment
   without waiting for any fresh audit write;
3. consumes the host's pre-reserved lifecycle capacity to append the observed
   loss while containment proceeds; audit media failure degrades health but
   never delays or cancels safety cleanup;
4. waits for confirmed death of the host and every registered worker/broker/
   job containment identity;
5. if confirmation fails within `timeoutContainmentGrace`, publishes
   `host_containment_unconfirmed`, keeps state/output reads available, and
   starts no replacement;
6. after confirmation, allocates one new host identity, applies backoff, starts
   the exact pinned build, initializes it once, and supplies the frozen recovery
   manifest; and
7. recovers eligible sessions independently, then publishes the host ready
   even when one session remains faulted or recovery-unknown.

Affected call/job terminals are classified when loss is observed but are
finalized and durably appended only with the resulting containment certainty.
All required terminal capacity was reserved before the corresponding effect;
if the journal is unavailable, startup reconciliation retains the existing
unclosed/outcome-unknown truth rather than inventing a successful terminal.

The guardian is the sole managed owner of the outer host containment lease. On
Windows the host generation and all descendants are inside one creation-time
`KILL_ON_JOB_CLOSE` Job Object; nested per-worker Job Objects must be probed and
supported. On Unix `PtkGuardianBroker` is the durable parent/reaper for the
private host, owns its creation-time host-generation process group, and is the
identity-fenced containment authority for descendant worker groups; it never
claims parentage or calls `waitpid` for a per-worker broker. The guardian sends
bounded launch/stop commands over its private broker channel.

Per-worker containment registration is two-phase. The per-worker broker and
its gated child start inside the host group. Before either may leave it, the
host reports the broker PID/start identity, worker PID/start identity, and
intended `PGID == worker PID` through the guardian; the outer broker records a
`pending` entry and acknowledges it. Only then may the per-worker broker arm
the worker group. The outer broker independently validates the leader identity
and group, promotes the entry to `armed`, and acknowledges again. Only that
second acknowledgment permits worker release/exec. Before the pending
acknowledgment, host-group teardown catches both processes. During the
pending-to-armed transition, teardown targets both the host group and the
identity-validated intended worker group. An entry is removed only after
confirmed broker/group disappearance; uncertainty quarantines the old host
generation.

Host death triggers guardian-requested teardown. Guardian death closes the
guardian-only liveness writer. Either path first blocks every release, then
makes the outer broker TERM/KILL the entire host-generation group plus every
pending/armed worker group, reap only its direct host child, and poll the
recorded nonchild identities until exit is confirmed; the platform reaps those
nonchildren. This contains ordinary native descendants and cold background
jobs while R5 still runs `SessionRuntime` inside the host, and works even when
the host is hung or kept its own descriptors open. Replacement never overlaps
an unconfirmed old identity, and lack of the outer broker primitive fails
startup rather than falling back to managed spawn.

Public job IDs, output handles, audit correlation, and alias generation
high-water marks never reset across host recovery. Running jobs are not
recreated. They receive one contained terminal or `job.outcome_unknown`; sealed
artifacts remain readable, incomplete artifacts remain labeled, and an old ID
can never address replacement work.

## Worker loss and automatic session recovery

Unexpected worker loss affects only its alias. The healthy host and other
sessions remain available. After confirmed old-tree death, the guardian/host
pair automatically allocates the alias's next generation and reconstructs the
declared baseline. It never continues under the old generation and never
silently treats arbitrary warm state as preserved.

Eligible automatic recovery requires all of:

- the alias had reached `ready` in its old generation;
- no open/close/restart/reset/bootstrap transition was dispatched without a
  complete acknowledged terminal;
- the binding still matches the guardian's frozen catalog/profile digest; and
- containment of the old generation is confirmed.

For a template-backed alias, recovery reruns the exact frozen bootstrap bytes
once in the new generation before ready. For an acknowledged dynamic alias,
recovery creates a fresh empty session with its frozen binding/options. A cold,
closed, never-opened, explicitly faulted, or `recovery_unknown` alias is not
speculatively opened.

Variables, functions, aliases, imported modules, cwd/environment mutations,
connections, pipelines, and jobs created after bootstrap are absent. State and
the compact session header report the old/new generations,
`warm_state_lost=true`, bootstrap restoration state, and last failure. A stale
`expectedGeneration` refuses before bootstrap or user effects.

If a worker or host dies while open, close, restart, reset, or bootstrap may
have taken effect but no terminal was acknowledged, the alias becomes
`recovery_unknown`. Its call returns `outcome_unknown`; no automatic bootstrap
or lifecycle replay occurs. The model may inspect state and issue a fresh
explicit lifecycle action, but PTK never guesses.

A deterministic configuration/bootstrap validation failure is `faulted`, not
an infinite automatic replay. Manual/model-issued restart remains available
under the existing binding/digest rules. Execution timeout remains the audited
containment transition; after confirmed death it may enter automatic recovery
only when the timed-out operation has received its one truthful terminal and
the alias otherwise meets the eligibility rules.

## Backoff, crash loops, and health

Host recovery and each alias have independent retry state. The first recovery
attempt after confirmed death is immediate. Failed attempts then wait exactly:

```text
250 ms, 1 s, 4 s, 15 s, 30 s
```

After six consecutive failed process generations, the scoped circuit opens for
60 seconds. It then permits exactly one half-open attempt; failure reopens for
60 seconds, while a generation that remains ready for a 60-second stability
window resets its failure count. There is no jitter: recovery is local to one
harness, and deterministic behavior is required for audit and tests. A ready
handshake alone does not satisfy the stability window.

Protocol/contract/digest mismatch and signed-generation exhaustion are
non-retryable for the guardian boot. Audit/disk unavailability uses the
existing slow health probe and admits no effects. Bootstrap/configuration
failure faults only its alias. One worker circuit never restarts the host or
another alias.

`ptk_state` exposes bounded normalized fields for guardian boot, host state,
host boot/generation, retry attempt, next retry time, circuit state, last
failure code, and each alias's desired/observed/recovery state. Repeated state
reads do not alter those values except ordinary elapsed-time display and do not
grow an unbounded audit reserve.

Recovery/containment observation is live work. Idle policy cannot terminate
the guardian or recycle a host while recovery, quarantine, an admitted call,
or any managed job remains live.

## Mandatory audit and output continuity

Audit remains at the public MCP acceptance boundary, which moves to the
guardian. Every call, recovery refusal, host/worker lifecycle transition,
automatic bootstrap, containment action, circuit transition, and
outcome-unknown terminal is reserved and durably recorded under the existing
pre-effect rules. The host cannot start work until the guardian's dispatch
authorization is committed.

The existing audit `producer.supervisor_boot_id` continues to identify the
one public MCP acceptance supervisor for the harness. The guardian assumes that
same role, so its boot ID stays stable across private-host replacement without
changing the field's meaning or the per-supervisor sequence contract. Product
cutover introduces `ptk.audit/3`. Version 3 preserves every v2 field, meaning,
order, and bound, then adds one always-present top-level `host` object
immediately after `producer` with exact keys:

```text
host:
  boot_id: UUIDv4 | null
  generation: integer 1..Int64.MaxValue | null
  state: "absent" | "starting" | "ready" | "recovering" | "backoff" |
         "containment_unconfirmed" | "circuit_open" | "half_open" |
         "stopped"
  recovery_attempt: integer 0..Int64.MaxValue
```

Boot ID and generation are nonnull together from allocation through confirmed
death; they are null before the first allocation and after confirmed death
while no next attempt has been allocated. State and recovery attempt describe
the guardian's exact snapshot at event creation. New closed event types are
`host.starting`, `host.ready`, `host.lost`,
`host.containment_unconfirmed`, `host.recovery_scheduled`,
`host.recovery_failed`, `host.recovered`, `host.circuit_open`,
`host.circuit_half_open`, and `host.stopped`, plus the corresponding existing-
shape `worker.recovery_scheduled`, `worker.recovered`, and
`worker.recovery_failed`. Readers accept exact v1, v2, or v3 shapes and never
reserialize older bytes. Worker/session generation fields are never overloaded
with host identity, and dynamic identity never enters a detail-code string.

Guardian death may leave accepted calls without terminals. Existing startup
reconciliation records those as outcome-unknown on the next guardian boot, but
the new process is a new harness and cannot restore its public connection or
warm sessions.

`OutputStore` and its capability table move to the guardian before host
cutover. A sealed artifact and opaque handle therefore survive host/worker
replacement for their ordinary TTL/quota. A partially written artifact is
sealed incomplete or abandoned under the existing truthful recovery rules;
recovery never reruns the producing command. Guardian death retains only the
already approved on-disk crash/retention behavior, not a promise that an
in-memory handle survives a new harness.

## Shutdown and intentional restart

Public stdin EOF or guardian cancellation atomically sets terminal shutdown
before any child stop. It disables every restart timer/half-open probe, closes
host protocol, invokes containment for host/workers/jobs, waits the bounded
grace, disposes audit/output resources in their approved order, and exits. No
restart is scheduled after public EOF.

An operator-requested host recycle uses the same containment and new-generation
rules but is not a crash. It never changes the pinned build or public contract.
Guardian/configuration/binary upgrades require a new public MCP connection.

## Implementation sequence

Every code slice requires a separate owner go, one finding per corrective
commit, mutation proof for each new guard, the full automated battery, and
fixed-SHA reviewloop acceptance before the next slice. No slice authorizes push
or history rewriting.

### R0 — freeze contracts and real-process feasibility

- Freeze the public recovery result shape, exact audit evolution, host protocol
  fields/limits, binary/contract digest computation, platform containment
  design, and published artifact layout.
- Build disposable fake guardian/host fixtures. Prove one public initialize,
  same guardian PID/pipes after an idle host kill, guardian-local state during
  recovery, exact one-terminal behavior for every delivery barrier, and no
  public-stdout contamination.
- Prove Windows nested Job Object behavior and the Unix guardian-broker
  liveness/registry topology before selecting production launch primitives.
  Kill a guardian while its host is hung and prove the native broker kills the
  creation-time host group, reaps its direct host, identity-kills/confirms
  every pending/armed worker group, never `waitpid`s a nonchild on macOS, and
  leaves the system reaper no persistent zombie. Stop at every boundary before
  pending registration, during group movement, before/after armed
  acknowledgment, and before/after release; no user code or process may
  escape. Failure to confirm old-tree death blocks implementation rather than
  weakening containment.
- Record direct Windows evidence on `NETWATCH-01` and native Unix evidence in
  `.agents/machines.md`.

### R1 — extract guardian-safe contracts and ownership

- Add a pure shared contracts project containing public tool schemas, strict
  host protocol DTOs/codecs, identifiers, limits, and failure codes.
- Extract audit admission/export, `OutputStore`, public ID allocation, frozen
  catalog, and recovery-manifest types behind guardian-safe interfaces without
  behavior change.
- Add dependency/source guards proving the guardian graph cannot reach
  PowerShell, runtime, job manager, execution runners, or user process launch.

### R2 — unwired guardian and host lifecycle cores

- Add deterministic host lifecycle, delivery-state, containment, retry/circuit,
  and session-recovery state machines using injected launch/wait/clock/
  transport seams.
- Add the private host server/client lifecycle and strict identity/correlation
  checks without moving production MCP registration or executing a tool.
- Guard all late-frame, duplicate-death, generation, deadline, cancellation,
  backoff, and shutdown races.

### R3 — standalone guardian against a crashable fake host

- Add the separate guardian apphost and let it own one real MCP stdio transport,
  frozen initialize/tools metadata, guardian-local state, and fake-host calls.
- Prove automatic same-pipe host recovery, no public reinitialize, one response
  per request, no ambiguous replay, crash-loop bounds, and guardian EOF cleanup
  across all CI platforms.
- Keep installed PTK registration pointed at the current server; this slice is
  not the production cutover.

### R4 — private real-host mode and control-plane transfer

- Add the exact private `--host` entry and route the nondefault guardian
  apphost's tool adapters through it while preserving current in-process
  `ISessionOperations` behavior. Keep the installed no-argument server mode and
  its public MCP behavior byte-for-byte intact through R6.
- In the guardian path, move audit admission, output capabilities, public IDs,
  frozen catalog, and idle policy outward atomically. The private host gets no
  audit credentials or public handles. Shared factories may serve the legacy
  path, but no installed registration points at `--host` or the guardian yet.
- Reconcile audit schema/event evolution and package both exact-version
  apphosts, but keep registration cutover separately guarded.

### R5 — automatic host recovery and declared-state restoration

- Wire real host containment/monitoring, exact-build restart, lifecycle audit,
  delivery truth, recovery manifest, output/job tombstones, backoff/circuit,
  and recovery-aware state.
- Crash the real host at every launch, lifecycle, request-delivery, effect, and
  response barrier. Prove no descendant overlap, no replay, monotonic identity,
  and exact declared-state-only recovery.
- A host crash while the current default runtime remains in-process still loses
  that runtime; recovery creates the exact documented fresh baseline.
- On Unix, prove that runtime's ordinary native child, grandchild, and cold
  background job inherit the host-generation group and all die when the host
  or guardian is hard-killed. An execution path that detaches from that group
  retains the existing explicit partial-coverage result; it is not mislabeled
  as completely contained.

### R6 — automatic worker recovery after live worker routing

- Complete the separately approved audited-harness worker dispatch/cutover
  prerequisites; do not smuggle them into resilience work.
- Replace unexpected-worker-loss/manual-restart behavior with scoped automatic
  new-generation recovery, frozen bootstrap reapplication, ambiguity handling,
  and per-alias circuit state.
- Prove one worker loss neither restarts the host nor affects another session.

### R7 — registration, distribution, and end-to-end cutover

- In one commit, change every install/registration path to launch only the
  guardian apphost and remove the legacy no-argument public server mode. Direct
  server use then requires private bootstrap-gated `--host` or `--worker`.
- Package the guardian, exact pinned host, shared contracts, required Unix
  guardian/worker brokers, and platform containment artifacts together.
  Partial/mixed-version installation fails before public initialization.
- Run the complete same-pipe crash matrix from a real MCP client, the full .NET
  and Pester suites, handshake, release archive checks, and direct Windows plus
  native Unix validation before making the guardian the default.
- Update README/server/operator guidance with the hard guardian-death boundary,
  state-loss semantics, recovery codes, and upgrade/new-session requirement.

## Acceptance matrix

### Public connection and host recovery

- Initialize once, list tools, kill an idle host, read `ptk_state` while it is
  recovering, then call a real tool successfully on the replacement using the
  same guardian PID, stdin/stdout, public request-ID domain, and initialization.
- Host EOF, exit, reader, and writer failures racing together start
  exactly one recovery. Public stdout remains open and valid.
- Replacement contract/build mismatch is refused and never changes the live
  public tool catalog.
- Complete decoded terminals are delivered once; partial responses and
  effect-before-response crashes are `outcome_unknown`; unwritten calls are
  definitely not started. None is resent.
- Old-generation responses/events and forged diagnostic JSON cannot complete a
  public request or mutate current state.

### Generation and state restoration

- Host generations never reuse across failed starts. Worker generations remain
  monotonic across host restart. A granted-but-unused worker-create capability
  leaves a visible generation gap; the next attempt uses a greater value.
- No replacement begins before confirmed old-tree death. Unconfirmed death
  keeps state/output reads available but blocks effects and generation advance.
- Exact frozen bootstrap bytes/digest are used once per recovered generation
  even after the source file is edited, replaced, deleted, or symlinked.
- Bootstrap baseline returns; arbitrary warm mutations, connections, and jobs
  do not. Dynamic sessions return empty; closed/cold aliases remain closed/cold.
- Ambiguous lifecycle/bootstrap calls become `recovery_unknown` and are never
  replayed. Stale expected generation refuses before effects.
- Public job/output IDs never reuse. Sealed output remains readable; incomplete
  output and lost jobs are truthful tombstones.

### Availability, loops, and isolation

- Guardian-local state/list/output and MCP ping/tools remain prompt during
  startup, containment, every backoff delay, circuit-open, and half-open.
- Fake-clock tests prove the exact delay sequence, six-failure circuit,
  60-second cooldown, one half-open attempt, and 60-second stability reset.
- A 100-cycle soak proves bounded processes, handles, FDs, readers, timers,
  buffers, audit reservations, and memory; identities remain monotonic.
- One worker crash affects only one alias. Host crash affects all live sessions
  but not the guardian connection. Guardian death remains connection-fatal and
  leaves no descendant.
- Recovery never starts after intentional public EOF, and idle policy never
  creates a restart loop.

### Platform, security, and compatibility

- Common deterministic suites pass on Windows, Linux, and macOS. Native tests
  hard-kill guardian/host/worker at creation, initialize, bootstrap, ready,
  foreground-busy, and job-running barriers.
- Windows proves creation-time outer containment, required nested Job Objects,
  noninheritance, and direct `NETWATCH-01` cleanup. Linux/macOS prove outer
  guardian-broker liveness cleanup, creation-time host-group ownership,
  pending/armed worker registration, start-identity fencing, direct-host reap,
  descendant exit confirmation, correct nonchild reaping, and no old/new group
  overlap. Guards fail if an ordinary R5 child misses the host group, a worker
  leaves it before pending acknowledgment, or release precedes armed
  acknowledgment.
- Guardian stdout is exclusively valid MCP. Every other output is bounded
  stderr/private diagnostics with no scripts, bootstrap, secrets, paths, raw
  environment, or exception text.
- Existing tool names/schemas/default successful outputs remain compatible;
  only frozen recovery/state fields and failures change. Existing .NET, Pester,
  handshake, audit, output, release, and platform batteries remain green.

## Required mutation proofs

At minimum, independently make each mutation and prove its intended guard
fails before final fixed-SHA acceptance:

1. close public stdout on host loss;
2. replay a request or commit after ambiguous delivery;
3. treat a partial private write as definitely not dispatched;
4. reuse a failed host or worker generation;
5. accept one stale identity/correlation dimension at a time;
6. launch replacement before confirmed old-tree death;
7. restore arbitrary warm state or a running job;
8. reread changed bootstrap bytes or execute bootstrap twice;
9. restore an ambiguously opened/closed/reset alias;
10. route `ptk_state` through the blocked host or let it trigger recovery;
11. remove/collapse backoff, reset it at handshake, or allow two half-open probes;
12. forward host/user diagnostics or one guardian log line to public stdout;
13. inherit a public MCP handle into host/worker/user code;
14. restart after public EOF or idle-exit the guardian;
15. fail to contain one descendant after host or guardian death;
16. let one worker crash restart the host or another alias;
17. accept an old expected generation or reuse a public job/output ID;
18. leak a planted script/bootstrap/secret through state, audit failure, or
    diagnostics;
19. let a changed host build/tool contract join the live guardian; and
20. bypass guardian-owned audit admission before an effect.

## Documentation and release dependency

Release-distribution work that publishes or registers the MCP server must not
freeze a single-process artifact layout that conflicts with this plan. Before
release slice 3 packages the server, either land the guardian layout or amend
that release slice explicitly. README end-state claims remain prospective until
R7; operator docs must never imply that guardian death itself is recoverable.

## Authoritative references

- `.agents/plans/audited-harness-sessions.md` — audit, output, worker,
  containment, prepared-operation, session, and isolation contracts retained
  except where this plan narrowly supersedes recovery/topology.
- `.agents/repo-guidance.md` — verification entry points.
- `.agents/playbooks/reviewloop.md` — fixed-SHA implementation review.
- `server/PtkMcpServer/Program.cs` — current single-process insertion point.
- Codex MCP configuration — `https://learn.chatgpt.com/docs/extend/mcp.md`.
- MCP lifecycle —
  `https://modelcontextprotocol.io/specification/2025-11-25/basic/lifecycle`.
- MCP transports —
  `https://modelcontextprotocol.io/specification/2025-11-25/basic/transports`.
