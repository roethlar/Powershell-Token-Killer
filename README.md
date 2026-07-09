# PowerShell Token Killer (`ptk`)

A warm PowerShell shell for coding agents, with token-compressed output.

The primary way to use ptk is the **MCP server**: register it with your agent
harness (for example Claude Code) and route shell work through the `ptk_invoke`
tool instead of the harness's own Bash/PowerShell tool. Every call runs in one
long-lived PowerShell runspace, and every result comes back compressed by
shape before it reaches the model's context.

Two things make that worth doing:

- **Warm state.** Variables, imported modules, the current directory, and
  established connections survive across calls for the whole session. Heavy
  imports like `ActiveDirectory` or a cloud SDK happen once, not per command.
- **Token compression.** Output is shaped by what it is, not blindly
  truncated: single native commands route through
  [rtk](https://github.com/rtk-ai/rtk)'s per-command filters,
  PowerShell objects become compact typed summaries, log-shaped text is
  deduplicated, and plain text passes through cleaned of terminal escape
  codes, bounded only at pathological sizes.

This is not a sandbox. Commands run with the same authority as the MCP client
or shell that calls them.

## How `ptk_invoke` Compresses Output

Each call is classified and shaped through one of four legs:

1. **Native command routing (rtk).** A script that is exactly one bare native
   command with constant arguments — `git status --short`, `npm ls`,
   `docker ps` — is rewritten to run through `rtk`, an external CLI whose
   per-command filters compress the output of common tools at the source.
   Commands rtk does not know pass through it unchanged.
2. **Object compression.** PowerShell commands that emit objects
   (`Get-ChildItem`, `Get-Process`, `Get-Service`, custom objects) are
   compressed into typed summaries before they are ever formatted to text.
3. **Log shaping.** Text output that looks like a log (timestamps, level tags)
   is deduplicated through `rtk log`.
4. **Passthrough.** Plain strings and scalars are returned with
   ANSI/terminal escape sequences stripped, otherwise unaltered.
   Pathologically large text is elided to a labeled head+tail window;
   `raw=true` returns the complete, uncompressed output (as plain
   formatted text — nothing elided or stripped).

Anything that is not a safe single native command — pipelines, chains,
cmdlets, variables, redirections — runs as ordinary PowerShell in the warm
runspace, and only its *output* is shaped (legs 2–4). Routing and shaping can
never fail a call: any internal failure falls back to labeled, unshaped
output.

Install `rtk` — the largest savings live there. Its native-command filters
are where tools like `git`, `npm`, and `docker` get compressed, and it powers
the log-shaping leg too; ptk without it only gets you object compression.
Put it on `PATH` (or pin an exact binary via `PTK_RTK_PATH`) and routing picks
it up automatically. ptk degrades gracefully if it is missing — native
commands run unchanged and log-shaped text comes back raw — but that is a
fallback, not the intended setup.

Per-call escape hatches: `raw=true` returns full uncompressed output executed
exactly as written; `route=pwsh` forces plain PowerShell execution;
`route=rtk` forces the rtk rewrite when the script shape allows it.

Long work has two paths, by workload: `background=true` runs the script as a
cold background job polled through `ptk_job` (builds, watchers — anything
stateless that could exceed the call timeout), while `timeoutSeconds` raises
the per-call limit for long work that needs the warm session itself.

## Server Tools

| Tool | Purpose |
| --- | --- |
| `ptk_invoke` | Run a PowerShell script or native command line in the warm runspace. |
| `ptk_job` | Poll, read (shaped), or kill background jobs started with `background=true`. |
| `ptk_state` | Session introspection and health check: engine, uptime, cwd, loaded modules, jobs, and drift (env/PATH/variable changes since server start). |
| `ptk_reset` | Recycle the runspace to factory state: server-start environment restored, background jobs killed. |

## Setup

Verify the server builds and answers the MCP handshake:

```powershell
dotnet test server/PtkMcpServer.slnx
pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90
```

Then install and register it user-wide (builds a self-contained binary
into `~/.ptk` and registers it with Claude Code):

```powershell
pwsh -File scripts/dev-install.ps1
```

Or register the checkout directly instead of installing:

```powershell
claude mcp add ptk --scope user -- dotnet run -v q --project <path-to-repo>/server/PtkMcpServer
```

(The committed `.mcp.json` is deliberately empty — a checkout has no
project-scope registration; pick one of the two paths above.)

Then use `ptk_invoke` for shell work. Calls are ordinary PowerShell or native
command lines:

```powershell
Get-ChildItem . -Recurse
Import-Module ActiveDirectory
git status --short
```

Prerequisites, configuration (timeouts, module/rtk paths), and operational
behavior (call serialization, timeout recycling, stdin handling) are in
[server/README.md](server/README.md).

## Claude Code Hook (Optional)

The hook makes the redirect automatic: it intercepts ordinary Bash/PowerShell
tool calls and steers the agent toward `ptk_invoke` instead. Adoption
evidence says this matters: harnesses hide MCP tools behind deferred
discovery, and instruction-only nudges decay — the hook is the mechanism
that actually holds.

```powershell
pwsh -File scripts/ptk_init.ps1 -Global
```

**Prefer `-Global`** (one install per machine, covers every repo). Local
mode edits the repo's `.claude/settings.json` — if anything in that repo
tracks the file by content (governance tooling, dotfile managers, a
hash-checking refresh), the edit reads as an owner modification forever
after. Global mode patches only `~/.claude/settings.json`, which
repo-level tooling never sees.

When a command genuinely needs the harness shell — interactive or
TTY-dependent tools, or the ptk server being down — include `PTK_DIRECT` in a
command comment to bypass the hook. Install options and details are in
[server/README.md](server/README.md#claude-code-hook).

## Nudging Other Harnesses

The hook covers Claude Code today (multi-harness install is planned). For
any harness, a short note in its **user-level** guidance file — not a repo
file — teaches the preference wherever ptk is registered and stays silent
where it is not. Suggested text, adapt freely:

> When the ptk MCP server is available, use `ptk_invoke` for shell
> commands instead of the built-in shell: one warm PowerShell session
> (imports, connections, variables persist across calls), compressed
> output. Long stateless work: `background=true`, then poll `ptk_job`;
> long work that needs the warm session: raise `timeoutSeconds`.
> `ptk_state` diagnoses session drift; `ptk_reset` restores factory
> state; `raw=true` returns full uncompressed output.

Known user-level homes: `~/.claude/CLAUDE.md` (Claude Code),
`~/.codex/AGENTS.md` (codex), `~/.gemini/GEMINI.md` (agy/gemini). Keep
the note conditional ("when available") so the same text is safe on
machines without ptk.

## The Module

`src/PwshTokenCompressor.psd1` is the server's shaping library — the MCP
server imports it into the warm runspace and every result flows through it.
It exports `Compress-PtcObject`, `Compress-PtcOutput`, and
`Resolve-PtcInvokeScript` for the server (and for local experiments); there
is no separate CLI face — `ptk_invoke` is the product surface.

## Verification

```powershell
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
dotnet test server/PtkMcpServer.slnx
pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90
```

## More Docs

- [MCP server setup, configuration, and operations](server/README.md)

## Credits

PowerShell Token Killer is named after, and heavily inspired by,
[rtk](https://github.com/rtk-ai/rtk) — the Rust Token Killer — which proved
the idea that agent shell output should be compressed at the source. rtk owns
the native-command side of that idea; ptk extends it to PowerShell — object
pipelines, warm runspace state — and routes back through rtk wherever rtk
does it better.
