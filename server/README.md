# PtkMcpServer

`PtkMcpServer` is a stdio MCP server that owns one long-lived PowerShell
runspace plus a serialized foreground routing gate. PowerShell and mixed
dataflow calls use that runspace, so variables, imported modules, and
established connections survive across those calls. Independently proven
parse-fatal Bash syntax uses bounded startup-pinned Bash/RTK child processes;
its shell state is process-local and does not enter the PowerShell session.

During runspace priming the server freezes the compressor source in memory,
captures its shaping command, and detaches the module from the user-visible
session. Routing and dialect preflight execute as C# parser logic over
data-only command facts captured through CLR APIs; no PowerShell command or
scriptblock runs before dispatch authorization. User scripts therefore cannot
replace preflight through shadowing, debugger hooks, type data, or later module
file edits. If the module cannot be found or loaded, calls fall back to plain
`Out-String` output and the server writes the problem to stderr.

## Prerequisites

- .NET SDK 10.x (`dotnet --list-sdks`)
- Network access on first build for NuGet restore
- PowerShell 7.x (`pwsh`) for the handshake script and hook installer
- An MCP client that can launch stdio servers, such as Claude Code

The server itself hosts PowerShell through the `Microsoft.PowerShell.SDK`
package pinned in `server/PtkMcpServer/PtkMcpServer.csproj`.

## Setup

Verify the server before registering it broadly:

```powershell
dotnet test server/PtkMcpServer.slnx
pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90
```

The handshake starts the server through the same `dotnet run` command used by
the MCP registration and must end with `HANDSHAKE PASSED`. The explicit
`-TimeoutSec 90` gives cold build/startup work room to finish.

A checkout has no project-scope registration (the committed `.mcp.json` is
deliberately empty). Install and register user-wide with
`pwsh -File scripts/dev-install.ps1` (builds a self-contained binary into
`~/.ptk` and registers it), or register the checkout directly:

```powershell
claude mcp add ptk --scope user -- dotnet run -v q --project <path-to-repo>/server/PtkMcpServer
```

Check with `claude mcp list`; remove with `claude mcp remove ptk`.

## Tools

| Tool | Arguments | Purpose |
| --- | --- | --- |
| `ptk_invoke` | `script`, optional `raw`, `route`, `background`, `timeoutSeconds` | Run shell work through PTK: persistent PowerShell execution, eligible terminal-native RTK routing, or bounded validated Bash delegation; `background: true` starts a separate cold child whose plan selects direct PowerShell or eligible RTK routing, and `timeoutSeconds` overrides the capped per-call timeout. The legacy `raw` flag is deprecated and inert except for compatibility telemetry. |
| `ptk_job` | `action` (`status`/`output`/`kill`/`list`), `id`, `offset` | Manage background jobs: `output` returns new output since `offset`, shaped and bounded, ending with the next offset to pass. After a direct job finalizes, status/output report a stable opaque `ptk_output` handle only when its immutable recovery artifact sealed successfully. Capture/storage failure and seam-absent RTK jobs report recovery unavailable; this does not guarantee that the distinct internal polling spool remains readable. Internal spool paths are never exposed. |
| `ptk_output` | `handle`, `action` (`read`/`search`/`status`), `offset`, `maxBytes`, optional `pattern` | Read, search, or inspect an immutable same-invocation artifact named by `ptk_invoke` or `ptk_job`. It never starts a session or worker and never executes or reruns a command; handles may be incomplete, expired, evicted, or unavailable. |
| `ptk_state` | optional `listAvailable` | Session introspection and health check: engine, server PID/uptime, cwd, loaded modules, and drift — env vars changed since server start, PATH as an entry diff, variable count. With `listAvailable: true`, also enumerate installed modules once and cache the result. Never queues: while another call holds the runspace it answers promptly with host-level facts plus a busy line (active-call age, waiter count), marking runspace-dependent details unavailable. |
| `ptk_reset` | none | Recycle the runspace to factory state: discards variables, loaded modules, current directory, default parameters, and connections, and restores environment variables to their server-start values. |

## Mandatory local audit

Audit is owned by the MCP supervisor and cannot be disabled by a tool argument
or user script. The default is local-only protected storage under
`~/.ptk/audit`; no SIEM, collector, endpoint, or credentials are required.
Core JSONL events live under `spool/`. Exact submitted script bytes live as
separate owner-only evidence files under `evidence/`; core events contain only
their opaque ID and SHA-256 digest, not script text.
The current writer emits strict `ptk.audit/2` records. Recovery and export also
accept retained byte-exact `ptk.audit/1` records; a chain may contain both
versions. Exact scripts can contain credentials, tokens, or other secrets, so
treat the audit root and every backup or copied evidence file as sensitive.

Admission is fail-closed. PTK durably stores script evidence, reserves the
worst-case terminal capacity, and flushes the accepted/dispatch records before
user work, reset, or job control can begin. Foreground calls and spawned jobs
retain terminal obligations, and graceful shutdown drains them before writing
`server.stopped`. If protected audit storage is unavailable, ordinary tools do
not run. `ptk_state` alone may return the minimal supervisor-only
`audit=unavailable, unrecorded=true` diagnostic; it does not inspect the
runspace or jobs in that mode.

The same ordering covers internal RTK helpers. A foreground
`execution.dispatched` record or a job `job.output_accessed` record identifies
and authorizes the startup-pinned RTK digest with `rtk_log_authorized` before
`rtk log` may start. `output.shaped` or `output.shaping_failed` then records the
typed result; a physical post-start journal failure still leaves the durable
authorization and degraded audit chain rather than an invisible helper call.

The executable currently freezes these storage bounds at startup; there are no
environment-variable overrides for them:

| Store | Default age threshold | Default aggregate limit | Per-item limit |
| --- | --- | --- | --- |
| Core journal | 30 days | 256 MiB, including a 4 MiB terminal-event reserve | 64 KiB per JSONL record; 16 MiB segments |
| Exact-script evidence | 30 days | 256 MiB | 128 KiB per script |

Local-only evidence becomes ordinarily retention-eligible only after its
referencing audit append is durable. In anchored mode, each referenced
evidence object becomes eligible only after its exact core event acknowledgment
is durably checkpointed; acknowledged closed spool prefixes separately become
eligible for chain retirement. Unacknowledged segments and evidence remain
pinned. Completed chains retire through a crash-recoverable durable deletion
intent instead of leaving checkpoint controls forever.

Every automatic evidence deletion is itself journal-bound: PTK flushes an
`evidence.retention_intent` containing the exact subject ID, digest, byte count,
state, and reason before unlink, then records `evidence.retention_completed` or
`evidence.retention_failed`. If terminal truth cannot be proved, the failure is
`outcome_unknown`. PTK fails new script-bearing admission rather than deleting
evidence without the complete audit reservation or when retention status is
ambiguous.

`PTK_AUDIT_ROOT` may select a different absolute operator-controlled root at
process startup. With `PTK_AUDIT_EXPORT_CONFIG` absent, the executable remains
local-only and needs no SIEM. Supplying that variable opts into strict anchored
mode: the protected configuration, HTTPS endpoint, authentication material,
and export runtime must all initialize before PTK serves tools. An empty,
malformed, incomplete, or unprotected configuration fails startup instead of
falling back to local-only.

Anchored mode sends core audit events as
[OTLP/HTTP protobuf logs](https://opentelemetry.io/docs/specs/otel/protocol/exporter/)
with at-least-once delivery. Retries preserve `ptk.audit.event_id` and
`ptk.audit.event_hash`, so a receiver must tolerate or deduplicate identical
duplicates. Exact submitted script bytes remain only in the protected local
evidence store; they are not sent in OTLP records. Configuration, receiver
durability requirements, and SIEM adapter patterns are in
[Anchored audit export](AUDIT-EXPORT.md).

The separate `PtkAuditAdmin` executable provides audited evidence read/export
and exact permanent-block disposition without adding a model-facing MCP tool.
It is still callable by any process that the OS permits to execute it; use an
external operator/OS boundary when the model-controlled account must not have
that capability. Commands and proof semantics are documented in
[Anchored audit export](AUDIT-EXPORT.md#out-of-band-audit-administration).

`ptk_invoke` returns command output, then labeled sections when present, in
this order: `[exit] N`, `[stderr]`, `[errors]`, and `[warnings]`. Empty
output returns `(no output)`. `[stderr]` is neutral, not a failure signal:
native tools routinely write progress and diagnostics to stderr while
succeeding (an exit-0 test run, for example), so native stderr is reported
under its own label. `[errors]` is reserved for genuine PowerShell error
records (`Write-Error`, exceptions, terminating errors).

## `ptk_invoke` Behavior

By default, `ptk_invoke` executes with `route=auto` and `raw=false`.

Routing rewrites eligible native commands through
[rtk](https://github.com/rtk-ai/rtk), an external CLI whose per-command
filters compress the output of common tools (`git`, `npm`, `docker`, ...) at
the source and pass through commands it does not recognize. The rewrite
(`<cmd>` becomes `& '<rtk>' <cmd>`) executes inside the warm runspace, so the
runspace's current directory and environment still apply.

Routing rules:

- A script that is exactly one bare native application command with constant
  arguments, such as `git status --short`, is rewritten through `rtk` when
  `rtk` is available.
- `rtk` itself is not double-routed.
- Cmdlets, aliases, functions, pipelines, chains, variables, expandable
  strings, redirections, mixed dataflow, and `.cmd`/`.bat` shims stay on the
  exact PowerShell path. RTK routing never prefilters bytes flowing into a
  PowerShell consumer or redirection sink.
- PTK freezes RTK's canonical path, bounded SHA-256 identity, and Unix mode at
  server startup. Warm-session `PATH`/`PTK_RTK_PATH` changes cannot substitute
  a different binary. Identity or availability loss before a routed process
  starts takes the already-audited exact-original fallback once; PTK never
  asks the model to reconstruct the command and never retries after start.
- Automatic Bash delegation requires all three independent facts: PowerShell
  parse-fatal input, detector evidence for a specific Bash construct outside
  comments/strings, and a successful post-dispatch
  `bash --noprofile --norc -n -c <exact-script>` syntax check. Only then does
  PTK execute the exact bytes once via startup-pinned RTK and
  `bash --noprofile --norc -c`. Both direct process environments remove Bash
  startup/function/option injection and platform loader-injection variables.
- Missing/drifted Bash or RTK, invalid syntax, validator timeout, audit loss,
  or exhausted call budget returns a labeled not-started result without
  running the submitted script or requesting a retry. Validator start/outcome
  and root-termination certainty are typed audit facts; descendant coverage
  remains explicitly unknown.
- A clean-parsing detector finding retains the fast `[ptk:dialect]` refusal.
  `route=pwsh` bypasses the detector/delegation path as explicit PowerShell
  consent; normal capture and shaping still apply. The deprecated `raw=true`
  flag is inert compatibility telemetry and does not affect dialect handling,
  interpreter, routing, process choice, capture, or shaping.
- High-confidence mixed file capture remains advisory: the exact original
  `<native application> | Set-Content <constant non-wildcard path>` pipeline
  runs first in PowerShell. Only the exact built-in
  `Microsoft.PowerShell.Management` `Set-Content` implementation in a
  filesystem location is eligible, and only after it completes without
  PowerShell errors may PTK
  append `[ptk:routing]` with the simpler direct-capture style
  `<native application> > <path>` for next time. PTK never rewrites or reruns
  the command, never refuses to teach style, and emits no suggestion for
  dynamic or provider-qualified paths, extra sink semantics, shadowed
  commands, multiline shapes, existing redirection, ambient WhatIf/Confirm or
  default-parameter overrides, or failed pipelines.

Output shaping:

- Object output compresses with `Compress-PtcObject`.
- Plain strings and primitive scalars pass through with ANSI/control
  sequences stripped, otherwise unaltered; pathologically large text is
  elided to a labeled head+tail window. When PTK successfully seals a
  same-invocation snapshot, the response names an opaque `ptk_output` handle
  that can recover the elided middle without rerunning the command; otherwise
  it explicitly reports recovery unavailable.
- Log-shaped text routes through `rtk log` when possible.
- Log-shaped text falls back to labeled raw text if `rtk` is absent or fails.
- The host passes the exact startup-frozen identity into shaping, bounds the
  rehash to a regular nonsymlink file of at most 128 MiB, checks Unix mode
  drift, and validates the returned routing envelope against the authorized
  digest. Foreground and job-output RTK use is auditable even on exceptional
  or timed-out shaping paths.
- Delegated Bash/RTK stdout and stderr are each captured to a 4 MiB response
  bound while the pipes continue draining. Truncation is labeled and never
  causes re-execution.
- Nonzero native exit codes are reported as `[exit] N`.

Overrides:

- `raw=true` is accepted only as deprecated compatibility telemetry. It does
  not change dialect handling, interpreter, routing, process choice, capture,
  or shaping.
- `route=pwsh`, independently of `raw`, is explicit consent to interpret the
  exact original text as PowerShell; normal capture and shaping still apply.
- `route=rtk` asserts the `rtk` rewrite only for the safe single-application
  shape. Ineligible or unavailable routing is labeled and executes the exact
  original once through PowerShell.
- When a response supplies a `ptk_output` handle, use it to read the immutable
  same-invocation artifact. `ptk_output` never executes or reruns the command.

Long-running work (two paths, by workload):

- `background=true` starts the script as a **separate cold child process** and
  returns a job id immediately. The cold plan selects direct PowerShell or
  eligible RTK routing. The job does not see warm session state; it
  starts in the session's current directory and writes output to an internal
  supervisor spool. Poll with `ptk_job action=output` (pass the returned next
  offset each time); output polls are shaped and bounded like foreground
  output. Once a direct job terminates and capture succeeds, `ptk_job` reports
  a stable `ptk_output` handle for the immutable same-invocation snapshot;
  otherwise it reports recovery unavailable without rerunning. Recovery
  sealing and live polling use distinct storage, so seal failure does not
  guarantee that the polling spool remains readable. The internal spool path
  is never model-facing. Use this for builds, watchers, deploys — stateless
  work that could exceed the call timeout.
- `timeoutSeconds` raises the per-call timeout (capped by
  `PTK_MAX_CALL_TIMEOUT_SECONDS`) for long work that **needs** the warm
  session — live connections, imported modules. A background job would
  forfeit exactly that state.
- A timed-out PowerShell pipeline recycles the runspace and loses its warm
  state. A timed-out delegated Bash/RTK call instead attempts bounded tracked
  root/process-tree termination, preserves the warm runspace, reports
  descendants and remote effects as unknown where PTK cannot prove them, and
  never retries the script.
- Background jobs are killed by `ptk_reset` and at graceful server shutdown.
  A hard-killed server can leave a running job orphaned (it finishes on its
  own); job logs older than seven days are swept at server start.

## Claude Code Hook

`scripts/ptk_init.ps1` installs a Claude Code `PreToolUse` hook that redirects
ordinary Bash and PowerShell tool calls toward the `ptk_invoke` MCP tool using
a deny-with-guidance response. The guidance names the tool without a harness
prefix — the same tool carries a different id per harness
([docs/harness-support.md](../docs/harness-support.md)).

The script is the multi-harness init surface (`-Agent claude|codex|grok|agy|all`,
defaulting to the agents detected on the machine). All four legs are
implemented: claude (hook + guidance block), codex (idempotent `codex mcp
add` + `~/.codex/AGENTS.md` block; no hook — trust-gated), grok
(`grok mcp add -s user` behind a config-presence short-circuit; its
guidance home is `~/.claude/CLAUDE.md`, which grok session-loads), and agy
(a user-level plugin directory carrying registration + rules; no hook —
enforcement is deferred until a live install run demonstrates agy's
documented deny surface). `dev-install.ps1` chains this script by default:
one command per machine produces the whole state.

```powershell
pwsh -File scripts/ptk_init.ps1              # user-level install (default)
pwsh -File scripts/ptk_init.ps1 -Show        # inspect per-leg status
pwsh -File scripts/ptk_init.ps1 -DryRun
pwsh -File scripts/ptk_init.ps1 -Uninstall   # hook out, nudge block out
pwsh -File scripts/ptk_init.ps1 -Local       # per-repo opt-in (warns, see below)
```

A bare install ships every layer the leg supports — for claude the hook
AND the `~/.claude/CLAUDE.md` guidance block (also grok's nudge home); for
codex the registration and the `~/.codex/AGENTS.md` block. There is no
opt-in flag for the guidance block: it is idempotent, marker-owned,
conditionally worded, and removed by `-Uninstall`.

Installs are **user-level by default** (`~/.claude/settings.json`; the old
`-Global` switch is accepted and means the same thing). `-Local` is the
explicit per-repo opt-in: it edits the repo's `.claude/settings.json`, and
any tooling that tracks that file by content — governance refresh
mechanisms, dotfile managers — will treat the repo as owner-modified from
then on; the installer warns about this. `-Show`/`-DryRun`/`-Uninstall`
operate on the same target the install form would.

The installer refuses to install the hook while no installed payload exists
at `~/.ptk` (run `scripts/dev-install.ps1` first): a redirect hook without a
server would deny every shell call while steering at a tool that cannot
answer. It preserves unrelated hooks and replaces only the ptk-owned entry
when re-run. The hook takes effect at the next Claude Code session start.

Failure semantics, precisely: the hook fails open only against its OWN
failure — if the hook script is missing or errors, harness shell calls
proceed normally. A down server does not fail open: shell calls are still
denied — but the hook checks for a running server process, and when none
exists the deny guidance says so and points at `PTK_DIRECT` up front
(liveness shapes the wording only, never the decision). `PTK_DIRECT` is the
way through until the server is back (`/mcp` reconnect respawns it).

The missing-script fail-open is exactly what a **stale registration**
produces: an entry written from a checkout that later moved fails open
silently on every shell call. The installer registers the installed copy
(`~/.ptk/scripts/ptk-hook.ps1`) to make that class structurally rare, and
`ptk_init.ps1 -Show` flags a registered target that no longer exists. Two
heal paths: re-running `ptk_init.ps1`, or a `dev-install.ps1` install —
the latter refreshes an existing hook entry only when it also registered
the server with Claude Code (no claude CLI → no refresh; run
`ptk_init.ps1 -Agent claude` yourself after registering manually).

A command containing `PTK_DIRECT` bypasses the hook. Use that for work that
genuinely needs the harness shell, such as interactive or TTY-dependent tools,
or when the ptk MCP server is unavailable.

## Configuration

Set these in the MCP registration `env` block when defaults do not fit:

| Variable | Default | Meaning |
| --- | --- | --- |
| `PTK_CALL_TIMEOUT_SECONDS` | `300` | Default per-call limit: a total wall-clock budget covering queue wait plus execution. A call whose budget expires while still queued behind another call fails fast without executing (warm state intact); a call that overruns while executing fails with the runspace recycled. |
| `PTK_MAX_CALL_TIMEOUT_SECONDS` | `3600` | Cap on the per-call `timeoutSeconds` override. |
| `PTK_IDLE_EXIT_SECONDS` | `14400` | Idle self-exit backstop for orphaned servers, in seconds. |
| `PTK_AUDIT_ROOT` | `~/.ptk/audit` | Absolute protected root for mandatory local audit JSONL and exact-script evidence. Local logging requires no SIEM configuration. |
| `PTK_AUDIT_EXPORT_CONFIG` | unset | Absolute path to a protected `ptk.export-config/2` JSON file. Unset selects local-only mode; presence, including an empty value, requests strict anchored mode and makes incomplete or invalid configuration a startup failure. |
| `PTK_MODULE_PATH` | auto-discovered `src/PwshTokenCompressor.psd1` | Explicit module manifest to import into the runspace. If set to a missing file, shaping is disabled. |
| `PTK_RTK_PATH` | `rtk` on `PATH` | Explicit `rtk` binary for native routing and log shaping. If set to a missing file, `rtk` is treated as absent. |

## Operational Notes

- Calls are serialized; one runspace runs one pipeline at a time. The
  per-call timeout is a wall-clock budget over the whole request — queue
  wait included — and deadlines are re-checked when timers fire, so a
  machine that sleeps mid-call times the call out promptly on wake instead
  of silently extending it.
- `useLocalScope: false` is intentional, so assignments and imported modules
  persist into later calls.
- `ptk_reset` and execution/preflight timeouts create a fresh primed
  runspace: warm state is lost, but later calls continue working. A queue
  expiry is neither — the call never ran and warm state survives.
- Caller cancellation tries to stop the pipeline and preserve the runspace. If
  the pipeline does not stop within the grace period, the runspace is recycled.
- Child native processes inherit EOF for stdin instead of the MCP JSON-RPC pipe,
  so stdin-reading commands do not hang forever waiting on the transport.
- No interactive prompts can be answered inside the server. Use unattended auth
  patterns for connection-bearing modules, or run those commands outside the
  server.

## Security Posture

The server is not a security boundary. `ptk_invoke` runs arbitrary PowerShell
with the same authority as the MCP client process. A destructive-cmdlet policy
gate is intentionally not implemented in the current code; review scripts at
the client permission prompt instead of blanket-allowing the tool. Mandatory
audit adds durable attribution, ordering, capacity guarantees, and tamper
evidence; it does not grant or remove PowerShell/OS permissions. Run the
harness under the restricted identity whose upstream RBAC is meant to govern
the work. Local-only files are protected from other identities but are not
claimed immutable against the same account, and their hash chain does not make
them immutable to that account. Anchored export improves that boundary only
after a separately administered receiver has durably acknowledged the event;
an in-memory proxy or a receiver controlled by the harness identity does not.
See [Anchored audit export](AUDIT-EXPORT.md) for the required trust boundary.
