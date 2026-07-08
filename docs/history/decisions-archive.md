# Decisions Archive

Historical decisions that have been adopted or superseded. Entries are moved here verbatim from `.agents/decisions.md` when their live rule now lives in its canonical home.

---

## Adopted 2026-06-26 - Adversarial review findings (PwshTokenCompressor.psm1)

All 11 findings fixed in commits a175d55..c3ca8d6 (2026-06-26). 27/27 tests passing. Final Opus review: Ready to merge.

### 2026-06-26 - Adversarial review findings (PwshTokenCompressor.psm1)

A full adversarial review of the module surfaced the findings below. All 19 Pester
tests pass, but several pass by luck or mask the defects. Findings are verified at
runtime where marked. None acted on yet; recorded here rather than fixed on the spot.

Recommendation: fix High items 1-3 and Medium item 4 before relying on the module;
each as its own commit per the one-fix-per-commit rule.

**High**

1. **Arg parser breaks on dash-prefixed values / negative numbers** -
   `Invoke-PtcBoundCommand` (psm1:69-85). Any token starting with `-` is treated as
   a flag name; the next token is its value unless it also starts with `-`. Verified:
   `ptk read f -MaxLines -5` -> "A parameter cannot be found that matches parameter
   name '5'"; `ptk grep "-Force" ./src` -> cannot search any pattern starting with
   `-`. Values like `-1`, `-v2.0`, regex `-\d+`, or path `-foo` are unreachable.
   Options: pass through to the real binder; or treat a token as a flag only if it
   matches `^-[A-Za-z]` (not `^-\d`) and add a `--` end-of-options sentinel.

2. **Stale `LASTEXITCODE` reported as failure** - `Invoke-PtcRun` ScriptBlock path +
   `Get-PtcLastExitCode` (psm1:50-54, 891-900). Reads global `$LASTEXITCODE`, which
   only reflects the last native exe, not a scriptblock. Verified: with leftover
   `LASTEXITCODE=7`, `ptk run { [pscustomobject]@{Name='ok'} }` appends a false
   `[exit] 7`. Options: reset `$global:LASTEXITCODE = 0` before invoking; or only
   surface an exit code when a native command actually ran.

3. **Required test fixture is git-ignored -> fresh clone fails** - `.gitignore:3`
   (`*.log`) vs Tests.ps1:89. `tests/fixtures/SmallLog.err.log` is read by a test but
   untracked (`git ls-files` confirms absent); the suite is green only because the
   file exists locally. Clean checkout -> CI failure. Options: `git add -f` the
   fixture; or rename it off the `*.log` pattern (e.g. `SmallLog.errlog`).

**Medium**

4. **Markdown code blocks counted double** - `Compress-PtcMarkdownSummary`
   (psm1:409). The fence regex matches both opening and closing ` ``` `.
   Verified: a single ` ```powershell ` block reports `2 code blocks` and
   `code: plain=1, powershell=1` (closing fence becomes a phantom `plain` block).
   Fix: count fences pairwise; take only odd-indexed openers.

5. **Comment stripping corrupts code via string/URL false positives** -
   `Remove-PtcGenericComments` (psm1:280). `^(//|#)` drops any line starting with
   those tokens even inside multi-line strings; `"""`/`=begin`/`=end` matching is
   language-agnostic and fires on non-Ruby/Python files; no inline or nested-block
   awareness. The `minimal` level is meant to be near-lossless but is not. The
   `Use-PtcNeverWorse` guard checks length, not correctness, so corrupted-but-shorter
   output is returned.

6. **`Use-PtcNeverWorse` guards size, not fidelity** - (psm1:214-225). Compares only
   `.Length`; output that silently drops a body/line counts as "better." Consider
   renaming to `Use-PtcShorter` and documenting that fidelity is not guaranteed above
   `none`.

**Low / quality**

7. **`Compress-PtcCodeAggressive` regex misses declarations** - (psm1:393-396).
   Anchored at `^` after trim, so indented members, C# methods (no `function`
   keyword), decorated/attributed lines, and `export default` are dropped. README
   oversells "keep signatures" for C#/Java.

8. **`$args` automatic variable shadowed** - `Invoke-PtcList` (psm1:723). Harmless
   now (splat only) but a smell; rename to `$gciArgs`.

9. **`Invoke-PtcRun` string path interpolates `$temp` into generated script** -
   (psm1:903-919). GUID names are safe, but a temp path containing `'` would break
   the generated script. Prefer passing the path via env var / `-Args`.

10. **`Format-PtcTable` magic number 50** - (psm1:155,168). Column width hard-capped
    at a literal 50, unrelated to `$Width`/`$DefaultWidth`; inconsistent truncation.
    Extract a constant.

11. **Test weaknesses** - Tests.ps1. Line 66 (`Should -Match '\+'`) is too loose;
    no test covers the arg parser with `-`-prefixed/negative values or the markdown
    double-count - exactly the broken paths, so green status overstates correctness.

---

## Closed 2026-07-08 - Universal PowerShell wrapper (dissolved by the greenfield design adoption)

Closed by the 2026-07-08 greenfield-design adoption entry in `.agents/decisions.md`:
`ptk_invoke` is the universal surface, so the question this entry asks no longer
has an object; the CLI face itself is retired by that plan's D5 (deferred until
after the go/no-go window). Entry preserved verbatim below.

### OPEN (2026-06-27): Whether to build the "universal PowerShell wrapper" rearchitecture

**Status:** Open - deferred by owner to decide later. No code change authorized. This
entry records the design exploration so a future session can resume without
re-deriving it.

**Question:** Should ptk grow a universal pass-through path - `ptk <any cmdlet ...>`
runs the command and compresses whatever it returns - replacing the current
fixed-verb dispatch? And if so, in what form?

**What triggered it:** `ptk Get-ChildItem` prints the help screen instead of running,
because the dispatch is a `switch -Regex` (`Invoke-Ptc`, src/PwshTokenCompressor.psm1)
whose arms match only short aliases (`ls|dir|gci|list`), with a `default` arm that
prints usage. Owner rejected three narrower fixes in turn (add cmdlet names to the
regex; a hashtable dispatch + alias table; an AST allowlist) as variations on a
"maintained debt list."

**Verified evidence gathered this session (keep - expensive to re-establish):**

- **RTK is text/stream-first and cannot compress PowerShell objects.** RTK (`../rtk`,
  upstream `rtk-ai/rtk`) has ~100 hand-written per-command proxies plus a universal
  `rtk summary <cmd>` that runs any command via `sh -c` / `cmd /C` and filters the
  **text** output. By the time RTK sees PowerShell output it is already
  `Format-Table` text; the objects are gone. ptk's reason to exist is the README's
  claim: compress objects *before* formatting. (Confirmed by reading RTK source:
  `src/main.rs`, `src/cmds/system/summary.rs` `detect_output_type`,
  `src/core/runner.rs` `run_passthrough`.)
- **Both agent harnesses run a fresh, cold PowerShell process per tool call by
  default.** Confirmed directly: Codex (`codex exec`, self-report) =
  `pwsh -Command "<string>"` per call, no persistence except an explicitly retained
  PTY (`write_stdin`/`session_id`). Claude Code (claude-code-guide agent, citing
  code.claude.com/docs) = fresh process per call; dedicated PowerShell tool
  auto-detects `pwsh.exe`/`powershell.exe`, `-ExecutionPolicy Bypass`, profiles not
  loaded; no REPL. Neither persists env/modules/sessions between default calls.
- **Owner's real Exchange workflow works because of a warm HOST process, not harness
  state.** Hybrid Exchange; `Get-Queue` is on-prem only; on-prem EMS takes 30s+ to
  connect to the CAS server; owner's `$PROFILE` auto-connects only EXO, nothing
  on-prem. Yet the agent returns on-prem queue data in ~2s, repeatedly. Deduction:
  the agent is running inside an already-open EMS host whose implicit-remoting
  PSSession persists in that process. **Consequence:** if ptk's universal path
  spawned a child `pwsh` (today's `Invoke-PtcRun` string path does), `ptk Get-Queue`
  would launch a cold process with no on-prem session and either fail or eat the 30s
  reconnect - breaking a workflow that currently works. Therefore the universal path,
  if built, MUST run in-process (in ptk's own host session), never a grandchild. The
  README design goal "use `pwsh -NoProfile -NonInteractive` for command-string
  execution" is wrong for this case.
- **Threat model: ptk is not a security boundary.** `ptk <cmd>` would run the same
  string in the same process the harness already spawns for raw PowerShell - identical
  blast radius. Prompt-injection defense belongs at the harness sandbox, not in a
  token compressor. (Matches RTK's stance: `rtk summary` runs arbitrary strings.) So
  no AST allowlist / injection guard on the universal path is warranted; the previous
  session's injection-guard tests on the string path would need deliberate
  reconciliation, not silent deletion, if that path is retired.

**Settled sub-decisions (conditional on building it at all):**

- Drop the `ls`/`ps`/`service`/`grep` verbs - redundant `<cmdlet> | Compress-PtcObject`
  bash-crutch wrappers, subsumed by a universal path. Keep `read`/`smart`/`savings`
  (text/file work, the RTK-derived path), `run`, `compress`.
- Fix `Compress-PtcObject` so a homogeneous `string[]` (e.g. `Get-Content` output)
  routes to the text filter at `minimal` (gentlest, never-worse-guarded) instead of
  being truncated as an object list. Escape hatch = reuse `read`'s
  `-Level`/`-MaxLines`/`-Tail`. This also fixes the standalone
  `ptk run { Get-Content }` truncation bug.

**Why deferred (owner's framing):** ptk is a personal/team tool complementing the
owner's `headroom` PoC on Windows/PowerShell work - not an org-wide tool. headroom
already saved ~39.5M tokens in one day without ptk or RTK. The universal path is the
large, risky piece (in-session execution; retiring hardened code). The owner's
build trigger is "if it shows real benefit," i.e. a measurement condition. Bar to
clear is "real savings on my daily Windows work," not org scale.

**Correction (2026-07-02, owner):** the headroom claim above is superseded. The
~39.5M/day figure was headroom's own metric, not net benefit: its compression
rewrote existing context, causing prompt-cache rewrites whose re-billing made it
NET NEGATIVE, and the owner has stopped headroom. Note the mechanism does not
transfer 1:1 to ptk - ptk compresses fresh tool output before it enters context,
which does not invalidate cached prompt prefixes - but the adoption evidence
against instruction-driven tools does transfer (see the 2026-07-02 continuation
decision below). This entry's premise ("complementing headroom") is stale;
deprioritized accordingly.

**Standing recommendation (for whoever picks this up):** Stage it. (1) Fix the
`string[]` truncation bug and (2) drop the bash-crutch verbs - both small and
clearly worth it on their own. Then (3) measure object-first savings on a week of
real Windows usage (the `savings`/`Measure-PtcSavings` primitive exists). Only build
(4) the universal in-process wrapper if the data justifies it. Sequences the cost
behind the proven benefit. Each of (1)-(4) is a separate authorized change requiring
its own go.

---

## Closed 2026-07-08 - Go/no-go: 100% GO (owner decision, in-session)

The gate this entry defined is decided: the owner declared an unqualified GO
on 2026-07-08 (ahead of the ~07-20 window, after the greenfield v2 build and
a second live-use feedback batch). ptk continues as an active product. The
destructive-cmdlet parking recorded inside this entry survives on its own
criterion as a fresh Open entry in `.agents/decisions.md`. Entry preserved
verbatim below.

### OPEN (2026-07-02): Whether ptk continues at all — substrate go/no-go after owner vacation

**Status:** Open, AMENDED 2026-07-03 (owner): Phase 2 compression is UNPAUSED —
the owner chose to build it ahead of the go/no-go so the test evaluates the full
product (warm runspace + compression together). Scope set by owner the same day:
`ptk_invoke` output routes objects → `Compress-PtcObject`, log-shaped text → rtk
when an rtk binary is present, all other text → full passthrough; the ollama /
local-model leg of the router experiment is dropped. Plan:
`.agents/plans/phase2-compression.md`. The universal wrapper and the
destructive-cmdlet gate REMAIN paused behind the test. Owner return date is now
~2026-07-20 (was ~2026-07-16). The go/no-go itself is unchanged: unprompted
adoption + experienced benefit on the real Windows box remain the criteria.

**AMENDED 2026-07-04 (owner):** unified shell routing is UNPAUSED — the owner
chose to make ptk the single tool surface for all shell work before the go/no-go,
plus a harness hook that enforces it. Scope set by owner the same day:

- **One tool for everything shell-shaped.** PowerShell scripts run in the warm
  runspace (the existing `ptk_invoke` path); log-shaped output routes to rtk
  (the existing `Compress-PtcOutput` leg); simple native command lines (git,
  npm, docker, ...) route to rtk so its per-command filters apply — compression
  happens for everything that supports it and the model has one tool to reach
  for.
- **A harness hook forces the redirect.** A PreToolUse hook on the harness's
  Bash and PowerShell tools redirects shell work to ptk, so adoption does not
  depend on model discipline — the direct answer to the 2026-07-02 headless
  dry-run (0/13 unprompted MCP usage; MCP tools hidden behind ToolSearch) and
  the rtk instruction-decay evidence.

**AMENDED 2026-07-04 (owner, later the same day):** a release/distribution
track is authorized ahead of the go/no-go. The owner judged the current
install story (running the MCP server out of a repo checkout via `dotnet run`)
unacceptable for anyone else to use, and set a **first public release target
of 2026-07-25**: prebuilt self-contained per-platform binaries on GitHub
Releases plus a one-line installer. A publish-and-register script and .NET
tool packaging are dev-only paths, explicitly not the public install story.
Plan (APPROVED by owner 2026-07-04, same day):
`.agents/plans/release-distribution.md`. Owner resolved the plan's open
questions the same day (5 RIDs incl. Windows/Linux ARM, version v0.2.0, one
`~/.ptk` home for payload+config on every platform and install method, winget
as the eventual primary Windows path with readiness — ARP entry, hostable
install logic — built into v0.2.0; resolutions recorded in the plan). One
question stays deliberately open: the public installer's hook default
("decision for later"; must close before the installer slice ships).
Interaction with the go/no-go is deliberate: CI produces only **draft**
releases; the `v0.2.0` tag and the publish click are owner actions after the
~2026-07-20 test window, so a no-go can still end the project with nothing
public shipped.

Consequence for the test: the ~2026-07-20 go/no-go now evaluates the routed
one-tool product with the hook installed; the criteria shift accordingly —
"unprompted adoption" is satisfied by construction on hooked sessions, so the
operative criterion becomes experienced benefit (real time/aggravation saved,
never a tool-reported metric) plus absence of friction that makes the owner
disable the hook. The universal PowerShell wrapper CLI face and the
destructive-cmdlet gate REMAIN paused. Plan (requires its own approval before
code): `.agents/plans/unified-shell-routing.md`. The zero-code alternative
(installing rtk's own Bash-rewrite hook and leaving ptk as-is) was considered
and declined in favor of the routed tool; rtk's hook may still complement the
bash leg if the probe slice finds it cheaper.

**Original status (2026-07-02):** Open - all further building is PAUSED by owner
decision until the test below runs. This gates Phase 2 (compression), the
universal wrapper, and the destructive-cmdlet gate. The warm-runspace server
itself (slices 1-6) is built, verified, and stays registered.

**What triggered it:** the owner stepped back and asked whether ptk is worth it,
citing two pieces of evidence from sibling tools:

- **headroom is stopped, net negative.** Its compression rewrote existing context,
  causing prompt-cache rewrites whose re-billing exceeded the savings. Its
  ~39.5M-tokens/day figure was the tool's own metric, not net benefit. (This
  corrects the claim recorded in the universal-wrapper entry above.)
- **rtk does not get used reliably** when instructed via AGENTS.md; usage is
  model-dependent and decays.

**The generalized finding (owner-confirmed):** tools whose benefit requires the
model's ongoing *discipline* - remembering a compression step whose payoff the
model never experiences - do not get adopted. ptk's CLI face and any
instruction-driven compression inherit this failure mode. Two distinctions keep
ptk from being dead on this evidence alone:

1. **Cache mechanism does not transfer:** ptk compresses fresh tool output before
   it enters context; unlike headroom it does not rewrite cached prompt prefixes,
   so the specific net-negative mechanism is headroom's, not ptk's.
2. **The warm-runspace server is a capability, not a discipline:** on the owner's
   Windows box, ptk_invoke is the only deterministic warm path to EMS/EXO (2s vs
   30s+ or failure). The model experiences that difference directly, which is an
   adoption story the rtk evidence does not already contradict - but it is a
   hypothesis, not a result.

**The test (after owner returns ~2026-07-16):** work normal days on the Windows box
with the server registered and observe two things:

- **Adoption:** does the model reach for ptk_invoke *unprompted* for Exchange/AD
  work?
- **Benefit:** does it save real time and aggravation (not a tool-reported metric -
  see the headroom trap)?

Both yes → Phase 2 (compression riding on an already-adopted tool) earns a second
look. Model ignores it like rtk → archive the project with the finding recorded.

**Also parked behind this gate - destructive-cmdlet security layer.** Design was
explored 2026-07-02 through three iterations, recorded so it need not be re-derived:
(1) two-tool split with harness permissions (ptk_invoke / ptk_invoke_unsafe) -
REJECTED by owner: a sticky "always allow" grant on the unsafe tool silently
removes the gate; (2) server-enforced per-call OS approval dialog - REJECTED by
owner: too cumbersome, unworkable headless/automation; (3) declarative policy file
outside the workspace, default read-only, destructive commands refused unless
pre-authorized, classification via each cmdlet's own SupportsShouldProcess /
ConfirmImpact metadata plus alias resolution, fail-closed on unknowns/natives -
tentatively acceptable to owner. All variants are guardrails against model
sloppiness, NOT security boundaries (the model has raw shell access; recorded
threat model unchanged). Interim posture: keep ptk_invoke on ask-per-call in the
harness; build the policy gate only if real usage creates the desire to
blanket-allow ptk_invoke.
