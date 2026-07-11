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

All workers are children of the supervisor and enter containment atomically at
creation, before any worker instruction or runspace/bootstrap can run. On
supported Windows, the supervisor first creates/configures the Job Object,
then uses `CreateProcessW`/`STARTUPINFOEX` with
`PROC_THREAD_ATTRIBUTE_JOB_LIST` so the worker belongs to the
`KILL_ON_JOB_CLOSE` object from its first runnable instruction; the sole Job
Object handle is noninherited and supervisor-owned. A platform without that
creation-time primitive fails worker startup rather than falling back to the
spawn-then-assign race.

On Unix, a tiny containment broker/reaper is started first with the
supervisor-only liveness pipe. It, not the supervisor, forks the worker behind
a closed start gate, places it in a dedicated process group, and releases the
gate only after liveness and group ownership are armed. EOF before release
kills/abandons the gated child; EOF later sends the group bounded TERM then
KILL and the broker exits. The supervisor waits for a containment-armed
acknowledgment before sending `initialize`. The ordinary protocol EOF watcher
remains a graceful fast path, not the hard-death proof.
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
ptk_reset(session="default", expectedGeneration=0, force=false)
```

Add:

```text
ptk_session(action, name=null, template=null, allowColdBackground=null,
            expectedGeneration=0, force=false)
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
  only open-time binding fields; `close` and `restart` branches require `name`
  and permit only their generation/force fields. The adapter validates the
  original JSON property-presence set before binding defaults, so omitted is
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

Run the existing executable in two modes:

```text
PtkMcpServer             # MCP supervisor
PtkMcpServer --worker    # one internal session worker
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
PreExecutionValidation None | BashSyntax
ResolutionContext     Warm | Cold
RequestedRoute
EffectiveRoute
OutputProvenance      PowerShellObjects | DirectText |
                      RtkFiltered | RtkPassthrough
FallbackReason
RtkExecutableIdentity
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
  `bash -lc` as the internal RTK delegation. Missing Bash, failed/timed-out
  validation, or expired remaining execution budget produces the existing
  labeled not-started terminal; the submitted script never starts. Record
  validator start/completion and identity as internal lifecycle facts under
  the already-audited plan. A clean-parsing dialect finding never reaches the
  validator. `route=pwsh` bypasses this path as explicit PowerShell consent.
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

RTK filtering happens before PTK sees the unfiltered bytes. Therefore raw
recovery for an RTK-filtered call has a hard external prerequisite: RTK must
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

### Event lifecycle

Foreground:

```text
call.accepted
execution.planned
execution.dispatched
execution.validation_started | execution.validation_completed  # when planned
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

The exporter checkpoint is atomic sidecar state containing spool file
identity, byte offset, sequence, and acknowledged event ID. It is not a core
audit event, is never exported, and does not participate in the event hash
chain. Acknowledging an event updates only that sidecar. Core events are
emitted only on exporter lifecycle transitions or hysteresis-bounded backlog
threshold crossings, not on each acknowledgment, so an idle fully
acknowledged exporter drains and becomes quiescent rather than recursively
creating checkpoint traffic.

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
  but receives provenance so RTK-filtered output is not shaped twice and all
  rerun-with-raw wording is removed.
- `RawUsageCounter.cs` is deleted only when the compatibility argument is
  removed.

## Implementation slices

Each slice is one coherent commit. Any new guard test must be proven red by
temporarily sabotaging/reverting the production behavior, then restored green.

### Slice 0 — freeze external and platform contracts (no product code)

- Probe and record the RTK machine-readable raw-capture seam. If absent,
  record the exact upstream requirement and freeze the seam-absent contract:
  RTK routing stays active, RTK-filtered calls advertise no recovery handle,
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
- Add evidence export/access auditing and retention integration without
  changing slice 1's pre-effect ordering.
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
  fixture now asserts exact-byte `bash -lc` execution when Bash is present and
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
  the explicit recovery-unavailable result for RTK-filtered calls without
  changing their route.
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
- Add RTK capture for safe terminal native jobs and exact cold PowerShell for
  mixed jobs.
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
- Bound every post-launch startup timeout/cancel by the request deadline plus
  `timeoutContainmentGrace`: confirmed death publishes `faulted`; grace expiry
  returns startup `containment_unconfirmed`, publishes `quarantined`, and
  forbids open/restart/next generation while observation continues. Only later
  confirmed death may transition to `faulted` and permit explicit restart.
- Route only the reserved default session through one worker initially.
- Move the authoritative audit writer to the supervisor and use pre-effect
  dispatch before worker commit.
- Preserve the existing MCP handshake/tool names and default outputs.

### Slice 8 — named harness-scoped sessions

- Add dynamic semantic session aliases, optional frozen templates, lifecycle
  state machine/gate/operation leases, `ptk_session`, and explicit session
  arguments on all tools.
- Register `ptk_session` with the explicit action-conditional `oneOf` schema
  and validate raw field presence before typed/default binding.
- Move public job-ID allocation to the supervisor's nonreusing 64-bit
  sequence; worker-local IDs never cross MCP.
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
- At anchored high water, flood `ptk_state`, session/job list/status/output,
  and `ptk_output` calls. All except the minimal unrecorded health diagnostic
  are rejected before acceptance and consume zero reserve bytes; a previously
  started job still appends its terminal event to the preallocated reserve.
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
- With the RTK capture seam present, an RTK-filtered call returns a working
  handle for its pre-filter stdout/stderr.
- With the seam absent, the same call remains RTK-routed, returns no handle,
  and reports `recovery=unavailable`; PowerShell/direct captures in the same
  server still return working handles.
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
