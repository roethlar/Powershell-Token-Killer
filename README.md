# PwshTokenCompressor (`pwsh_token_compressor`)

PowerShell-first token compression for agent workflows, invoked locally as
`ptk`.

The project has two related pieces:

- A PowerShell module and launcher for compact local reads, searches, object
  summaries, and one-off command runs.
- A stdio MCP server that keeps one PowerShell runspace warm for an agent
  session and exposes `ptk_invoke` as the preferred shell tool.

It is a structured-output compressor. PowerShell objects are captured before
formatting, summarized by type and selected properties, and rendered as compact
text. It is not a security boundary and it is not a replacement shell sandbox:
commands run with the same authority as the shell or MCP client that called
them.

## What It Compresses

`Compress-PtcObject` has specialized summaries for filesystem objects,
`Select-String` matches, processes, services, and generic objects. It falls back
to a compact table for mixed or projected shapes instead of assuming a type name
is enough.

`Compress-PtcOutput`, used by the MCP server, applies the agent-facing shaping
rules:

- PowerShell objects go through `Compress-PtcObject`.
- Strings and primitive scalars pass through verbatim and are not truncated.
- Log-shaped text goes through `rtk log` when `rtk` is available.
- If `rtk` is absent or fails, log-shaped text returns with a labeled raw
  fallback.

The MCP server also routes a single native command with constant arguments
through `rtk` when available, so commands like `git status --short` can use
rtk's command filters. Pipelines, chains, variables, cmdlets, aliases,
redirections, parse errors, and batch shims stay on the PowerShell path.

## Quick Start: Local Module

```powershell
Import-Module .\src\PwshTokenCompressor.psd1 -Force

ptk ls .
ptk read .\README.md -MaxLines 20
ptk read .\src\PwshTokenCompressor.psm1 -Level aggressive
ptk smart .\src\PwshTokenCompressor.psm1
ptk savings .\src\PwshTokenCompressor.psm1
ptk grep "function" .\src
ptk run { Get-ChildItem . | Select-Object Name,Length }
Get-ChildItem . | ptk compress -MaxItems 5
```

Or use the repo launcher, which imports the module for the call:

```powershell
.\ptk.ps1 read .\README.md -MaxLines 20
.\ptk.ps1 run "Get-ChildItem . | Select-Object Name,Length"
```

Local command details are in [docs/usage.md](docs/usage.md).

## Quick Start: MCP Server

The server in [server/](server/) is the normal agent-facing path. It hosts one
long-lived PowerShell runspace, so variables, imported modules, and established
connections persist across `ptk_invoke` calls.

```powershell
dotnet test server/PtkMcpServer.slnx
pwsh -NoProfile -File server/test-handshake.ps1 -UseRegistrationCommand -TimeoutSec 90
```

Inside this repo, `.mcp.json` registers the server as `ptk` for Claude Code.
For sessions launched from other directories, register the same command
user-wide:

```powershell
claude mcp add ptk --scope user -- dotnet run -v q --project <path-to-repo>/server/PtkMcpServer
```

Server setup, tool arguments, routing overrides, hook installation, and
environment variables are documented in [server/README.md](server/README.md).

## Verification

Current local verification entry points are:

```powershell
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
dotnet test server/PtkMcpServer.slnx
pwsh -NoProfile -File server/test-handshake.ps1
```

The handshake can also be run with `-UseRegistrationCommand -TimeoutSec 90` to
exercise the same `dotnet run -v q --project server/PtkMcpServer` command used
by `.mcp.json`; the longer timeout gives the registration launch path room for
cold build/startup work.

## Attribution

RTK inspired the filter-level model and heuristic summary approach. This project
uses a PowerShell-native implementation for object shaping and PowerShell AST
parsing for source summaries.
