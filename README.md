# ptk (PwshTokenCompressor)

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
  truncated: single native commands route through `rtk`'s per-command filters,
  PowerShell objects become compact typed summaries, log-shaped text is
  deduplicated, and plain text passes through untouched.

This is not a sandbox. Commands run with the same authority as the MCP client
or shell that calls them.

## How `ptk_invoke` Compresses Output

Each call is classified and shaped through one of four legs:

1. **Native command routing (rtk).** A script that is exactly one bare native
   command with constant arguments — `git status --short`, `npm ls`,
   `docker ps` — is rewritten to run through [rtk](https://github.com/rtk-ai/rtk),
   an external CLI whose per-command filters compress the output of common
   tools at the source. Commands rtk does not know pass through it unchanged.
2. **Object compression.** PowerShell commands that emit objects
   (`Get-ChildItem`, `Get-Process`, `Get-Service`, custom objects) are
   compressed into typed summaries before they are ever formatted to text.
3. **Log shaping.** Text output that looks like a log (timestamps, level tags)
   is deduplicated through `rtk log`.
4. **Passthrough.** Plain strings and scalars are returned verbatim, never
   truncated.

Anything that is not a safe single native command — pipelines, chains,
cmdlets, variables, redirections — runs as ordinary PowerShell in the warm
runspace, and only its *output* is shaped (legs 2–4). Routing and shaping can
never fail a call: any internal failure falls back to labeled, unshaped
output.

`rtk` is optional. Without it, native commands run unchanged and log-shaped
text is returned raw; object compression still applies. With it installed (on
`PATH`, or pinned via `PTK_RTK_PATH`), you get the native-command filters —
that is where the largest savings on tools like `git`, `npm`, and `docker`
come from.

Per-call escape hatches: `raw=true` returns full uncompressed output executed
exactly as written; `route=pwsh` forces plain PowerShell execution;
`route=rtk` forces the rtk rewrite when the script shape allows it.

## Server Tools

| Tool | Purpose |
| --- | --- |
| `ptk_invoke` | Run a PowerShell script or native command line in the warm runspace. |
| `ptk_modules` | List loaded modules; optionally enumerate installed ones (cached). |
| `ptk_reset` | Recycle the runspace, discarding all warm state. |
| `ptk_ping` | Health check. |

## Setup

Verify the server builds and answers the MCP handshake:

```powershell
dotnet test server/PtkMcpServer.slnx
pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90
```

Inside this repo, the committed `.mcp.json` already registers the server as
`ptk` for Claude Code. To use it from any project directory, register it
user-wide:

```powershell
claude mcp add ptk --scope user -- dotnet run -v q --project <path-to-repo>/server/PtkMcpServer
```

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
tool calls and steers the agent toward `ptk_invoke` instead.

```powershell
pwsh -File scripts/ptk_init.ps1 -Global
```

When a command genuinely needs the harness shell — interactive or
TTY-dependent tools, or the ptk server being down — include `PTK_DIRECT` in a
command comment to bypass the hook. Install options and details are in
[server/README.md](server/README.md#claude-code-hook).

## Local CLI

The same compression is available as a plain PowerShell module, without the
MCP server, for compact file reads, listings, and searches:

```powershell
Import-Module .\src\PwshTokenCompressor.psd1 -Force

ptk ls . -Recurse
ptk read .\README.md -MaxLines 20
ptk smart .\src\PwshTokenCompressor.psm1
ptk grep "function" .\src
ptk run { Get-ChildItem . | Select-Object Name,Length }
Get-ChildItem . | ptk compress -MaxItems 5
```

Or use the repo launcher without importing first:

```powershell
.\ptk.ps1 read .\README.md -MaxLines 20
```

The full command reference is in [docs/usage.md](docs/usage.md).

## Verification

```powershell
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
dotnet test server/PtkMcpServer.slnx
pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90
```

## More Docs

- [MCP server setup, configuration, and operations](server/README.md)
- [Local CLI usage](docs/usage.md)
