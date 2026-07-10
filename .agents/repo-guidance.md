# Repo-Specific Guidance
<!-- Extends AGENTS.md; never overrides it. Rules and pointers only — state
     lives in .agents/state.md. -->

## Mission Detail

PowerShell Token Killer (invoked as `ptk`; the module on disk is named
`PwshTokenCompressor`, and the name credits rtk, the Rust Token Killer) is a
PowerShell-first token-compression tool for agent workflows: it captures PowerShell objects before they are
formatted to text, summarizes them by type and selected properties, and
renders compact output for LLM tool use. It is a structured-output compressor,
not a Unix-command wrapper (see `README.md`).

The owner treats this as a personal/team tool, not an org-wide product. (An
earlier framing said it complements the owner's `headroom` PoC; headroom was
stopped in 2026 as net negative — see the corrections in `.agents/decisions.md`.)
The build trigger for larger architectural changes is measured benefit on real
daily usage, not anticipated need — and specifically *experienced* benefit, not
a tool's self-reported savings metric. All further building is currently gated
on a go/no-go adoption test of the warm-runspace MCP server; see the Open
Decisions in `.agents/decisions.md` rather than re-deriving this framing.

## Reading Order

1. `AGENTS.md`
2. `.agents/repo-guidance.md` (this file)
3. `.agents/state.md`
4. `.agents/decisions.md`
5. `README.md`
6. `src/PwshTokenCompressor.psm1` and `tests/PwshTokenCompressor.Tests.ps1`
7. `server/README.md` and `server/PtkMcpServer/Program.cs` (warm-runspace MCP
   server; see `.agents/plans/warm-runspace-mcp-server.md`)

## Verification

Confirmed automated verification commands (re-run 2026-07-03, all passing):

```
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
```
— 43/43 passed (PowerShell module suite; requires the Pester module, 5.8.0
confirmed present in this environment).

```
dotnet test server/PtkMcpServer.slnx
```
— 29/29 passed (C# warm-runspace MCP server suite).

```
pwsh -NoProfile -File server/test-handshake.ps1
```
— stdio handshake check for the MCP server; run manually when server-facing
code changes (not re-run by this governance refresh).

CI exists as of 2026-07-08 (release-plan slice 2): `.github/workflows/ci.yml`
runs the same battery (Pester, dotnet test, handshake) on an
ubuntu/windows/macos matrix for pushes to `master`/`ci/**` and PRs to
`master`. Local verification before claiming completion still applies. See
`.agents/repo-map.json` for the machine-readable record.

## Remotes & Sync

Two remotes are configured (`git remote -v`, confirmed 2026-07-09):

- `origin` — `https://github.com/AlsoBeltrix/PowerShell-Token-Killer.git`
- `gitea` — `http://q:3000/michael/Powershell-Token-Killer.git` (owner's
  local Gitea mirror; observed in the owner's push flow 2026-07-09)

`master` tracks `origin/master`. A `personal` remote
(`https://github.com/roethlar/-PowerShell-Token-Killer.git`) was recorded
here previously but no longer exists in this repo's git config as of
2026-07-03 — flagged in this refresh's approval summary rather than silently
dropped. Push policy stays in `.agents/push-policy.md`, not here.

## Earned Practices

- **Agent experience leads on model-facing guidance text (owner,
  2026-07-10, sd3-1 adjudication).** ptk's model-visible wording — tool
  descriptions, in-band markers, nudge text, refusal guidance — is
  guidance by an agent for an agent. When an approved plan's letter runs
  contrary to what the implementing agent and the reviewer judge works
  best for model interaction, lean toward that judgment and surface the
  final question to the owner rather than implementing wording the agents
  believe misleads. Incident: sd3-1 (`.agents/review/findings/sd3-1.md`)
  — the plan's per-surface pairing requirement would have put a useless
  recovery suggestion inside every elision marker; the owner delegated
  the call and the marker stayed lean, with the D2 amendment recorded in
  `.agents/decisions.md`.
