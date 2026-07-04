# Plan: Unified shell routing — one tool, hook-enforced

**Status:** Draft — awaiting owner approval. No code until approved.
**Decision basis:** 2026-07-04 owner amendment to the continuation decision in
`.agents/decisions.md`: ptk becomes the single tool surface for all shell work,
enforced by a harness redirect hook, ahead of the ~2026-07-20 go/no-go (which now
evaluates this product with the hook installed).

## Goal

The model has exactly one shell tool worth reaching for, and compression happens
for everything that supports it:

- **PowerShell scripts** run in the warm runspace (existing `ptk_invoke` path;
  objects → `Compress-PtcObject`, log-shaped text → rtk, other text verbatim).
- **Simple native command lines** (git, npm, docker, cargo, ...) route through
  rtk so its per-command filters apply (the 60-90% savings live there, not in
  `rtk log`).
- **A PreToolUse hook** on the harness's Bash and PowerShell tools redirects
  shell work to ptk, so adoption does not depend on model discipline (the
  0/13 dry-run and rtk instruction-decay evidence).

## Design commitments (from prior decisions, not renegotiated here)

- The rtk leg executes **inside the warm runspace** as a script rewrite
  (`<cmd>` → `rtk <cmd>` or the probe-chosen form), never a separate child
  process manager: cwd/state live in the runspace, and the existing timeout,
  cancellation, exit-code reporting ($LASTEXITCODE snapshot/restore), and
  shaping machinery apply unchanged.
- **No maintained command list.** The owner rejected "maintained debt list"
  designs in the universal-wrapper exploration. Which commands rtk can filter
  must come from rtk itself (probed at startup or per call) or from a
  try-with-fallback contract — never a hand-curated table in this repo.
- ptk is **not a security boundary** (recorded threat model); the hook is an
  adoption device, not a guard. The destructive-cmdlet gate stays paused.

## Slices

0. **Probe, then freeze the design.** Evidence-gathering only, results recorded
   in this plan before any implementation:
   - rtk interface: how to enumerate/detect supported commands without a list
     (`rtk --help`, config, or unknown-command behavior); whether an
     unsupported command passes through or errors; whether rtk propagates the
     wrapped command's exit code and stderr faithfully; cwd semantics;
     `rtk proxy` as the raw escape hatch.
   - Hook mechanics on this harness/box: PreToolUse matcher for Bash and the
     PowerShell tool; deny-with-guidance vs command-rewrite capability; where
     it lives (user-level `~/.claude/settings.json` vs per-project); how the
     model behaves after a deny (does it reliably switch to ptk_invoke).
   - Loop/friction cases: agent sessions in THIS repo (hook would fire on our
     own verification commands); interactive one-liners; commands ptk cannot
     run (true bash-isms on Windows).
1. **Routing leg (module/server).** Detect a simple native command line via
   PowerShell AST (single pipeline, single command element, name resolves to a
   native application), rewrite to the probe-chosen rtk form, execute in the
   warm runspace; anything else runs as today. Route override argument on the
   tool (`route=auto|pwsh|rtk|raw`) for explicit control; `raw=true` keeps its
   current meaning (no shaping). Guard: routed and non-routed calls covered by
   Pester + dotnet tests incl. exit-code fidelity through the rtk leg.
2. **Tool surface.** Reposition `ptk_invoke`'s name/description as the single
   shell tool ("run any shell command; output comes back compressed") so the
   surface matches the hook's redirect text. Guard: handshake + live check.
3. **Redirect hook.** PreToolUse hook on Bash/PowerShell tool use that blocks
   with guidance to use ptk (or rewrites, if the probe shows the harness
   supports it), with an explicit off switch and an escape hatch for commands
   ptk cannot serve. Deliverable includes install location + uninstall note.
   Guard: live-session check (hooked Bash call lands in ptk_invoke); friction
   log started for the go/no-go.
4. **Docs + battery.** Full suites, handshake, live spot-checks; update
   `.agents/state.md`, the decision entry (probe results + any scope
   corrections), `README.md`/`server/README.md` surface description.

## Open questions (owner input at approval)

- **Hook scope:** user-global (all projects on this box) or per-project? A
  global hook fires in this repo's own dev sessions too — including agent
  verification commands like `dotnet test`; the escape-hatch semantics decide
  whether that is friction or fine.
- **True bash syntax** (heredocs, `&&` chains with bash-isms) on Windows: out
  of ptk's scope (stays on the native Bash tool via the escape hatch), or
  routed to rtk anyway since rtk itself shells out?
- **Naming:** keep `ptk_invoke` (continuity with recorded live checks) or
  rename (`ptk_run`/`ptk_shell`) for the one-tool story? Renaming invalidates
  recorded harness allow-decisions.

## Risks

- **Hook friction is the product risk:** a deny-per-call redirect adds a
  round trip every time the model reaches for Bash. If that friction makes the
  owner disable the hook, the amended go/no-go criterion fails on its own
  terms — the friction log in slice 3 exists to catch this early.
- **rtk fidelity:** if rtk swallows exit codes, reorders stderr, or mangles
  interactive/ANSI output, the rtk leg silently degrades tool output the model
  acts on. Slice 0 exists to find this before the design freezes; the round-2
  review history (rtk clobbering $LASTEXITCODE) is the precedent.
- **Windows PATH/environment drift:** the server's PATH sees rtk only after a
  session restart post-install (recorded 2026-07-03); the hook must not
  redirect to a ptk that cannot see rtk — degrade visibly, never silently.

## Non-goals

- The `ptk <cmdlet>` CLI universal wrapper (separate open decision, stays
  paused). The destructive-cmdlet gate (stays paused). Any change to the
  go/no-go date. ollama/local-model shaping (dropped 2026-07-03).
