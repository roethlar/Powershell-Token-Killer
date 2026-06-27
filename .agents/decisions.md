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

No durable decisions recorded yet.

## Open Decisions (deferred - not yet adopted)

### OPEN (2026-06-27): Whether to build the "universal PowerShell wrapper" rearchitecture

**Status:** Open - deferred by owner to decide later. No code change authorized. This
entry records the design exploration so a future session can resume without
re-deriving it.

**Question:** Should ptk grow a universal pass-through path - `ptk <any cmdlet ...>`
runs the command and compresses whatever it returns - replacing the current
fixed-verb dispatch? And if so, in what form?

**What triggered it:** `ptk Get-ChildItem` prints the help screen instead of running,
because the dispatch is a `switch -Regex` (`Invoke-Ptc`, src/PwshTokenCompressor.psm1)
whose arms match only short aliases (`ls|dir|gci|list`), with a `default` arm that
prints usage. Owner rejected three narrower fixes in turn (add cmdlet names to the
regex; a hashtable dispatch + alias table; an AST allowlist) as variations on a
"maintained debt list."

**Verified evidence gathered this session (keep - expensive to re-establish):**

- **RTK is text/stream-first and cannot compress PowerShell objects.** RTK (`../rtk`,
  upstream `rtk-ai/rtk`) has ~100 hand-written per-command proxies plus a universal
  `rtk summary <cmd>` that runs any command via `sh -c` / `cmd /C` and filters the
  **text** output. By the time RTK sees PowerShell output it is already
  `Format-Table` text; the objects are gone. ptk's reason to exist is the README's
  claim: compress objects *before* formatting. (Confirmed by reading RTK source:
  `src/main.rs`, `src/cmds/system/summary.rs` `detect_output_type`,
  `src/core/runner.rs` `run_passthrough`.)
- **Both agent harnesses run a fresh, cold PowerShell process per tool call by
  default.** Confirmed directly: Codex (`codex exec`, self-report) =
  `pwsh -Command "<string>"` per call, no persistence except an explicitly retained
  PTY (`write_stdin`/`session_id`). Claude Code (claude-code-guide agent, citing
  code.claude.com/docs) = fresh process per call; dedicated PowerShell tool
  auto-detects `pwsh.exe`/`powershell.exe`, `-ExecutionPolicy Bypass`, profiles not
  loaded; no REPL. Neither persists env/modules/sessions between default calls.
- **Owner's real Exchange workflow works because of a warm HOST process, not harness
  state.** Hybrid Exchange; `Get-Queue` is on-prem only; on-prem EMS takes 30s+ to
  connect to the CAS server; owner's `$PROFILE` auto-connects only EXO, nothing
  on-prem. Yet the agent returns on-prem queue data in ~2s, repeatedly. Deduction:
  the agent is running inside an already-open EMS host whose implicit-remoting
  PSSession persists in that process. **Consequence:** if ptk's universal path
  spawned a child `pwsh` (today's `Invoke-PtcRun` string path does), `ptk Get-Queue`
  would launch a cold process with no on-prem session and either fail or eat the 30s
  reconnect - breaking a workflow that currently works. Therefore the universal path,
  if built, MUST run in-process (in ptk's own host session), never a grandchild. The
  README design goal "use `pwsh -NoProfile -NonInteractive` for command-string
  execution" is wrong for this case.
- **Threat model: ptk is not a security boundary.** `ptk <cmd>` would run the same
  string in the same process the harness already spawns for raw PowerShell - identical
  blast radius. Prompt-injection defense belongs at the harness sandbox, not in a
  token compressor. (Matches RTK's stance: `rtk summary` runs arbitrary strings.) So
  no AST allowlist / injection guard on the universal path is warranted; the previous
  session's injection-guard tests on the string path would need deliberate
  reconciliation, not silent deletion, if that path is retired.

**Settled sub-decisions (conditional on building it at all):**

- Drop the `ls`/`ps`/`service`/`grep` verbs - redundant `<cmdlet> | Compress-PtcObject`
  bash-crutch wrappers, subsumed by a universal path. Keep `read`/`smart`/`savings`
  (text/file work, the RTK-derived path), `run`, `compress`.
- Fix `Compress-PtcObject` so a homogeneous `string[]` (e.g. `Get-Content` output)
  routes to the text filter at `minimal` (gentlest, never-worse-guarded) instead of
  being truncated as an object list. Escape hatch = reuse `read`'s
  `-Level`/`-MaxLines`/`-Tail`. This also fixes the standalone
  `ptk run { Get-Content }` truncation bug.

**Why deferred (owner's framing):** ptk is a personal/team tool complementing the
owner's `headroom` PoC on Windows/PowerShell work - not an org-wide tool. headroom
already saved ~39.5M tokens in one day without ptk or RTK. The universal path is the
large, risky piece (in-session execution; retiring hardened code). The owner's
build trigger is "if it shows real benefit," i.e. a measurement condition. Bar to
clear is "real savings on my daily Windows work," not org scale.

**Standing recommendation (for whoever picks this up):** Stage it. (1) Fix the
`string[]` truncation bug and (2) drop the bash-crutch verbs - both small and
clearly worth it on their own. Then (3) measure object-first savings on a week of
real Windows usage (the `savings`/`Measure-PtcSavings` primitive exists). Only build
(4) the universal in-process wrapper if the data justifies it. Sequences the cost
behind the proven benefit. Each of (1)-(4) is a separate authorized change requiring
its own go.

### OPEN (2026-06-27): Whether to give ptk a session-persistent warm-runspace backend

**Status:** Open - deferred, recording the design exploration. No code change
authorized. This is the **substrate** counterpart to the universal-wrapper decision
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
- **EXO/Graph auth = app registration + certificate (app-only) is a HARD REQUIREMENT.**
  No interactive `Connect-*` in the server. A tenant/box that cannot meet this is out of
  scope rather than a reason to add an interactive fallback.
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

**Relationship to the universal-wrapper decision:** complementary, not competing. The
universal wrapper is the *surface* (`ptk <cmdlet>`); the persistent runspace is the
*substrate* (a deterministic warm host). The MCP tool is the portable replacement for
"the agent happens to live in a warm EMS host." If both are built, `ptk_invoke` runs the
cmdlet inside the owned runspace.

**Why deferred (owner's framing):** unchanged from the universal-wrapper entry - ptk is
a personal/team tool complementing `headroom`; the build trigger is measured benefit on
real daily Windows work, not faith. This is a *larger* build than the universal wrapper
(a whole MCP server + .NET hosting + app-reg/cert setup), so the bar is at least as high.

**Standing recommendation (for whoever picks this up):** Do not build the server first.
(1) Quantify the pain - count cold `Import-Module` / `Connect-*` invocations and their
latency over a week of real sessions; if the ambient-warm-host accident already covers
the daily workflow, the deterministic host may not pay for itself yet. (2) If material,
prototype the smallest possible .NET stdio MCP server exposing one tool
`ptk_invoke { <scriptblock> }` against a single warm `Runspace` with cert-based EXO
preconnect, returning `Compress-PtcObject` output. (3) Only then add the module map,
`ptk_reset`, and the router shaping layer. Each step is a separate authorized change
requiring its own go.
