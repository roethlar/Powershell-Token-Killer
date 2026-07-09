# Plan: shell dialect — bash-shaped input, honest fast failures, raw posture

**Status:** APPROVED — owner, in-session 2026-07-09. D1 = option (a) as
recommended; D2 = non-breaking subset as written; D3 as written; the #4
comment's four acceptance suggestions are reconciled under D2. Sources:
GitHub issue #3 (item 1 only) and issue #4 (problems 1-3), triaged
2026-07-09. Slice 0 runs first and freezes its results into this file
before any implementation.

## Problem

Verified against the code, not just the issue text:

1. **Bash-shaped scripts fail late and confusingly.** The hook redirect
   tells the model to re-issue "this same command" through `ptk_invoke`
   (`scripts/ptk-hook.ps1:57-64` — the deny reason never interpolates the
   literal command text), and agents compose bash by habit, so
   bash-shaped strings arrive at the router by design. The router is
   deliberately infallible — a script with parse errors returns unchanged
   into the pwsh runspace (`src/PwshTokenCompressor.psm1:523`) — so
   bash-only constructs are never refused at the door: they die later as
   pwsh parse errors, or worse, parse fine with different semantics
   (backticks are pwsh escape characters, so `` `cmd` `` substitution
   silently degrades to a literal; `export X=1` becomes a runtime
   CommandNotFound). Nothing in the chain names the dialect mismatch.
2. **`raw=true` is described neutrally and invisibly.** "Skip output
   compression and return plain formatted text"
   (`server/PtkMcpServer/Tools/InvokeTool.cs:28`) reads as a preference;
   issue #4 observed an agent using raw on 3/3 calls out of fidelity habit
   — complying with the redirect while zeroing the compression value.
   Nothing counts or surfaces raw usage.
3. **The deny and nudge texts invite the mismatch.** The hook deny names
   the "persistent warm PowerShell runspace" (`scripts/ptk-hook.ps1:57-64`)
   but never says bash-only syntax must be translated or wrapped; the
   ptk_init nudge block (`scripts/ptk_init.ps1:115-128`) says "use
   ptk_invoke for shell commands" with no dialect note.

**Unverified mechanism claim — MUST be probed before design freezes.**
Issue #3's repro (`cd /path && node scripts/build.mjs` failing with
"No such file or directory (os error 2)") carries Rust/rtk error phrasing,
but this build's resolver cannot route a `&&` chain to rtk: a pipeline
chain fails the single-`PipelineAst` check
(`src/PwshTokenCompressor.psm1:529`) and runs as valid pwsh, where `cd X
&& node Y` should simply work. Either the repro reached rtk another way,
the reporter's build differed, or the mechanism story in the issue is
wrong. Slice 0(a) pins this; nothing in the design below depends on the
unverified version.

## Goal

Every construct on the probed detection list, arriving by any
non-consenting execution path (foreground or `background=true`, rtk
present or absent), is detected and refused fast — with guidance naming
the offending construct and the platform-appropriate recovery paths —
instead of failing silently-late with a mystery error. Undetected bash
shapes keep today's behavior *by design*: recall is bounded by the
slice-0 inventory (precision principle below), and the D3 texts carry
the dialect lesson for the misses. Explicit `bash -lc '...'` already
runs correctly as a first-class, compressed path (bash is a native
Application, so the string executes via the rtk leg when constant or the
pwsh leg otherwise, and output flows through the compressor either way).
`raw=true` reads and logs as a recovery hatch, not a preference. The
redirect and nudge texts stop inviting the dialect mismatch at the
source.

## Design principles

- **Routing never fails a call — unchanged.** Detection produces a
  classified, labeled refusal *result* (never a thrown routing error);
  anything undetected runs exactly as today. The refusal is the same
  honesty move as teach-at-timeout (greenfield D3): name the problem, name
  both recovery paths, cost one fast round-trip instead of one confusing
  late failure.
- **High precision over recall.** A false positive blocks a legitimate
  PowerShell script — strictly worse than a miss (a miss is only today's
  behavior). Only constructs that are (a) bash-only and (b) fatal or
  semantically treacherous in pwsh enter the detection list, and each
  entry earns its place with a slice-0 probe. Shared-dialect shapes
  (`&&`, pipes, `2>/dev/null`-style redirects, single-quote literalness)
  must never trip it.
- **No magic translation.** Auto-rewriting bash to pwsh is a semantic
  rabbit hole (quoting, expansions, exit-status wiring). The "run bash"
  machinery already exists and is compressed; the gap is detection and
  teaching, not execution.
- **The escape hatch stays simple.** `raw=true` keeps no-questions
  semantics — the D2 bounded-passthrough decision (adopted 2026-07-08)
  deliberately names it as the escape hatch. Posture changes are wording
  and visibility, not gates.

## Open decisions (owner)

- **D1 — remedy for detected bash-only shapes.**
  - (a) **RECOMMENDED:** refuse fast with guidance naming the construct
    and the recovery paths — rewrite in PowerShell, or wrap the whole
    script in `bash -lc '...'` (with the apostrophe-escaping note). The
    recovery advice is **platform-aware, not uniform**: the `bash -lc`
    wrap is offered only when `bash` resolves as an Application in the
    runspace; on a box without bash (true bashisms on Windows are
    commands ptk cannot run — `unified-shell-routing.md:60-62`) the
    guidance leads with rewrite-in-PowerShell. The already-settled
    `PTK_DIRECT` marker (`unified-shell-routing.md:128-130`; semantics
    stay out of scope) is named only as what it is — a hook bypass
    (`scripts/ptk-hook.ps1:34-35` just exits 0; it changes no dialect) —
    useful solely when the harness shell itself can run the construct.
    The refusal must never present it as making a bashism run: on a
    PowerShell-shell harness the same script re-issued under
    `PTK_DIRECT` fails identically. Explicit `route=pwsh` **and** `raw=true` both bypass
    detection: each is consent, and raw already skips routing entirely
    ("executed exactly as written", `RunspaceHost.cs:349-354`), so the
    detector never sees a raw call by construction — this is a documented
    property, not a leak. Smallest change, no semantic surprises.
  - (b) Issue #3's ask: auto-wrap detected scripts in `$SHELL -lc` on
    POSIX. Executes "as intended" but silently switches dialect
    per-platform, inherits bash quoting/env surprises into a tool whose
    contract is PowerShell, and Windows still needs (a).
  - Recommendation: (a); revisit (b) only if real use shows agents do not
    act on the refusal guidance.
- **D2 — raw posture scope.** Adopt the non-breaking subset: reword
  **every model-visible raw surface** to recovery-only ("for recovering
  detail the compressed form lost — not a default; compressed output
  already preserves errors, exit codes, and structure") — the `raw`
  parameter description (`InvokeTool.cs:28`), the tool description's "Set
  raw=true for full uncompressed output" (`InvokeTool.cs:20-21`), the
  ptk_init nudge block (`scripts/ptk_init.ps1:127-128`), the README
  (`:43-44`, `:60-62`, plus the suggested harness note `:165-171`),
  server/README (`:86`, `:93`), **and the compressor's own elision
  markers** — "[N lines elided - use raw=true for everything]"
  (`src/PwshTokenCompressor.psm1:593`, `:605`, `:608`) — the loudest
  in-band invitation, delivered mid-output at the exact moment raw is
  tempting; four Pester assertions pin that marker wording
  (`tests/PwshTokenCompressor.Tests.ps1:1039-1069`) and are updated in
  the same slice, so a half-applied reword fails the battery. Rewording
  only the parameter line while louder surfaces still invite raw changes
  nothing the model acts on. Make raw usage visible (server
  log line per raw call; candidate: a counter in `ptk_state`), **counted
  at the user-call boundary only** — the tool's `raw` parameter in
  `InvokeTool`, never inside `RunspaceHost`: internal probes pass
  `raw:true` (`StateTool.cs:38-41`, `:92-96`; the cwd probe before every
  background job, `RunspaceHost.cs:496-502`) and must not inflate the
  signal (guard test: `ptk_state` and `background=true` leave the
  counter unchanged). Decline for now: first-use gating, justification
  strings, deny semantics — friction on a deliberate escape hatch;
  revisit only with evidence that rewording fails.
  **#4-comment reconciliation (cross-model audit; reconciled at approval,
  2026-07-09):** the comment's four acceptance suggestions map here as
  follows. (1) "No preemptive raw" *is* the recovery-only rewording
  above. (2) "Teach `route=pwsh` + `raw=false` as 'exact execution,
  shaped output'" is ADOPTED into the reword inventory: every reworded
  surface that describes `raw` also names that pairing as the fidelity
  path that keeps shaping (slice-3 assertions cover the pairing phrase
  alongside the marker wording). (3) "Reason/cost gate on unjustified
  raw" is DECLINED — it is exactly the gating/justification friction
  declined above; revisit trigger unchanged. (4) "Raw-usage telemetry in
  `ptk_state`" *is* the visibility counter above, counted at the
  user-call boundary only.
- **D3 — where the dialect line lands.** One added line in the hook deny
  text and the ptk_init nudge block (plus the README routing section):
  the runspace is PowerShell 7 — translate bash-only syntax or wrap it in
  `bash -lc '...'`. NOTE owner action on adoption: installed machines
  carry hook/nudge text from their last install; text changes reach a box
  only after a dev-install re-run (issue-2 lesson).

## Slices

0. **Probes — results frozen into this plan before any implementation.**
   (a) Issue #3's repro verbatim through `ptk_invoke` on this box under
   `route=auto|pwsh|rtk`: pin which leg fails with what error, plus the
   resolver's actual classification of the exact string. (b) Bash-only
   construct inventory from live probes — heredoc `<<`, `export X=`,
   leading `VAR=x cmd`, `[ -f x ]` and `[[ ... ]]` tests, backtick
   substitution, `local`, `set -e`, `if/fi`, `source file`, shell
   function definitions (`f() {`), `for/do/done` loops, process
   substitution `<(...)`, trailing-backslash line continuation — for
   each: pwsh parse error, runtime CommandNotFound, or silent semantic
   change? Only fatal-or-treacherous constructs enter the detection
   list; the list is finite and its misses are accepted (see Goal) — the
   probe's job is to make the high-frequency forms members, not to
   promise recall. Probe the false-positive set too (`&&`, pipes,
   redirects, subexpressions). (c) The `bash -lc` recovery
   path end to end: compression applied, exit code surfaces, cwd
   anchoring, both legs. (d) Snapshot current hook/nudge/README wording.
1. **Detector in the module** (`Test-PtcBashOnlyScript`, or folded into
   `Resolve-PtcInvokeScript`): anchored per-construct patterns from slice
   0, under four structural requirements. (i) *Ordering:* detection runs
   for every non-raw, non-`route=pwsh` script **regardless of rtk
   availability** — `Resolve-PtcInvokeScript` returns before parsing when
   rtk is absent (`src/PwshTokenCompressor.psm1:517-518`), so folding the
   detector in means placing it ahead of that early return (test via the
   no-rtk seam). (ii) *Token-aware matching:* patterns evaluate parser
   tokens/AST, never raw text, so comments and string literals (including
   here-strings carrying bash text) cannot trip it. (iii) *Shadowing
   guard, scoped to session-defined commands:* a leading word that
   resolves to a **user/session-defined** function or alias is not
   classified — a user-defined `export` function is legitimate pwsh
   (same resolution discipline as `:552-566`). The exemption does NOT
   extend to default built-in aliases: `set` resolves to `Set-Variable`
   in every pwsh (verified: `set -e` dies with "Missing an argument for
   parameter 'Exclude'"), so a blanket resolves-therefore-exempt rule
   would strike `set -e` off its own detection list — for collision
   names like `set`, classification keys on the probed full argument
   shape, never the leading word alone. Resolution context must also
   match execution context: a `background=true` script runs in a cold
   `pwsh -NoProfile` child (`JobManager.cs:82-91`), so its shadow check
   resolves against the cold default command table, never warm session
   state — a warm-defined `export` function must not exempt a background
   script that will die without it. In BOTH contexts the script's own
   text joins the table: a definition of the name lexically preceding
   the use site in the same submitted script (`function export {...}`,
   `Set-Alias export ...`) marks that name shadowed for the remainder
   of the script — the detector already walks the token stream, so it
   records definitions as it goes; `function export {
   param($Assignment) "ran:$Assignment" }; export X=1` is valid in a
   cold child and must not be refused (execution is sequential,
   `JobManager.cs:73-79`). (iv) *Recovery
   exemption:* the `bash -lc '...'` wrap itself must never be classified.
   Pester tests per construct plus false-positive guards covering exactly
   these: the recovery wrapper, here-strings/comments containing bash
   text, shadowed names (session-defined AND script-local definitions
   preceding use, in warm and background contexts), multiline scripts
   mixing pwsh with a bash-ish line, and the shared-dialect set.
2. **Server wiring per D1**: detection flows to a labeled refusal result
   naming the construct and the platform-aware recovery paths;
   `route=pwsh` and `raw=true` bypass. Covers **both** execution paths:
   the background branch returns before any route handling today
   (`InvokeTool.cs:47-61`) and compiles the text in a cold pwsh
   (`JobManager.cs:73-79`), so detection must run before `jobs.Start` — a
   detected script is refused fast, never started as a job that dies in
   its log. Guard tests on both paths; handshake (server-facing).
3. **raw posture per D2**: reword every surface in the D2 inventory
   (tool + parameter descriptions, ptk_init nudge block, both READMEs,
   and the compressor's elision markers with their four Pester
   assertions) in one pass — partial rewording leaves the louder surface
   winning. Raw-usage visibility: server log line + `ptk_state` counter
   at the user-call boundary only. Verification is positive as well as
   negative: one user `raw=true` call increments the counter exactly
   once, surfaces in `ptk_state`, and emits the log line (a permanently
   zero counter must fail the battery, not satisfy it); the negative
   guard — `ptk_state` and `background=true` internal probes leave the
   counter unchanged — rides alongside. dotnet tests where testable.
4. **Texts per D3**: the dialect line in hook deny + nudge block + README
   routing section, phrased consistently with D1's platform-aware recovery
   (the hook deny is a static string rendered per-box, so the line either
   stays platform-neutral — "translate bash-only syntax or wrap it in
   `bash -lc '...'` where bash exists" — or branches at install time;
   pick during implementation, never advise `bash -lc` unconditionally on
   a box that cannot run it). Docs slice; owner-action note for installed
   boxes (text changes reach a machine only after a dev-install re-run —
   issue-2 lesson, already flagged under D3).

Each slice: one commit, battery, codex loop per repo precedent.

## Slice 0 results (frozen 2026-07-09)

Box: macOS (darwin), warm server pwsh 7.6.3, node v26.4.0, bash
`/bin/bash`, rtk `/opt/homebrew/bin/rtk`. Probe hygiene note: the first
runtime pass piped child output through `Select-Object -First 2`, whose
pipeline stop contaminated exit codes; every runtime value below is from
the clean re-probe (full capture first, truncate after).

**(a) Issue #3 repro: does not reproduce; mechanism claim pinned false
on this build.** `cd /tmp/ptk-slice0 && node scripts/build.mjs` (real
directory, real script) ran verbatim through `ptk_invoke` under
`route=auto`, `route=pwsh`, and `route=rtk` — success all three, no
failure of any shape. Resolver classification of the exact string:
returned **unchanged** (pwsh leg); contrast `git status --short` →
`& '/opt/homebrew/bin/rtk' git status --short`. The resolver never
rtk-wraps a `&&` chain (the single-`PipelineAst` check holds), so the
issue's "No such file or directory (os error 2)" rtk phrasing cannot
arise on this build. Design proceeds on the verified failure modes in
(b) only, as the plan anticipated.

**(b) Construct inventory.** Parse = `[Parser]::ParseInput`; runtime =
cold `pwsh -NoProfile` child (the `JobManager` context; the warm
runspace shares the same parser). Detection-list membership follows the
precision principle.

*Fatal at parse — cold, but no error names the dialect:*

- heredoc `cat <<EOF …` — 3 errors, `MissingFileSpecification`
  ("Missing file specification after redirection operator"); actively
  misleading (names redirection, not heredocs). **IN.**
- `[ -f x ]` / `[[ -f x ]]` — `MissingTypename` ("Missing type name
  after '['"). **IN** (keyed to the spaced test shape, never type
  literals).
- `if [ … ]; then …; fi` — `MissingOpenParenthesisInIfStatement`. **IN.**
- `f() { … }` — `ExpectedExpression` ("An expression was expected after
  '('"). **IN.**
- `for i in …; do …; done` — `MissingOpenParenthesisAfterKeyword`. **IN.**
- `<(…)` process substitution — `RedirectionNotSupported` ("The '<'
  operator is reserved for future use"). **IN.**

*Parses clean, dies late as runtime CommandNotFound (exit 1):*

- `export X=1` — "The term 'export' is not recognized …". **IN.**
- `FOO=bar echo hi` — "The term 'FOO=bar' is not recognized …". **IN.**
- `local x=1` — same shape. **IN.**
- `source ./env.sh` — same shape. **IN.**

*Silent or treacherous semantic change:*

- `` echo `date` `` — parses clean, prints literal text, **exit 0**: the
  worst case probed — no error at all. **IN**, token-aware paired-substitution
  shape only (a lone pwsh escape/line-continuation backtick is
  legitimate and must never trip).
- `set -e` — `Set-Variable: Missing an argument for parameter
  'Exclude'`, exit 1: a mystery error naming a parameter the agent never
  wrote (probed 2026-07-09, matches the D1 note). **IN**, keyed on the
  probed full argument shape per slice 1(iii).
- trailing-`\` line continuation — prints the first fragment plus a
  literal `\` line, exit 1 (split-statement semantics). **OUT** — a
  trailing backslash is a legitimate line ending for Windows path
  literals; the false-positive risk fails the precision principle.
  Accepted miss; the D3 texts carry the lesson.

*False-positive set — must never trip; all verified clean (exit 0):*
`echo hi && echo there`, `Get-Date | Out-String`,
`node --version 2>/dev/null`, `echo $(1+1)`, `echo 'literal $x'`, and
the recovery wrapper itself, `bash -lc 'echo hi'`.

**(c) `bash -lc` recovery path — verified end to end.** cwd anchoring:
`bash -lc 'pwd'` → the session cwd. Exit codes surface: `bash -lc 'exit
3'` → `[exit] 3`. Compression applies on the recovery path: 40
log-shaped lines generated under `bash -lc` → `[ptk:log via rtk]`
summary. Both legs: a constant `bash -lc '…'` is rtk-wrapped by the
resolver (`& '/opt/homebrew/bin/rtk' bash -lc 'echo hi'`) and executes;
a variable-bearing variant returns unchanged (pwsh leg) and executes
(the log-generation probe carried `$(seq …)` and ran that leg).

**(d) Wording snapshot — the D2/D3 reword baseline (2026-07-09).**

- Hook deny, `scripts/ptk-hook.ps1:57-64`: "Shell commands run through
  ptk: call the ptk_invoke MCP tool with \"script\" set to this same
  command." + cwd-anchor advice + "It runs in a persistent warm
  PowerShell runspace (state and imported modules survive across calls)
  and output comes back token-compressed. Only if the command genuinely
  needs this harness shell (interactive/TTY, or ptk is unavailable),
  re-run it here with PTK_DIRECT in a comment." + a server-down NOTE
  variant (`:63-64`). No dialect line. `PTK_DIRECT` bypass confirmed at
  `:34-35` (plain `exit 0`; changes no dialect).
- Nudge block, `scripts/ptk_init.ps1:120-130`: ends "… ptk_state
  diagnoses session drift; ptk_reset restores factory state; raw=true
  returns full uncompressed output. When the ptk tools are not
  available in this session, use the normal shell tools." No dialect
  line.
- `README.md:43-44`: "`raw=true` returns the complete, uncompressed
  output (as plain formatted text — nothing elided or stripped)";
  `:60-62`: "Per-call escape hatches: `raw=true` returns full
  uncompressed output executed exactly as written; `route=pwsh` forces
  plain PowerShell execution; `route=rtk` forces the rtk rewrite when
  the script shape allows it."; `:165-171`: the suggested harness note,
  ending "`raw=true` returns full uncompressed output."
- `server/README.md:86`: "(`raw=true` returns everything)"; `:93`:
  "`raw=true` skips routing and shaping and returns plain formatted
  text."
- Elision markers, `src/PwshTokenCompressor.psm1:593`, `:605`, `:608`:
  "[{N} lines elided - use raw=true for everything]", "[{N} lines and
  {M} chars elided - use raw=true for everything]", "[{N} chars elided
  - use raw=true for everything]".
- `server/PtkMcpServer/Tools/InvokeTool.cs:20-21`: "Set raw=true for
  full uncompressed output."; `:28` (raw parameter): "Skip output
  compression and return plain formatted text."

Every surface above reads raw as a neutral preference; none names the
recovery-only posture or the `route=pwsh` + `raw=false` pairing. The
slice-3 reword inventory is exactly this list.

## Out of scope

- Issue #3's MCP permission-bypass / policy+audit layer — its own plan,
  separately owner-gated.
- Issue #3 items 2-4 (README warm-runspace scoping, thin missing-exe
  error, timeout hint) — candidate small follow-up batch.
- Bash-to-PowerShell translation of any kind.
- PTK_DIRECT semantics.
- raw gating/justification (declined under D2 unless evidence reopens).

## Verification

Battery per slice (Pester; dotnet test and handshake when server-facing).
Live end-to-end at close: the #3 repro and one representative bash-only
construct through a real session — refusal text observed verbatim, then
the `bash -lc` recovery works with compression. The live pass must also
cover the two paths that dodge the foreground/rtk-present happy path:
the same construct via `background=true` (refused fast, no job started,
nothing dies in a job log) and via the rtk-absent seam (detection still
fires before the `Resolve-PtcInvokeScript` early return). Raw-counter
checks observed live, positive then negative: one deliberate user
`raw=true` call increments the counter exactly once, surfaces in
`ptk_state`, and emits the server log line (a counter that never moves
fails verification rather than passing it); then one `ptk_state` call
and one background job leave the raw counter unchanged. After landing +
owner push, issues #3 (item 1) and #4 get fix references.