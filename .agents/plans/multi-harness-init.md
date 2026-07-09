# Plan: multi-harness init — registration, hook, and nudge per harness

**Status:** APPROVED by owner 2026-07-08 ("go for 4" on the presented
status list). **Slice 0 EXECUTED the same day — all probes green; results
in `docs/harness-support.md` (the durable table) and summarized here:**

- (a) Claude hooked check **PASSED end to end**: both matchers (Bash,
  PowerShell) denied live in-session with the guidance; a fresh headless
  session quoted the deny verbatim, re-issued via `ptk_invoke`
  unprompted, and completed. The standing verify-once gate is CLOSED.
- (b) grok: registered live (`ptk__*` naming); **no** Claude-hook
  spillover (the ledger's auto-scan claim is wrong for this build);
  nudge home **VERIFIED** by marker probe — grok session-loads
  `~/.claude/CLAUDE.md`, so the Claude nudge covers it.
- (c) codex: the ptk entry was found MISSING and re-registered
  (`codex mcp add`); tools list (`mcp__ptk.` dot naming); headless exec
  still auto-denies MCP calls (known 2026-07-02 limitation,
  re-confirmed v0.143.0) — interactive use is the codex path.
- (d) agy: registered via the documented `mcp_config.json`; tools appear
  (unprefixed), live state call answered; headless auth worked. Plugin
  hook demonstration deferred to slice 4 as planned.

Probes were run THROUGH ptk itself (background jobs + polling) — the D3
machinery carried its own verification. Codex loop per implementation
slice from here.

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
| agy (Antigravity) | DOCUMENTED (agy's bundled docs on this machine, `antigravity-cli/builtin/skills/agy-customizations/docs/`): global `~/.gemini/config/mcp_config.json`, `mcpServers` map, stdio entries `command`/`args`/`env`; file VERIFIED present (2026-07-08, empty). Plugins can carry their own `mcp_config.json`. NOTE: agy's sandbox blocks it from reading its own config — probes run from outside or by the owner | DOCUMENTED and the conflict RESOLVED on paper: hooks live in `hooks.json` in a customization root (`~/.gemini/config/` globally, `.agents/` per-repo — the ledger's settings.json claim was wrong/stale) or inside a plugin. PreToolUse with `matcher: run_command`, stdin camelCase JSON (`toolCall.name`, `args.CommandLine`), stdout `{"decision":"deny","reason":"..."}` — full deny-with-guidance, plus `ask`/`force_ask`. Live firing still to prove (slice 0) | DOCUMENTED: directory-based `GEMINI.md`/`AGENTS.md` rules, walked up from cwd; plugins carry `rules/*.md`. No global `~/.gemini/GEMINI.md` exists yet on this machine |
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
4. **agy leg — ship a PLUGIN.** agy's documented packaging fits ptk
   exactly: one user-level plugin at `~/.gemini/config/plugins/ptk/`
   bundling `plugin.json`, `mcp_config.json` (registration),
   `hooks.json` (PreToolUse `run_command` deny-with-guidance — agy's
   documented equivalent of the Claude redirect), and `rules/ptk.md`
   (the nudge). Install = write one directory; uninstall = remove it.
   Slice-0 probes that remain: does auto-discovery from the global root
   suffice or is a `plugins.json` enablement entry needed; the
   namespaced tool name the deny text should reference; and one live
   deny-and-reissue demonstration before the hook part ships (the same
   verify-once bar as Claude).
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
