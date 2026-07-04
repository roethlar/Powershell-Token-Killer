# PtkMcpServer

`PtkMcpServer` is a stdio MCP server that owns one long-lived PowerShell
runspace. Agent shell calls can run inside that runspace through `ptk_invoke`,
so variables, imported modules, and established connections survive across
calls for the life of the MCP server process.

The server imports `src/PwshTokenCompressor.psd1` into the warm runspace and
uses `Compress-PtcOutput` to shape tool output. If the module cannot be found or
imported, calls fall back to plain `Out-String` output and the server writes the
problem to stderr.

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

Sessions started inside this repo use the committed `.mcp.json`:

```json
{
  "mcpServers": {
    "ptk": {
      "command": "dotnet",
      "args": ["run", "-v", "q", "--project", "server/PtkMcpServer"]
    }
  }
}
```

For sessions started from other project directories, register the same command
user-wide and point it at this clone:

```powershell
claude mcp add ptk --scope user -- dotnet run -v q --project <path-to-repo>/server/PtkMcpServer
```

Check with `claude mcp list`; remove with `claude mcp remove ptk`.

## Tools

| Tool | Arguments | Purpose |
| --- | --- | --- |
| `ptk_invoke` | `script`, optional `raw`, optional `route` | Run a PowerShell script or native command line in the warm runspace. |
| `ptk_modules` | optional `listAvailable` | List loaded modules by default; with `listAvailable: true`, enumerate installed modules once and cache the result. |
| `ptk_reset` | none | Recycle the runspace, discarding variables, loaded modules, current directory, default parameters, and connections. |
| `ptk_ping` | none | Health check returning `pong`. |

`ptk_invoke` returns command output, then labeled sections when present:
`[exit] N`, `[errors]`, and `[warnings]`. Empty output returns `(no output)`.

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
  strings, redirections, parse errors, and `.cmd`/`.bat` shims stay on the
  PowerShell path.
- If `rtk` is absent, the script runs unchanged.

Output shaping:

- Object output compresses with `Compress-PtcObject`.
- Plain strings and primitive scalars pass through without truncation.
- Log-shaped text routes through `rtk log` when possible.
- Log-shaped text falls back to labeled raw text if `rtk` is absent or fails.
- Nonzero native exit codes are reported as `[exit] N`.

Overrides:

- `raw=true` skips routing and shaping and returns plain formatted text.
- `route=pwsh` forces execution exactly as PowerShell.
- `route=rtk` forces the `rtk` rewrite when the script has the safe
  single-command shape.

## Claude Code Hook

`scripts/ptk_init.ps1` installs a Claude Code `PreToolUse` hook that redirects
ordinary Bash and PowerShell tool calls toward `mcp__ptk__ptk_invoke` using a
deny-with-guidance response.

```powershell
pwsh -File scripts/ptk_init.ps1             # local .claude/settings.json
pwsh -File scripts/ptk_init.ps1 -Global     # ~/.claude/settings.json
pwsh -File scripts/ptk_init.ps1 -Show
pwsh -File scripts/ptk_init.ps1 -DryRun
pwsh -File scripts/ptk_init.ps1 -Uninstall
```

The installer preserves unrelated hooks and replaces only the ptk-owned entry
when re-run. The hook takes effect at the next Claude Code session start.

A command containing `PTK_DIRECT` bypasses the hook. Use that for work that
genuinely needs the harness shell, such as interactive or TTY-dependent tools,
or when the ptk MCP server is unavailable.

## Configuration

Set these in the MCP registration `env` block when defaults do not fit:

| Variable | Default | Meaning |
| --- | --- | --- |
| `PTK_CALL_TIMEOUT_SECONDS` | `300` | Per-call limit. On timeout, the call fails and the runspace is recycled. |
| `PTK_IDLE_EXIT_SECONDS` | `14400` | Idle self-exit backstop for orphaned servers, in seconds. |
| `PTK_MODULE_PATH` | auto-discovered `src/PwshTokenCompressor.psd1` | Explicit module manifest to import into the runspace. If set to a missing file, shaping is disabled. |
| `PTK_RTK_PATH` | `rtk` on `PATH` | Explicit `rtk` binary for native routing and log shaping. If set to a missing file, `rtk` is treated as absent. |

## Operational Notes

- Calls are serialized; one runspace runs one pipeline at a time.
- `useLocalScope: false` is intentional, so assignments and imported modules
  persist into later calls.
- `ptk_reset` and call timeouts create a fresh primed runspace. Warm state is
  lost, but later calls continue working.
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
the client permission prompt instead of blanket-allowing the tool.
