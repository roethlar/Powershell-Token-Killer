# PtkMcpServer — warm-runspace MCP server

A stdio MCP server that owns one long-lived PowerShell runspace, so heavy
modules (`ExchangeOnlineManagement`, `ActiveDirectory`, ...) import **once**
per session and every later tool call runs against warm state, instead of
paying module-load and connection cost in a fresh `pwsh` process per call.

Status and open decisions live in `.agents/state.md` and `.agents/decisions.md`
at the repo root; the build plan is `.agents/plans/warm-runspace-mcp-server.md`.

## Prerequisites

- .NET SDK 10.x (`dotnet --list-sdks`)
- PowerShell 7.4+ (`pwsh`) on PATH — 7.6.3 is what the server SDK pins
- Network access on first build (NuGet restore)
- Claude Code (or any MCP client that speaks stdio)

## Setup on a new machine

1. Clone this repo.
2. Verify the server works before wiring it to a client:

   ```
   dotnet test server/PtkMcpServer.slnx
   pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand
   ```

   The handshake script starts the server exactly the way the MCP
   registration does and must end with `HANDSHAKE PASSED`.

3. **Sessions started inside this repo:** nothing else to do. The committed
   `.mcp.json` registers the server as `ptk`; Claude Code asks for one-time
   approval at session start. First launch builds automatically (`dotnet run`).

4. **Sessions in other project directories** (the normal case for daily work):
   register it user-wide, pointing at this clone:

   ```
   claude mcp add ptk --scope user -- dotnet run -v q --project <path-to-repo>/server/PtkMcpServer
   ```

   Check with `claude mcp list`; remove with `claude mcp remove ptk`.

## Tools

| Tool | Purpose |
| --- | --- |
| `ptk_invoke` | Run any shell command (PowerShell or native) in the warm runspace; state persists across calls |
| `ptk_modules` | List loaded modules; `listAvailable: true` for all installed (cached) |
| `ptk_reset` | Recycle the runspace, discarding all warm state |
| `ptk_ping` | Health check |

## Routing (unified shell surface)

`ptk_invoke` is the single tool for all shell work
(`.agents/plans/unified-shell-routing.md`):

- A script that is exactly one bare native command with constant arguments
  (`git status`, `npm ls`, ...) is rewritten to run through **rtk**, whose
  per-command filters compress output at the source; commands rtk doesn't
  know pass through it unchanged. No rtk on PATH → the script runs as-is.
- Everything else — pipelines, chains, variables, cmdlets, aliases — runs as
  PowerShell in the warm runspace; log-shaped text output still routes
  through `rtk log`, objects compress via `Compress-PtcObject`.
- Overrides: `route=pwsh` forces plain execution, `route=rtk` forces the
  rewrite, `raw=true` skips routing and shaping entirely.

`scripts/ptk_init.ps1` installs a Claude Code PreToolUse hook that redirects
Bash/PowerShell tool calls to `ptk_invoke` (local settings by default,
`-Global` for all projects, `-Show`/`-Uninstall`/`-DryRun`; takes effect at
next session start). A command containing `PTK_DIRECT` bypasses the redirect
for genuinely interactive/TTY work.

## Configuration (environment variables)

| Variable | Default | Meaning |
| --- | --- | --- |
| `PTK_CALL_TIMEOUT_SECONDS` | `300` | Per-call limit; on timeout the call fails and the runspace is recycled (warm state lost) |
| `PTK_IDLE_EXIT_SECONDS` | `14400` (4h) | Server exits after this long with no calls — backstop for orphaned processes |

Set them via the `env` block of the MCP registration if the defaults don't fit.

## Security posture

The server is **not** a security boundary: `ptk_invoke` runs arbitrary
PowerShell with the same blast radius as the shell your harness already has.
Recommended posture: leave `ptk_invoke` on ask-per-call in Claude Code and read
the script in the prompt — do not blanket-allow it. A declarative policy gate
for destructive cmdlets is designed but deliberately not built; see the
2026-07-02 continuation decision in `.agents/decisions.md`.

## Behavior worth knowing

- Calls are serialized — the single runspace runs one pipeline at a time.
- A timed-out call recycles the runspace: the next call works, but variables,
  modules, and connections are gone (re-import/reconnect).
- No interactive prompts can be answered inside the server; anything needing
  `Connect-*` MFA-style interaction must use an unattended pattern (e.g.
  app-registration + certificate) or stay out of the server.
