# Plan: multi-harness init — registration, hook, and nudge per harness

**Status:** DRAFT for owner approval (requested 2026-07-08 with the README
findings update). No code authorized until approved. Codex loop per slice
once execution starts.

## Goal

One installer surface that makes ptk available and preferred on a machine,
per harness: **register** the MCP server everywhere it can run, **enforce**
the redirect only where a blocking pre-tool hook is verified, and **nudge**
via user-level guidance everywhere else. Modeled on `rtk init`'s per-agent
legs (`--agent <name>`, global default, show/dry-run/uninstall, patch
consent), adapted for the axis rtk does not have: MCP registration.

## Design principles

- **Three independent layers.** Registration (required for anything),
  enforcement (hook — only where verified to fire AND block), nudge
  (user-level guidance note, conditional wording, safe on ptk-less
  machines). A harness leg ships whichever layers its verified surface
  supports; missing layers degrade to the next one down.
- **Probe live; encode nothing volatile.** The 2026-07-08 lesson in
  miniature: a model id recorded in another repo's ops notes (`grok-build`)
  was already stale when used the same week. The installer verifies each
  harness at run time (CLI present, `mcp add` surface answers) and fails
  per-leg with guidance — never all-or-nothing, never trusting recorded
  flag spellings. The durable record keeps *capabilities with dates*, not
  invocation syntax.
- **User-level only by default.** The cross-repo review's core finding:
  repo-file writes collide with content-tracking tooling (a governance
  refresh flags a patched `.claude/settings.json` forever). Every leg
  writes user-level config only; Claude's local mode survives as an
  explicit opt-in that prints the tracking warning.
- **Self-reports are hypotheses.** Harness self-interviews (2026-07-08,
  below) scope the design; every leg still gets a live verification on
  that harness before it ships — mirroring the governance toolkit's
  verify-once gate. Results land in `docs/harness-support.md` (new), the
  durable per-harness facts table.
- **Idempotent everywhere.** Marker-delimited nudge blocks
  (`<!-- ptk-guidance --> ... <!-- /ptk-guidance -->`, rtk's mechanism),
  remove-then-add registration, `-Show`/`-DryRun`/`-Uninstall` parity for
  every leg.

## Evidence (frozen 2026-07-08)

Machine CLIs: codex, agy, grok present; gemini CLI and cursor absent
(their legs are designed but stay dormant until a machine with the CLI
verifies them).

| Harness | MCP registration | Hook (enforcement) | Nudge home |
| --- | --- | --- | --- |
| Claude Code | `claude mcp add --scope user` (in use today) | PreToolUse deny-with-guidance — mechanism verified, ptk's own live deny-and-reissue check STILL PENDING (the standing gate) | `~/.claude/CLAUDE.md` |
| codex | `codex mcp add ptk -- <exe>` → `~/.codex/config.toml [mcp_servers.ptk]`; loads in `codex exec` (self-report + this machine's live config) | repo-level `.codex/hooks.json` pre_tool_use verified firing elsewhere (2026-06-29), but hooks are TRUST-GATED (`--dangerously-bypass-hook-trust` exists; persistence mechanism unknown) and repo-level writes violate user-level-only → **no hook in v1; probe later** | `~/.codex/AGENTS.md` (self-report, confident) |
| grok | `grok mcp add -s user ptk <exe>` → `~/.grok/config.toml` (CLI-verified surface) | Self-report: all unsure. Third-party ledger: global `~/.grok/hooks/*.json` + auto-scan of `~/.claude/settings.json` — meaning ptk's GLOBAL CLAUDE HOOK may already fire in grok. Probe. | SELF-REPORT, slice-0 probe target: grok claimed it session-loads `~/.claude/Claude.md` (note the casing it reported — fine on Windows, not on case-sensitive systems). If the probe confirms it, the Claude user nudge covers grok; if not, the grok leg needs its own nudge home. MCP tools named `{server}__{tool}` (self-report) |
| agy (Antigravity) | `~/.gemini/config/mcp_config.json` — file VERIFIED present on this machine (2026-07-08, currently empty `{}`); the `mcpServers` entry schema is agy's RECALL, confirmed at slice-4 install time by writing the entry and seeing the tools appear. agy also recalls a `/mcp` interactive manager. NOTE: agy's own sandbox blocks it from reading its config — agy probes run from outside or by the owner | CONFLICT to resolve at slice 0: the third-party ledger recorded Claude-style hook events in `~/.gemini/settings.json` (docs, 2026-06-29), but this machine has NO top-level settings.json, `antigravity-cli/settings.json` has no hooks key, and agy itself recalls plugin-bundled `hooks.json` (`before_tool_call`, block via non-zero exit / JSON) instead. All unverified live | `~/.gemini/GEMINI.md` (agy recall, consistent with the ledger; file does not exist yet on this machine — the nudge leg creates it) |
| gemini CLI / cursor | absent on this machine | cursor: `.cursor` `BeforeTool` hooks.json per rtk's constants | dormant legs |

Cross-cutting finding from grok's tool naming: ptk's hook guidance text
names the Claude tool id (`mcp__ptk__ptk_invoke`); on any harness with a
different prefix scheme the deny message would name a tool that does not
exist there. The hook text needs harness-neutral naming ("the ptk_invoke
MCP tool") or per-harness wording.

## Slices

0. **Probes (results frozen into this plan before implementation).**
   (a) Claude Code live hooked check — install `-Global`, fresh session, a
   Bash and a PowerShell call come back denied with guidance, model
   re-issues via ptk_invoke. This is the standing verify-once gate both
   repos want closed. (b) grok: register via `grok mcp add -s user`,
   confirm tool naming and a live `ptk_invoke` call headless; observe
   whether the global Claude hook fires there and whether its deny JSON
   is honored; confirm which user-level guidance file grok actually
   session-loads (its self-reported `~/.claude/Claude.md`, exact casing
   included — this decides whether the grok nudge rides the Claude file
   or needs its own home). (c) codex: confirm the registered tools fire in
   `codex exec` (registration is already live on this machine). (d) agy:
   interactive session (OAuth), find the MCP surface, register, verify a
   call. Each probe's outcome goes in `docs/harness-support.md` with a
   date.
1. **Installer framework + Claude leg.** Refactor `scripts/ptk_init.ps1`
   to per-agent legs: `-Agent claude|codex|grok|agy|all` (default: the
   detected set), global-by-default (`-Local` becomes the explicit
   Claude-only opt-in and prints the tracking warning), `-Show`/`-DryRun`/
   `-Uninstall` per leg. Claude leg = registration check (offers
   dev-install if `~/.ptk` is missing) + hook + optional nudge block in
   `~/.claude/CLAUDE.md`. Hook guidance text goes harness-neutral.
   **Compatibility (deliberate breaking change, handled loudly):** the
   current script is local-by-default; after the flip, a bare invocation
   installs globally and PRINTS what changed and how to get the old
   behavior (`-Local`). `dev-install.ps1 -Hook` already invokes the
   global install and keeps working as an alias for the Claude leg; it
   gains a deprecation note pointing at `-InitAgents` (slice 5) but is
   not removed in this plan.
   **Candidate within this slice (decide at implementation):** a cheap
   server-liveness check in the hook, or at minimum down-server wording
   in the deny guidance — today a dead server still yields denials
   steering to an unavailable tool (mhi-2); PTK_DIRECT is the documented
   escape either way.
2. **codex leg.** `codex mcp add` (idempotent via `codex mcp get`) +
   marker-delimited nudge block in `~/.codex/AGENTS.md`. No hook.
3. **grok leg.** `grok mcp add -s user` + whatever slice-0 said about the
   Claude-hook spillover (if it fires and blocks correctly, document it;
   if it fires and misbehaves, scope the Claude hook's matcher or text).
   Nudge home per the slice-0 probe outcome: the Claude user file if the
   session-load claim verified, otherwise whatever guidance surface the
   probe established for grok.
4. **agy leg.** Registration = write the `mcpServers` entry into the
   verified `~/.gemini/config/mcp_config.json` (create-or-merge,
   preserving foreign entries), then confirm the tools appear in a live
   session — that confirmation IS the schema probe. Nudge = create/append
   the marker block in `~/.gemini/GEMINI.md`. Hook = only if slice 0
   resolves the hooks conflict (plugin `hooks.json` vs the ledger's
   settings.json claim) with a live blocking demonstration; otherwise
   nudge-only like codex.
5. **dev-install integration + uninstall symmetry.** `dev-install.ps1`
   offers `-InitAgents` chaining; `-Uninstall` reverses every leg it
   installed (mcp remove per harness, nudge blocks out, hook out).
6. **Docs.** README install matrix; `docs/harness-support.md` as the
   dated capabilities table (capabilities only — no volatile invocation
   syntax beyond what the installer itself probes).

Each slice: one commit, guard tests where the logic is testable (nudge
block writer/remover and leg detection are pure and Pester-friendly;
registration legs are thin CLI wrappers covered by `-DryRun` snapshot
tests), battery, codex loop.

## Out of scope

- Hooks on harnesses without a live-verified blocking pre-tool surface.
- rtk's long tail (windsurf, cline, kilocode, pi, hermes) — until demand.
- The shared multi-client host (its own open decision).
- The governance toolkit's template line (the toolkit's own change, gated
  on slice-0(a) provenance per the cross-repo settlement).

## Verification

Battery per slice (Pester, dotnet test, handshake when server-facing) plus
per-leg `-DryRun`/`-Show` snapshots; slice-0 probe outcomes recorded with
dates in `docs/harness-support.md` and this plan.
