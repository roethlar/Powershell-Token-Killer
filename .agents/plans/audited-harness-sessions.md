# Plan: mandatory audit, harness-scoped sessions, and internal RTK routing

**Status:** DRAFT — owner-directed 2026-07-11; implementation is NOT
approved. Claude and Grok reviewloop are required before this plan is
presented for implementation approval.

This plan is the candidate replacement for the still-open security response,
the unapproved durable/shared-session idea, and the reviewed-but-unapproved
`rtk rewrite` draft. It does not amend `.agents/decisions.md` while the
owner's hold on that file remains active. If approved, the final documentation
slice must reconcile those older records rather than leaving competing
contracts.

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
`SessionRuntime` containing one `RunspaceHost`, one `JobManager`, one output
store partition, one module cache, and one authentication/environment
context. The supervisor owns MCP, session lifecycle, worker process trees,
audit persistence/export, and correlation IDs.

All workers are children of the supervisor and receive an EOF/parent-death
signal. Graceful shutdown asks workers to stop, waits a bounded grace, then
kills every remaining worker process tree. A hard-killed supervisor must not
silently turn a worker into a durable session.

## Public MCP contract

Keep the existing tools and add an optional `session` argument whose default
is `default`:

```text
ptk_invoke(script, route="auto", background=false, timeoutSeconds=0,
           raw=false [deprecated], session="default")
ptk_job(action, id, offset=0, session="default")
ptk_state(listAvailable=false, session="default")
ptk_reset(session="default", expectedGeneration=0, force=false)
```

Add:

```text
ptk_session(action, name, template=null, expectedGeneration=0, force=false)
  action = list | open | close | restart

ptk_output(handle, action="read", offset=0, maxBytes=<bounded>, pattern=null)
  action = read | search | status
```

Rules:

- `default` always exists and preserves unqualified-call behavior.
- Session names are harness-local, nonsecret semantic aliases, canonical
  lowercase, and match `[a-z0-9][a-z0-9._-]{0,63}`.
- `open` is idempotent for the same live name/template digest.
- Every non-default operation names its session. There is no `select`.
- An unknown/closed session never falls back to `default` and never
  auto-creates after a typo.
- `list` never starts a worker.
- Each worker has a random boot ID and a monotonic generation. An optional
  nonzero `expectedGeneration` mismatch refuses before any side effect.
- A compact session/worker/generation/declared-purpose header appears on
  non-default results. Default output remains compatible where practical.
- `ptk_output` reads a stored artifact only. It accepts no script and cannot
  execute or rerun work.

### Optional session templates

Dynamic empty sessions satisfy the basic workflow: the agent opens `ad`,
`exop`, or `exol`, imports/connects once, then addresses that session on each
call. Optional owner-defined templates make bootstrap deterministic without
making templates an authorization boundary.

Load templates once at supervisor start from `~/.ptk/profiles.json`, with
`PTK_PROFILES_PATH` only as a test/operator override. A template contains a
description, a bootstrap script path, startup timeout, declared target and
identity labels, and background mode. Bootstrap bytes and normalized
definition are frozen and SHA-256-digested at catalog load. No inline secret
belongs in this file. Missing configuration means no templates, not a startup
failure.

Editing configuration cannot mutate a live supervisor catalog. Restart the
harness to reload it. Template names and labels are operational metadata;
the effective upstream identity remains authoritative.

## Supervisor/worker protocol

Run the existing executable in two modes:

```text
PtkMcpServer             # MCP supervisor
PtkMcpServer --worker    # one internal session worker
```

Use captured redirected stdin/stdout for versioned newline-delimited JSON.
Worker stderr is pumped to supervisor stderr with its session/boot prefix.
User children inherit EOF/NUL through `ChildStdinGuard`, not the protocol
pipe.

Envelope kinds are `initialize`, `request`, `cancel`, `event`, `response`,
and `shutdown`. Every envelope contains protocol version, worker boot ID,
request ID where applicable, and a bounded payload. Requests carry an
absolute UTC deadline computed at the MCP boundary; worker startup,
bootstrap, queue wait, routing, execution, and shaping consume the same
budget.

Protocol requirements:

- `initialize` is first and sends frozen session/template metadata and
  nonsecret runtime options; no script, secret, or audit credential appears
  in argv.
- One reader task demultiplexes responses/events; one write gate prevents
  interleaved JSON.
- The worker can serve zero-wait `state` while the runspace is busy.
- Cancellation targets one request and is propagated to the active pipeline
  or pre-start job operation.
- EOF cancels work, disposes `SessionRuntime`, kills managed jobs, and exits.
- Unknown versions/methods, malformed frames, excess frame size, and protocol
  output on stdout fail that worker closed.
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
cold | starting | ready | resetting | closing | faulted | lost
```

- Concurrent opens of one cold name share one start task.
- Bootstrap output is suppressed; any PowerShell error, timeout, prompt, or
  state loss faults startup. Successful bootstrap becomes that session's
  drift baseline.
- Unexpected process exit marks `lost`. Ordinary invocation never silently
  starts a fresh context under the same generation.
- `restart` replaces the whole worker process, reruns bootstrap, and
  increments generation.
- `close` terminates the worker tree and leaves the name cold/closed for this
  harness; reopening creates a new generation.
- `ptk_reset` becomes session-local process replacement. Authorization/checks
  occur before job or process termination.
- Busy reset/close with `force=false` returns a no-side-effect busy result.
  `force=true` cancels and terminates the session worker tree.
- Timeout/recycle, reset, close, and worker loss never affect another
  session.
- Supervisor idle exit is forbidden while a worker is starting/resetting, a
  foreground call is active, or a managed job is running.

No worker, alias, boot ID, generation, job ID, or output handle is valid in a
new harness.

## Execution and routing contract

Introduce a structured `ExecutionPlan`:

```text
OriginalScript
Domain                PowerShell | NativeTerminal | MixedDataflow | Bash
ExecutionPath         PowerShellDirect | Rtk | NativeDirect | BashViaRtk
ResolutionContext     Warm | Cold
RequestedRoute
EffectiveRoute
OutputProvenance      PowerShellObjects | DirectText |
                      RtkFiltered | RtkPassthrough
FallbackReason
RtkExecutableIdentity
```

The planner is side-effect-free. It returns metadata and a planned execution;
it never starts the user command. Audit commits the pre-effect dispatch event
before the worker receives permission to execute.

### Automatic routing

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
- A narrow, high-confidence Bash-dialect finding runs the exact original
  script through `bash -lc` when Bash is available, with argument-list
  passing rather than string concatenation. Bash itself is an internal native
  RTK delegation. If Bash is unavailable, that is an actual not-started
  failure; do not pretend PowerShell executed it.
- RTK absent, routing timeout, routing error, or pre-execution resolution
  change falls back to the original once. No fallback occurs after execution
  begins.
- RTK-filtered stdout is never sent to generic `rtk log` a second time.
- Direct PowerShell/native log-shaped final text may use `rtk log`.
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
- A connection-bearing template may select `backgroundMode=session`, which
  runs asynchronously on and occupies the warm worker, or
  `backgroundMode=cold`, which preserves current stateless behavior. It must
  never silently run cold when the declared mode requires warm state.

## Same-invocation output recovery

Add a per-session `OutputStore` distinct from audit and job logs. It stores
the canonical unshaped response from the completed invocation: stdout and
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
- A storage failure never reruns work and never fabricates recoverability.

Foreground PowerShell execution is split from shaping: execute the user
pipeline once, capture its object/stream results, render the recovery snapshot,
then shape those same captured results in a private internal pipeline. Tests
must prove shaping/recovery do not change `$LASTEXITCODE`, `$Error`, cwd,
variables, or module/connection state.

RTK filtering happens before PTK sees the unfiltered bytes. Therefore removal
of preemptive raw execution has a hard external prerequisite: RTK must provide
a machine-readable per-call capture contract that writes the original
stdout/stderr and completion metadata from the same child invocation for
filtered, passthrough, success, and failure cases. Human
`[full output: path]` hints and user-configured tee mode are not sufficient.
This plan does not authorize changes in the adjacent RTK repository; the
integration slice stops and reports the blocker if that contract is absent.

Until the RTK capture contract is available, a command for which PTK promises
recovery must execute directly under PTK capture rather than run through a
lossy, unrecoverable RTK path. It must never execute twice.

### `raw=true` transition

For one compatibility release, accept `raw=true` but do not let it affect
dialect handling, routing, process selection, or whether output is captured.
It returns the normal shaped result plus the same-invocation recovery handle;
it does not return a cheaper immediate bypass. Stop advertising raw as a
recovery instruction. Remove the argument and `RawUsageCounter` in the next
breaking tool-schema revision.

Every current “rerun with raw=true” marker becomes a stable
`ptk_output` handle instruction. That second call reads captured bytes; the
original command has already completed and cannot be degraded by model
reconstruction.

## Mandatory audit contract

Audit begins at the MCP supervisor boundary. The hook is not an audit
producer. The supervisor assigns stable event/call IDs and appends
`call.accepted` before routing or worker startup. Every accepted tool,
including `ptk_state`, `ptk_session`, `ptk_output`, and every `ptk_job`
action, receives a terminal event.

### Pre-effect rule

Before the supervisor sends any operation capable of side effects to a
worker, it durably commits the corresponding intent/dispatch event. This
includes foreground execution, job start, job kill, reset, restart, close,
and forced cancellation. If the required journal cannot commit and flush the
event, the operation does not execute.

There is no model-facing audit-off switch. SIEM network loss does not block
while the durable local spool is healthy and below quota. A missing, corrupt,
full, unflushable, or permission-invalid spool fails PTK calls closed. The
error must state that the original operation was not started; no routing/style
retry text is emitted.

### Event lifecycle

Foreground:

```text
call.accepted
execution.planned
execution.dispatched
execution.completed | failed | canceled | timed_out | outcome_unknown
call.completed | failed | not_started
```

Background:

```text
call.accepted
job.start_requested
job.started
job.completed | killed | start_failed | outcome_unknown
call.completed | failed | not_started
```

Control/lifecycle events include `job.kill_requested`, `reset.requested`,
`session.opened`, `session.close_requested`, `worker.started`,
`worker.exited`, `worker.lost`, `runspace.recycled`,
`audit.export_checkpoint`, `audit.degraded`, and `audit.recovered`.

Queue expiry is `not_started`. A timeout after execution begins is not assumed
stopped; if descendant or remote outcome cannot be proven, use
`outcome_unknown`. A hard writer/worker death leaves an externally detectable
unclosed event, never “still running forever.”

### Versioned event envelope

```text
schema_version, event_id, event_type, occurred_utc, observed_utc
producer: host_id, supervisor_boot_id, worker_boot_id, pid, version,
          binary_digest
sequence, previous_event_hash, event_hash
session: name, generation, declared purpose/target/identity,
         effective identity when verifiable
actor: transport/client metadata and attribution_strength
correlation: call_id, job_id, parent_event_id, trace_id
request: tool/action, cwd, timeout, background, original_script_digest,
         exact-script evidence reference
routing: domain, requested/effective route, RTK identity, provenance,
         fallback reason
outcome: state, exit_code, duration, queue time, warm_state_lost,
         worker_replaced, termination_certainty
coverage: ptk_request, root_process_observed, descendants_observed,
          remote_effect_observed
```

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

Audit/export credentials remain in the supervisor and are removed from worker
environment and protocol messages. Direct in-process export is acceptable for
personal telemetry but must not be described as resistant to a hostile
same-user process.

SIEM alerts should cover sequence gaps, missing supervisor heartbeats,
unclosed execution/job events, stalled exporter checkpoints, worker death,
and prolonged local-spool backlog.

## Jobs and process containment

Jobs are keyed by session, worker boot ID/generation, and job ID. A job in one
session is not visible or controllable through another. `ptk_job list` is
session-local; `ptk_session list` shows only counts.

`JobManager` emits completion independently of polling and exactly once.
Status/output/kill/list are all audited. Output reads record offsets and byte
counts, not returned content. Reset/close/shutdown kill only the target
session's jobs.

The supervisor must own worker process groups on Unix and Windows Job Objects
on Windows, or an equivalently proven tree-kill mechanism. Whole-worker
replacement is the reset/timeout containment primitive for connection-bearing
sessions. Detached processes, scheduled tasks, services, WMI, SSH, and remote
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

Tools/
  SessionTool.cs
  OutputTool.cs
```

Existing ownership changes:

- `Program.cs` selects supervisor/worker mode, loads audit/template options,
  and registers only supervisor services in MCP mode.
- `InvokeTool`, `JobTool`, `StateTool`, and `ResetTool` become thin session
  routers and begin/finish supervisor audit scopes.
- `RunspaceHost` accepts absolute deadlines and structured execution/output
  contexts; it no longer owns string-only route inference.
- `JobManager` stores correlation, route/provenance, session generation,
  output handle, and a polling-independent terminal continuation.
- `IdleWatchdog` observes `SessionManager` aggregate activity/live work.
- `PwshTokenCompressor.psm1` retains PowerShell-object and log-text shaping,
  but receives provenance so RTK-filtered output is not shaped twice and all
  rerun-with-raw wording is removed.
- `RawUsageCounter.cs` is deleted only when the compatibility argument is
  removed.

## Implementation slices

Each slice is one coherent commit. Any new guard test must be proven red by
temporarily sabotaging/reverting the production behavior, then restored green.

### Slice 0 — freeze external and platform contracts (no product code)

- Probe and record the RTK machine-readable raw-capture seam. If absent,
  record the exact upstream requirement and stop the recovery-dependent slice;
  do not fake recovery or authorize cross-repo changes.
- Freeze OTLP collector test endpoint/auth behavior and the JSONL schema
  version.
- Probe Windows Job Object and Unix process-group teardown strategy in small
  disposable children.
- Freeze session/profile JSON schema and supervisor/worker frame limits.
- Reconcile this plan with the still-relevant rrp regression cases; do not
  copy the old plan's unsafe broad compound-routing claim.

### Slice 1 — mandatory audit foundation on the current server

- Add schema, journal, required pre-effect commits, health, rotation,
  retention, sequence/hash chain, server lifecycle, and in-memory test sink.
- Audit every current tool boundary and all no-start/terminal outcomes.
- `ptk_state` reports audit health without exposing credentials.
- Guard: forced journal failure produces no foreground/background/reset/kill
  side effect.

### Slice 2 — complete jobs/control audit and SIEM export

- Add asynchronous job terminal events, kill reasons, reset partial-effect
  events, hard-death/unclosed semantics, and audit retrieval access events.
- Add at-least-once OTLP exporter, durable checkpoints, retry/backlog health,
  duplicate-safe IDs, and a fake collector integration suite.
- Add protected evidence payload handling for exact submitted scripts.
- Document collector/SIEM deployment without claiming local hash-chain
  immutability.

### Slice 3 — structured routing with no model retry

- Replace string-only resolution with `ExecutionPlan` and provenance.
- Route safe terminal native commands through RTK; keep mixed/dataflow and
  fidelity exclusions exact and single-execution.
- Internally run high-confidence Bash syntax through Bash when present.
- Preserve current cwd, pinned RTK path, remaining deadline, live resolution,
  exit code, streams, and preference state.
- Remove double `rtk log` shaping.
- Mixed-domain suggestion follows success; it never refuses or rewrites.

### Slice 4 — same-invocation output recovery and raw retirement

- Land `OutputStore`/`ptk_output` and two-stage foreground capture/shaping.
- Integrate the verified RTK raw-capture seam; otherwise use direct
  single-execution capture for recoverability.
- Replace raw rerun markers with opaque handle instructions.
- Make legacy `raw=true` non-routing and non-bypass behavior; keep it only for
  the announced compatibility interval.
- Prove recovery never changes state or re-executes.

### Slice 5 — foreground/background routing and recovery parity

- Compute cold-context execution plans before job start.
- Persist route/provenance/output handles with job metadata.
- Add RTK capture for safe terminal native jobs and exact cold PowerShell for
  mixed jobs.
- Add warm asynchronous session-task mode for connection-bearing templates;
  never silently substitute a cold job.
- Make output polling provenance-aware and stop exposing filesystem paths as
  the model recovery interface.

### Slice 6 — extract `SessionRuntime` without behavior change

- Move current invoke/job/state/reset formatting and caches behind one runtime
  object.
- Retarget tool-body tests to the runtime; keep MCP adapters thin.
- Preserve default-session state, error, timeout, routing, output, and job
  behavior established by slices 1-5.

### Slice 7 — worker mode and default-session supervisor

- Add versioned protocol, worker launch, cancellation, deadlines, stderr pump,
  EOF/parent-death cleanup, and process-tree ownership.
- Route only the reserved default session through one worker initially.
- Move the authoritative audit writer to the supervisor and use pre-effect
  dispatch before worker commit.
- Preserve the existing MCP handshake/tool names and default outputs.

### Slice 8 — named harness-scoped sessions

- Add dynamic semantic session aliases, optional frozen templates, lifecycle
  state machine, `ptk_session`, and explicit session arguments on all tools.
- Prove isolation of variables, aliases, cwd, environment, modules, auth
  process, caches, jobs, reset, timeout, and concurrency.
- Unknown/lost/faulted sessions fail visibly and never masquerade as a fresh
  context.

### Slice 9 — reset/close/teardown hardening

- Make reset/restart whole-worker, target-local, generation-checked, and
  pre-effect audited.
- Add bounded graceful shutdown plus proven tree kill.
- Aggregate idle activity/live-work semantics in the supervisor.
- Prove ordinary MCP EOF removes every worker and managed job; classify hard
  death/outcome uncertainty honestly.

### Slice 10 — contract reconciliation and live Windows validation

- Update tool schema/descriptions, hook guidance, installer-preserved
  template/audit config, both READMEs, and handshake fixtures.
- Mark the incompatible implementation directions in
  `security-layer.md`, `rtk-rewrite-routing.md`, and
  `shared-persistent-runspace.md` superseded/parked with a pointer here.
- Reconcile `.agents/decisions.md` only after the owner releases its hold.
- Run owner-controlled AD, on-prem Exchange, and EXO proof: distinct worker
  PIDs/identities/targets, warm reuse, independent reset, job behavior, and
  harness teardown.
- Run a protected collector/SIEM smoke test and demonstrate sequence/gap and
  unclosed-event alerts.

## Acceptance matrix

### Audit completeness and failure

- Every MCP tool/action produces accepted and terminal records.
- Foreground success, PowerShell errors, native stderr, queue expiry,
  cancellation, routing fallback, timeout, recycle, and unknown outcome.
- Background preflight failure, start, completion without polling, output
  reads, status/list, explicit kill, reset kill, shutdown kill, and hard
  worker death.
- Required-journal failure before foreground, job start, reset, close, and
  kill proves zero side effects.
- Concurrent events produce no torn records, duplicate sequence, or broken
  chain.
- Rotation/retention bound a long-lived supervisor; a live file is not swept.
- Export retry/checkpoint and duplicate delivery; network loss proceeds only
  while spool remains healthy.
- Exact script evidence is retrievable only from the protected evidence
  stream; fixture tokens/output never enter core events.

### Single-execution routing

- Known RTK win, unsupported RTK passthrough, RTK absent, rewrite/capture
  failure, and near-expired deadline all execute at most once.
- Warm alias/function and preceding/ambient resolution changes preserve
  PowerShell semantics.
- Mixed pipeline, assignment, parsing/counting, and redirection execute the
  original once with no refusal/retry text.
- `git diff | Set-Content patch.txt` writes an applyable unfiltered patch;
  `git diff > patch.txt` is accepted without prefiltering file bytes.
- RTK-filtered stdout is not passed through `rtk log` again.
- Bash-only fixture executes exact bytes through Bash when present and has a
  truthful not-started result when absent.
- Foreground/background effective routes match their respective warm/cold
  resolution contexts.

### Output recovery

- PowerShell objects, bounded text, log shaping, RTK filtering, stderr,
  warnings/errors, and background streams recover from the same invocation.
- A persistent counter/file sentinel proves `ptk_output` never reruns.
- Retrieval after underlying files/state change returns the captured snapshot.
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
- A barrier test, not timing alone, proves different sessions can progress
  concurrently while one session remains serial.
- Concurrent open starts one worker; list/state on cold does not start it.
- Bootstrap runs once, faults visibly, and becomes the drift baseline.
- Reset/restart increments generation and affects only the named session.
- Stale generation and non-force busy close/reset have zero side effects.
- Worker loss fails pending calls once and requires explicit restart.
- Supervisor EOF/shutdown leaves no managed workers/jobs.

### Compatibility and live verification

- Existing Pester suite, .NET suite, and MCP handshake remain green after
  every code slice.
- New tests receive red-leg guard proof per repo guidance.
- Default tool schemas remain compatible through the declared raw transition.
- Real MCP stdio tests cover audit IDs, RTK path, output handle, two sessions,
  independent jobs, process teardown, and malformed worker protocol.
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

This plan becomes the canonical implementation contract only after explicit
owner approval following reviewloop. Until then:

- No code is authorized.
- `security-layer.md` remains the record of the rejected policy-gate
  exploration.
- `rtk-rewrite-routing.md` remains a reviewed draft whose regression evidence
  is reused here, not approved implementation.
- `shared-persistent-runspace.md` remains an unapproved idea and must not be
  implemented.
- The decisions-log hold remains in force.
