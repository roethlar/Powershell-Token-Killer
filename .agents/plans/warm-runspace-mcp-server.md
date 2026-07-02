# Plan: Warm-Runspace stdio MCP Server (prototype)

**Status:** APPROVED by owner 2026-07-02 ("go"). Implementation in progress, one
commit per slice.
**Decision basis:** `.agents/decisions.md` → Open Decision (2026-06-27) "session-persistent
warm-runspace backend", selected as active work by owner 2026-07-02. Settled
sub-decisions there are binding on this plan; read that entry first.

## Goal

A stdio MCP server that owns one long-lived PowerShell `Runspace`, so heavy modules
import **once** per session and every subsequent tool call runs against warm state —
eliminating the per-call reload tax of cold `pwsh -Command` processes. Core
requirement is warm module load; unattended (e.g. cert-based) auth is the pattern for
connection-bearing modules, not itself the requirement.

Phase 2 (separate go, after the server works): route output through
`Compress-PtcObject` for token compression. This plan deliberately returns plain
output first — owner sequenced "server working first, then compression."

## Fixed constraints (from the decision entry)

- .NET stdio server hosting `System.Management.Automation` in-process.
- One serial runspace, no pool. Calls serialized; per-call timeout recycles the
  runspace on a wedge instead of hanging the session.
- Modules lazy-load on first use and stay loaded; no interactive `Connect-*` ever.
- Lifetime managed inside the server (idle self-exit backstop), never via
  `SessionEnd` hooks.
- Not a security boundary — same blast radius as the raw shell the harness already has.

## Stack (verified 2026-07-02 on this machine)

- `net10.0`, dotnet SDK 10.0.301 installed.
- `ModelContextProtocol` 1.4.0 (official C# SDK, stable) — stdio transport,
  attribute-based tools.
- `Microsoft.PowerShell.SDK` 7.6.3 — matches installed pwsh 7.6.3.
- Dev/CI-equivalent verification runs on macOS; the payoff workload (AD, EMS, EXO)
  is Windows. Cross-platform correctness is validated here; a Windows smoke test by
  the owner is a named step, not assumed.

## Layout

- `server/PtkMcpServer/` — console project (the server).
- `server/PtkMcpServer.Tests/` — xUnit tests targeting `RunspaceHost` directly
  (the MCP layer stays a thin shell over it).
- Existing PowerShell module (`src/`, `tests/`) untouched in this plan.

## Slices (one commit each, tests first where the slice has logic)

1. **Scaffold.** Solution + two projects, MCP server boots over stdio and answers
   `initialize`/`tools/list` with a `ptk_ping` placeholder tool.
   *Verify:* scripted JSON-RPC handshake against `dotnet run` (checked-in script);
   `dotnet test` green.
2. **RunspaceHost.** Class owning the single runspace: serialized execution
   (semaphore), per-call timeout (default 300s, configurable) that recycles the
   runspace, error capture (streams → structured result).
   *Verify (xUnit):* state persists across calls (`$x=1` then `$x` → `1`); an
   imported module stays loaded; a timed-out call recycles (state gone, host alive);
   errors surface without killing the host.
3. **`ptk_invoke` tool.** `ptk_invoke(script)` runs in the warm runspace, returns
   output as text (`Out-String`-style) plus error/warning streams. Compression is
   Phase 2.
   *Verify:* handshake script drives two invokes proving cross-call state; xUnit on
   the tool adapter.
4. **`ptk_modules` + `ptk_reset` tools.** Enumerate available/loaded modules
   (cached `Get-Module -ListAvailable` at startup); reset = recycle runspace.
   *Verify:* xUnit — loaded list grows after an import; reset clears state and
   the loaded list.
5. **Lifetime backstop.** Idle self-exit (default 4h since last call, configurable);
   idempotent startup.
   *Verify:* xUnit with short timer.
6. **Registration + real-session smoke test.** Project-scoped `.mcp.json` entry
   (`dotnet run --project server/PtkMcpServer` or published binary). Manual smoke
   in a live Claude Code session: two `ptk_invoke` calls sharing state; time
   `Import-Module` on first vs. second call to show the reload tax gone.
   *Verify:* manual, results recorded in `state.md`.
7. **Windows validation (owner-run).** On the real Windows box: AD/EMS/EXO module
   load-once behavior, unattended auth pattern for whichever connection-bearing
   modules matter that week. Findings feed Phase 2 scope.

Each slice: run `dotnet test` (and the handshake script where named) before
claiming done; prove new tests guard by reverting the change once (per AGENTS.md
Verification). Record the new verification command in `.agents/repo-map.json` when
slice 1 lands.

## Non-goals (this plan)

- No `Compress-PtcObject` integration (Phase 2).
- No RunspacePool / parallelism.
- No interactive auth fallback.
- No changes to the ptk CLI dispatch / universal-wrapper surface.
- No router/shaping layer.

## Risks

- MCP SDK or PowerShell SDK API drift vs. my knowledge — slice 1 exists partly to
  surface this early and cheaply.
- Runspace recycle-on-timeout can strand external state (e.g. a half-open remote
  session); acceptable for a prototype, note in tool description.
- macOS dev can't exercise Windows-only modules; slice 7 is the honest check.
