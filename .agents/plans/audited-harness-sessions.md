# Plan: mandatory audit, harness-scoped sessions, and internal RTK routing

**Status:** IMPLEMENTING — owner-approved 2026-07-11 after Claude and Grok
reviewloop convergence. Slices 0-4 are complete on local `master`. Slice 4
product head `76d4f0c` integrates the supervisor output store and audited
`ptk_output`, bounded two-stage same-invocation capture/recovery, anonymous
retained artifacts, truthful recovery hints, and behaviorally inert legacy
`raw`. Claude accepted the final fixed-SHA integrated range
`9c89abf..76d4f0c` with `guard_confirmed=true` after eight independent
cross-slice mutation proofs and the full local battery on 2026-07-13. The
accepted feature history was fast-forwarded to local `master`, verified by
content diff, and its feature branch removed. Slice 5a code head `ed7c782`
adds the audited cold-background admission barrier; Claude accepted exact
range `1a2f3e9..ed7c782` with `guard_confirmed=true`. Slice 5b code head
`bbcb1b7` adds the typed cold-background dispatch/JobManager foundation;
Claude accepted exact range `ff42447..bbcb1b7` with
`guard_confirmed=true` after independent mutation proofs and the full local
battery on 2026-07-13. Slice 5c code head `c936866` makes polling and its
audit facts provenance-aware; Claude accepted exact range
`5d12205..c936866` with `guard_confirmed=true` after four independent
mutation proofs and the full battery. Slice 5d code head `f7d5940`, with the
platform-faithful guard correction at reviewed head `36e0f49`, adds cold PATH
and PATHEXT resolution, target identity/revalidation, target-aware native
argument mode, and cold-plan fidelity gates. Claude accepted exact range
`43d1307..36e0f49` with `guard_confirmed=true` after an independent
trailing-space mutation proof and the full coder battery. The production
activation code head `d3ff115` wires one post-cwd cold plan into invoke,
splits durable plan/dispatch authorization, revalidates frozen launch facts
at commit, permits one exact-original fallback only after typed proved-no-start,
and preserves actual terminal routing. Claude accepted exact range
`79b1d3e..d3ff115` with `guard_confirmed=true` after independent PATH
re-resolution and audit-route rollback mutation proofs plus the full battery.
Final Slice 5 code head `fc61be6` adds path-free background output recovery:
eligible direct-text jobs reserve before start, seal one immutable bounded
artifact through the supervisor output store, publish its opaque handle before
terminal notification, keep RTK seam-absent output explicitly unrecoverable,
and expose no internal path. Claude and Grok independently accepted exact
range `ee21f16..fc61be6` with `guard_confirmed=true` after independent guard
proofs and the full battery on 2026-07-14. Slice 6 code head `7999328`
extracts the behavior-preserving `SessionRuntime`; Claude accepted exact range
`aca20a6..7999328` with `guard_confirmed=true` after independent ownership,
cache-isolation, adapter, and reset-lifetime mutation proofs plus the full
battery. Slice 6 is complete and landed locally. Slice 7a code head `f86de26`
adds the strict bounded v1 worker protocol foundation; Claude accepted exact
range `a88d605..f86de26` with `guard_confirmed=true` after six independent
mutation proofs and the full battery on 2026-07-14. Slice 7b code head
`e70089f` adds the behavior-preserving tool-to-provider operations seam and
separate disposable lifetime seam; Claude accepted exact range
`2eca287..e70089f` with `guard_confirmed=true` after four independent mutation
proofs and the full battery on 2026-07-14. Slice 7c code head `56734e3` adds
the platform-neutral worker lifecycle core: hello/initialize/ready/shutdown
phases, strict identity and correlation, one absolute startup deadline,
explicit host-cancellation races, and exactly-once owned-lifetime cleanup.
Claude accepted exact range `cfaee5f..56734e3` with
`guard_confirmed=true` after seven independent mutation proofs and the full
battery on 2026-07-14. Slice 7d code head `bbc2a0e` adds the deliberately
unwired Windows creation-time containment primitive; Claude accepted exact
range `3348167..bbc2a0e` with `guard_confirmed=true`, and direct Windows
validation passed. Wait-ownership prerequisite head `d1cca1b` replaces the
borrowed cancellable wait with an owning duplicated process handle; Claude
accepted exact range `4578e6f..d1cca1b` with `guard_confirmed=true` after the
corrected two-mutation proof. Slice 7e code head `12617cc` adds the Windows-only
managed lifecycle entry, secure bootstrap ownership, stable process-exit
mapping, and bounded abnormal diagnostics while leaving default MCP routing
unchanged. Claude accepted exact range `eec7ed1..12617cc` with
`guard_confirmed=true` after eleven independent mutation proofs, and direct
Windows validation passed. Real `SessionRuntime` dispatch and the
default-session cutover remain later sub-slices. Slice 7f code head `a9e757e`
adds deliberately unwired strict request/cancel parsers, a response codec, and
a standalone scheduler. Claude accepted exact range `3580e67..a9e757e` with
`guard_confirmed=true` after independent mutation proof and the full battery
on 2026-07-15. The Slice 7g transport-neutral operation-codec boundary below
was owner-approved on 2026-07-15. Slice 7g code head `eef38cb` adds the strict
transport-neutral invoke/job-control/state value codecs while remaining
deliberately unwired. Claude Code 2.1.210 accepted exact range
`a83e2e6..eef38cb` with `guard_confirmed=true` after ten independent mutation
proofs and the full battery on 2026-07-15. Slice 7g is complete and landed on
local `master`. The strict still-unwired Slice 7h prepare/commit/abort payload
codec boundary below was owner-approved on 2026-07-15. Slice 7h code head
`8f5c57c` adds the strict prepared-operation value codecs while remaining
deliberately unwired. Claude Code 2.1.210 accepted exact range
`1179ed0..8f5c57c` with `guard_confirmed=true` after eleven independent
mutation proofs and the full battery on 2026-07-15.

This plan is the canonical implementation contract replacing the still-open
security response, the unapproved durable/shared-session idea, and the
reviewed-but-unapproved `rtk rewrite` draft. It does not amend
`.agents/decisions.md` while the owner's hold on that file remains active. The
final documentation slice must reconcile those older records rather than
leaving competing contracts.

## Outcome

Build one PTK shell surface with three properties:

1. Every operation accepted by PTK has a mandatory, PTK-owned lifecycle audit
   trail, including RTK-routed commands, control calls, and spawned jobs. The
   stream can be exported to a SIEM and externally anchored.
2. One harness may own several isolated warm PowerShell sessions. Each session
   is a separate worker process, is addressed explicitly by a harness-local
   semantic name, and dies with the harness.
3. Agents submit the original command to PTK only. PTK internally chooses
   PowerShell, RTK, or an exact-semantics fallback; routing, compression, or
   style guidance never makes the model reconstruct and resubmit an accepted
   command.

## Explicit non-goals

- No runspace, worker, output handle, or session survives the harness-owned
  supervisor.
- No daemon, checkout key, reattachment, cross-harness client, shared
  runspace, or shared signals.
- No policy-file allow/deny classifier and no claim that a worker/process
  boundary replaces OS identity or upstream RBAC.
- No automatic selection of Exchange/AD target from cmdlet spelling.
- No mutable current-session/`select` state.
- No live PowerShell object transfer between sessions.
- No asynchronous job that borrows a connection-bearing warm runspace.
  Session-mode jobs require a separate concurrency/cancellation design and are
  deferred; the first implementation supports only explicit cold stateless
  jobs.
- No hook audit. The Claude hook only blocks a harness shell call and nudges
  the model to PTK.
- No complete-host audit claim. PTK covers operations PTK accepts; OS and
  remote-service audit cover execution paths and effects PTK cannot observe.
- No full stdout/stderr duplication into the core SIEM event stream.

## Authority and settled product rules

The implementation must preserve these owner-settled rules:

- The harness runs under the appropriately restricted identity; Exchange,
  AD, Graph, and similar upstream roles are the authorization boundary.
- Process-per-session is operational isolation for environment, module,
  authentication context, failure, reset, and resource containment.
- Shared and durable sessions are out of scope.
- Agents never invoke RTK as a workflow. They call `ptk_invoke` with
  `git status`, `cargo build`, `npm test`, or PowerShell; PTK may delegate
  internally to RTK.
- RTK owns native-command filtering/passthrough and log-text compression where
  it is semantically safe. PTK owns PowerShell state, object shaping,
  orchestration, lifecycle, and audit.
- Mixed native/PowerShell dataflow is discouraged but not refused merely to
  teach style. PTK executes the original text once with exact semantics and
  may emit a post-success suggestion.
- A routing or compression optimization may fall back before execution, but
  may never re-execute after any user process or pipeline starts.
- `raw=true` must cease being a lower-friction execution bypass. Output
  recovery reads bytes captured from the same invocation; it never reruns the
  script.
- Approval of this plan explicitly supersedes two bounded parts of active
  shell-dialect D1: `raw=true` no longer changes interpreter/routing, and a
  parse-fatal script may run as Bash only after the independent three-part
  proof below. `route=pwsh` remains explicit consent to execute the original
  text as PowerShell and bypass automatic dialect/RTK routing. Clean-parsing
  dialect findings retain D1's labeled refusal. The deferred decisions-log
  reconciliation must record that split; the hold is not permission for an
  implementer to guess.
- A failed optimization/style check is not a legitimate reason to return a
  failed tool call. Failure is reserved for an actual execution/lifecycle
  problem or for mandatory audit being unavailable.

## Current implementation evidence

As of the draft base:

- `Program.cs` registers one singleton `RunspaceHost`, one `JobManager`, one
  `RawUsageCounter`, and one stdio MCP transport.
- `RunspaceHost.InvokeGateHeldAsync` executes user script with
  `useLocalScope: false` and appends `Compress-PtcOutput` in the same
  PowerShell pipeline.
- `Resolve-PtcInvokeScript` routes only one bare native Application with
  constant arguments. Pipelines, chains, redirections, variables, cmdlets,
  aliases, functions, and `.cmd`/`.bat` shims execute unchanged in
  PowerShell.
- The RTK leg currently returns only rewritten script text, so callers cannot
  reliably distinguish filtered, passthrough, unavailable, or fallback
  outcomes. Already-RTK-filtered stdout may be handed to generic
  `rtk log` again.
- `raw=true` currently skips dialect detection, RTK execution routing, and
  output shaping together.
- `background=true` starts the original script in a cold
  `pwsh -NoProfile` child; it does not use foreground RTK routing.
- `JobManager` stores job metadata only in memory, exposes filesystem log
  paths, and needs polling-independent completion events for audit.
- `ResetTool` calls `jobs.KillAll()` before the runspace reset.
- No client/session selector, audit journal, SIEM exporter, worker supervisor,
  structured execution plan, or same-invocation foreground output store
  exists.

## Target process architecture

```text
Harness
  └─ PtkMcpServer supervisor (private MCP stdio; harness lifetime)
       ├─ mandatory audit journal/exporter
       ├─ output metadata and session registry
       ├─ session default  ───────────────► worker process ─► SessionRuntime
       ├─ session ad       ───────────────► worker process ─► SessionRuntime
       ├─ session exop     ───────────────► worker process ─► SessionRuntime
       └─ session exol     ───────────────► worker process ─► SessionRuntime
```

The supervisor never hosts user PowerShell. A worker owns exactly one
`SessionRuntime` containing one `RunspaceHost`, one `JobManager`, output-capture
staging, one module cache, and one authentication/environment context. The
supervisor owns MCP, session lifecycle, worker process trees, the output-store
handle table/artifact directory, audit persistence/export, and correlation
IDs.

All workers are harness-owned descendants of the supervisor and enter
containment atomically at creation, before the worker executable or any
runspace/bootstrap instruction can run. On Windows the worker is a direct
supervisor child. On Unix the topology is unconditionally
`supervisor -> native broker -> worker`; calling the Unix worker a supervisor
"child" anywhere below means managed descendant, not direct parentage. On
supported Windows, the supervisor first creates/configures the Job Object,
then uses `CreateProcessW`/`STARTUPINFOEX` with
`PROC_THREAD_ATTRIBUTE_JOB_LIST` so the worker belongs to the
`KILL_ON_JOB_CLOSE` object from its first runnable instruction; the sole Job
Object handle is noninherited and supervisor-owned. A platform without that
creation-time primitive fails worker startup rather than falling back to the
spawn-then-assign race.

On Unix, a tiny single-threaded native `PtkContainmentBroker` executable is
started first with the supervisor-only liveness pipe. It is portable C/POSIX
source built and shipped beside PTK for `linux-x64`, `linux-arm64`, and
`osx-arm64`; there is no managed-runtime or shell-script broker fallback. It,
not the supervisor, forks the worker. The pre-exec child performs only
async-signal-safe close/dup/group/gate/`execve` operations, blocks behind a
closed pipe gate, and executes the managed worker only after containment is
armed. Broker and child both perform the race-safe `setpgid` checks; the broker
retains reaper parentage and places no user code before the gate.

The broker arms supervisor liveness and group ownership, then sends a bounded
containment-armed acknowledgment. The supervisor tells the broker to release;
only then does the child `execve` the worker, complete its private-protocol
hello, and receive `initialize`. EOF before release kills/abandons the gated
child; EOF later sends the group bounded TERM then KILL and the broker exits.
The ordinary protocol EOF watcher remains a graceful fast path, not the
hard-death proof. Failure to build, locate, authenticate, launch, or arm the
RID-matched broker refuses worker startup; the supervisor never spawns the
Unix worker itself.

The broker contract is fixed and contains no user/script data:

- Resolve `PtkContainmentBroker` from the published server's sibling directory,
  never PATH. A generated sibling
  `ptk-containment-broker.manifest.json` contains exactly
  `schemaVersion:1`, `protocolVersion:1`, runtime `rid`, `fileName`, and
  lowercase SHA-256. Require the current RID, exact digest, a nonsymlink
  regular executable owned by the effective UID, and no group/world write
  bits before launch. A development/test override requires both an absolute
  path and explicit expected SHA-256. "Authenticate" below means this package
  identity/drift check, not resistance to a hostile same-user process.
- Map only these inherited descriptors into the broker: FD 3 liveness-read,
  FD 4 supervisor-control-read, FD 5 broker-event-write, FD 6 worker-protocol
  request-read, FD 7 worker-protocol event/write, FD 8 worker-stdout-write,
  and FD 9 worker-stderr-write. The supervisor alone retains the
  liveness/control write ends, broker-event read end, opposite worker-protocol
  ends, and diagnostic read ends. Broker stdin/stdout/stderr are `/dev/null`;
  broker failures use event/exit status. Close every other unintended
  descriptor. Broker-only FDs 3..5 and broker copies of 6..9 are
  `FD_CLOEXEC`; after fork the gated child closes broker FDs, opens worker
  stdin from `/dev/null`, `dup2`s 8/9 to stdout/stderr and 6/7 to worker FDs
  3/4, closes 6..9, and leaves only 0..4 non-CLOEXEC for `execve`. The worker
  sets protocol FDs 3/4 CLOEXEC before any runspace/user process can inherit
  them; the supervisor continuously drains and applies the frozen per-boot
  caps to diagnostic stdout/stderr.
- Control/event frames have an eight-byte header: ASCII `PTKB`, version byte
  `1`, message-type byte, and unsigned two-byte big-endian payload length.
  Payload is at most 64 bytes. Readers loop through `EINTR`/short reads to read
  exactly one header and its declared payload; EOF/error before completion is
  protocol-fatal. Coalesced frames are parsed sequentially, never rejected as
  "extra" bytes. Unknown type/version, oversize length, or a type-specific
  payload-length mismatch is protocol-fatal.
  Integers below are unsigned big-endian. Supervisor commands are `START=1`,
  `RELEASE=2`, and `SHUTDOWN=3`, all with empty payload. The exact absolute
  worker exec vector (`PtkMcpServer --worker`, or pinned `dotnet` plus absolute
  entry DLL under tests) is nonsecret broker argv fixed at launch, never a
  control-frame string.
- Broker events are `HELLO=1`, `ARMED=2`, `RELEASED=3`, and
  `START_FAILED=4`. `HELLO` carries broker PID `u32` plus start identity
  `(u64 high,u64 low)`. After `START`, `ARMED` carries broker PID/identity,
  worker PID/identity, and PGID `u32`; the supervisor requires the spawned
  broker PID, `PGID == worker PID`, and independently queried matching start
  identities before `RELEASE`. Linux identity is `(0, /proc/<pid>/stat
  starttime ticks)`; Darwin identity is `(proc_bsdinfo start seconds, start
  microseconds)`. `RELEASED` is empty and is sent only after the child's
  CLOEXEC exec-error pipe proves successful `execve`. `START_FAILED` carries
  stage `u8` and errno `u32`, after confirmed gated-child/group death.
- Broker exit codes are `0` for requested clean shutdown/reap, `64` protocol
  error, `70` internal failure before child creation, `71` arm/identity
  failure after confirmed child death, `72` exec failure after confirmed child
  death, `73` supervisor-liveness EOF after confirmed group teardown, and `74`
  unconfirmed teardown. Any pre-ready nonzero fails startup. Any post-ready
  exit, including zero not paired with the admitted shutdown transition,
  triggers the generation-fatal `broker_lost` path; code 74 goes directly to
  quarantine/direct identity-validated teardown.

The supervisor retains the broker PID/start identity plus the worker
PGID/leader start identity and continuously waits the broker process. An
unexpected broker exit is a generation-fatal containment loss: under the
session lifecycle gate, stop all admission, publish
`resetting(reason=broker_lost)`, cancel generation leases, and perform the same
bounded TERM/KILL directly against the identity-validated group. The original
worker-start dispatch preauthorizes this safety cleanup, so it cannot be
blocked by a fresh audit failure. Confirmed group death publishes
`worker.lost`/`faulted`; unconfirmed death publishes `quarantined` and remains
live work. Never attach a replacement broker to a live generation; recovery
creates a new broker, group, worker, and generation only after old death is
confirmed. Once broker loss is observed, no later tool work is served until
that outcome is settled.
Graceful shutdown asks workers to stop, waits a bounded grace, then invokes
the same containment kill. A hard-killed supervisor therefore cannot silently
turn a managed worker into a durable session. Deliberate `setsid`, scheduled
tasks, services, and remote work remain explicit partial-coverage escapes.

## Public MCP contract

Keep the existing tools and add an optional `session` argument whose default
is `default`:

```text
ptk_invoke(script, route="auto", background=false, timeoutSeconds=0,
           raw=false [deprecated], session="default")
ptk_job(action, id, offset=0, session="default")
ptk_state(listAvailable=false, session="default")
ptk_reset(session="default", expectedGeneration=0, force=false,
          timeoutSeconds=0)
```

Add:

```text
ptk_session(action, name=null, template=null, allowColdBackground=null,
            expectedGeneration=0, force=false, timeoutSeconds=0)
  action = list | open | close | restart

ptk_output(handle, action="read", offset=0, maxBytes=<bounded>, pattern=null)
  action = read | search | status
```

Rules:

- `default` always exists and preserves unqualified-call behavior.
- Session names are harness-local, nonsecret semantic aliases, canonical
  lowercase, and match `[a-z0-9][a-z0-9._-]{0,63}`.
- The first catalog-validated and lifecycle-admitted `open` freezes an alias
  binding for the harness before worker launch or bootstrap:
  dynamic-versus-template source, template name/digest, bootstrap digest,
  declared labels, and effective cold-background policy. Reopening a live or
  closed alias with the identical binding is idempotent or starts its next
  generation as appropriate. Any different template/digest, a dynamic versus
  template switch, or any attempt to bind `default` refuses before worker
  creation and cannot mutate the old binding. Choose a new alias or restart
  the harness to change meaning.
- Every non-default operation names its session. There is no `select`.
- An unknown or closed **non-default** session never falls back to `default`
  and never auto-creates after a typo. The reserved default slot is the one
  explicit exception: it always exists, may be cold, and lazily starts a new
  worker generation on the next unqualified effectful call.
- `list` never starts a worker.
- `name` is schema-optional because `list` is global: `list` requires it to be
  absent and rejects lifecycle-only arguments, while `open`, `close`, and
  `restart` require a valid name. No ignored sentinel name is invented.
- The flat signature above is notation, not the generated schema.
  `SessionTool` is registered with an explicit `oneOf` JSON schema: a `list`
  branch permits only `action`; an `open` branch requires `name` and permits
  only open-time binding fields plus `timeoutSeconds`; `close` and `restart`
  branches require `name` and permit only their generation/force/deadline
  fields. The adapter
  validates the original JSON property-presence set before binding defaults,
  so omitted is
  distinguishable from explicitly supplied `null`, `false`, or `0`.
- Each worker has a random boot ID and a monotonic generation. An optional
  nonzero `expectedGeneration` mismatch refuses before any side effect.
- A compact session/worker/generation/declared-purpose header appears on
  non-default results. Default output remains compatible where practical.
- `ptk_output` reads a stored artifact only. It accepts no script and cannot
  execute or rerun work.
- The reserved `default` session has effective `allowColdBackground=true` to
  preserve today's explicit cold-child job contract. A template-less named
  session defaults it to `false`; `ptk_session open` may set it explicitly
  only for that session's first binding, after which it is frozen. A
  template-backed session takes the frozen template value and rejects a
  conflicting call-site override.

### Optional session templates

Dynamic empty sessions satisfy the basic workflow: the agent opens `ad`,
`exop`, or `exol`, imports/connects once, then addresses that session on each
call. Optional owner-defined templates make bootstrap deterministic without
making templates an authorization boundary.

Load templates once at supervisor start from `~/.ptk/profiles.json`, with
`PTK_PROFILES_PATH` only as a test/operator override. A template contains a
description, a bootstrap script path, startup timeout, declared target and
identity labels, and `allowColdBackground`. Bootstrap bytes and normalized
definition are frozen and SHA-256-digested at catalog load. No inline secret
belongs in this file. Missing configuration means no templates, not a startup
failure.

The catalog is versioned and all-or-nothing. Canonicalized duplicate/invalid
names, unsupported schema, malformed JSON, an absent/unreadable bootstrap, or
an invalid definition faults the entire external catalog; never partially
load it. Resolve a relative bootstrap path against the catalog file's
directory, never the worker/session cwd, then read and freeze its bytes before
any open. `default` and explicit template-less dynamic opens remain available,
but every template-backed open refuses with the persistent catalog error.
An unknown requested template likewise refuses before alias binding or worker
creation and never falls back to an empty dynamic session. Surface the catalog
digest/error in supervisor state and the startup audit without exposing
bootstrap contents.

Editing configuration cannot mutate a live supervisor catalog. Restart the
harness to reload it. Template names and labels are operational metadata;
the effective upstream identity remains authoritative.

## Supervisor/worker protocol

Run the managed executable in two modes and ship the Unix-only native helper:

```text
PtkMcpServer             # MCP supervisor
PtkMcpServer --worker    # one internal session worker
PtkContainmentBroker     # Unix direct child; forks/gates/reaps one worker
```

Use two dedicated inherited anonymous-pipe handles for versioned
newline-delimited JSON; the protocol is never worker standard input or
standard output. The worker opens those handles before constructing any
FullLanguage runspace, removes their bootstrap identifiers from its
environment, disables further handle inheritance, and keeps the streams in a
private host object not injected into PowerShell. Worker standard input is
EOF/NUL. Standard output and stderr are bounded, untrusted diagnostics pumped
to supervisor stderr with the session/boot prefix. Thus
`[Console]::Out.WriteLine(...)`, native children, and JSON-looking user text
cannot enter the protocol decoder. This is an operational isolation seam, not
a security boundary against arbitrary same-process P/Invoke, which remains an
explicit assurance limit.

Envelope kinds are `initialize`, `prepare`, `commit`, `abort`, `request`,
`cancel`, `event`, `response`, and `shutdown`. Every envelope contains
protocol version, worker boot ID, request ID where applicable, and a bounded
payload. Requests carry an
absolute UTC deadline computed at the MCP boundary; worker startup,
bootstrap, queue wait, routing, execution, and shaping consume the same
budget. A fixed startup-configured `timeoutContainmentGrace` is separate: it
permits no user work and may extend a post-launch startup-failure or post-start
execution-timeout response only long enough to confirm process-tree death.
Tool descriptions disclose the maximum deadline-plus-grace wall clock.

Every lifecycle action that may launch, replace, stop, or wait for a worker
uses the same boundary deadline function as invoke:

- `timeoutSeconds=0` selects the operator-configured server call default. A
  positive override is capped by the server's max-call-timeout; as in the
  shipped contract, that max caps caller overrides but does not second-guess a
  longer operator-configured default.
- Template-backed open/restart/reset additionally applies the frozen
  `startupTimeout` as a ceiling to initialize/bootstrap: the earlier of the
  call deadline and template ceiling wins.
- Template-less dynamic open/restart, explicit default open/restart/reset, and
  close use the same default/override/max rule. A lazy `default` start inside
  `ptk_invoke` has no fresh budget; it consumes that invoke's already-computed
  absolute deadline.
- Reset/restart's graceful stop, new worker creation, initialize, and bootstrap
  share one deadline. Close's graceful stop shares it. Only containment after
  a launched process misses that deadline may use
  `timeoutContainmentGrace`; no user/bootstrap work runs in the grace.

The `ptk_reset` and action-conditional `ptk_session` schemas expose
`timeoutSeconds`; `list` rejects it. Their descriptions state the
default/override/template-ceiling function and the maximum
deadline-plus-containment-grace response time.

Protocol requirements:

- `initialize` is first and sends frozen session/template metadata and
  nonsecret runtime options; no script, secret, or audit credential appears
  in argv.
- One reader task demultiplexes responses/events; one write gate prevents
  interleaved JSON.
- Script-bearing invoke, job-start, and bootstrap operations use a prepared
  execution reservation. `prepare` acquires the worker runtime gate, performs
  only parse and non-executing inspection of already-loaded command state,
  and returns an immutable descriptor containing a random single-use plan ID,
  exact script digest, worker boot/generation, execution plan, permitted
  exact-semantics fallback set, and deadline. It must not autoload a module,
  invoke a provider or command-not-found hook, or start a process; uncertainty
  selects the conservative exact PowerShell plan.
- The worker holds that reservation/gate until matching `commit`, `abort`, or
  deadline expiry. A commit with the wrong plan ID, script digest,
  boot/generation, or stale command identity executes nothing and returns
  `replan_required`. A valid commit is idempotent: the first one starts the
  prepared operation once, and duplicates return the same task/result rather
  than re-executing. Abort and expiry release the reservation without user
  effects.
- The worker can serve zero-wait `state` while the runspace is busy.
- Cancellation targets one request and is propagated to the active pipeline
  or pre-start job operation.
- EOF cancels work, disposes `SessionRuntime`, kills managed jobs, and exits.
- Parent-death containment is armed and acknowledged before `initialize`; it
  does not depend on the runspace thread, protocol loop, or bootstrap honoring
  cancellation. The Unix liveness-pipe write end and Windows Job Object handle
  exist only in the supervisor and are not inherited by workers or children.
- On Unix, broker exit monitoring is part of the containment lease. A broker
  exit before ready fails startup; after ready it triggers the generation-fatal
  direct-group-kill transition above. PGID/PID reuse is rejected by stored
  leader/broker start identity.
- Unknown versions/methods, malformed protocol-pipe frames, and excess frame
  size fail that worker closed. Stray standard output is bounded/labeled as
  diagnostics and never parsed as a frame.
- Supervisor startup strips audit/SIEM credentials and unrelated sensitive
  variables from the worker environment.
- Launch resolution supports both a published apphost and
  `dotnet <entry-assembly.dll>` under tests.

The worker sends structured lifecycle facts but never writes the authoritative
audit journal. If a worker dies after a dispatch event and before a terminal
event, the supervisor records `outcome_unknown`.

## Session lifecycle

Extract the current tool behavior into a worker-owned `SessionRuntime` before
adding subprocesses. `SessionRuntime` owns `RunspaceHost`, `JobManager`,
output capture, and the available-module cache; MCP tool types become thin
supervisor adapters.

Each session slot is one of:

```text
cold | starting | ready | resetting | closing | faulted | lost | quarantined
```

`SessionSlot` owns one asynchronous lifecycle gate and a monotonically
changing transition version. Every invoke, job start/kill/status/output tied
to a worker, bootstrap, reset, restart, close, or open is admitted under that
gate. Supervisor-owned `ptk_output` is not:

1. An ordinary worker-bound call validates state and expected generation,
   captures boot ID/generation/transition version, and increments an operation
   lease before the gate is released. Queued and prepared work keeps that
   lease until its terminal/no-start result.
2. Open/cold-start publishes `starting` and the one shared start task before
   releasing the gate; no second caller can launch another worker.
3. Reset/restart/close publishes its transitional state before releasing the
   gate, which rejects every later worker-bound admission. Its `force=false`
   busy decision atomically includes queued/prepared/foreground leases,
   in-flight job starts, and another lifecycle transition, but not an already
   started cold background job. Running managed jobs are owned resources that
   reset/restart/close intentionally terminate after the pre-effect audit,
   preserving the current reset-kills-jobs contract. `force=true` first blocks
   admission, then cancels active leases and invokes containment.
4. A late response is accepted only for the captured boot/generation/version.
   It may complete its caller/audit outcome but can never install state or a
   job into a replacement generation.

`ptk_session list`, `ptk_output`, and supervisor-only state snapshots do not
take worker operation leases. Worker `ptk_state` uses a validated
ready-generation lease but remains zero-wait with respect to the runspace
execution gate. This lifecycle gate
linearizes admission and replacement; the worker runtime gate separately
serializes scripts within the admitted generation.

- Concurrent opens of one cold name share one start task.
- Bootstrap output is suppressed; any PowerShell error, timeout, prompt, or
  state loss faults startup. Successful bootstrap becomes that session's
  drift baseline.
- Cancellation/deadline before process launch leaves the slot cold. After a
  worker is launched, startup failure, cancellation, or deadline keeps the
  slot in `starting` while the supervisor aborts protocol and invokes
  OS/reaper containment. Wait at most `timeoutContainmentGrace` for confirmed
  worker/process-group exit. On confirmation publish `faulted` and return. On
  grace expiry return a terminal startup
  `containment_unconfirmed`, publish `quarantined`, and keep observing the old
  containment identity; no open/restart or next generation is admitted. When
  death is later confirmed, transition to `faulted` so explicit restart can
  proceed. A synchronous runspace or bootstrap that ignores cancellation gets
  the same bounded response, and a late ready frame for that boot/transition
  is always discarded.
- Unexpected process exit marks `lost`. Ordinary invocation never silently
  starts a fresh context under the same generation.
- `restart` replaces the whole worker process, reruns bootstrap, and
  increments generation.
- `close` terminates the worker tree and leaves a non-default name closed for
  this harness; `open` is required to create its next generation. Closing
  `default` leaves its reserved slot cold, and the next unqualified effectful
  call lazily starts a new generation. `restart`/`ptk_reset` on `default`
  replace it immediately rather than leaving it unavailable.
- Close, fault, loss, reset, and restart never discard or rewrite the frozen
  alias binding; they change only worker generation/lifecycle state.
- `ptk_reset` becomes session-local process replacement. Authorization/checks
  occur before job or process termination.
- Busy reset/restart/close with `force=false` returns the same no-side-effect
  busy result. `ptk_session restart` and `ptk_reset` are equivalent for this
  rule; neither queues behind or kills an active/queued invocation without
  force. A running cold background job alone is not “busy”: all three
  lifecycle operations proceed and terminate that job as documented.
  `force=true` cancels and terminates the session worker tree.
- Timeout/recycle, reset, close, and worker loss never affect another
  session.
- Queue expiry or cancellation before a prepared commit leaves the worker
  unchanged. Any execution timeout after commit uses this ordered transition,
  for `default`, template-backed, and dynamically opened sessions alike:

  1. Under the lifecycle gate, verify the timed-out boot/generation, publish
     `resetting(reason=timeout)`, block new admission, and convert the timed-out
     call's own lease into the transition owner so it cannot deadlock waiting
     on itself.
  2. Cancel every other queued/prepared/foreground lease and in-flight job
     start for that generation. The original durable dispatch already records
     timeout tree-kill as a permitted containment action, so safety cleanup
     does not depend on a new audit append succeeding after the deadline.
  3. Close protocol and invoke OS/reaper containment. No shaping or other user
     work runs after the original deadline. Wait at most
     `timeoutContainmentGrace` for confirmed worker/group death.
  4. On confirmation, terminate its managed jobs, increment generation, and
     leave a named alias `lost(reason=timeout)` pending explicit restart; leave
     reserved `default` cold for its documented lazy next generation. On
     grace expiry, return `timed_out, containment_unconfirmed`, publish
     `quarantined` with no new admission/generation, and keep observing
     containment; only confirmed death can unblock explicit recovery.

  The timeout response is returned after confirmation or grace expiry and
  labels session/job state loss plus containment certainty. PTK never tries to
  infer whether the old process acquired a connection, never overlaps old/new
  workers, and never lets the expired caller's lease block recovery. This
  deliberately replaces the current in-process runspace rebuild.
- `SessionManager.HasLiveWork` forbids supervisor idle exit while a worker is
  starting/resetting/closing, a foreground call is active, a managed job is
  running, or any slot is `quarantined` with a live containment observer.
  Quarantine remains live work after its caller returns and until confirmed
  death drives the documented faulted/lost/cold transition; idle shutdown may
  be reconsidered only after that observer and its reserved audit obligation
  are released.

No worker, alias, boot ID, generation, job ID, or output handle is valid in a
new harness.

## Execution and routing contract

Introduce a structured `ExecutionPlan`:

```text
OriginalScript
Domain                PowerShell | NativeTerminal | MixedDataflow | Bash
ExecutionPath         PowerShellDirect | Rtk | NativeDirect | BashViaRtk
PreExecutionValidation None | BashSyntax
ResolutionContext     Warm | Cold
RequestedRoute
EffectiveRoute
OutputProvenance      PowerShellObjects | DirectText |
                      RtkUnknown | RtkFiltered | RtkPassthrough
FallbackReason
RtkExecutableIdentity
OutputShapingRtkExecutableIdentity
PostSuccessGuidance    None | PreferNativeRedirection
```

The planner is side-effect-free under the prepared-reservation protocol. It
returns metadata and a planned execution; it never starts the user command.
The supervisor commits the pre-effect dispatch event for that exact plan ID,
script digest, worker generation, route, and bounded fallback set before the
worker receives `commit` permission. If preparation becomes stale, the worker
returns `replan_required` without execution; the supervisor may prepare again
inside the same original call and remaining deadline, with a new audited plan,
but never asks the model to reconstruct or resubmit the script.

### Automatic routing

- `route=auto` applies the planner rules below.
- `route=pwsh` selects `PowerShellDirect` for the exact original text in the
  warm foreground or cold job context. It suppresses dialect refusal/Bash
  delegation and RTK execution routing, but retains ordinary output capture
  and shaping.
- `route=rtk` is a routing assertion only for a semantically eligible terminal
  native command. If safe RTK execution cannot be prepared before any process
  starts, execute the original once through the exact-semantics fallback with
  a concise effective-route label. It must never silently look routed and must
  never retry after execution begins.
- A single terminal native Application whose output is returned to the model
  is offered to RTK. RTK chooses a specialized filter or passthrough.
- A cmdlet, alias, function, script, variable-dependent invocation, or
  PowerShell object pipeline executes unchanged in the warm runspace.
- Assignments, redirections, native/PowerShell mixtures, producer-to-consumer
  pipelines, parsing/counting/control flow, and compounds without proved data
  equivalence are `MixedDataflow`. They execute the original text once in
  PowerShell; no intermediate lossy filter runs.
- Redirection such as `git diff > patch.txt` is a semantic sink, not a reason
  to prefilter the bytes written to the file.
- `.cmd`/`.bat`, wrapper-context, alias/function shadowing, and any other
  fidelity exclusion execute the original once.
- Automatic Bash execution still requires three independent conditions, but
  they are ordered around the audit boundary. Side-effect-free `prepare`
  verifies that PowerShell reports a parse-fatal script and PTK's detector
  identifies a specific Bash construct from non-comment/non-string evidence;
  it returns `PreExecutionValidation=BashSyntax` without starting Bash. The
  supervisor's flushed dispatch record authorizes and identifies both the
  validator and conditional Bash execution. Only after `commit` does the
  worker run a bounded direct validator using the pinned executable,
  `bash --noprofile --norc -n -c <exact-script>`, argument-list passing, and a
  scrubbed startup environment (`BASH_ENV`, `ENV`, shell-option/function
  injection removed). The validator never runs through RTK and never executes
  the submitted commands.

  A successful validator permits the same exact bytes to run once through
  `bash --noprofile --norc -c` as the internal RTK delegation. Startup-file
  suppression and the validator's environment scrub prevent unvalidated shell
  content from running before those bytes. Missing Bash, failed/timed-out
  validation, or expired remaining execution budget produces the existing
  labeled not-started terminal; the submitted script never starts. Record
  validator start/completion and identity as internal lifecycle facts under
  the already-audited plan. A clean-parsing dialect finding never reaches the
  validator. `route=pwsh` bypasses this path as explicit PowerShell consent.
- RTK absent, routing timeout, routing error, or pre-execution resolution
  change falls back to the original once. No fallback occurs after execution
  begins.
- Any RTK-routed stdout, including seam-absent `RtkUnknown`, is treated as
  already RTK-processed and is never sent to generic `rtk log` a second time.
- Direct PowerShell/native log-shaped final text may use `rtk log`.
  A shaped direct plan carries a separate startup-frozen RTK identity into
  both plan and dispatch audit facts; `execution.dispatched` records
  `rtk_log_authorized` before that helper can start. A job-output call freezes
  the same identity into the flushed `job.output_accessed` fact before
  shaping. Success/failure envelopes must match that authorized digest.
- PowerShell objects remain PTK-shaped.

Wider compound routing is deliberately not in the first implementation.
The reviewed `rtk-rewrite-routing.md` findings remain required regression
evidence if widening is reconsidered: live resolution, pinned RTK path,
wrapper context, cwd, remaining budget, preference-independent capture,
producer/consumer fidelity, and name-keyed-hook divergence.

### Mixed-domain guidance

High-confidence mixed dataflow is routing metadata, not a refusal. Execute the
exact original first. A concise warning may follow successful output when PTK
has a safe, text-only suggestion such as `git diff > patch.txt`. Never
auto-rewrite, require an override, or ask the model to repeat the command.
Uncertain/dynamic cases execute unchanged without invented guidance.

### Foreground/background parity

The same planner runs against the actual resolution context:

- Foreground uses the warm session.
- A cold stateless job uses a pristine cold command table and the session cwd.
- A safe terminal native job executes through RTK with the same capture and
  audit contract.
- Complex/mixed jobs execute the original once in cold PowerShell.
- Route/provenance metadata is stored with the job and controls later output
  shaping.
- Every first-version background job is explicitly cold and stateless. The
  session's frozen effective `allowColdBackground` value governs it: true for
  reserved `default`, false by default for a template-less named session, and
  the declared value for a template-backed session. If false, record the
  audited `call.accepted`/`job.not_started` refusal but stop before cwd probing,
  output allocation, job dispatch, or process start, with a truthful
  capability message. If true, run the cold job under that session's
  cwd/environment contract. Never silently turn a warm
  connection-dependent request into a cold job. Warm session-mode jobs are a
  deferred feature, not a mode a cold implementer may invent here.

## Same-invocation output recovery

Add a supervisor-owned, harness-lifetime `OutputStore` distinct from audit and
job logs. Its quota is partitioned/attributed by session alias, not worker
generation. Before a script commit the supervisor creates a restricted capture
reservation and passes the worker only its internal write capability/artifact
ID; the worker never owns the handle table. The supervisor atomically seals
the artifact on terminal metadata, or marks it incomplete if the worker dies.
It stores the canonical unshaped response from the invocation: stdout and
stderr separately, PowerShell error/warning streams, exit code, provenance,
and completeness. Handles are opaque random values; filesystem paths are
never model-facing.

Properties:

- Short TTL, per-artifact cap, aggregate cap, owner-only directory/files,
  continuous retention, and explicit expired/evicted/incomplete states.
- Chunked read and bounded search; no default whole-file dump.
- Retrieval is audited, but raw content is not copied into the core audit
  event.
- Recovery returns the snapshot from that invocation even if files/session
  state later change.
- A handle survives worker timeout/replacement, reset, restart, close, fault,
  and loss until normal TTL/quota eviction or supervisor exit. It is readable
  while its alias is closed and never routes through or restarts a worker. A
  worker death during capture yields the explicit incomplete artifact already
  written, not a bare not-found. No handle is valid in a new harness.
- A storage failure never reruns work and never fabricates recoverability.

Foreground PowerShell execution is split from shaping: execute the user
pipeline once, capture its object/stream results, render the recovery snapshot,
then shape those same captured results in a private internal pipeline. Tests
must prove shaping/recovery do not change `$LASTEXITCODE`, `$Error`, cwd,
variables, or module/connection state.

RTK execution may filter before PTK sees the unfiltered bytes. Therefore raw
recovery for any RTK-routed call has a hard external prerequisite: RTK must
provide a machine-readable per-call capture contract that writes the original
stdout/stderr and completion metadata from the same child invocation for
filtered, passthrough, success, and failure cases. Human
`[full output: path]` hints and user-configured tee mode are not sufficient.
This plan does not authorize changes in the adjacent RTK repository.

**Selected seam-absent contract:** RTK routing and compression win. A
successfully RTK-routed call with no machine-readable raw artifact returns its
filtered result with `recovery=unavailable: rtk capture unsupported` and no
`ptk_output` handle. PTK must not bypass RTK merely to manufacture a recovery
promise, must not expose RTK's human filesystem hint as an opaque handle, and
must never rerun the command. PowerShell, native-direct, generic-log, and any
other path whose same-invocation unshaped bytes PTK actually captured still
returns a handle when shaping was lossy. Once the RTK seam exists, the same
RTK-routed result gains a truthful handle without changing execution routing.

### `raw=true` transition

For one compatibility release, accept `raw=true` but do not let it affect
dialect handling, routing, process selection, or whether output is captured.
It returns the normal shaped result plus a same-invocation recovery handle
only when that execution path produced a raw artifact; an RTK-routed call
without the upstream seam gets the explicit unavailable marker instead. It
does not return a cheaper immediate bypass. Stop advertising raw as a
recovery instruction. Remove the argument and `RawUsageCounter` in the next
breaking tool-schema revision.

Every current “rerun with raw=true” marker becomes either a stable
`ptk_output` handle instruction when the same invocation captured raw bytes,
or an honest recovery-unavailable marker when it did not. A `ptk_output` call
reads captured bytes; the original command has already completed and cannot
be degraded by model reconstruction.

## Mandatory audit contract

Audit begins at the MCP supervisor boundary. The hook is not an audit
producer. The supervisor assigns stable event/call IDs and appends
`call.accepted` before routing or worker startup. Every accepted tool,
including `ptk_state`, `ptk_session`, `ptk_output`, and every `ptk_job`
action, receives a terminal event while the journal is healthy.

Acceptance is capacity-reserved, not optimistic. Because every record has a
fixed maximum size, `AuditJournal` computes the worst-case post-effect event
set for the operation and atomically reserves those bytes in the physically
allocated terminal segment **before** appending `call.accepted`. Foreground
calls reserve execution plus call terminals; a background start reserves its
start-call terminal and, before process start, a separate job-terminal slot
that remains charged after the MCP call completes; each live worker retains a
worker-exit/loss slot. Reset/close/kill reuse the already-held job/worker
lifecycle slots and reserve their own call/control terminals. Fixed exporter
and recovery transitions have separately budgeted slots.

If the full reservation cannot be acquired, reject before acceptance and user
effects, using only the minimal unrecorded health diagnostic path. A failed
`call.accepted` append releases its reservation. Successful terminal appends
consume their assigned capacity and release any overestimate; reservations
cannot be borrowed by another call. Thus effective free space is physical
free bytes minus all outstanding call/job/worker reservations, and concurrency
or job admission naturally stops before future terminal facts are overbooked.
Reservations cannot prevent media/write failure, which remains an explicit
degraded/unknown interval, but ordinary load cannot exhaust promised terminal
capacity.

There is one diagnostic exception when the journal cannot admit a new call,
including anchored high-water reserve mode: `ptk_state` may return a minimal
supervisor-only
`audit=unavailable, unrecorded=true` response containing the failure class,
degraded-since time, and spool/export configuration identity. It must not
query or reveal runspace, session, job, output, script, or credential data and
is not treated as an accepted audited operation. Count these emergency probes
and their first/last timestamps in memory; the first successful
`audit.recovered` event records that gap summary. `ptk_session list`,
`ptk_output`, job reads, and every effectful action remain fail-closed.

### Pre-effect rule

Before the supervisor sends any operation capable of side effects to a
worker, it durably commits the operation's complete pre-effect record. For an
invoke/job/bootstrap this is ordered:

1. Write the exact submitted script evidence payload, flush it to disk, and
   atomically publish its opaque ID/digest.
2. Append and flush `call.accepted` plus a prepare authorization referencing
   that evidence. It authorizes only the side-effect-free `prepare` operation
   defined above, not user execution.
3. Send `prepare`; receive and validate the immutable prepared descriptor
   while the worker holds its runtime reservation.
4. Append and flush the intent/dispatch event containing the evidence ID,
   plan ID, exact digest, boot/generation, planned route, and explicitly
   permitted pre-execution fallback set.
5. Only then send the matching single-use worker `commit`.

Reset, restart, close, kill, and forced cancellation have no planning phase:
their generation-bound intent is appended/flushed before their single-use
control commit. A lost response never authorizes a retry that repeats user
work. Duplicate script commits are idempotent; a lost worker after commit is
`outcome_unknown`. A stale prepared descriptor produces an audited
not-started/replan transition, never an execution under unaudited metadata.

If evidence persistence or journal append/flush fails, the operation does not
execute. An evidence blob published before a later journal failure is an
unreferenced orphan and is retention-swept; the reverse state (a dispatched
event referring to missing evidence) is forbidden. This rule also covers job
start, job kill, reset, restart, close, and forced cancellation, with complete
tool-specific parameters in the pre-effect record.

There is no model-facing audit-off switch. SIEM network loss does not block
while the durable local spool is healthy and below quota. A missing, corrupt,
full, unflushable, or permission-invalid spool fails PTK calls closed. The
error must state that the original operation was not started; no routing/style
retry text is emitted.

### Evidence-retention audit

Automatic evidence deletion is journal-bound. Constructors, storage probes,
and pre-writer reconciliation may measure and promote state but never delete;
a direct store/publication path with no live journal fails capacity admission
instead of sweeping. After a writer exists, publication, periodic
reconciliation, runtime startup, and admin startup may retain eligible
artifacts. Local-committed, checkpoint-proved anchored, and proved
unreferenced artifacts are ordinary retention candidates; a crash-left
canonical temporary artifact is a separate `crash_temporary` candidate.

Each exact artifact deletion reserves two core-event slots before any unlink.
It appends and flushes `evidence.retention_intent` with the subject UUID,
SHA-256 digest, exact byte count, retained state, and reason before deleting
through the still-verified protected handle. It then appends either
`evidence.retention_completed` or `evidence.retention_failed`; a failure is
`failed` only when the exact protected name is proved still present and is
otherwise `outcome_unknown`. A hard death may leave an intent-only attempt,
never an automatic deletion with no durable intent. Failure to acquire the
two-slot reservation deletes nothing; a capacity-triggering request then
fails admission rather than taking an unaudited shortcut. A multi-artifact
sweep records each completed prefix independently and stops on the first
failed or unreservable artifact.

Retention subject fields are not script-reference fields: export
acknowledgment must not try to anchor an artifact intentionally removed by
retention. Request-triggered capacity retention carries the triggering call's
already allocated call ID and bounded actor/session facts. Startup, periodic,
and admin sweeps remain explicitly system-triggered with unknown actor
attribution.

### Event lifecycle

Foreground:

```text
call.accepted
execution.planned
execution.dispatched
execution.validation_started | execution.validation_completed  # when planned
output.shaped | output.shaping_failed                            # when RTK log shaping is attempted
execution.completed | failed | canceled | timed_out | outcome_unknown
call.completed | failed | not_started
```

Background:

```text
call.accepted
job.start_requested
execution.validation_started | execution.validation_completed  # when planned
job.started | job.start_failed | job.not_started
call.completed | call.failed | call.not_started

# only after job.started, asynchronously and independently of the start call
job.completed | job.killed | job.outcome_unknown
```

The background MCP call terminates as soon as start succeeds, fails, or is
refused; it never waits for job completion. `job.start_failed` and
`job.not_started` are mutually exclusive alternatives to `job.started`, so
neither may be followed by a job terminal event. A started job receives
exactly one later asynchronous terminal event regardless of polling.

Control/lifecycle events include `job.kill_requested`, `reset.requested`,
`session.opened`, `session.close_requested`, `worker.started`,
`worker.exited`, `worker.lost`, `runspace.recycled`,
`audit.export_stalled`, `audit.export_recovered`, `audit.degraded`, and
`audit.recovered`.

Queue expiry is `not_started`. A timeout after execution begins is not assumed
stopped; if descendant or remote outcome cannot be proven, use
`outcome_unknown`. A hard writer/worker death leaves an externally detectable
unclosed event, never “still running forever.”

### Versioned event envelope — retained v1 read contract

```text
schema_version: const "ptk.audit/1"
event_id: lowercase UUIDv7
event_type: event-code
occurred_utc: UTC `yyyy-MM-ddTHH:mm:ss.fffffffZ`
observed_utc: UTC `yyyy-MM-ddTHH:mm:ss.fffffffZ`

producer:                                      # all keys required
  host_id: persisted opaque UUIDv4
  supervisor_boot_id: UUIDv4
  worker_boot_id: UUIDv4 | null
  pid: positive integer | null                 # process originating the fact
  version: text-128
  binary_digest: lowercase SHA-256 hex | null

sequence: integer 1..Int64.MaxValue            # restarts at 1 per supervisor
previous_event_hash: lowercase SHA-256 hex | null  # null only at sequence 1

session:
  name: canonical session name | null
  generation: integer 0..Int64.MaxValue | null
  binding_kind: "default" | "dynamic" | "template" | null
  template_name: canonical template name | null
  template_digest: lowercase SHA-256 hex | null
  bootstrap_digest: lowercase SHA-256 hex | null
  declared_purpose: text-512 | null
  declared_target: text-256 | null
  declared_identity: text-256 | null
  effective_identity: text-256 | null
  allow_cold_background: boolean | null

actor:
  transport: "mcp_stdio" | null
  client_name: text-256 | null
  client_version: text-256 | null
  client_session_id: text-256 | null
  attribution_strength: "unknown" | "transport_only" |
                        "client_asserted" | "authenticated"

correlation:
  call_id: lowercase UUIDv7 | null
  job_id: integer 1..Int64.MaxValue | null
  parent_event_id: lowercase UUIDv7 | null
  trace_id: 32 lowercase hex | null
  plan_id: lowercase UUIDv4 | null

request:
  tool: machine-name | null
  action: machine-name | null
  provided_fields: sorted unique property-name array, maximum 64
  session_requested: canonical session name | null
  cwd: path-text | null
  timeout_ms: integer 0..Int64.MaxValue | null
  deadline_utc: UTC timestamp | null
  route: "auto" | "pwsh" | "rtk" | null
  background: boolean | null
  raw: boolean | null
  list_available: boolean | null
  job_id: integer 1..Int64.MaxValue | null
  offset: integer 0..Int64.MaxValue | null
  expected_generation: integer 0..Int64.MaxValue | null
  force: boolean | null
  template: canonical template name | null
  allow_cold_background: boolean | null
  max_bytes: integer 0..Int64.MaxValue | null
  pattern_fingerprint: lowercase HMAC-SHA-256 hex | null
  output_handle_digest: lowercase SHA-256 hex | null
  original_script_digest: lowercase SHA-256 hex | null
  script_evidence_id: opaque UUIDv4 | null

routing:
  domain: "powershell" | "native_terminal" | "mixed_dataflow" | "bash" | null
  requested_route: "auto" | "pwsh" | "rtk" | null
  effective_route: "powershell_direct" | "rtk" | "native_direct" |
                   "bash_via_rtk" | null
  permitted_fallbacks: sorted unique ("powershell_direct" |
                       "native_direct") array, maximum 2
  rtk_version: text-128 | null
  rtk_binary_digest: lowercase SHA-256 hex | null
  provenance: "powershell_objects" | "direct_text" | "rtk_unknown" |
              "rtk_filtered" | "rtk_passthrough" | null
  fallback_reason: machine-code | null

outcome:
  state: machine-code | null
  detail_code: machine-code | null
  exit_code: integer Int64.MinValue..Int64.MaxValue | null
  duration_ms: integer 0..Int64.MaxValue | null
  queue_ms: integer 0..Int64.MaxValue | null
  bytes_returned: integer 0..Int64.MaxValue | null
  next_offset: integer 0..Int64.MaxValue | null
  warm_state_lost: boolean | null
  worker_replaced: boolean | null
  termination_certainty: "not_applicable" | "confirmed" |
                           "unconfirmed" | "unknown" | null

coverage:
  ptk_request: boolean
  root_process_observed: "complete" | "none" | "unknown" | "not_applicable"
  descendants_observed: "complete" | "partial" | "none" | "unknown" |
                        "not_applicable"
  remote_effect_observed: "complete" | "partial" | "none" | "unknown" |
                          "not_applicable"

audit:                                         # all keys required
  protection_mode: "local-only" | "anchored"
  export_configuration_identity: lowercase HMAC-SHA-256 hex | null
  health_state: "healthy" | "degraded" | "recovered" | null
  failure_class: machine-code | null
  degraded_since_utc: UTC timestamp | null
  emergency_probe_count: integer 0..Int64.MaxValue | null
  emergency_probe_first_utc: UTC timestamp | null
  emergency_probe_last_utc: UTC timestamp | null

event_hash: lowercase SHA-256 hex             # required final property
```

The current writer emits `ptk.audit/2`. V2 preserves every v1 field,
meaning, default, order, and bound above, changes only the
`schema_version` literal, and adds the following always-present keys in this
exact order:

```text
request:                                       # v2 additions
  # immediately after cwd
  destination_kind: "stdout" | "protected_file" | null
  destination_path: path-text | null           # nonnull only for protected_file
  # immediately after script_evidence_id
  evidence_subject_id: opaque UUIDv4 | null
  evidence_subject_digest: lowercase SHA-256 hex | null
  evidence_subject_bytes: integer 0..Int64.MaxValue | null
  evidence_subject_state: "local_committed" | "anchored" |
                          "unreferenced" | "temporary" | null
  retention_reason: "age_expired" | "capacity_pressure" |
                    "crash_temporary" | null

# immediately after request; null outside disposition events
operator_disposition: null | object
  disposition_id: opaque UUIDv4 | null
  target_supervisor_boot_id: UUIDv4
  target_spool_file: canonical spool filename | null
  target_start_offset: integer 0..Int64.MaxValue | null
  target_next_offset: integer 1..Int64.MaxValue | null
  target_sequence: integer 1..Int64.MaxValue | null
  target_event_id: lowercase UUIDv7
  failure_class: "partial_rejection" | "data" | "protocol" | null
  detail_code: machine-code | null
  response_digest: lowercase SHA-256 hex | null
  first_failure_utc: UTC timestamp | null
  target_export_configuration_identity: lowercase SHA-256 hex | null
  proof_kind: "verified_receipt" | "acknowledged_gap"
  verified_receipt_digest: lowercase SHA-256 hex | null
  acknowledged_gap_reason: machine-code | null
```

Readers accept either the exact original v1 shape or the exact v2 shape.
They reject v1 records carrying v2 keys, v2 records missing any v2 key, and
mixed-version object shapes. Retained v1 bytes remain authoritative and are
exported without reserialization; a boot hash chain may therefore contain
either supported version so long as its sequence and exact-byte hash links
remain valid.

For v2, `destination_path` is nonnull only with `protected_file`. All five
evidence-subject fields are nonnull together only on the three evidence
retention event types; their ordinary script-reference ID/digest fields are
null. Subject state `temporary` is present if and only if the reason is
`crash_temporary`. These pairings are strict schema facts, not consumer hints.

All top-level and nested keys above are required; `null` is an explicit value,
not omission. Request values are the effective values used by PTK, while
`provided_fields` preserves omitted versus explicit `null`, `false`, or `0` at
the MCP boundary. The schema rejects every unlisted key. Bounded strings and
arrays must fit the frozen core-record limit below; inability to represent an
accepted request's worst-case records is a pre-effect no-start, never silent
truncation. `previous_event_hash` must equal the prior record's `event_hash`.

V1 scalar aliases are exact: `text-N` is 1..N Unicode scalar values with no
Unicode `Cc` scalar; `path-text` is 1..4,096 Unicode scalar values with no NUL;
`machine-name` is ASCII `[a-z][a-z0-9_.-]{0,63}`; `machine-code` and
`event-code` are ASCII `[a-z][a-z0-9_.-]{0,127}` (an event code contains at
least one dot); `property-name` is ASCII `[A-Za-z][A-Za-z0-9_]{0,63}`;
canonical session/template names use the public 64-character pattern. UUID
and hex fields have the exact lengths implied above. JSON arrays preserve the
stated order. `provided_fields` and `routing.permitted_fallbacks` use ordinal
sorting after duplicate removal.

`correlation.plan_id` is nonnull for every prepared plan, dispatch,
validation, and execution terminal. It is stable for one single-use prepared
attempt and changes on every audited replan. `routing.permitted_fallbacks` is
the exact bounded set authorized by that plan and is `[]` on events without a
prepared execution; an actual fallback still records its separate
`fallback_reason`.

If the pinned RTK path becomes unavailable during the first durable dispatch
barrier, one later `execution.dispatched` under the same `plan_id` may supersede
that unstarted RTK authorization with the plan's declared exact fallback. The
ordered second record is the actual single execution route; SIEM consumers
must not count the superseded pre-effect authorization as an execution.

Current RTK execution, direct Bash validation/delegation, and `rtk log`
shaping cannot bind an OS executable identity across the final
availability-check-to-process-start window. PTK freezes canonical path,
bounded SHA-256 bytes, and Unix mode, rejects links/special/oversized files,
and rechecks immediately before each launch, but Windows ACL changes, macOS
xattrs/quarantine, dynamically loaded code, and a same-path change inside the
last check/start window remain outside that proof. These installations
therefore remain administrator-protected dependencies. A late replacement or
disappearance is reported under the attempted terminal and never triggers a
fallback after user execution starts; eliminating the residual requires a
future execution-bound Unix/Windows handle, not another path check.

`audit.protection_mode` is the startup-frozen mode on every event so local-only
telemetry is visibly unanchored. `export_configuration_identity` is nonnull
only in anchored mode. Ordinary healthy records use `health_state="healthy"`
and null recovery fields. `audit.degraded` records `health_state="degraded"`,
the failure class, and `degraded_since_utc`. The first successful
`audit.recovered` record uses `health_state="recovered"` and carries the same
failure/degraded start plus the exact in-memory emergency-probe count; first
and last probe timestamps are both nonnull when the count is positive and both
null when it is zero. This explicitly represents the gap summary required by
the journal-unavailable diagnostic contract.

The added `plan_id`, `permitted_fallbacks`, and `audit` fields were a
pre-implementation completeness correction: the approved ordering/recovery
rules already required those facts but the initial field list omitted their
representation. They shipped in `ptk.audit/1`. Both supported schemas are now
frozen; any further field, meaning, default, ordering, or hash-input change
requires a new schema version after `ptk.audit/2`.

`pattern_fingerprint` is
`HMAC-SHA-256(per-supervisor random nonexported key,
"ptk.output-pattern/1\0" || strict-UTF8-pattern)`; the key exists only in
supervisor memory. This records equality within one harness without putting a
dictionary-recoverable digest of a low-entropy token/search string into the
SIEM stream. Raw pattern text remains excluded.

Attribution values are normative. `unknown` means no actor fact is available;
`transport_only` means PTK knows only the local stdio transport instance;
`client_asserted` means client name/version/session came from unauthenticated
MCP initialize metadata; `authenticated` requires a future cryptographically
authenticated client transport. Current MCP stdio is never `authenticated`,
regardless of the worker's OS/upstream identity.

Coverage is scoped, never a whole-host claim. `root_process_observed=complete`
means PTK observed its managed execution root from confirmed start to terminal
state; `none` means no root started, `unknown` means start/termination could
not be proved, and `not_applicable` is a nonexecution operation.
`descendants_observed=complete` means only the local descendants inside the
armed Job Object/process group were contained and confirmed terminal;
scheduled tasks, services, WMI, SSH, deliberate detach/breakaway, and remote
work are outside that word. Use `partial` when only part of that local tree or
an escape is observed, `none` when no descendant exists/started, `unknown`
when membership/outcome is uncertain, and `not_applicable` when the operation
cannot create descendants. `remote_effect_observed=complete` requires an
authoritative provider receipt for the specific effect, `partial` means PTK
observed only a request/response or subset, `none` means a proved no-start,
`unknown` means remote outcome is uncertain, and `not_applicable` means no
remote effect was requested.

The core event stream excludes stdout/stderr, environment values, raw job
content, credentials, tokens, certificate material, and session/output
secrets. Exact submitted script text is incident evidence and must be captured
once in a separately protected evidence payload/index referenced by digest and
opaque ID from the core event. Do not use heuristic redaction as a secrecy
claim. The deployment documentation must warn that exact scripts may contain
secrets and require restricted access/retention.

### Journal and SIEM export

The authoritative writer is a supervisor singleton, never PowerShell code.
Use one append-only versioned JSONL spool per supervisor under
`~/.ptk/audit/` with owner-only permissions, exclusive creation, bounded
records, one-write append, flush-to-disk for pre-effect events, size rotation,
age and aggregate-byte retention, and symlink/reparse-point refusal. Add a
monotonic sequence and previous-event hash. Hash chaining is gap evidence only
after an external receipt; it is not same-user tamper resistance.

Export at least once with stable event IDs and checkpoint only after remote
acknowledgment. First-class transport is OTLP/HTTP over authenticated TLS to
an OpenTelemetry Collector. Document collector routes to common SIEMs plus
Windows Event Log/WEF and RFC 5424 syslog/TLS adapters; PTK does not carry a
vendor SDK per SIEM. A collector running under a different OS principal and a
remote append-controlled index are the recommended protected deployment.

The exporter checkpoint is per-boot atomic sidecar state containing supervisor
boot identity, chain-complete state, spool file identity, byte offset,
sequence, and acknowledged event ID plus a nullable blocked-record tuple:
blocked file/offset/sequence/event ID, failure class, HTTP/protocol detail
code, response digest, first-failure UTC, and stable export-configuration
identity. The protected files are
`export.checkpoint-<supervisor-boot-id:N>.json` and the persistent exclusive
lease `export.checkpoint-<supervisor-boot-id:N>.lock`. The checkpoint is not a
core audit event, is never exported, and does not participate in the event
hash chain. Acknowledging an event updates only that sidecar.

The stable configuration identity is HMAC-SHA-256 over ASCII
`ptk.export-config/1\0` and the complete export configuration: verbatim
endpoint, headers sorted by lowercase ordinal name with original raw values,
CA bytes, client certificate/key bytes, protocol, and timeout. Each string or
byte field is framed by an unsigned four-byte big-endian byte length; timeout
is unsigned eight-byte big-endian milliseconds. The HMAC key is 32 random
bytes, exclusively created owner-only beside the sidecar, persistent across
supervisor restarts, and never exported. A partial
rejection or permanent data/protocol failure remains blocked across restart
and every configuration identity until an explicit out-of-band operator
disposition records either a separately verified receipt or an acknowledged
evidence gap before advancing. A configuration/auth/TLS block remains across
restart while identity is unchanged; a changed identity permits one new
attempt with the same stable event ID/body. No model-facing call can clear a
block. Core events are emitted only on exporter lifecycle transitions,
operator disposition, or hysteresis-bounded backlog threshold crossings, not
on each acknowledgment, so an idle fully acknowledged exporter drains and
becomes quiescent rather than recursively creating checkpoint traffic.

**Slice 2 implementation clarification (2026-07-11, accepted by the final
Slice 2 review on 2026-07-12):** checkpoint and exclusive exporter-lease
ownership are per
supervisor boot, matching the plan's one spool per supervisor. Segment order
is total only within one boot chain; UUIDv4 boot IDs provide no safe global
order. A later process may adopt an orphan boot only after it holds that
boot's lease and every segment in the chain is unlocked. No checkpoint may
advance another live supervisor's chain.

Freeze one operator-controlled protection mode at supervisor startup; there
is no per-call/model override:

- `local-only`: no remote acknowledgment exists. Document this as bounded
  same-user telemetry, expose it in `ptk_state`, and allow age/aggregate-byte
  retention to sweep closed segments.
- `anchored`: every closed segment and referenced script-evidence object is
  retention-ineligible until the configured collector acknowledges its final
  event IDs. A network outage pins that backlog. At the audit high-water mark,
  stop admission of **every** new audited call before the spool is full,
  including state/session/job/output reads and lists. Only the minimal
  unrecorded `ptk_state` diagnostic above remains available. Keep a physically
  separate/preallocated emergency segment writable only by already-accepted
  terminal events, automatic containment facts, exporter health transitions,
  and recovery facts; ordinary tool-call scopes cannot consume it. Exhausting
  that reserve is a loud degraded/unknown interval, never permission to delete
  unacknowledged data.

Changing protection mode requires supervisor restart and produces a startup
configuration event. In anchored mode, quota pressure caused by unacknowledged
segments remains fail-closed until acknowledgment or an explicit out-of-band
operator retention action that is itself recorded; ordinary retention never
turns a SIEM outage into silent evidence loss.

Audit/export credentials remain in the supervisor and are removed from worker
environment and protocol messages. Direct in-process export is acceptable for
personal telemetry but must not be described as resistant to a hostile
same-user process.

SIEM alerts should cover sequence gaps, missing supervisor heartbeats,
unclosed execution/job events, stalled exporter checkpoints, worker death,
and prolonged local-spool backlog.

## Jobs and process containment

The supervisor allocates a monotonically increasing 64-bit public job ID that
is never reused during its harness lifetime, including across failed starts,
worker replacement, reset, or close. It passes that ID into the worker start
commit; any worker-local counter is private and never accepted by MCP. Job
records retain session, worker boot ID/generation, and public ID. A job in one
session is not visible or controllable through another, and an old-generation
status/output/kill returns the old job's terminal/tombstone result or not-found
rather than matching a new worker's job. `ptk_job list` is session-local;
`ptk_session list` shows only counts.

`JobManager` emits completion independently of polling and exactly once.
Status/output/kill/list are all audited. Output reads record offsets and byte
counts, not returned content. Reset/close/shutdown kill only the target
session's jobs.

The supervisor must use the armed Windows Job Object / Unix process-group
reaper contract above, or an equivalently proven hard-parent-death mechanism.
Containment setup failure prevents worker initialization. Whole-worker
termination followed only by a confirmed-death new generation is the
unconditional post-start timeout containment primitive for every session; it
is never gated on a claimed connection-bearing profile.
Detached processes, scheduled tasks, services, WMI, SSH, and remote
effects require OS/provider audit and are reported with partial/unknown
coverage rather than false certainty.

## Concrete code ownership

New or extracted server components:

```text
Audit/
  AuditEvent.cs
  AuditCallScope.cs
  AuditJournal.cs
  AuditHealth.cs
  AuditExportService.cs
  AuditOptions.cs

Execution/
  ExecutionPlan.cs
  ExecutionPlanner.cs
  RtkRunner.cs
  OutputStore.cs

Sessions/
  SessionRuntime.cs
  SessionCatalog.cs
  SessionManager.cs
  SessionSlot.cs
  SessionSnapshot.cs

Worker/
  WorkerProtocol.cs
  WorkerClient.cs
  WorkerServer.cs
  WorkerProcessFactory.cs
  ProcessTreeSupervisor.cs

PtkContainmentBroker/
  ptk-containment-broker.c   # native Unix broker, built per supported RID

Tools/
  SessionTool.cs
  OutputTool.cs
```

Existing ownership changes:

- `Program.cs` selects supervisor/worker mode, loads audit/template options,
  and registers only supervisor services in MCP mode. It excludes
  `SessionTool` from reflection-only `.WithToolsFromAssembly()` discovery and
  registers that tool with its explicit conditional schema/raw-JSON adapter.
- `InvokeTool`, `JobTool`, `StateTool`, and `ResetTool` become thin session
  routers and begin/finish supervisor audit scopes.
- `RunspaceHost` accepts absolute deadlines and structured execution/output
  contexts; it no longer owns string-only route inference.
- `JobManager` stores correlation, route/provenance, session generation,
  output handle, and a polling-independent terminal continuation.
- `IdleWatchdog` observes `SessionManager` aggregate activity/live work.
- `PwshTokenCompressor.psm1` retains PowerShell-object and log-text shaping,
  but receives provenance so every RTK-routed output, including `RtkUnknown`,
  is not shaped twice and all rerun-with-raw wording is removed.
- `RawUsageCounter.cs` is deleted only when the compatibility argument is
  removed.

## Implementation slices

Each independently reviewable implementation sub-slice is one coherent commit;
a numbered product slice may comprise multiple such reviewlooped commits plus
one final fixed-SHA integrated review. Any new guard test must be proven red by
temporarily sabotaging/reverting the production behavior, then restored green.
After every sub-slice commit, and before the next sub-slice starts, run the
synchronous `.agents/playbooks/reviewloop.md` workflow with the Claude harness
against the fixed pre-sub-slice base SHA and sub-slice head SHA. Record a clean
pass or triage every material finding through the playbook; a
reopened/contested loop blocks the next sub-slice until it is closed or the
owner adjudicates it. Review acceptance does not authorize push or history
rewriting. A docs-only sub-slice uses the applicable manual guard instead of
inventing a code sabotage.

### Slice 0 — freeze external and platform contracts (no product code)

- Probe and record the RTK machine-readable raw-capture seam. If absent,
  record the exact upstream requirement and freeze the seam-absent contract:
  RTK routing stays active, seam-absent calls use `RtkUnknown` and advertise
  no recovery handle or second shaping,
  and other captured paths remain recoverable. Do not fake recovery or
  authorize cross-repo changes.
- Freeze OTLP collector test endpoint/auth behavior and the JSONL schema
  version.
- Probe Windows Job Object and Unix process-group teardown strategy in small
  disposable children. The Windows probe must prove creation-time Job Object
  membership; the Unix probe must prove broker-first, gated worker creation.
  A spawn-then-assign/arm fallback is forbidden.
- Freeze session/profile JSON schema and supervisor/worker frame limits.
  Include all-or-nothing validation, catalog-relative bootstrap resolution,
  frozen bytes/digests, and the explicit unknown-template refusal.
- Reconcile this plan with the still-relevant rrp regression cases; do not
  copy the old plan's unsafe broad compound-routing claim.

### Slice 1 — mandatory audit foundation on the current server

- Add schema, journal, required pre-effect commits, health, rotation,
  retention, sequence/hash chain, server lifecycle, and in-memory test sink.
- Add atomic worst-case terminal-capacity reservation at call acceptance and
  persistent job/worker lifecycle reservations; no side effect may begin on
  unreserved future audit obligations.
- Add the protected exact-script evidence store in the same slice. Evidence
  write+flush and the referencing dispatch append+flush are the ordered
  pre-effect commit; no script-bearing call may execute with only a digest.
- Audit every current tool boundary and all no-start/terminal outcomes.
- `ptk_state` reports audit health without exposing credentials; its narrow
  journal-unavailable response follows the diagnostic exception above.
- Guard: forced journal failure produces no foreground/background/reset/kill
  side effect.

### Slice 2 — complete jobs/control audit and SIEM export

- Add asynchronous job terminal events, kill reasons, reset partial-effect
  events, hard-death/unclosed semantics, and audit retrieval access events.
- Add at-least-once OTLP exporter, durable checkpoints, retry/backlog health,
  duplicate-safe IDs, and a fake collector integration suite.
- Add auditing for evidence reads and operator-initiated exports plus evidence
  retention integration, without changing slice 1's pre-effect ordering. Slice 1
  has no automatic evidence-byte exporter; its OTLP core event anchors the
  evidence reference/digest only.
- Document collector/SIEM deployment without claiming local hash-chain
  immutability.

### Slice 3 — structured routing with no model retry

- Replace string-only resolution with `ExecutionPlan` and provenance.
- Route safe terminal native commands through RTK; keep mixed/dataflow and
  fidelity exclusions exact and single-execution.
- Add the three-part parse-fatal + detector + bounded-`bash -n` gate, then run
  only proven Bash syntax through Bash. Keep parse/detector in `prepare`; run
  the scrubbed no-execution validator only after the audited commit. Retain D1
  refusal for every other finding.
- Intentionally replace
  `Parse_fatal_bash_shape_is_refused_with_its_construct_named`: its heredoc
  fixture now asserts exact-byte scrubbed `bash -c` execution when Bash is present and
  `bash -n` accepts it, and the labeled not-started refusal when Bash is absent
  or validation fails. This is a load-bearing guard amendment required by the
  newly approved gate, not a test to leave red.
- Preserve current cwd, pinned RTK path, remaining deadline, live resolution,
  exit code, streams, and preference state.
- Remove double `rtk log` shaping.
- Mixed-domain suggestion follows success; it never refuses or rewrites.

### Slice 4 — same-invocation output recovery and raw retirement

- Land `OutputStore`/`ptk_output` and two-stage foreground capture/shaping.
- Host the handle table/artifact retention in the supervisor; workers receive
  per-call write reservations only, so generation replacement does not erase
  prior handles.
- Integrate the verified RTK raw-capture seam when available; otherwise ship
  `RtkUnknown` plus the explicit recovery-unavailable result for every
  seam-absent RTK-routed call without changing its route or shaping it again.
- Replace raw rerun markers with opaque handle instructions.
- Intentionally replace the `$ElisionHint` default and the four Pester guards
  named `bounds pathological line counts with a labeled head+tail window`,
  `bounds pathological character counts even at few lines`, `keeps line
  elision explicit when the char bound cuts the line marker`, and `bounds the
  labeled log-leg fallback too`. The server supplies a stable `ptk_output`
  handle hint when that invocation has an artifact; otherwise the marker says
  recovery is unavailable and that the command was not rerun. Preserve the
  sd3-3/sd3-4 invariant: the elision function composes the caller-supplied
  hint into its marker; no downstream text scan invents it.
- Intentionally replace
  `RawUsageTests.Invoke_descriptions_teach_recovery_only_raw_and_the_pwsh_pairing`:
  tool/parameter descriptions teach ordinary shaped invocation followed by
  `ptk_output` when a handle exists, label legacy `raw` deprecated/non-bypass,
  and teach `route=pwsh` as interpreter/routing consent without pairing it to
  a raw-output switch.
- Make legacy `raw=true` non-routing and non-bypass behavior; keep it only for
  the announced compatibility interval.
- Replace the shipped
  `Raw_true_bypasses_detection_as_consent` and
  `Background_raw_bypasses_detection_and_starts_the_job` guards with the new
  same-route/captured-output contract in this same slice.
  `Route_pwsh_bypasses_detection_as_consent` and
  `Background_route_pwsh_bypasses_detection_and_starts_the_job` remain and
  gain assertions that the execution plan is `PowerShellDirect`. This is an
  intentional guard amendment, not a requirement that the obsolete assertions
  remain green.
- Prove recovery never changes state or re-executes.

### Slice 5 — foreground/background routing and recovery parity

- Compute cold-context execution plans before job start.
- Persist route/provenance/output handles with job metadata.
- Route safe terminal native jobs through RTK and add capture only when the
  negotiated seam proves it. Otherwise persist `RtkUnknown`, return no
  recovery handle, never use wrapper output as raw, and keep exact cold
  PowerShell for mixed jobs.
- Enforce `allowColdBackground` before every pre-start side effect. Keep warm
  asynchronous session tasks out of this plan; never silently substitute a
  cold job for a connection-dependent request.
- Make output polling provenance-aware and stop exposing filesystem paths as
  the model recovery interface.
- Intentionally replace
  `RawUsageTests.Oversized_job_poll_names_the_log_as_the_recovery_not_raw`:
  an elided job poll names its stable `ptk_output` handle, that handle retrieves
  the same job artifact, and no model-facing text contains the raw log path.

### Slice 6 — extract `SessionRuntime` without behavior change

- Move current invoke/job/state/reset formatting and caches behind one runtime
  object.
- Retarget tool-body tests to the runtime; keep MCP adapters thin.
- Preserve default-session state, error, timeout, routing, output, and job
  behavior established by slices 1-5.

### Slice 7 — worker mode and default-session supervisor

- Add versioned dedicated-pipe protocol, worker launch, cancellation,
  deadlines, bounded stdout/stderr pumps, EOF/parent-death cleanup, and
  process-tree ownership.
- Monitor the Unix broker as a required containment lease; broker loss blocks
  admission and tears down the target generation rather than silently
  degrading the later parent-death guarantee.
- Bound every post-launch startup timeout/cancel by the request deadline plus
  `timeoutContainmentGrace`: confirmed death publishes `faulted`; grace expiry
  returns startup `containment_unconfirmed`, publishes `quarantined`, and
  forbids open/restart/next generation while observation continues. Only later
  confirmed death may transition to `faulted` and permit explicit restart.
- Route only the reserved default session through one worker initially.
- Move the authoritative audit writer to the supervisor and use pre-effect
  dispatch before worker commit.
- Preserve the existing MCP handshake/tool names and default outputs.

#### Slice 7e staging boundary — owner-approved 2026-07-14

- Land the managed worker entry as a separate fixed-SHA-reviewed sub-slice
  before operation dispatch or default-session cutover. Recognize exact
  `--worker` mode before host, audit, output-store, MCP, or supervisor-runtime
  construction; extra worker arguments fail closed.
- This sub-slice is Windows-only. It opens the two existing private protocol
  handles named by `PTK_WORKER_REQUEST_HANDLE` and
  `PTK_WORKER_EVENT_HANDLE`, removes those bootstrap identifiers from the
  worker environment, and makes both protocol handles noninheritable before
  constructing `WorkerServer` or any session runtime. Standard input remains
  launcher-provided NUL; standard output and error remain diagnostics and are
  never protocol.
- Exercise only lifecycle hello, initialize, ready, shutdown/EOF, cleanup, and
  process-exit behavior in a real subprocess. Construct `SessionRuntime` only
  from the validated initialize factory. Do not add operation DTOs or route an
  MCP tool through this worker in Slice 7e.
- Normal supervisor mode retains the current in-process default
  `ISessionOperations` registration during this sub-slice. The later cutover
  replaces it atomically with the supervisor proxy only after dispatch,
  cancellation, audit, output, diagnostics, and compatibility guards exist;
  no in-process fallback remains after that cutover.
- Managed process exits are fixed as follows: `0` for `Shutdown`, `Eof`, or
  `Canceled` after cleanup; `64` for malformed `--worker` invocation; `80` for
  entry/bootstrap failure before `WorkerServer`; `81` for `InitializeFailed`;
  `82` for `ProtocolError`; `83` for `TransportFailure`; and `84` for
  `RuntimeFailure`, including cleanup or shutdown failure. Codes `80..84`
  deliberately do not overlap the Unix broker's reserved `70..74` range.
  `WorkerServerExit.DetailCode` preserves the specific diagnosis.
- Process exit zero never proves an expected stop by itself. The supervisor's
  admitted lifecycle transition is authoritative: zero is expected only when
  paired with its requested shutdown, deliberate protocol EOF, or cancellation;
  every unpaired zero is worker loss and follows the same containment/audit
  path as another unexpected exit.
- Bootstrap captures the values of `PTK_WORKER_REQUEST_HANDLE` and
  `PTK_WORKER_EVENT_HANDLE` exactly once, then unconditionally removes both
  variables from the process environment before platform, syntax, or handle
  validation. No host, audit object, `WorkerServer`, runtime factory, runspace,
  or child process may exist before this removal.
- Each captured value must be canonical invariant unsigned decimal matching
  `[1-9][0-9]*`: no sign, whitespace, separator, hexadecimal prefix, or leading
  zero. It must fit the current pointer width and must not equal zero or the
  pointer-width `INVALID_HANDLE_VALUE`; the two values must be distinct.
- Wrap both inherited originals as owned handles, then duplicate each within
  the current process using `DuplicateHandle` with `desiredAccess: 0`,
  `inheritHandle: false`, and `DUPLICATE_SAME_ACCESS`. After both duplicates
  exist, close both inherited originals. Require both duplicates to be valid
  pipe handles and to have `HANDLE_FLAG_INHERIT` clear before stream creation.
- Construct the request stream as worker-readable and the event stream as
  worker-writable over those owned noninheritable duplicates. A private
  idempotent bootstrap owner holds both streams, disposes request first and
  event last on normal termination, and transfers no handle into PowerShell.
  `SessionRuntime` remains constructible only inside `WorkerServer`'s validated
  initialize factory after both streams exist.
- Every missing, malformed, aliased, out-of-range, invalid, non-pipe,
  inheritable-duplicate, duplication, or stream-construction failure exits as
  bootstrap code `80`. Partial acquisition is transactional: every original,
  duplicate, and stream acquired by that attempt is closed exactly once, and
  neither runtime construction nor user code occurs.
- Guards must cover unconditional environment removal, the complete canonical
  parse matrix, pointer-width and alias rejection, duplication/close/stream
  ordering, noninheritability and pipe type on Windows, partial failure at
  every acquisition boundary, and the prohibition on runtime construction
  before both streams. Mutations that retain an inheritable protocol handle or
  enter the runtime factory early must fail their intended guards.
- PTK infrastructure in worker mode writes no bytes to standard output.
  Standard output remains available only as bounded untrusted diagnostics from
  user/runtime code after successful initialization; it is never protocol.
- A nonzero managed worker exit (`64` or `80..84`) makes exactly one best-effort
  standard-error write attempt containing at most 256 ASCII bytes including
  the terminating LF. The exact shape is
  `ptk_worker_exit kind=<kind> detail=<detail>\n`, where `kind` and `detail`
  are lowercase fixed internal codes selected from compile-time allow-lists.
  Cleaned-up zero exits write no infrastructure diagnostic.
- The diagnostic formatter must map every unknown or non-allow-listed detail
  to a fixed generic code for its exit kind. It never includes exception text,
  stack traces, numeric/raw handles, paths, environment names or values,
  scripts, payloads, command output, user text, or culture-dependent data.
  Failure or partial completion of the single stderr write neither retries nor
  changes the already selected process exit code.
- Any caller that launches the worker must continuously drain stdout and stderr
  until containment closes and both streams reach EOF. The existing per-boot
  65,536-byte cap and one-marker-then-drain/discard contract applies when the
  production supervisor is wired; the Slice 7e contained-process smoke drains
  both streams without adding MCP/default-session routing.
- Guards must prove zero-exit silence, exact one-line output for every nonzero
  class, the 256-byte/ASCII/allow-list bounds, generic mapping for an unknown
  detail, no exception/data interpolation, no stdout infrastructure write, and
  write-failure invariance of the exit code. A mutation that interpolates an
  exception or emits a second line must fail its intended guard.

#### Slice 7f staging boundary — owner-approved 2026-07-14

- Land a deliberately unwired worker-operation transport and scheduler before
  any real `SessionRuntime` dispatch, supervisor proxy, audit/output transfer,
  or default-session cutover. Production `WorkerServer`, `WorkerProcessEntry`,
  `Program`, DI, tool schemas, and the in-process `ISessionOperations`
  registration remain unchanged; post-ready operation frames therefore remain
  unsupported in the live worker.
- This sub-slice owns only strict outer payloads for `request`, `cancel`, and
  `response`. A request payload has exactly positive `generation`, positive
  valid-UTC `deadlineUnixTimeMilliseconds`, an ASCII operation name matching
  `[a-z][a-z0-9_]{0,63}`, and an object-valued `arguments`. Operation-specific
  argument/result schemas remain mandatory later codecs and cannot reach a real
  runtime through this staging seam.
- A cancel uses the target operation's envelope `requestId` and has exactly one
  positive `generation` field. It is a notification, not a second request: it
  emits no response of its own. Unknown, duplicate, or late cancellation is a
  benign no-op. Cancellation can only signal the named active request.
- A terminal response echoes the worker boot ID and target request ID. Its
  payload is one closed discriminated union: `completed` has exactly
  `generation`, `status`, and an object-valued `result`; `failed`, `canceled`,
  and `timed_out` have exactly `generation`, `status`, and a bounded ASCII
  `detailCode` matching `[a-z][a-z0-9_]{0,63}`. No branch permits null or fields
  from another branch. Operation-specific result codecs must enforce the frozen
  131,072-byte logical inline-result limit before live wiring; the outer codec
  continues to enforce the already-frozen frame/depth bounds.
- The standalone scheduler receives the frozen worker boot ID, generation,
  initial request-ID high-water mark, an injected operation executor, response
  writer, clock/deadline wait, and task scheduler. It admits request envelopes
  without awaiting execution, executes each admitted request exactly once off
  the reader thread, and permits at most 64 outstanding requests. Request IDs
  must increase strictly for that boot; reservation occurs before scheduling,
  so duplicate/replayed/lower IDs never execute.
- An already-expired request never enters the executor and returns one
  `timed_out` terminal. Active deadlines and explicit cancellation signal only
  their target. A cooperative owned cancellation returns `canceled`; an
  unrelated `OperationCanceledException` is a redacted `failed`. If an executor
  returns successfully after receiving cancellation, its completed result is
  authoritative. Every request owns exactly one terminal response attempt after
  executor termination; request cancellation never cancels that write.
- A response-writer failure is attempted once, latches one scheduler-fatal
  outcome, cancels/observes peer work, and rejects later admission. Shutdown of
  the standalone scheduler is idempotent, blocks new admission, targets all
  outstanding requests, and observes them before returning. This staging owner
  never retries a write, emits exception text, or leaves a detached task.
- Guards must cover closed DTO branches and bounds, identity/generation checks,
  request-ID reservation, off-reader-thread dispatch, targeted and raced
  cancellation, exactly-one terminal ownership, expired admission, capacity,
  exception redaction, writer-failure latching, idempotent drain, and the absence
  of production wiring or supervisor-owned audit/output/runtime capabilities.
- `prepare`, `commit`, `abort`, job-terminal `event`, concrete invoke/job/state
  argument/result DTOs, supervisor audit/output capability transfer, public job
  IDs, reset/process replacement, and template bootstrap remain separate
  owner-approved sub-slices. In particular, reset is never serialized as a
  worker runtime call, and the initialize/ready bootstrap contract must be
  reconciled before Slice 8 template work.

#### Slice 7g staging boundary — owner-approved 2026-07-15

- Land strict, standalone concrete operation-value codecs while leaving them
  transport-kind-neutral and deliberately unwired. They do not register with
  `WorkerOperationRequest`, `WorkerOperationScheduler`, `WorkerServer`, a
  concrete executor, or any envelope kind. In particular, script-bearing
  invocation remains inert payload data for the later `prepare`/`commit`
  protocol and is never classified as an ordinary `request` by this slice.
- Freeze the concrete operation names as `invoke`, `job_list`, `job_status`,
  `job_output`, `job_kill`, and `state`. Each name has a distinct typed
  argument and result DTO. Unknown names and a `reset` operation fail closed.
- The foreground-only `invoke` arguments are exactly required `script`, `raw`,
  and `route`. `script` is a nonnull, strict-UTF-8-representable logical string
  of zero through 131,072 UTF-8 bytes; `raw` is a JSON boolean; and `route` is
  exactly `auto`, `pwsh`, or `rtk`. `background` is not a field in this codec:
  background job start remains a later prepared operation that also receives a
  supervisor-issued public job ID before commit. `timeoutSeconds` is likewise
  absent because the outer protocol's absolute deadline is the sole worker
  budget.
- Job-control argument objects are fully normalized and action-free:
  `job_list` is exactly `{}`; `job_status` and `job_kill` are exactly a required
  positive signed-64-bit `jobId`; and `job_output` is exactly that `jobId` plus
  a required nonnegative signed-64-bit `offset`. The ID is an opaque future
  supervisor-issued value. This slice neither allocates, maps, validates
  ownership of, nor exposes any job ID through MCP, and no worker-local ID is
  a source for this codec.
- `state` arguments are exactly required boolean `listAvailable`. State remains
  zero-wait only as a later runtime behavior; this codec executes no probe.
- Each operation's successful-result DTO is exactly required nonnull `text`.
  Its decoded logical value must be strict-UTF-8-representable and at most
  131,072 UTF-8 bytes. Parse and create paths both enforce the script/result
  limits without truncation or replacement fallback; the exact byte boundary
  passes and one byte over fails. The outer 1 MiB encoded-frame and depth-32
  bounds remain separate.
- Every argument/result object rejects duplicate, unknown, missing,
  explicit-null, wrong-kind, nonintegral, out-of-range, and noncanonical enum
  values as applicable. Stable failures never echo script or result content.
  Direct codec guards must exercise nested duplicate rejection independently
  of the outer envelope decoder.
- Extend the staging boundary guards over every new codec/DTO type and file.
  No new type may carry or reference `ISessionOperations`, `ISessionLifetime`,
  `SessionRuntime`, audit context, `OutputStore`, `RunspaceHost`, `JobManager`,
  MCP tools, DI, or process lifecycle capabilities, and no production type may
  construct or call the codecs in this slice.
- Runtime execution, `prepare`/`commit`/`abort`/`event`, audit or output
  capability transfer, background start/result handling, public job-ID
  allocation or mapping, reset/process replacement, supervisor proxy wiring,
  default-session cutover, and all MCP/schema/output behavior changes remain
  separately owner-approved work.

#### Slice 7h staging boundary — owner-approved 2026-07-15

- Land strict standalone value codecs for foreground-invoke `prepare`, its
  prepared-correlation fragment, `commit`, and `abort` while leaving them
  deliberately unwired. The codecs accept and create payload objects only:
  they do not bind a `WorkerEnvelope`, classify a frame, allocate a request ID,
  register with `WorkerServer`, or execute any operation.
- A `prepare` payload has exactly canonical lowercase RFC 4122 UUIDv4
  `planId`, positive signed-64-bit `generation`, positive valid-UTC millisecond
  `deadlineUnixTimeMilliseconds`, a 64-character lowercase hexadecimal SHA-256
  `scriptDigest`, exact operation name `invoke`, and `arguments` in the
  existing foreground-only `WorkerInvokeArguments` shape. The supervisor
  generates the plan ID before the durable prepare-authorization record; the
  codec validates but never generates it. Both parse and create recompute the
  digest from that exact script and reject a mismatch. Background job start and
  bootstrap prepare shapes remain later separately approved work.
- `scriptDigest` is SHA-256 over the strict UTF-8 bytes of the exact original
  script with no normalization, BOM, or domain prefix, matching the audit
  evidence digest. Every temporary byte buffer used to recompute it is cleared
  in `finally`, including failure paths. The deadline is the original absolute
  operation deadline, not a new budget. Creation requires UTC
  whole-millisecond precision so serialization is lossless; parsing does not
  reject an otherwise valid deadline merely because it has already expired.
- Before live binding, the MCP deadline boundary must floor `utcNow + budget`
  once to its positive Unix-millisecond value, never extending authority, and
  use that same aligned UTC instant in audit metadata and every protocol
  payload. This Slice 7h codec neither changes current audit capture nor rounds
  independently; until that prerequisite lands, it remains unwired.
- A prepared-correlation fragment echoes exactly `planId`, `scriptDigest`,
  `generation`, and `deadlineUnixTimeMilliseconds`. It is not the final
  prepared descriptor or a live response: execution-plan and
  permitted-fallback fields remain for a later approved codec. This slice
  validates values supplied by its caller and never generates a plan ID.
- A `commit` payload and an `abort` payload each contain exactly the same four
  correlation fields and have distinct DTO types despite their identical wire
  shapes. Stateless comparisons require the prepared fragment to match all
  four prepare correlation values and require commit or abort to match all four
  prepared values exactly. They return a typed match/mismatch value rather than
  a protocol parse failure; future commit binding maps mismatch to the required
  no-execution `replan_required` result. Worker boot and request IDs remain
  solely in the future outer-envelope binding and are not duplicated in these
  payloads.
- Commit and abort contain no script, operation, arguments, result, abort
  reason, retry flag, or timeout override. The codecs perform no current-worker
  identity, current-time, reservation, or replay lookup.
- Every payload rejects duplicate, unknown, missing, explicit-null,
  wrong-kind, nonintegral, out-of-range, empty, noncanonical, or mismatched
  values as applicable. Codec shape failures use exactly `duplicate_field`,
  `unknown_prepared_field`, `missing_prepared_field`,
  `invalid_prepared_field`, `unsupported_prepared_operation`, or
  `prepared_script_digest_mismatch`; comparison mismatch is not an exception.
  Failures contain no script, digest, identifier, or inner-exception content.
  Direct guards cover nested duplicate arguments independently of the outer
  envelope decoder.
- Extend the staging boundary over every new codec and DTO type. No production
  type may construct or call the prepared-operation codec, and its type graph
  may not carry `ISessionOperations`, `ISessionLifetime`, `SessionRuntime`,
  audit context, `OutputStore`, `RunspaceHost`, `JobManager`, an executor,
  scheduler, server, DI, or process-lifecycle capability.
- Guards must cover exact round trips, all closed-object and numeric bounds,
  canonical UUIDv4/digest/deadline handling, parse/create digest matching,
  distinct commit/abort types, content-free failures, and the absence of
  production wiring. Mutations that admit another operation, normalize an
  identifier, skip digest matching, accept a lossy deadline, or add a
  production reference must fail their intended guards.
- Reservation acquisition, final prepared-descriptor contents and response
  binding, planning, command-identity revalidation, commit idempotency,
  abort/expiry behavior,
  runtime execution, audit/output transfer, background start and public job
  IDs, job-terminal events, reset/process replacement, supervisor proxy
  wiring, default-session cutover, and all MCP/schema/output changes remain
  separately owner-approved work.

### Slice 8 — named harness-scoped sessions

- Add dynamic semantic session aliases, optional frozen templates, lifecycle
  state machine/gate/operation leases, `ptk_session`, and explicit session
  arguments on all tools.
- Register `ptk_session` with the explicit action-conditional `oneOf` schema
  and validate raw field presence before typed/default binding.
- Compute open/restart/close/reset deadlines at the tool boundary with the
  shared default/override/max/template-ceiling function; pass the absolute
  deadline through shutdown, worker launch, initialize, and bootstrap.
- Move public job-ID allocation to the supervisor's nonreusing 64-bit
  sequence; worker-local IDs never cross MCP.
- Prove isolation of variables, aliases, cwd, environment, modules, auth
  process, caches, jobs, reset, timeout, and concurrency.
- Unknown/lost/faulted sessions fail visibly and never masquerade as a fresh
  context.

### Slice 9 — reset/close/teardown hardening

- Make reset/restart whole-worker, target-local, generation-checked, and
  pre-effect audited.
- Apply the same lifecycle deadline to old-worker shutdown and replacement
  startup; use containment grace only after the user/startup deadline expires.
- Add bounded graceful shutdown plus proven tree kill.
- Prove broker-loss teardown and quarantine separately from ordinary worker
  loss; no live generation may continue without its armed broker.
- Aggregate idle activity/live-work semantics in the supervisor.
- Count quarantined/unconfirmed-containment observers as live work so idle
  shutdown cannot discard the recovery gate or its terminal audit obligation.
- Prove ordinary MCP EOF removes every worker and managed job; classify hard
  death/outcome uncertainty honestly. Hard-kill the supervisor during worker
  launch, blocked bootstrap, ready idle, foreground native execution, and a
  background job; removable fixtures must exit through armed OS/reaper
  containment without help from the runspace thread. Production evidence that
  cannot confirm death is reported as `containment_unconfirmed`, never as a
  proved kill.

### Slice 10 — contract reconciliation and live Windows validation

- Update tool schema/descriptions, hook guidance, installer-preserved
  template/audit config, both READMEs, and handshake fixtures.
- Mark the incompatible implementation directions in
  `security-layer.md`, `rtk-rewrite-routing.md`, and
  `shared-persistent-runspace.md` superseded/parked with a pointer here.
- Amend `unified-shell-routing.md` with a pointer here and mark its frozen
  single-bare-native routing rule plus `raw=true`-skips-routing clause
  superseded; retain its still-current hook/one-PTK-surface decisions.
- Amend `shell-dialect.md` with a pointer here and mark only the changed
  clauses superseded. In D1 those are the `raw=true` consent bypass and the
  parse-fatal, independently Bash-validated subset of “no auto-translation.”
  In D2 those are the recovery-only `raw=true` wording inventory, the sd3-1
  requirement that elision markers advise `raw=true`, and the raw-usage
  counter/log-line requirement when `RawUsageCounter` is removed. Retain the
  clean-parse refusal, `route=pwsh` consent, and unaffected precision guards.
- Amend the greenfield decision's `raw=true`-unbounded clause: the 400-line /
  40-KB ordinary bounds remain, but same-invocation recovery moves to
  `ptk_output` and legacy `raw=true` no longer selects unbounded execution or
  output.
- Reconcile `.agents/decisions.md` only after the owner releases its hold.
  That deferred amendment must carry the complete D1, D2, sd3-1, and
  greenfield split above rather than replacing either whole decision.
- Run owner-controlled AD, on-prem Exchange, and EXO proof: distinct worker
  PIDs/identities/targets, warm reuse, independent reset, job behavior, and
  harness teardown.
- Run a protected collector/SIEM smoke test and demonstrate sequence/gap and
  unclosed-event alerts.

## Slice 0 results (frozen 2026-07-11)

No product code changed in this slice. These contracts are implementation
inputs for the later slices and may not be silently widened or weakened.

### RTK same-invocation capture seam

The adjacent RTK source probe used product-equivalent commit `5d32d07` (the
checkout's extra commit was governance-only), and the independent installed
runtime probe used RTK 0.43.0. No PTK-usable raw-capture seam exists:

- `rtk rewrite` returns rewritten command text and routing status; it does not
  execute or expose capture metadata.
- The shared streaming runner has bounded internal string capture, while
  other capture paths differ and passthrough can inherit stdout/stderr with no
  captured bytes. Normal tee output is optional, command-path-dependent,
  size/mode-gated, truncated/rotated, and disableable; a force-hint path can
  bypass some tee gates without becoming a stable API. Tee concatenates
  streams and exposes only a spoofable human `[full output: path]` hint.
- RTK has a product version, but no capture-capability/schema negotiation, PTK
  correlation ID, stream-separated artifact, child-started bit,
  provenance/completeness manifest, or atomic terminal record. PTK therefore
  cannot turn current tee files into truthful `ptk_output` handles.

The selected seam-absent behavior above is final: keep safe RTK routing,
return `recovery=unavailable: rtk capture unsupported` with no handle for an
RTK-routed result, preserve handles only for non-RTK execution paths whose
unshaped bytes PTK captured before any lossy shaping, and never rerun or parse
a human tee hint. Until a valid seam proves more, every RTK-routed result has
`RtkUnknown` provenance, is treated as already RTK-processed (never sent to
`rtk log` again), and cannot masquerade as a raw capture. `RtkFiltered` and
`RtkPassthrough` require the negotiated machine record.

Any future upstream RTK seam must provide all of the following before PTK may
advertise RTK recovery:

1. Stable capability/schema negotiation before a user process starts.
2. A PTK-supplied per-call correlation ID and restricted destinations or
   inherited handles owned by PTK, independent of RTK tee configuration.
3. Binary-safe pre-filter stdout and stderr bytes in separate artifacts for
   filtered, passthrough, success, nonzero-exit, spawn-failure, and capture
   failure paths. A stream marked complete is exact and untruncated. A
   post-start capture failure preserves whatever prefix was captured, marks
   that stream incomplete, and never silently labels it exact. RTK does not
   rotate these artifacts.
4. An atomically completed machine record containing schema/correlation,
   `child_started`, filtered-versus-passthrough provenance, exit/signal,
   per-stream byte counts and SHA-256 digests, and per-stream completeness.
5. A pre-start capture/setup failure distinguishable from any post-start
   failure. PTK may use an allowed exact-semantics fallback at most once only
   when an atomically complete, schema-supported, correlation-matched machine
   record explicitly proves `child_started=false`. A missing, malformed,
   stale, mismatched, or partial record is `outcome_unknown` and forbids
   fallback. After a proved start PTK reports incomplete/unknown and never
   re-executes.
6. PTK-owned retention and cleanup. A filesystem path or prose hint is not the
   machine protocol.

This plan still authorizes no change to the adjacent RTK repository.

### Audit JSONL and OTLP/HTTP

The supported core JSONL schema identities are the literals `ptk.audit/1` and
`ptk.audit/2`; the current writer emits v2 and retained v1 remains readable.
Records are
compact UTF-8 without BOM, exactly one JSON object followed by LF, with a
maximum of 65,536 UTF-8 bytes including LF. The envelope under **Versioned
event envelope** is the frozen v1 semantic field set plus the explicit v2
extension above. Every key defined for the record's declared version is
present; non-applicable or unknown scalar values are `null`, arrays use `[]`,
and unrecognized properties are forbidden. A field/meaning/default/hash
change after v2 requires a new schema version; adding a new dotted
`event_type` does not.
Oversize variable data is represented by an existing bounded code/digest or
the operation refuses before effects; it is never silently truncated into a
core event.

V1 uses lowercase UUIDv7 event/call IDs, UUIDv4 boot/host IDs, UTC RFC 3339
timestamps, signed 64-bit monotonic sequence values starting at one per
supervisor, and lowercase SHA-256 hex. Every emitted integer token uses
canonical base-10 integer syntax without fraction, exponent, leading plus, or
leading zero. Emit properties in the envelope's documented order. Serialize
compactly without `event_hash`, SHA-256 those
exact UTF-8 bytes (which already contain `schema_version` and
`previous_event_hash`), append `event_hash` as the final property, then LF.
Do not reserialize an event to compute or export its hash. Independent
verification strips LF, requires the exact final suffix
`,"event_hash":"<64-lowercase-hex>"}`, replaces that suffix with `}` to
recover the original pre-hash byte sequence, hashes those raw bytes, compares
the extracted value, then performs strict schema and previous-link validation.

`local-only` is the startup default and needs no SIEM, collector, endpoint, or
credentials. Anchored mode has no default endpoint and fails startup if its
export configuration is incomplete. The operator supplies a complete
signal-specific URL such as `https://collector.example:4318/v1/logs`; PTK uses
its path verbatim and never appends `/v1/logs`. A bare
`https://collector.example:4318` therefore posts to `/`; it is not silently
rewritten. The URL must be HTTPS with normal name/chain validation and may not
contain userinfo, query, or fragment.

Anchored mode requires either at least one configured authentication header
(normally `Authorization: Bearer ...`) or an mTLS PEM certificate/private-key
pair. Header names must be unique case-insensitively and valid HTTP tokens;
values may not contain CR/LF. Configuration may not override `Host`,
`Content-Type`, `Content-Length`, `Transfer-Encoding`, `Connection`, or
`User-Agent`, `Content-Encoding`, or `Accept-Encoding`; fixed PTK protocol
headers win by rejecting such configuration, not by silently replacing it.
The fixed user agent is
`PowerShell-Token-Killer/<version> OTLP-HTTP-dotnet/1`. With no CA file, use
the system trust store. A configured PEM CA bundle is a replacement custom
trust store, not an implicit augmentation. mTLS certificate and key paths are
both required, loaded once at startup, must form a matching private-key pair,
be currently valid, and, if an EKU extension exists, permit client
authentication. Secrets never enter audit events, diagnostics, worker
environments, or protocol frames.

The first-class exporter contract is:

- OTLP/HTTP binary protobuf, one JSONL event per `ExportLogsServiceRequest`,
  one request in flight, no compression initially, 10-second request timeout,
  `Content-Type: application/x-protobuf`, and automatic HTTP redirects
  disabled. A `3xx` is observed/classified at the configured anchor; PTK never
  forwards the body, headers, or mTLS identity to its `Location` target.
- The JSONL line without LF is the exact `LogRecord` string body. Resource
  attributes are `service.namespace="ptk"`,
  `service.name="powershell-token-killer"`, `service.version`,
  `service.instance.id=<supervisor_boot_id>`, and `host.id`. Instrumentation
  scope is `PtkMcpServer.Audit` at the producer version.
- `time_unix_nano` and `observed_time_unix_nano` map the two event timestamps;
  `event_name` is `ptk.audit.<event_type>`; `trace_id` is set only when present;
  span ID and severity are unset. Query attributes, each unique and typed, are
  `ptk.audit.schema_version` (string), `ptk.audit.event_id` (string),
  `ptk.audit.event_type` (string), `ptk.audit.sequence` (int64),
  `ptk.audit.previous_event_hash` (string when nonnull),
  `ptk.audit.event_hash` (string), `ptk.supervisor.boot_id` (string),
  `ptk.worker.boot_id` (string when nonnull), `ptk.session.name` (string when
  nonnull), `ptk.session.generation` (int64 when nonnull), `ptk.call.id`
  (string when nonnull), `ptk.job.id` (int64 when nonnull),
  `ptk.outcome.state` (string when nonnull),
  `ptk.termination.certainty` (string when nonnull),
  `ptk.evidence.subject.id` (string when nonnull),
  `ptk.evidence.subject.digest` (string when nonnull),
  `ptk.evidence.subject.bytes` (int64 when nonnull),
  `ptk.evidence.subject.state` (string when nonnull),
  `ptk.evidence.retention.reason` (string when nonnull),
  `ptk.disposition.id` (string when nonnull),
  `ptk.disposition.target.boot_id` (string when nonnull),
  `ptk.disposition.target.event_id` (string when nonnull),
  `ptk.disposition.proof_kind` (string when nonnull),
  `ptk.disposition.failure_class` (string when nonnull),
  `ptk.disposition.target.export_configuration_identity` (string when
  nonnull), `ptk.disposition.verified_receipt_digest` (string when nonnull),
  and `ptk.disposition.acknowledged_gap_reason` (string when nonnull). No
  null-valued OTLP attribute is emitted; `dropped_attributes_count` remains
  zero. Any mapping or attribute loss is export failure.
- Stable IDs and exact body bytes survive retry. Full-jitter backoff starts at
  one second and caps at 60 seconds. Honor `Retry-After`.
- The fake receiver binds
  `https://127.0.0.1:<ephemeral>/v1/logs` with a generated test CA/leaf whose
  SAN contains `127.0.0.1` and a random 256-bit bearer token. Missing/wrong
  auth returns `401`, `WWW-Authenticate: Bearer`,
  `Content-Type: application/x-protobuf`, and a binary `google.rpc.Status`; it
  persists nothing. Every fake-receiver `4xx`/`5xx` uses that OTLP failure
  content type/body shape. Decoded fake bodies are `{code:0,
  message:"unauthenticated", details:[]}` for `401`, `{code:0,
  message:"bad data", details:[]}` for `400`, and `{code:0,
  message:"unavailable", details:[]}` for `503`; the `503` fixture also sends
  `Retry-After: 1`.

Acknowledgment is exact because each request carries one event:

| Response | Frozen PTK behavior |
|---|---|
| `200` plus a valid `ExportLogsServiceResponse`, no partial response or `rejected_log_records=0` | Acknowledge and atomically checkpoint. A zero-rejection warning is health metadata, not rejection. |
| `200` plus `rejected_log_records=1` | Do not checkpoint and do not automatically retry the prohibited partial request; pin the record and mark export stalled/degraded. |
| `200` with malformed protobuf, wrong content type, or rejected count outside `0..1` | Acknowledgment unknown; keep the record and retry the same ID/body. |
| `429`, `502`, `503`, or `504` | Retry the same ID/body; honor `Retry-After`, otherwise jittered backoff. |
| Timeout, disconnect/EOF, DNS, or connection failure | No checkpoint; retry the same ID/body with jittered backoff. |
| `401`, `403`, or `404` | Non-retryable configuration/auth failure: no checkpoint or automatic retry, retain/persist the block, and permit one new attempt only after configuration identity changes. |
| `400` or `413` | Permanent data failure: no checkpoint or automatic retry under any configuration identity; retain/persist the block for explicit operator disposition. |
| Any other `4xx`/`5xx`, including `500` | Permanent protocol/server disposition: no checkpoint or automatic retry under any configuration identity; retain/persist the block for explicit operator disposition. |
| Any other `2xx`/`3xx`, including `202` and `204`, or TLS validation/client-auth failure | Not an acknowledgment; retain and pin the record, enter permanent stalled/configuration state, and do not automatically retry until the startup configuration identity changes. |

The configured receiver is the anchor boundary: an OTLP `200` is not evidence
that an arbitrary downstream SIEM indexed the event. The fake receiver must
durably append and flush the exact body/event ID before `200`; production docs
must require an equivalently durable collector queue/index under the intended
separate principal. OTLP requests for either supported core schema carry the
core event only, never exact script-evidence bytes. Core acknowledgment
externally anchors the evidence ID/digest and releases the referenced local
evidence object from the anchored mode's acknowledgment pin into ordinary
retention; it does not claim remote possession of the script. Any operator
evidence read/export is separately audited; Slice 2 has no automatic
evidence-byte exporter.

Required integration cases are success/checkpoint,
`503` then identical retry, persisted response-loss then duplicate replay,
wrong auth, partial rejection, `400`, zero-rejection warning without audit
recursion, wrong CA/hostname, and `307` with an instrumented redirect target
that receives no request while the checkpoint remains unchanged. This follows the official
[OTLP/HTTP request/response contract](https://opentelemetry.io/docs/specs/otlp/)
and [OTLP exporter configuration](https://opentelemetry.io/docs/specs/otel/protocol/exporter/).

### Parent-death containment probes

Both platform shapes passed with disposable children; machine-specific
versions/hashes are recorded only in `.agents/machines.md`.

**Windows:** On `NETWATCH-01`, a native .NET probe created an unnamed,
noninheritable Job Object, set and queried back exactly
`JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE`, and created a suspended probe worker with
`CreateProcessW`, `STARTUPINFOEX`, and
`PROC_THREAD_ATTRIBUTE_JOB_LIST`. Suspension was probe instrumentation only;
creation-time membership came from `JOB_LIST`. The job handle was excluded
from the explicit five-handle `PROC_THREAD_ATTRIBUTE_HANDLE_LIST`, was never
duplicated, and the probe contained no `AssignProcessToJobObject` or
`TerminateJobObject` path.

`IsProcessInJob(worker, exact_job)` was true before `ResumeThread`; the worker
was alive, acknowledged the closed gate, and did not signal work. A gated
supervisor-only termination killed that worker. In the released case an
ordinary no-breakaway descendant was alive and independently confirmed in the
same exact job. Raw `TerminateProcess` targeted only the supervisor; held
worker/descendant process handles then signaled death, proving last-job-handle
closure killed both without a PID/name sweep or controller tree-kill. No probe
process survived. Production Windows support therefore requires Windows 10 /
Server 2016 or newer and this creation attribute; unsupported/failing systems
refuse worker startup, never spawn then assign. See Microsoft
[`UpdateProcThreadAttribute`](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-updateprocthreadattribute),
[`IsProcessInJob`](https://learn.microsoft.com/en-us/windows/win32/api/jobapi/nf-jobapi-isprocessinjob),
and [Job Objects](https://learn.microsoft.com/en-us/windows/win32/procthread/job-objects).

**Unix:** On Darwin, a broker-first fork/pipe probe passed three barriers:
supervisor death after worker fork but before group arm, after arm but before
gate release, and after release with a TERM-ignoring descendant. The broker
alone forked the worker, made `PGID == worker PID`, withheld the gate until
group/liveness setup, observed EOF from the supervisor's sole liveness writer,
and killed the group; no pre-gate work or survivor remained. This validates
the topology/primitives, not yet the later .NET implementation. The durable
implementation must additionally capture broker/leader start identity and
prove broker-loss teardown while the supervisor remains alive. There is no
spawn-then-arm fallback.

### Session/profile schemas and worker protocol bounds

`~/.ptk/profiles.json` uses this exact v1 shape (the API calls these entries
templates):

```json
{
  "schemaVersion": 1,
  "templates": [
    {
      "name": "ad",
      "description": "Contoso on-prem AD",
      "bootstrapScript": "bootstrap/ad.ps1",
      "startupTimeoutSeconds": 300,
      "declaredTarget": "contoso-ad",
      "declaredIdentity": "svc-ad-read",
      "allowColdBackground": false
    }
  ]
}
```

The catalog schema is JSON Schema draft 2020-12 semantics with
`additionalProperties:false` throughout and every shown property required.
The JSON file is strict UTF-8 with an optional UTF-8 BOM, at most 1,048,576
raw bytes including that BOM, and at most 128 templates. Strip the optional
BOM only for parsing; the source-file diagnostic digest covers it. Comments,
trailing commas, duplicate properties/names, explicit null, and unsupported
versions fault the entire external catalog. Before ordinary deserialization,
an incremental `Utf8JsonReader` pass tracks each decoded property name with an
ordinal set at every object depth and rejects a duplicate (including an escape
spelling of the same name); last-property-wins binding is forbidden.

Names are already canonical lowercase, match
`[a-z0-9][a-z0-9._-]{0,63}`, and may not be `default`. Description is 1..512
Unicode scalar values; declared target/identity are 1..256; bootstrap path is
1..4096. Those three metadata fields and the path contain no Unicode `Cc`
control scalar (therefore no NUL). Startup timeout is an integer 1..86,400.
All `integer` schema values accept every valid JSON number whose mathematical
value is integral and in the target range, including `300.0` or `3e2`; the raw
adapter normalizes it to the target integer before typed binding. Schema and
runtime parity tests cover fractional, exponent, overflow, and boundary forms.

Resolve relative bootstrap paths against the directory of the lexical
`Path.GetFullPath` catalog path, never cwd or the final target directory of a
catalog symlink. The digest path is exactly the bootstrap path string returned
by `Path.GetFullPath`: do not resolve a link target, fold case, normalize
Unicode, or replace the platform separators. Thus the digest is intentionally
platform-scoped. Open the resulting path as one finite regular target at
catalog load, accept strict UTF-8 with an optional UTF-8 BOM, cap raw bytes at
131,072, and freeze those raw bytes before any session open. Strip a bootstrap BOM for
the frozen decoded script passed to the worker, while `bootstrap_digest`
remains SHA-256 of the raw bytes including the BOM. A link/reparse path may
open a regular target, but its final target path is not hashed; later target
changes cannot affect the frozen bytes.

`template_digest` is SHA-256 over ASCII `ptk.session-template/1\0`, followed
in this order by canonical name, description, lexical absolute path string,
startup-timeout integer, declared target, declared identity, cold-background
boolean, and raw bootstrap bytes. Strings/bytes use a four-byte unsigned
big-endian length followed by strict UTF-8/raw bytes; timeout is unsigned
four-byte big-endian and boolean is one byte (`0`/`1`). The healthy
`catalog_digest` is SHA-256 over ASCII `ptk.session-catalog/1\0` followed by
ordinal-name-sorted, length-prefixed canonical names and their 32 raw
`template_digest` bytes. Also retain a SHA-256 of readable source JSON for
fault diagnosis.

A missing file is a healthy empty catalog. Any malformed definition,
duplicate, unsupported schema, invalid/unreadable/oversize bootstrap, or
decode/read error faults all external templates; none partially loads.
`default` and explicit template-less dynamic sessions remain available. Every
template-backed open then returns the stable catalog fault, and an unknown
template always refuses before alias binding, bootstrap, or worker creation.

The explicit `ptk_session` `oneOf` schema is frozen as follows:

| Action | Required properties | Optional properties |
|---|---|---|
| `list` | `action` | none |
| `open` | `action`, `name` | `template`, `allowColdBackground`, `timeoutSeconds` |
| `close`, `restart` | `action`, `name` | `expectedGeneration`, `force`, `timeoutSeconds` |

`action` is the exact branch constant (`list`, `open`, `close`, or `restart`),
so precisely one branch can validate. Each branch rejects null and every
unlisted property. Before binding, run the same decoded-name duplicate-key
scan and validate the raw property-presence set. `timeoutSeconds` is integer
`0..Int32.MaxValue` and runtime-capped; `expectedGeneration` is integer
`0..Int64.MaxValue`. These numeric fields use the same schema-valid integral
normalization rule as the catalog. `open default` is a semantic refusal.

Supervisor/worker protocol v1 is compact strict-UTF-8 NDJSON over the private
pipes described above, no BOM, one LF terminator, maximum JSON depth 32, and a
symmetric maximum encoded frame of 1,048,576 bytes excluding LF. Readers cap
incrementally: after 1,048,576 payload bytes the next byte must be LF and is
accepted as the terminator; a next non-LF payload byte fails before it is
buffered. No unbounded line reader is permitted. Submitted/bootstrap decoded
script is at most 131,072 bytes of its strict-UTF-8 logical text before JSON
escaping. Inline worker result is at most 131,072 aggregate pre-escape UTF-8
bytes across all inline stream/result fields, and initialize metadata is at
most 65,536 aggregate pre-escape UTF-8 bytes. Larger result content uses the
output artifact. Worker stdout and stderr each retain at most 65,536 bytes per
worker boot (not per call), then continue draining/discarding for that boot
with exactly one per-stream truncation marker so a child cannot block on a
full pipe.

Supervisor-originated oversize input produces an audited no-start before
prepare/commit. Invalid UTF-8, malformed/unknown-version/method, excess depth,
or oversize worker frames fail closed and contain that worker; after dispatch,
the supervisor records the appropriately unknown terminal outcome. Worst-case
JSON escaping of a 131,072-byte script remains below the frame cap with
envelope headroom.

### Frozen `rrp` regression carry-forward

Only the still-relevant correctness cases carry forward:

| Prior case | Required result here |
|---|---|
| rrp-1 | The configured/pinned off-PATH RTK identity beats a conflicting PATH copy. |
| rrp-2, rrp-15 | Resolve against live warm/cold command state at prepare and revalidate at commit. Aliases, functions, cmdlets, `.cmd`/`.bat`, and preceding/ambient mutations keep exact PowerShell semantics. |
| rrp-3, rrp-8 | Redirection, producer/consumer pipelines, counts/parsers, and prefixed `find`/`fd` pipelines remain unfiltered exact-original execution. |
| rrp-12 | Wrapper contexts such as `docker exec ... git ...` stay original; never inject the host RTK path into the target context. |
| rrp-13 | Foreground RTK uses warm-session cwd; cold jobs use their documented session cwd. |
| rrp-11 | Native error preferences cannot discard RTK stdout, pollute `$Error`, or alter stream/exit capture. |
| rrp-4, rrp-14 | RTK absence, pre-start routing/capture failure, or insufficient remaining budget may use an allowed exact fallback at most once only with valid machine proof that no user process started; missing/invalid proof is unknown and never falls back. |
| rrp-6 | Every forced-route fallback is labeled. Seam-absent RTK is explicitly `RtkUnknown`; filtered versus passthrough is asserted only from a valid negotiated record. |
| rrp-7 | If PTK invokes `rtk rewrite`, exits `0`/`3` plus nonempty stdout are candidate rewrites; exit `1` with empty stdout is no-rewrite exact fallback. Exit `2` is an RTK permission deny, but RTK config is not PTK authorization: record `rtk_rewrite_deny_ignored` and execute the exact original once through the allowed pre-start fallback (labeled for `route=rtk`). Any wrong output/exit pairing is protocol error and exact pre-start fallback. Only the old PS7 compound matrix is conditional on later routing breadth. |
| name-keyed hooks | Preserve the documented hook-name divergence and `route=pwsh` escape; do not claim a routed `git` fires `git`-keyed hooks. |
| former rrp-5 | Exercise foreground and cold-background contexts separately; the old foreground-only limit is superseded. |

Do not import the prior broad compound-routing claim. The PS7 compound matrix
becomes mandatory only if routing later widens; rrp-9's savings benchmark is
not a correctness guard, and rrp-10 remains documentation reconciliation.

## Acceptance matrix

### Audit completeness and failure

- Every MCP tool/action produces accepted and terminal records.
- Foreground success, PowerShell errors, native stderr, queue expiry,
  cancellation, routing fallback, timeout, recycle, and unknown outcome.
- Background preflight failure, start, completion without polling, output
  reads, status/list, explicit kill, reset kill, shutdown kill, and hard
  worker death.
- Background start failure/refusal emits no `job.started` or asynchronous job
  terminal; successful start completes the MCP call before a barrier releases
  the job, then emits exactly one later job terminal event.
- A template with `allowColdBackground=false` starts no cwd probe, output
  artifact, or child process and records `job.not_started`. Enabling it starts
  a cold job that demonstrably cannot see the warm session's variables or
  connection. No session-mode job can occupy/starve the serialized runspace.
- Reserved `default` continues to start today's cold background child. A
  template-less named session defaults to the same audited no-start behavior
  as a false template; an explicit true value on its first open enables only
  the documented cold/stateless behavior and is immutable thereafter.
- Required-journal failure before foreground, job start, reset, close, and
  kill proves zero side effects.
- With an unwritable/corrupt spool, `ptk_state` returns only the labeled
  unrecorded audit-health diagnostic; `ptk_session list`, output/job reads,
  invoke, job start/kill, reset, and close all refuse with zero side effects.
  On recovery, the next durable event records the emergency-probe count and
  first/last timestamps.
- Hard-kill after a dispatch commit but before its terminal event; the exact
  script evidence referenced by that dispatch remains retrievable. Inject
  evidence-write, evidence-flush, event-append, and event-flush failures
  independently and prove no user work starts.
- Lose a response after commit, deliver the same commit twice, expire a
  prepared reservation, and change a prepared command identity. The sentinel
  executes at most once; stale/expired plans execute zero times; every
  internally prepared replacement remains under the original call/deadline
  and receives its own durable dispatch record.
- Concurrent events produce no torn records, duplicate sequence, or broken
  chain.
- Fill the ordinary spool to just below high water, then race foreground
  calls, background starts, and worker opens across sessions. Each operation
  either reserves its complete worst-case terminal set before acceptance or
  is rejected with zero effects. After every accepted operation/job/worker is
  driven terminal during collector outage, all promised terminal events fit;
  no burst can overbook the preallocated segment.
- Rotation/retention bound a long-lived supervisor; a live file is not swept.
- Export retry/checkpoint and duplicate delivery; network loss proceeds only
  while spool remains healthy.
- After the collector acknowledges the final event, the atomic checkpoint
  advances without creating another exportable event; an idle exporter emits
  no unbounded checkpoint/event loop.
- In anchored mode, an offline collector pins unacknowledged segments through
  age and byte sweeps; reaching the high-water mark refuses a new side-effect
  sentinel while a started job can still append its terminal event from the
  reserve. In local-only mode, bounded retention is allowed and is visibly
  labeled as unanchored telemetry.
- Automatic evidence retention reserves its complete intent/terminal pair,
  durably appends the exact subject intent before unlink, and emits completed,
  failed, or outcome-unknown truth afterward. Constructor/probe/pre-writer and
  no-journal publication paths delete nothing; an unreservable capacity sweep
  fails the triggering admission. Crash temporaries use only the paired
  `temporary`/`crash_temporary` facts, and request-triggered pressure carries
  the triggering call attribution.
- At anchored high water, flood `ptk_state`, session/job list/status/output,
  and `ptk_output` calls. All except the minimal unrecorded health diagnostic
  are rejected before acceptance and consume zero reserve bytes; a previously
  started job still appends its terminal event to the preallocated reserve.
- Exact script evidence is retrievable only from the protected evidence
  stream; fixture tokens/output never enter core events.
- Evidence administration distinguishes invalid/absent/control/storage and
  destination failures from no disclosure, disclosure unknown, flush failure
  after disclosure, terminal-audit failure after disclosure, and
  terminal-audit failure after protected publication. Disposition failures
  likewise preserve the exact pre-effect or post-checkpoint stage rather than
  collapsing every failure to `operation.failed`.

### Single-execution routing

- Known RTK win, unsupported RTK passthrough, RTK absent, rewrite/capture
  failure, and near-expired deadline all execute at most once.
- Warm alias/function and preceding/ambient resolution changes preserve
  PowerShell semantics.
- Mixed pipeline, assignment, parsing/counting, and redirection execute the
  original once with no refusal/retry text.
- `git diff | Set-Content patch.txt` writes an applyable unfiltered patch;
  `git diff > patch.txt` is accepted without prefiltering file bytes.
- No RTK-routed stdout is passed through `rtk log` again; seam-absent
  foreground and job fixtures assert `RtkUnknown`.
- A parse-fatal, detector-positive, `bash -n`-valid fixture executes exact
  bytes through Bash when present and has a truthful not-started result when
  absent.
- A validator fixture sets hostile `BASH_ENV`/exported-function sentinels and
  a side-effecting submitted command. Prepare starts no process; after the
  dispatch flush, scrubbed `bash -n` is audited but creates no sentinel; only
  successful validation permits the submitted command's single execution.
- ``Write-Output `tColumn` Name`` (the recorded sd1-2 valid-PowerShell
  false-positive class) never executes under Bash even if the dialect
  detector is test-sabotaged to return a finding, because PowerShell parsing
  succeeds.
- `foo 'bar'() { echo hi; }` (the recorded sd1-6 synthesized-shape class)
  never executes under Bash when the detector is test-sabotaged, because
  `bash -n` rejects the exact text.
- Foreground/background effective routes match their respective warm/cold
  resolution contexts.

### Output recovery

- PowerShell objects, bounded text, log shaping, RTK filtering, stderr,
  warnings/errors, and background streams recover from the same invocation
  whenever their execution path produced a raw artifact.
- With the RTK capture seam present, a machine-proven `RtkFiltered` or
  `RtkPassthrough` call returns a working handle for its pre-filter
  stdout/stderr.
- With the seam absent, every foreground/background RTK route is
  `RtkUnknown`, remains RTK-routed, receives no handle or second `rtk log`
  shaping, and reports `recovery=unavailable`; PowerShell/direct captures in
  the same server still return working handles.
- A persistent counter/file sentinel proves `ptk_output` never reruns.
- Retrieval after underlying files/state change returns the captured snapshot.
- A handle issued in generation N remains readable after timeout replacement,
  reset, restart, close, and loss of N until its ordinary TTL/quota eviction.
  A worker killed mid-capture returns an explicit incomplete snapshot/status,
  never not-found and never rerun advice.
- Offset/search reads are stable; expired/evicted/incomplete handles are
  explicit and never advise rerun.
- Output-store failure leaves the execution count at one and reports recovery
  unavailable.
- Legacy `raw=true` cannot alter the effective route.

### Session isolation and lifecycle

- No config preserves default-session warm variables/cwd and current tool
  calls.
- Two named sessions use distinct PIDs/boot IDs and isolate variables,
  aliases, environment, modules, cwd, caches, `$LASTEXITCODE`, jobs, and
  timeout/reset effects.
- A template-less dynamic session establishes a process-scoped auth/module
  sentinel, then times out after execution starts. Its whole worker exits, its
  generation changes, its managed jobs stop, and the alias requires explicit
  restart before a new empty worker can exist; the sentinel is absent there
  and the other session remains untouched.
- Barrier tests expire a call while it owns the only operation lease and while
  other calls/jobs are queued. The slot enters timeout-resetting without
  self-deadlock, admits no new work, cancels the other generation leases, and
  never starts a replacement before confirmed old-group death. A stuck kill
  returns by deadline plus the fixed containment grace with the slot
  quarantined and `containment_unconfirmed`.
- A barrier test, not timing alone, proves different sessions can progress
  concurrently while one session remains serial.
- Concurrent open starts one worker; list/state on cold does not start it.
- The generated `ptk_session` schema accepts `list` with no name and rejects
  open/close/restart without one; list neither requires nor ignores a sentinel
  session name.
- Schema and runtime tests also reject `{action:"list",force:false}` and
  explicit-null lifecycle fields, proving absence is preserved rather than
  collapsed into CLR defaults.
- The schema accepts `timeoutSeconds` on `open`, `close`, and `restart` and
  rejects it on `list`; an open override reaches the shared cap/template
  ceiling rather than failing additional-property validation.
- Barrier-controlled invoke/job-start versus reset/restart/close races prove
  the lifecycle transition wins before admission or the operation lease wins
  before the busy check; work never starts in a dying generation, duplicate
  workers never appear, and late replies cannot populate the replacement.
- Bootstrap runs once, faults visibly, and becomes the drift baseline.
- A bootstrap fixture that ignores cancellation is timed out; the open call
  returns by deadline plus containment grace. A confirmed kill publishes
  `faulted`; a test-sabotaged unconfirmed kill returns the explicit terminal
  and `quarantined`, rejects open/restart, ignores late-ready frames, and only
  permits one different worker after later confirmed death plus explicit
  restart.
- Dynamic open and reset replacement against wedged initialize/bootstrap
  return by the documented server-default-or-override deadline plus containment
  grace. Template-backed startup stops at the earlier template ceiling;
  positive overrides above the server maximum are capped; lazy default start
  under invoke cannot reset or extend the invoke deadline.
- Missing catalog yields no templates. Malformed/unsupported/duplicate
  catalog data and missing/unreadable bootstrap paths disable every template
  as one visible catalog fault while default and explicit dynamic sessions
  remain available. Relative paths resolve against the catalog directory even
  when the session cwd is attacker-controlled; an unknown template never
  opens an empty session.
- An alias opened from template digest A refuses a later open using digest B,
  both while live and after close; its original binding/target labels remain
  unchanged and no worker starts. Reopen with digest A starts only the expected
  next generation.
- If digest A's first bootstrap fails, the slot is faulted but remains bound to
  A. A later open/restart with B refuses without launch; explicit restart with
  A is the only way to attempt the next worker generation under that alias.
- Reset/restart increments generation and affects only the named session.
- Stale generation and non-force busy close/reset/restart have zero side
  effects and share one observable busy contract.
- `ResetToolTests.Reset_kills_running_background_jobs` remains unchanged:
  with no foreground/queued call, default `ptk_reset(force=false)` kills the
  running cold job and replaces the session; a separate foreground-busy case
  refuses until `force=true`.
- Worker loss fails pending calls once and requires explicit restart.
- Start a job, replace its worker, then start another job. The public IDs
  differ, and status/output/kill using the old ID cannot observe or affect the
  new job even when the worker's private counter reused its first value.
- Closing `default` kills its jobs/worker, leaves the reserved slot cold, and
  the next unqualified invoke starts exactly one new generation. Closing a
  named session never auto-reopens it and never redirects its calls to
  `default`.
- Supervisor EOF/shutdown leaves no managed workers/jobs. Forced supervisor
  death in starting, bootstrapping, ready, foreground-busy, and job-running
  phases exercises the platform containment path and leaves none after its
  bounded grace.
- Launch tests stop at every barrier (before worker creation, during atomic
  creation, before Unix gate release, and immediately after release), kill the
  supervisor, and prove no runnable or suspended worker escapes containment.
- On Unix, kill the broker during blocked bootstrap and after ready while the
  supervisor remains alive. New work stops, the identity-validated group is
  killed or quarantined within the bound, and no replacement generation
  starts. After confirmed group death, hard-kill the supervisor with the
  broker still absent and prove no orphan exists.
- With idle timeout shorter than a delayed containment confirmation, a
  quarantined slot keeps the supervisor/observer alive and rejects new
  generations. Only after confirmed death and the resulting terminal audit
  fact may the ordinary idle countdown resume.

### Compatibility and live verification

- Existing Pester suite, .NET suite, and MCP handshake remain green after
  every code slice, with the named heredoc refusal guard replaced in slice 3,
  the two named obsolete raw-consent guards plus the four named Pester marker
  guards and the named .NET invoke-description guard replaced in slice 4, the
  named .NET job-log-path guard replaced in slice 5, and the route-pwsh consent
  guard retained/strengthened.
- New tests receive red-leg guard proof per repo guidance.
- Default tool schemas remain compatible through the declared raw transition.
- Real MCP stdio tests cover audit IDs, RTK path, output handle, two sessions,
  independent jobs, process teardown, and malformed worker protocol.
- A FullLanguage fixture writes plain text and forged response-shaped JSON via
  `[Console]::Out` and a native child; neither completes a pending protocol
  request nor creates an authoritative audit fact, and the worker remains
  usable.
- Windows live checks cover native `.cmd` shims, AD, Exchange implicit
  remoting, EXO certificate auth, worker/process reset, and SIEM forwarding.

## Assurance limits and residual risk

- Same-user arbitrary code can kill PTK, alter unanchored local files, invoke a
  different shell path, or reauthenticate with any credential available to
  that user. External receipt, OS audit, and upstream RBAC are the real
  boundaries.
- The separate supervisor keeps audit credentials/state outside the warm
  runspace and records intent before dispatch, but same-user process
  separation is not hostile-code isolation.
- A local hash chain is rewritable until externally anchored.
- Client/harness labels are attribution hints unless the transport
  authenticates them.
- PTK cannot prove every descendant, scheduled task, service, WMI/SSH launch,
  remote mutation, or rollback. Coverage and termination certainty must say
  so.
- Exact script evidence creates a sensitive repository and requires restricted
  SIEM access, retention, and access auditing.
- Output artifacts create another short-lived sensitive store; they are not
  audit records and must not inherit audit retention.
- RTK behavior/version is an external dependency. PTK must preserve exact
  execution on pre-start integration failure and must never claim raw recovery
  without the capture artifact.
- Process-per-session prevents accidental context mixing and limits reset/
  crash blast radius; it does not strengthen upstream authorization when
  every worker has the same credentials.

## Documentation state after approval

The owner approved this plan on 2026-07-11 after dual-review convergence.
Following approval:

- This is the canonical implementation contract. No implementation slice was
  requested with the approval; execution still begins only on an explicit go.
- `security-layer.md` remains the record of the rejected policy-gate
  exploration.
- `rtk-rewrite-routing.md` remains a reviewed draft whose regression evidence
  is reused here, not approved implementation.
- `shared-persistent-runspace.md` remains an unapproved idea and must not be
  implemented.
- The decisions-log hold remains in force.
