# Review status

Workflow: see `.agents/playbooks/reviewloop.md`.
Per-finding detail: see `.agents/review/findings/<id>.md`.

Loop run 2026-07-04 — reviewer: codex (codex-cli 0.142.5), scope: the
release-distribution plan commits `a43897a..e622cba` (docs/governance only).
Process note: fixes are committed directly to `master`, one finding per
commit, per this repo's recorded codex-loop precedent (`.agents/state.md`,
2026-07-04 routing entry) rather than the playbook's per-finding branches —
the scope is prose in governance files, and the owner-gated push boundary
still applies to the whole batch.

## Legend
- `[ ]` Admitted, open (passed intake triage; not yet started)
- `[~]` In progress / pending review
- `[x]` Verified (awaiting owner-gated merge/push)
- `[!]` Contested — declined, disputed, or ruled invalid; awaiting owner adjudication
- `[-]` Declined at intake (kept for the record; no work)

## Findings

| ID        | Severity | Impact (one line)                                                      | Status | Branch |
|-----------|----------|------------------------------------------------------------------------|--------|--------|
| gov-doc-1 | LOW      | Slice 5 (.NET tool) textually violates the one-`~/.ptk`-home commitment | `[x]`  | master (direct, fd52e1e) |
| gov-doc-2 | LOW      | Plan contradicts itself on PTK_MODULE_PATH in the registration          | `[x]`  | master (direct, 2704cb7) |
| gov-doc-3 | LOW      | state.md still says "four open questions" after owner resolved three    | `[x]`  | master (direct, f77b353) |

**Loop CLOSED 2026-07-04T15:11Z:** re-grade at head f77b353 — all three
accepted, zero new findings (codex, codex-cli 0.142.5). The reviewed commits
remain unpushed pending the owner's push go.

---

Loop run 2026-07-04 (later) — reviewer: codex (codex-cli 0.142.5), scope:
release-plan slice 0, commits `baef818..26fd66f` (handshake -ServerCommand
mode + probe-results/state docs), fixes committed directly to `master` per
the recorded precedent. A three-lens pre-commit review (this harness's
subagents) ran before the codex dispatch; its 9 admitted findings were fixed
pre-commit (folded into a8553dc/8161af2) and 1 was declined (the default-mode
guard run did happen — agent's method summary was incomplete).

## Findings (slice-0 loop)

| ID     | Severity | Impact (one line)                                                   | Status | Branch |
|--------|----------|----------------------------------------------------------------------|--------|--------|
| rel0-1 | LOW      | Documented `-ServerCommand <exe> [args...]` form fails binding for space-separated tokens | `[x]`  | master (direct, c80dfbf) |

**Loop CLOSED 2026-07-04T20:33Z:** re-grade at head 4591c9e — rel0-1
resolution accepted, zero new findings (codex, codex-cli 0.142.5). Commits
remain unpushed pending the owner's push go.

---

Loop run 2026-07-04 (test fixes) — reviewer: codex (codex-cli 0.142.5),
scope: `dc68bdc..d0e34d6` (owner-approved fixes for the 3 pre-existing red
Pester tests: 849081d deterministic temp fixtures for the two read-wrapper
tests; d0e34d6 platform-aware ls routing assertion). **CLOSED first pass:
NO FINDINGS** (no_findings=true, reviewed_head_sha=d0e34d6). Suite on this
Mac: 69 passed / 0 failed / 1 skipped.

---

Loop run 2026-07-04 (slice 1) — reviewer: codex (codex-cli 0.142.5), scope:
release-plan slice 1, `d0e34d6..719fd85` (discovery flip 35fd472 +
composition-guard fix dc26c30 + dev-install 10d4a1a + fixes), direct to
`master` per precedent. A three-lens pre-commit review (this harness's
subagents) ran first: 10 unique findings, ALL admitted and fixed in-tree
before commit (running-server refusal, settings.json non-touch contract,
Unix hidden-dir Remove-Item -Force, git-absent fallback, explicit-false
switch dispatch, TOML snippet, ~/.ptk-as-file error, add-failure guidance,
partial-home hook fallback, cwd-fallback test vacuity).

## Findings (slice-1 loop)

| ID     | Severity | Impact (one line)                                              | Status | Branch |
|--------|----------|-----------------------------------------------------------------|--------|--------|
| rel1-1 | MEDIUM   | v* tag passed raw to -p:Version fails MSBuild on first rc build | `[x]`  | master (direct, b11eb66) |
| rel1-2 | LOW      | TOML literal string cannot hold an apostrophe path              | `[x]`  | master (direct, 719fd85) |

**Loop CLOSED 2026-07-04:** re-grade at head 719fd85 — both resolutions
accepted, zero new findings (codex, codex-cli 0.142.5). Commits remain
unpushed pending the owner's master push go.

---

Loop run 2026-07-08 (setup-findings docs + multi-harness-init plan) —
reviewer: codex (Codex v0.143.0, gpt-5.5, read-only), docs-only scope
(5f3d69b README findings, 96e8979 plan draft), fixes one per commit.

## Findings (multi-harness-init loop)

| ID     | Severity | Impact (one line)                                                     | Status | Branch |
|--------|----------|-------------------------------------------------------------------------|--------|--------|
| mhi-1  | HIGH     | server/README still claimed the empty .mcp.json registers the server    | `[x]`  | master (direct, 4ccd916) |
| mhi-2  | HIGH     | Docs claimed the hook fails open on a down server (it denies; PTK_DIRECT is the escape) | `[x]`  | master (direct, c4461f9) |
| mhi-3  | MEDIUM   | Lifecycle examples omitted -Global, targeting the wrong settings file    | `[x]`  | master (direct, d39b534) |
| mhi-4  | MEDIUM   | Plan lacked the migration story for the global-by-default flip           | `[x]`  | master (direct, 81b8ace) |
| mhi-5  | MEDIUM   | Grok nudge-home self-report treated as fact (re-grade caught a dangling slice-3 sentence) | `[x]`  | master (direct, 274d4c1 + 08078ee) |

**Loop CLOSED 2026-07-08 (converged):** re-grade RESOLVED x4, NO NEW
FINDINGS; mhi-5's reopen was the one dangling sentence, fixed verbatim
(08078ee) — closed on the trivial-docs-fix convergence precedent. The
multi-harness-init plan remains a DRAFT awaiting owner approval and the
owner's manual agy interview.

---

Loop run 2026-07-08 (v2-feedback fixes + D5 retirement, post-GO) —
reviewer: codex (Codex v0.143.0, gpt-5.5, read-only), fixes committed
directly to `master` one finding per commit per precedent. Scope: the
v2-feedback-fixes plan (42e3480) and slices 1-3 (56b1af3 inheritable NUL
stdin, 9cc74de UTF-8 decoding, 4f957ab teach/nag/probe-null) in one pass;
slice 4 / greenfield D5 retirement (bfc6323) with the re-grades in a
second pass.

## Findings (v2-feedback loop)

| ID     | Severity | Impact (one line)                                                    | Status | Branch |
|--------|----------|------------------------------------------------------------------------|--------|--------|
| v2fb-1 | LOW      | Nag filter's bare "[rtk] /!\" prefix could hide real diagnostics       | `[x]`  | master (direct, 799e421; re-grade RESOLVED) |
| v2fb-2 | LOW      | Plan overstated raw=true as an encoding escape hatch                    | `[x]`  | master (direct, 15c5927; re-grade RESOLVED) |
| d5-1   | LOW      | README claimed the checkout is project-registered (.mcp.json is empty)  | `[x]`  | master (direct, 427f05f) |

**Loop CLOSED 2026-07-08 (converged):** v2fb-1/v2fb-2 re-graded RESOLVED;
d5-1 was the retirement pass's only finding and its fix is a factual
wording correction verified directly against repo records (`.mcp.json`
empty; dev-install the recorded path) — closed on the trivial-docs-fix
convergence precedent rather than a further re-grade pass. Battery at
head: Pester 51/51, dotnet 59/59, handshake PASSED (canonical counts).
Commits unpushed pending the owner's master push go.

---

Loop run 2026-07-08 (greenfield implementation, slices D1/D2/D4/D3) —
reviewer: codex (Codex v0.143.0, gpt-5.5, read-only), one fresh session per
slice commit, fixes committed directly to `master` one finding per commit
per the recorded precedent. Re-grades were folded into the next slice's
dispatch to save passes (noted per row). All findings ADMITTED at triage.

## Findings (greenfield-implementation loop)

| ID    | Severity | Impact (one line)                                                          | Status | Branch |
|-------|----------|------------------------------------------------------------------------------|--------|--------|
| d1-1  | LOW      | Docs still promised byte-exact passthrough after the ANSI strip               | `[x]`  | master (direct, 9ee9886) |
| d2-1  | LOW      | Char window could cut the line-elision marker when both bounds fired          | `[x]`  | master (direct, c446e0c) |
| d2-2  | LOW      | README overstated raw=true as byte-exact (Out-String + TrimEnd)               | `[x]`  | master (direct, 50e0c2c) |
| d4-1  | LOW      | ptk_state ignored probe failures; failed listAvailable probe cached           | `[x]`  | master (direct, b17493d + 29f1de5 — first fix graded NOT RESOLVED for non-terminating errors, completed as d4-1b) |
| d3-1  | MEDIUM   | A wedged shaping call (hung rtk) held the gate forever                        | `[x]`  | master (direct, 3938666) |
| d3-2  | MEDIUM   | Job children lacked the execution-policy bypass (dddbb6b class)               | `[x]`  | master (direct, 1a11c46 — two vacuous guard drafts discarded; capability-based test proven red/green) |
| d3-3  | LOW      | Parse errors in background scripts never reached the job log                  | `[x]`  | master (direct, d10e9ee) |

**Loop CLOSED 2026-07-08:** final re-grade at head d10e9ee — d4-1b/d3-1/
d3-2/d3-3 all RESOLVED, NO NEW FINDINGS (codex, Codex v0.143.0, gpt-5.5).
Battery at head: Pester 76/76, dotnet 57/57 (new canonical count),
handshake PASSED on the four-tool surface. Commits unpushed pending the
owner's master push go.

---

Loop run 2026-07-08 (greenfield design plan) — reviewer: codex (Codex
v0.143.0, gpt-5.5, read-only), scope: the owner-requested greenfield design
document `.agents/plans/greenfield-design.md` at `991a79d` (docs-only; the
plan text is the artifact — no guard proofs, per the plan-review precedent
above). Fixes committed directly to `master`, one finding per commit, per
the recorded precedent. All four findings ADMITTED at triage (gfd-4 had
also been self-caught before the verdict arrived).

## Findings (greenfield-design loop)

| ID    | Severity | Impact (one line)                                                        | Status | Branch |
|-------|----------|---------------------------------------------------------------------------|--------|--------|
| gfd-1 | MEDIUM   | Teach-at-timeout text sent warm-state workloads to cold background jobs   | `[x]`  | master (direct, e06733a) |
| gfd-2 | MEDIUM   | ptk_state contract impossible: "no arguments" yet keeps listAvailable     | `[x]`  | master (direct, 24e5266) |
| gfd-3 | MEDIUM   | 5.1 exclusion leaned on the untested PS7 implicit-remoting route as settled | `[x]`  | master (direct, 8963439) |
| gfd-4 | LOW      | ANSI/log-shape mechanism overstated (only the timestamp regex is anchored) | `[x]`  | master (direct, f4f6c3c) |

**Loop CLOSED 2026-07-08:** re-grade at the plan-file state of `f4f6c3c`
(dispatched from the index commit one ahead; the plan file is byte-identical
at both) — all four resolutions RESOLVED, NO NEW FINDINGS (codex, Codex
v0.143.0, gpt-5.5). The plan remains a DRAFT awaiting owner review; commits
unpushed pending the owner's master push go.

---

Loop run 2026-07-08 (slice 2) — reviewer: codex (codex-cli 0.142.5), scope:
release-plan slice 2, reviewed at the `ci/slice-2` branch SHAs `831bcc3`
(ci.yml, three-OS matrix) and `30f283d` (Windows execution-policy fix +
regression test), each in its own read-only codex run. **Both CLOSED first
pass: NO FINDINGS.** The commits landed on `master` by cherry-pick as
`74a2604`/`dddbb6b` (identical content; the branch was deleted per the
owner's no-lingering-branches condition), so the master SHAs differ from
the reviewed SHAs. CI matrix green on all three OSes at `30f283d`
(run 28971482704). Commits remain unpushed pending the owner's master
push go.

---

Loop run 2026-07-09 (multi-harness slice 1) — reviewer: codex (Codex
v0.143.0, gpt-5.5, read-only), scope: commit `057a5ee` (ptk_init per-agent
framework + Claude leg, hook liveness wording, dev-install -Hook chaining,
tests, README sections). Fixes committed directly to `master`, one finding
per commit, per the recorded precedent. Both findings ADMITTED at triage.
Per-finding detail: `.agents/review/findings/mhi-6.md`, `mhi-7.md`.

## Findings (multi-harness slice-1 loop)

| ID    | Severity | Impact (one line)                                                        | Status | Branch |
|-------|----------|---------------------------------------------------------------------------|--------|--------|
| mhi-6 | MEDIUM   | dev-install -Hook ships the blocking hook even when registration was skipped | `[x]`  | master (direct, 1e06351) |
| mhi-7 | LOW      | Nudge writer trims user-owned whitespace outside the marker block          | `[x]`  | master (direct, ec6e094) |

**Loop CLOSED 2026-07-09:** re-grade at head ec6e094 — mhi-6 and mhi-7 both
RESOLVED, NO NEW FINDINGS (codex, Codex v0.143.0, gpt-5.5, read-only;
static pass — Pester was run coder-side: 62/62 at ec6e094, the new
canonical count; dotnet 59/59 at 057a5ee, server untouched since).
Commits remain unpushed pending the owner's master push go.

---

Loop run 2026-07-09 (multi-harness slice 2) — reviewer: codex (Codex
v0.143.0, gpt-5.5, read-only), scope: commit `7a068b9` (the codex leg).
One finding, ADMITTED at triage; fixed direct to `master` per precedent.
Per-finding detail: `.agents/review/findings/mhi-8.md`.

## Findings (multi-harness slice-2 loop)

| ID    | Severity | Impact (one line)                                                        | Status | Branch |
|-------|----------|---------------------------------------------------------------------------|--------|--------|
| mhi-8 | MEDIUM   | Payload gate before the get probe broke leave-as-is for existing registrations | `[x]`  | master (direct, 3caa78f) |

**Loop CLOSED 2026-07-09:** re-grade at head 3caa78f — mhi-8 RESOLVED, NO
NEW FINDINGS (codex, Codex v0.143.0, gpt-5.5, read-only; static pass —
Pester run coder-side: 66/66 at 3caa78f, the new canonical count; dotnet
59/59, server untouched). Commits remain unpushed pending the owner's
master push go.

---

Loop run 2026-07-09 (issue-1 mixed-stream shaping) — reviewer: codex
(Codex v0.143.0, gpt-5.5, read-only), scope: commits `caa7714` (string-
bearing mixed streams render as text) + `66a53df` (heterogeneous header +
samples), the two slices of `.agents/plans/issue-1-mixed-stream-shaping.md`
(GitHub issue #1). One finding, ADMITTED; fixed direct to `master` per
precedent. Writing its guard exposed a second, untracked-by-codex defect
(Select-Object TypeNames mutation) fixed in its own commit. Per-finding
detail: `.agents/review/findings/i1-1.md`.

## Findings (issue-1 loop)

| ID   | Severity | Impact (one line)                                             | Status | Branch |
|------|----------|----------------------------------------------------------------|--------|--------|
| i1-1 | MEDIUM   | mixed-type header grows unbounded with unique type names       | `[x]`  | master (direct, c2d8a4a) |
| i1-2 | MEDIUM   | specialized routes still stamp Selected.* onto deserialized PSObjects | `[x]`  | master (direct, 1e1ab99) |
| i1-3 | LOW      | Format-PtcTable -MaxItems 0 regressed to a first+last wraparound slice | `[x]`  | master (direct, d3f4569) |

Companion fix outside the findings table: 86f990e (Selected.* mutation in
the GENERIC path, self-caught by i1-1's guard before codex saw it; codex
then found the specialized-route remainder as i1-2).

**Loop CLOSED 2026-07-09:** final re-grade at head 1e1ab99 — i1-2 and
i1-3 both RESOLVED, NO NEW FINDINGS (codex, Codex v0.143.0, gpt-5.5,
read-only; static pass — coder-side battery at 1e1ab99: Pester 71/71 (the
new canonical count), dotnet 59/59, handshake PASSED). Two review rounds
total on the issue-1 slices (i1-1 → i1-2/i1-3 → clean). Commits unpushed
pending the owner's master push go; GitHub issue #1 gets the fix
reference after push.

---

Loop run 2026-07-09 (issue-2 stale hook registration) — reviewer: codex
(Codex v0.143.0, gpt-5.5, read-only), scope: commits `bcb24c1` (stable
hook target + stale flagging/healing in ptk_init) + `67fe442`
(dev-install refresh + docs), the slices of
`.agents/plans/issue-2-stale-hook-registration.md` (GitHub issue #2).
Three findings, all ADMITTED, fixed one commit each. Live heal executed
on this box's real stale entry (src\ path → installed copy) as the
end-to-end check. Per-finding detail: `.agents/review/findings/i2-*.md`.

## Findings (issue-2 loop)

| ID   | Severity | Impact (one line)                                                | Status | Branch |
|------|----------|-------------------------------------------------------------------|--------|--------|
| i2-1 | MEDIUM   | Raw text match as hook consent could install an unconsented hook  | `[x]`  | master (direct, 665d99a) |
| i2-2 | LOW      | README overclaimed the dev-install heal (registration-gated)      | `[x]`  | master (direct, ea95d48) |
| i2-3 | LOW      | Directory at the hook path passed existence checks (fail-open)    | `[x]`  | master (direct, 69a2b13) |

**Loop CLOSED 2026-07-09:** re-grade at head 71878c6 — i2-1/i2-2/i2-3 all
RESOLVED, NO NEW FINDINGS (codex, Codex v0.143.0, gpt-5.5, read-only;
static pass — coder-side: Pester 75/75, the new canonical count; dotnet/
handshake unaffected since 1e1ab99). This box's live stale entry healed
(src\ path → `~/.ptk/scripts/ptk-hook.ps1`, effective next session).
Commits unpushed pending the owner's master push go; GitHub issue #2 gets
the fix reference after push.

---

Loop run 2026-07-09 (multi-harness init slices 3-6) — reviewer: codex
(codex-cli 0.142.5, read-only, this macOS box), scope: commits
`86b51ae..6134a2f` — slices 3-6 of `.agents/plans/multi-harness-init.md`
(grok leg 08080ac, wording strip 2356f4c, agy plugin leg 3a802ee,
dev-install multi-harness 1a8092a, install matrix docs 6134a2f). Three
findings, all ADMITTED at intake (evidence verified against the cited
lines before admission). Per-finding detail:
`.agents/review/findings/mhi-{9,10,11}.md`.

## Findings (slices 3-6 loop)

| ID     | Severity | Impact (one line)                                                        | Status | Branch |
|--------|----------|--------------------------------------------------------------------------|--------|--------|
| mhi-9  | MEDIUM   | Non-Claude machines get no harness legs from the one-command install     | `[x]`  | master `ce0caf2` |
| mhi-10 | MEDIUM   | Uninstall skips legs whose CLI left PATH; stale cross-harness state      | `[x]`  | master `fa3620a`+`e8363f3` |
| mhi-11 | MEDIUM   | Pre-existing agy hooks.json survives an install that reports no hook     | `[x]`  | master `6c1d025` |
| mhi-12 | HIGH     | Uninstall orphans codex approval subtables; codex CLI bricks at config load | `[x]`  | master `9d00c6e` |
**Re-grade status:** round 1 (codex, read-only) held mhi-10 NOT RESOLVED
— the no-CLI codex uninstall still reported "no registration to remove"
without reading the config; completion landed at `e8363f3` (grok-leg
parity warning + guard; battery 85/84/0/1). mhi-12 (HIGH, self-found
live: `codex mcp remove ptk` orphans `[mcp_servers.ptk.tools.*]`
subtables and bricks the codex CLI; this box repaired in-session) fixed
at `9d00c6e` — detail: `.agents/review/findings/mhi-12.md`. Round 2 was then dispatched over the mhi-10 completion + mhi-12 at head `d58be68`.

**Loop CLOSED 2026-07-09T17:33Z (converged):** re-grade round 1 (codex, read-only) at
head `3ec608b` cleared mhi-9 and mhi-11; round 2 at head `d58be68` (base
`3ec608b`) graded the mhi-10 completion and mhi-12 RESOLVED with
guard_confirmed=true and NO NEW FINDINGS (codex-cli 0.142.5). All four
mhi-9..mhi-12 findings closed. Commits remain unpushed pending the
owner's master push go.

---

Loop run 2026-07-09 (shell-dialect slice 1) — reviewer: codex (codex-cli
0.144.0, read-only), scope: commit `6694dc5` (base `d369d9a`) — the
token-aware dialect detector `Get-PtcShellDialectFinding` + tests
(shell-dialect plan D1, slice 1). Three findings, all ADMITTED at intake
(each carries evidence + a deterministic predicted failure); fixes direct
to `master`, one per commit, per the recorded precedent. Per-finding
detail: `.agents/review/findings/sd1-{1,2,3}.md`.

## Findings (shell-dialect slice-1 loop)

| ID    | Severity | Impact (one line)                                                      | Status | Branch |
|-------|----------|--------------------------------------------------------------------------|--------|--------|
| sd1-1 | MEDIUM   | Session-shadowed export/local/source would be refused once wired         | `[x]`  | master (direct, ca0a7e2 + bc5638d + 9b5e326 + eb5e193; closed CONVERGED) |
| sd1-2 | MEDIUM   | A later escape or comment can close the backtick pair (FP on valid pwsh) | `[x]`  | master (direct, 8c1c77f; re-grade RESOLVED) |
| sd1-3 | LOW      | Parse-fatal keys take bash evidence from comments/strings                | `[x]`  | master (direct, ceae832 + 4e6a223 + 20ba7fd + f5229a7; closed CONVERGED) |
| sd1-4 | LOW      | Alias-shadowed `set` refused once wired; contests the frozen slice-0 `set` exemption | `[x]`  | master (direct, c43360c — owner unparked the frozen decision and authorized the fix) |
| sd1-5 | LOW      | Expandable-string blanking erases `$()` evidence (miss, inside sd1-3's recorded known gap) | `[-]`  | n/a — declined at intake |
| sd1-6 | MEDIUM   | Space-filler blanking SYNTHESIZES bash shapes (new FP; disproves the "never an over-match" claim) | `[x]`  | master (direct, 5f4b3fa; re-grade round 2 RESOLVED) |
| sd1-7 | LOW      | Parse-fatal error IDs pair with shape evidence globally, not locally      | `[x]`  | master (direct, ef9f3ed + f30ddde + 0c43b05; closed CONVERGED) |

**Re-grade round 1 (codex, Codex v0.144.0, gpt-5.6-sol, read-only) at head
`acb0f39`:** sd1-2 RESOLVED; sd1-1 and sd1-3 NOT RESOLVED (each fix covered
one half of its finding — ambient-only resolution, comment-only blanking);
4 new findings reported, every claim independently verified by the master
in-session before triage (repros re-run at head, including the alias-shadowed
`set` execution proof). Triage: sd1-6/sd1-7 ADMITTED, sd1-5 DECLINED
(recorded known-gap miss), sd1-4 CONTESTED (frozen slice-0 decision — owner
call). Reviewer's read-only battery at head: named guards pass, Pester
119 passed / 1 skipped, worktree clean. Per-finding detail:
`.agents/review/findings/sd1-{1..7}.md`.

**Fix round 2 LANDED (2026-07-09 night):** `bc5638d` (sd1-1 script-local
definitions), `4e6a223` (sd1-3 Generic-fragment blanking), `5f4b3fa`
(sd1-6 non-bridging blank filler), `ef9f3ed` (sd1-7 error/evidence
locality — probed offsets for all eight frozen IN cases satisfy the
overlap-or-after rule). One commit + guard each; every guard's red leg
was live-verified at the pre-fix head. Battery at `ef9f3ed`: Pester
123 passed / 1 skipped, dotnet 59/59.

**Re-grade round 2 (codex, gpt-5.6-sol, read-only) at head `293eda6`:**
sd1-6 RESOLVED; sd1-1/sd1-3/sd1-7 NOT RESOLVED on residual tails
(preceding `Set-Alias` definitions; escaped quotes inside fragments;
argument-position evidence after the error) — explicitly "unresolved
portions, not new IDs"; every tail independently master-verified at head
before round 3. **Fix round 3 LANDED (2026-07-09 night):** `9b5e326`
(sd1-1 Set-Alias/New-Alias tracking + lexical ordering per plan slice
1(iii)), `20ba7fd` (sd1-3 escape-aware fragment patterns), `f30ddde`
(sd1-7 keyword evidence must sit in command position — probed: pwsh
error recovery leaves genuine bash then/done as command names, argument
keywords never). Battery at `f30ddde`: Pester 127/1 skipped, dotnet
59/59.

**Re-grade round 3 (codex, gpt-5.6-sol, read-only) at head `374666b`:**
sd1-1/sd1-3/sd1-7 NOT RESOLVED again — each round-3 fix cleared its
named repro, and the reviewer held each ID on a strictly more crafted
tail (recursion of a function named `export`; a backtick-newline inside
a glued quoted fragment; a function literally named `then`), explicitly
no new IDs for the third consecutive round; all three master-verified at
head. **Fix round 4 LANDED:** `eb5e193` (a use inside its own
definition's body counts as defined), `f5229a7` (`(?s)` — fragments span
newlines), `0c43b05` (defined/resolving keywords are not bash evidence)
— one commit + guard each. Battery at `0c43b05`: Pester 130/1 skipped,
dotnet 59/59.

**Loop CLOSED 2026-07-09 (CONVERGED):** per the recorded convergence
precedent (iterate to NO FINDINGS or contrived-only tails — the ccc9686
disposition), four rounds deep with every named repro guarded; the
residual conceivable tails are crafted inputs in the LOW
already-unparsable harm class or recorded accepted residuals (sd1-1
exotic alias spellings). The owner may reopen or order re-grade round 4.
**Owner RATIFIED the convergence close 2026-07-09 (in-session, later the
same day) — no re-grade round 4 ordered; the loop record is final.**
sd1-4 was subsequently UNPARKED by owner adjudication (in-session,
2026-07-09) and fixed at `c43360c` — `set -e` flags only while `set`
still resolves to the stock `Set-Variable` alias (battery: Pester 132/1
skipped, dotnet 59/59; decision amendment in `.agents/decisions.md`).
**All seven sd1 findings are closed.** Commits unpushed pending the
owner's master push go.

---

Loop run 2026-07-09 (shell-dialect slice 2) — reviewer: codex (codex-cli
0.144.0, read-only), scope: commit `8c234e8` (base `3121fc0`) — the
detector refusal wired on both execution paths (shell-dialect plan D1,
slice 2). Six findings, all ADMITTED at intake; the two live-verifiable
claims (sd2-1 parse failure of the advised escape, sd2-2 warm shadow over
the Application lookup) were master-verified before triage. Fixes direct
to `master`, one per commit, per the recorded precedent. Per-finding
detail: `.agents/review/findings/sd2-{1..6}.md`.

## Findings (shell-dialect slice-2 loop)

| ID    | Severity | Impact (one line)                                                      | Status | Branch |
|-------|----------|--------------------------------------------------------------------------|--------|--------|
| sd2-1 | MEDIUM   | Refusal's apostrophe note is POSIX-layer; following it parse-fails in pwsh | `[x]`  | master (direct, d5dbb48) |
| sd2-2 | LOW      | bash probe ignores warm shadowing; advice can run the shadow, not bash    | `[x]`  | master (direct, 168d027) |
| sd2-3 | LOW      | Background refusal skips LastActivityUtc; idle watchdog can kill mid-use  | `[x]`  | master (direct, c5a3d21) |
| sd2-4 | LOW      | Non-execution guard breaks on apostrophes in the temp path                | `[x]`  | master (direct, fc7a38e) |
| sd2-5 | LOW      | route=pwsh consent unguarded on the background branch                     | `[x]`  | master (direct, 32f181b) |
| sd2-6 | LOW      | Detection-precedes-routing unguarded on the forced-rtk leg                | `[x]`  | master (direct, 79b63fb) |

**Loop CLOSED 2026-07-10T01:38Z:** re-grade at head `79b63fb` (base
`712939b`) — all six resolutions **RESOLVED, NO NEW FINDINGS** (codex,
codex-cli 0.144.0, read-only). Every fix carried a live red-leg proof
(sd2-5/sd2-6 by injecting the exact predicted regression). Battery at
head: dotnet 74/74 (new canonical count), Pester 132 passed / 1 skipped,
handshake PASSED. Commits unpushed pending the owner's master push go.

---

Loop run 2026-07-10 (shell-dialect slice 3) — reviewer: codex (codex-cli
0.144.0, read-only), scope: commit `fa1b23c` (base `d72adcb`) — raw
posture per D2 (recovery-only rewording + raw visibility). One finding,
ADMITTED IN PART: the raw-parameter half fixed; the elision-marker half
DECLINED on coder-verified evidence (elision applies on every shaped
route, so the pairing cannot recover an elided middle — teaching it there
would be misadvice). Detail: `.agents/review/findings/sd3-1.md`.

## Findings (shell-dialect slice-3 loop)

| ID    | Severity | Impact (one line)                                                      | Status | Branch |
|-------|----------|--------------------------------------------------------------------------|--------|--------|
| sd3-1 | MEDIUM   | raw surfaces omit the route=pwsh+raw=false pairing (admitted: raw param; declined: marker surfaces) | `[x]`  | master (direct, 1c92cd6 + D2 amendment — owner-delegated adjudication) |
| sd3-2 | MEDIUM   | Elided job polls advise raw=true, a control ptk_job does not have         | `[x]`  | master (direct, f1c7744; mechanism superseded by 0840d13) |
| sd3-3 | LOW      | Job-poll recovery note fired on marker text the job itself printed        | `[x]`  | master (direct, 302891a + 0840d13; closed round 4) |
| sd3-4 | MEDIUM   | Elision can lengthen text; the shortening gate suppressed genuine notes   | `[x]`  | master (direct, 0840d13, joint with sd3-3) |

**Re-grade round 1 (codex, codex-cli 0.144.0, read-only) at head
`1c92cd6`:** sd3-1 graded **CONTESTED** — the admitted raw-parameter fix
is accepted as correct and guarded, and the reviewer grants the technical
premise (route=pwsh + raw=false cannot recover an elided middle), but
holds that approved D2 requires the pairing per-surface and that truthful
wording can carry both messages; "omitting those surfaces requires owner
amendment". Routed to the owner per the playbook (disagreement is a
recorded verdict, never a silent veto). sd3-2 was filed in the same pass,
ADMITTED, and fixed at `f1c7744` with its red-leg proof. **sd3-1
adjudicated 2026-07-10:** the owner delegated the call under the
agent-experience principle (recorded as an Earned Practice in
`.agents/repo-guidance.md`); the decline stands, the D2 amendment is in
`.agents/decisions.md`, sd3-1 CLOSED.

**Rounds 2-4:** round 2 at head `6bd62e0` graded sd3-2 RESOLVED and filed
sd3-3 (origin-blind note trigger; fixed `302891a` with an anchored-regex +
shortened-text heuristic). Round 3 at head `302891a` broke that heuristic
in BOTH directions with live probes (ANSI strip shortens without eliding;
near-boundary elision lengthens — filed as sd3-4, MEDIUM); both probes
coder-reproduced verbatim and admitted. Fix `0840d13` deleted downstream
inference: `-ElisionHint` rides into shaping and the marker composes the
job's raw-log advice itself, exactly when the module elides.

**Loop CLOSED 2026-07-10 (round 4):** at head `0840d13` (base `302891a`)
sd3-3 and sd3-4 both **RESOLVED, NO NEW FINDINGS** (codex, codex-cli
0.144.0, read-only). All four sd3 findings closed. Battery at head:
dotnet 80/80, Pester 133 passed / 1 skipped (new canonical counts),
handshake PASSED. Commits unpushed pending the owner's master push go.

---

Loop run 2026-07-10 (shell-dialect slice 4, final slice) — reviewer:
codex (codex-cli 0.144.0, read-only), scope: commit `8bb96b1` (base
`f0f3238`) — the D3 dialect texts (hook deny, ptk_init nudge, README
routing section + suggested-note mirror, server/README refusal bullet).
Two findings, both ADMITTED at intake and fixed one commit each.
Per-finding detail: `.agents/review/findings/sd4-{1,2}.md`.

## Findings (shell-dialect slice-4 loop)

| ID    | Severity | Impact (one line)                                              | Status | Branch |
|-------|----------|-------------------------------------------------------------------|--------|--------|
| sd4-1 | LOW      | server/README read as whole-class refusal coverage (recall bound absent) | `[x]`  | master (direct, 428ac82) |
| sd4-2 | LOW      | Nudge guard passed without the concrete bash -lc wrapper phrase   | `[x]`  | master (direct, e576962; red leg 132/1→133/0) |

**Loop CLOSED 2026-07-10:** re-grade at head `e576962` (base `8bb96b1`) —
both **RESOLVED, NO NEW FINDINGS** (codex, codex-cli 0.144.0, read-only).
Battery at head: Pester 133 passed / 1 skipped, dotnet 80/80. **The
shell-dialect plan's live end-to-end Verification pass ran against the
built server over real MCP stdio and passed 11/11** (the #3 repro
un-refused; verbatim export refusal; bash -lc recovery with compression;
background refusal with no job started; raw counter exactly-once +
stderr log line + negative legs; rtk-absent seam). With this close, all
four shell-dialect slice loops (sd1..sd4) are closed and the plan is
COMPLETE. Commits unpushed pending the owner's master push go.

---

Loop run 2026-07-10 (issue-5/6 batch plan draft) — reviewer: codex
(codex-cli 0.144.1, gpt-5.6-sol, read-only), scope: commit `deea88c`
(base `a6a4c5d`) — the DRAFT plan
`.agents/plans/issue-5-6-invoke-semantics.md` (docs-only; the plan text
is the artifact — no guard proofs, per the plan-review precedent). Eight
findings, ALL ADMITTED at intake; the i56p-5 FQID-collision claim was
master-verified live before admission (`Write-Error -ErrorId
NativeCommandError` yields FQID exactly `NativeCommandError`). i56p-7
admitted with the fix scoped to `server/README.md` as the canonical
output-contract location (the completed greenfield-design plan's label
list is a historical record — one-canonical-location invariant). Fixes
direct to `master`, one per commit, per the recorded precedent.

## Findings (issue-5/6 plan loop)

| ID     | Severity | Impact (one line)                                                        | Status | Branch |
|--------|----------|---------------------------------------------------------------------------|--------|--------|
| i56p-1 | MEDIUM   | Gate-held preflight (dialect check, ResolveScript) stays outside the promised total budget | `[x]`  | master (direct) |
| i56p-2 | MEDIUM   | Snapshot-then-probe ptk_state races a new long call and still blocks      | `[x]`  | master (direct) |
| i56p-3 | MEDIUM   | background=true pre-start steps ignore the request budget; expired request still starts | `[x]`  | master (direct) |
| i56p-4 | MEDIUM   | Accepted cold-cwd fallback silently runs a job in the wrong project       | `[x]`  | master (direct) |
| i56p-5 | LOW      | FQID-only partition mislabels forged Write-Error records as [stderr]      | `[x]`  | master (direct) |
| i56p-6 | LOW      | RuntimeException path never captures [exit] N beside preserved stderr     | `[x]`  | master (direct) |
| i56p-7 | LOW      | server/README output contract omits the new [stderr] section              | `[x]`  | master (direct) |
| i56p-8 | MEDIUM   | Model-visible timeout texts become false (queue expiry ≠ recycle)         | `[x]`  | master (direct) |
| i56p-9 | LOW      | Plan offered exception type as unspoofable (Write-Error -Exception forges it) | `[x]`  | master (direct) |
| i56p-10 | LOW     | Busy ptk_state fast path would skip LastActivityUtc (sd2-3 class)         | `[x]`  | master (direct) |

**Re-grade round 1 (codex, codex-cli 0.144.1, gpt-5.6-sol, read-only) at
head `720fc0a`:** all eight i56p-1..8 **RESOLVED**; two new LOW findings
filed (i56p-9, i56p-10), both ADMITTED — i56p-9's forgery claim and the
native record's true discriminator (InvocationInfo.MyCommand.CommandType
= Application; exception type RemoteException is forgeable via
`Write-Error -Exception`) master-verified live in a hosted runspace
before admission; i56p-10 is the fixed sd2-3 class recurring on the new
busy fast path.

**Round-3 status:** i56p-9 (`e242a8b`) and i56p-10 (`62d25b8`) fixed by
applying the reviewer's own suggestions with the live master
verification above. The first round-3 dispatch was blocked by the codex
account usage limit and a retry through the warm runspace wedged and
killed the ptk server mid-review (itself a live #6 datum: ptk_state
blocked 1817s behind the wedged call; also observed: codex called
ptk_invoke from inside its read-only review — an issue-3
permission-surface datum). Fail-closed: no verdict was accepted from the
dead dispatch; round 3 was re-dispatched directly.

| ID      | Severity | Impact (one line)                                                       | Status | Branch |
|---------|----------|--------------------------------------------------------------------------|--------|--------|
| i56p-11 | LOW      | Matrix tests each forgery in isolation; a combined -ErrorId + -Exception forgery passes | `[x]`  | master (direct) |

**Loop CLOSED 2026-07-10 (round 3, CONVERGED):** re-grade at head
`62d25b8` — i56p-9 and i56p-10 both **RESOLVED** (codex, codex-cli
0.144.1, gpt-5.6-sol, read-only, MCP disabled for the re-dispatch; the
reviewer independently re-probed the Application/Script provenance split
on PowerShell 7.6.3). One new LOW tail, i56p-11, is a single
reviewer-specified regression case added verbatim to the matrix — closed
on the trivial-fix convergence precedent (strictly diminishing rounds:
8 → 2 LOW → 1 LOW test-case addition). The owner may reopen or order a
further re-grade. The plan remains a DRAFT awaiting owner approval;
commits unpushed pending the owner's master push go.

---

Loop run 2026-07-10 (issue-5/6 implementation, slices 1-3) — reviewer:
codex (codex-cli 0.144.1, gpt-5.6-sol, read-only), scope: commits
`cf167ce` (slice 1, neutral [stderr]), `04214fc` (slice 2, total
wall-clock budget), `1841e46` (slice 3, never-queueing ptk_state), base
`d16c644`, one pass over the batch per the v2fb precedent. Owner APPROVED
the plan in-session 2026-07-10 (50-words-or-less summary form; slice 0
root cause: system sleep vs monotonic timers — no code defect). Coder-side
verification at head: dotnet 94/94 (all three slices guard-proven by
revert/sabotage: 4+3+3 red legs), Pester 133 passed / 1 skipped, handshake
PASSED, live MCP-stdio issue-repro checks 11/11 (ptk_state 306ms during a
3s call; 1s-budget queue expiry at 1305ms, never executed). Verdict: **10 findings** (2 HIGH, 5 MEDIUM, 3 LOW), reviewed_sha
`0673023` (docs commit atop the reviewed head; code identical to
`1841e46`). Triage: 9 ADMITTED, i56-1 ADMITTED IN PART (late-gate leg
admitted; the completed-work leg DECLINED — returning results that
finished during a sleep race beats discarding them to honor an elapsed
deadline: nothing runs past budget either way, and the caller gets the
answer it paid for). i56-5 extends the i56p-4 fail-closed rationale to
ALL null-cwd cases on the background path (behavior change beyond the
plan letter, recorded here).

## Findings (issue-5/6 implementation loop)

| ID     | Severity | Impact (one line)                                                        | Status | Branch |
|--------|----------|---------------------------------------------------------------------------|--------|--------|
| i56-1  | HIGH     | Sleep race: gate acquired past the wall deadline still executes the expired call | `[x]`  | master (direct, 3bbe389; guard by-inspection - no clock seam) |
| i56-2  | MEDIUM   | Synchronous runspace rebuild on the timeout path can block the response (slice-0 class) | `[x]`  | master (direct, 6f79b70) |
| i56-3  | MEDIUM   | Exit-code bookkeeping ignores the deadline and hides its own recycle      | `[x]`  | master (direct, 6410a06; adds InvokeResult.WarmStateLost) |
| i56-4  | MEDIUM   | Cold pre-start (gate wait, import, detection) escapes the request deadline | `[x]`  | master (direct, 34bf4e6; cold-import delay leg stated untested) |
| i56-5  | HIGH     | Canceled/failed cwd probe still starts the job in the server directory    | `[x]`  | master (direct, 4cd9cdf joint with i56-6 - shared lines) |
| i56-6  | MEDIUM   | A cwd execution timeout is misreported as queue expiry (warm state claim false) | `[x]`  | master (direct, 4cd9cdf joint with i56-5) |
| i56-7  | MEDIUM   | Second listAvailable ptk_state blocks on the cache gate for minutes       | `[x]`  | master (direct, d7fbaac + dd97e12 - first guard draft vacuous, replaced with red-leg proof) |
| i56-8  | LOW      | Busy on the listAvailable leg omits the promised age/waiter line          | `[x]`  | master (direct, 27dcbca; race interleave stated untested) |
| i56-9  | LOW      | Queued ptk_reset invisible to waiter accounting                           | `[x]`  | master (direct, 0be93a3) |
| i56-10 | LOW      | README operational note still equates every timeout with state loss       | `[x]`  | master (direct, 188d421) |

**Re-grade round 1 (codex, codex-cli 0.144.1, gpt-5.6-sol, read-only) at
head `8dffa25`:** i56-2/4/6/7/8/9/10 **RESOLVED**; i56-1, i56-3, i56-5
NOT RESOLVED on sharper legs (late-completing preflight still STARTS the
user pipeline past deadline — the contested decline narrowed to
starting-new-work, which the coder accepts; the bookkeeping floor can
overshoot the advertised budget by 2s; cancellation during bookkeeping
converts to success and still starts a job). Five new findings, ALL
ADMITTED after code verification (i56-11..15 below). Fixes one per
commit follow.

| ID     | Severity | Impact (one line)                                                        | Status | Branch |
|--------|----------|---------------------------------------------------------------------------|--------|--------|
| i56-11 | MEDIUM   | Readiness outcomes collapse: cancel→false timeout; recovery→false queue-expiry text | `[x]`  | master (direct, f767c0c; adds InvokeResult.Recovering + CwdProbeOutcome.Recovering) |
| i56-12 | MEDIUM   | Superseded rebuild overwrites ModuleLoaded/baseline for the post-reset runspace | `[x]`  | master (direct, ccbafa8; red-leg proven via stale failed-import rebuild) |
| i56-13 | MEDIUM   | Expired cold checks accumulate uncancelable queued workers; disposal races | `[x]`  | master (direct, 6d33a94; race legs stated untested) |
| i56-14 | LOW      | Canceled-preflight recycle leaves WarmStateLost false                     | `[x]`  | master (direct, 5476b57 joint with i56-5 reopen - one line) |
| i56-15 | LOW      | Cached listAvailable falsely reports enumeration-in-progress              | `[x]`  | master (direct, 6f6fdca) |

**Loop CLOSED 2026-07-10 (round 3, NO FINDINGS):** re-grade at head
`9a894e1` — all eight round-2 items (the three reopened legs + i56-11..15)
**RESOLVED, NO NEW FINDINGS** (codex, codex-cli 0.144.1, gpt-5.6-sol,
read-only; no_new_findings=true — a clean pass, not a convergence call).
The i56-1 completed-work decline stands as narrowed (work already done
returns its results; nothing STARTS past deadline). All 25 implementation
findings across two rounds are closed. Battery at head: dotnet 100/100,
Pester 133 passed / 1 skipped, handshake PASSED, live MCP-stdio
issue-repro checks 11/11 (canonical counts). Process notes for the
record: one guard was caught vacuous by running its red leg post-commit
and replaced (dd97e12); one careless `git checkout` during a sabotage
proof discarded uncommitted i56-12 work, which was re-applied and
verified before commit — history unaffected. Commits unpushed pending
the owner's master push go; issues #5 and #6 get fix references and
closure after push.
---

Loop run 2026-07-10 (shared-persistent-runspace idea plan) — reviewer:
codex (codex-cli 0.144.1, gpt-5.6-sol, read-only), docs-only scope:
commit `903cfb8` (base `5c0c2cc`), the IDEA-stage plan
`.agents/plans/shared-persistent-runspace.md`. Four findings, ALL
ADMITTED after master verification against `.agents/decisions.md` and
the archive (the draft was written without re-reading decisions.md — a
coder process miss the loop caught). Fixes one per commit. The review
also surfaced repo drift fixed separately at `35ebf35`: the go/no-go was
decided GO 2026-07-08 while state.md/repo-guidance still framed it as
pending.

## Findings (shared-runspace idea loop)

| ID    | Severity | Impact (one line)                                                        | Status | Branch |
|-------|----------|---------------------------------------------------------------------------|--------|--------|
| spr-1 | MEDIUM   | Plan reopens the already-decided GO as its gate                           | `[x]`  | master (direct, db88989) |
| spr-2 | MEDIUM   | N-key sketch conflicts with the recorded attach-only-first staging        | `[x]`  | master (direct, 0ed7bcb) |
| spr-3 | HIGH     | Keyed runspaces presented as an auth isolation boundary (ContextScope Process disproves) | `[x]`  | master (direct, 94f9a3e) |
| spr-4 | MEDIUM   | Busy telemetry presented as making shared state safe between calls        | `[x]`  | master (direct, 550986b) |

**Loop CLOSED 2026-07-10 (converged):** re-grade at head `a91d2a1`
(codex, codex-cli 0.144.1, gpt-5.6-sol, read-only) — spr-1/2/4 RESOLVED,
NO NEW FINDINGS; spr-3's residual was one dangling sketch sentence still
claiming "isolated warm sessions," fixed verbatim per the reviewer's own
identification — closed on the trivial-docs-fix convergence precedent
(the mhi-5 disposition). The plan remains an IDEA record, NOT approved;
the keys-vs-attach-only staging call is the owner's. Commits unpushed
pending the owner's push go.

---

Loop run 2026-07-10 (grok second pass, unpushed docs batch) — reviewer:
grok (grok 0.2.93, read-only sandbox; incantation probed fresh per the
playbook and cached in `harnesses.local.json`), docs-only scope:
`5c0c2cc..ad9aa77` — the shared-persistent-runspace idea plan (post
codex loop) plus the drift fixes and loop records. **CLOSED first pass:
NO FINDINGS** (structured verdict no_findings=true at reviewed head
`ad9aa77`); the reviewer independently re-verified the plan's technical
claims (runspace pipeline behavior, process-scoped env, Graph
ContextScope Process, the GO decision and the OPEN shared-host entry)
against code and records. Two reviewer observations not filed as
findings, handled coder-side: a stale issue-5/6 "gates all
implementation" Next bullet in state.md (fixed with this close) and a
process note that the spr loop used index rows without per-finding docs
(accepted — the trivial-docs-fix convergence precedent covers it).

---

Loop run 2026-07-10 (agy third pass, unpushed docs batch) — reviewer:
agy (Antigravity 1.1.1, sandboxed print mode; incantation probed fresh
per the playbook and cached in `harnesses.local.json` — no JSON mode, so
the verdict contract was prompt-enforced and parsed fail-closed), scope:
`5c0c2cc..09a83dc` (the grok-reviewed batch plus that loop''s own
record). **CLOSED first pass: NO FINDINGS** (valid single-object JSON,
reviewed_sha matches head). Three harnesses (codex, grok, agy) are now
probed, cached, and have each run at least one real loop.

---

Loop run 2026-07-10 (shared-runspace plan amendments) — reviewer: codex
(codex-cli 0.144.1, read-only), docs-only scope: `83d431a..e2003cd` —
the staging adjudication, interaction sketch + admin-CLI requirement,
and web-option amendments to the previously closed spr loop. One
finding, ADMITTED and fixed with the reviewer''s own architecture:

| ID    | Severity | Impact (one line)                                                    | Status | Branch |
|-------|----------|-----------------------------------------------------------------------|--------|--------|
| spr-5 | MEDIUM   | "Daemon-served dashboard is a bolt-on, not a daemon change" self-contradicts | `[x]`  | master (direct, 6de55e4 - fix and record in one commit) |

**Loop CLOSED 2026-07-10 (converged):** the fix adopts the reviewer''s
suggested external-companion architecture verbatim (daemon grows no
HTTP surface; --json stabilizes the data contract, does not make a
dashboard free) — trivial-docs-fix convergence precedent.

---

Loop run 2026-07-10 (rtk-rewrite-routing + security-layer plan drafts) —
reviewer: codex (codex-cli 0.144.1, read-only), docs-only scope: the two
DRAFT plans reviewed at head `63b4cbc` (introduced at `8c524c9` and
`ef4896e`), one dispatch per plan. **20 findings, ALL ADMITTED at
intake** — every disputed factual claim was master-verified before
admission: live rtk 0.43.0 probes (exit protocol 0/1/2/3 read from
`../rtk/src/hooks/rewrite_cmd.rs` and probe-confirmed — the draft's
"passthrough = exit 3" observation was wrong; producer-side pipe
rewrites including `git diff | Set-Content`; `sudo find | wc -l`
bypassing the find-guard; lexical `ls` → `rtk ls`) and repo records
(archived default-read-only policy design; release-plan `policy.psd1`
reservation; ResetTool's KillAll-before-reset ordering). Index rows
without per-finding docs per the accepted spr-loop precedent; fixes
direct to `master`, one per commit.

## Findings (rtk-rewrite-routing plan loop)

| ID     | Severity | Impact (one line)                                                        | Status | Branch |
|--------|----------|---------------------------------------------------------------------------|--------|--------|
| rrp-1  | HIGH     | Emitted literal `rtk` tokens break the PTK_RTK_PATH pin                  | `[x]`  | master (direct, 7975152) |
| rrp-2  | HIGH     | Lexical rewrite tramples PS7 aliases/functions/shims (`ls` → `rtk ls`)   | `[x]`  | master (direct, 818181e) |
| rrp-3  | HIGH     | Producer-side pipe rewrites feed filtered data to consumers (silent corruption) | `[x]`  | master (direct, 4460808) |
| rrp-4  | HIGH     | A hung rewrite eats the call budget; fail-open promise impossible as written | `[x]`  | master (direct, 96243a5) |
| rrp-5  | MEDIUM   | Background jobs silently out of scope while the plan claims all native work | `[x]`  | master (direct, 3d1dafb) |
| rrp-6  | MEDIUM   | route=rtk undefined under rewrite-based routing                           | `[x]`  | master (direct, 01aa10e) |
| rrp-7  | MEDIUM   | Recorded exit-code observation wrong (passthrough=1, not 3); CI has no rtk | `[x]`  | master (direct, 7e63a52) |
| rrp-8  | MEDIUM   | Env/sudo-prefixed find defeats the find-before-pipe guard                 | `[x]`  | master (direct, 3358936) |
| rrp-9  | MEDIUM   | Savings measurement not reproducible by a cold agent                      | `[x]`  | master (direct, fe9e2cf) |
| rrp-10 | MEDIUM   | No slice reconciles the still-active single-command-routing contracts     | `[x]`  | master (direct, 0f22e24) |

## Findings (security-layer plan loop)

| ID     | Severity | Impact (one line)                                                        | Status | Branch |
|--------|----------|---------------------------------------------------------------------------|--------|--------|
| slp-1  | HIGH     | Opt-in gate silently replaces the recorded default-read-only baseline    | `[x]`  | master (direct, 72108c2) |
| slp-2  | HIGH     | Optional background coverage = one-flag bypass of every deny rule        | `[x]`  | master (direct, d635787) |
| slp-3  | HIGH     | Warm-alias classification can bless a cold-context destructive command   | `[x]`  | master (direct, d3da99e) |
| slp-4  | HIGH     | Policy language/evaluator not implementable by a cold agent; psd1 already reserved | `[x]`  | master (direct, d2b247e) |
| slp-5  | HIGH     | "Human-only" policy edit claim contradicts the same-user threat model    | `[x]`  | master (direct, 475af37) |
| slp-6  | MEDIUM   | Reset/job-kill have no policy vocabulary; in-Reset check fires after KillAll | `[x]`  | master (direct, 3964c56) |
| slp-7  | MEDIUM   | One-line-per-call audit cannot represent background execution            | `[x]`  | master (direct, 3bbb007) |
| slp-8  | MEDIUM   | Audit fields omit cwd, control args, policy identity, session fields     | `[x]`  | master (direct, f08e7d0) |
| slp-9  | MEDIUM   | Multi-process rotation and write-failure semantics undefined             | `[x]`  | master (direct, b22d0b5) |
| slp-10 | HIGH     | Verification could pass while the gate's core properties fail            | `[x]`  | master (direct, 0f4d875) |

**Re-grade round 2 (codex, codex-cli 0.144.1, read-only) at head
`f4d5dce`:** security plan — ALL TEN slp findings **RESOLVED**. rtk plan
— seven RESOLVED; rrp-6/rrp-9/rrp-10 NOT RESOLVED on completion tails
(no route=rtk contract actually selected; measurement cases still
categories, not frozen commands; top-level tool description and the
frozen unified-shell-routing rule unnamed) — all three completed one
commit each (`9ef4f8c`, `2617212`, `afced6d`). Four new findings, ALL
ADMITTED (the rrp-11 preference-throw was reviewer-probed live on PS
7.6; rrp-12's transparent-prefix re-prepend is in rtk's own tests):

| ID     | Severity | Impact (one line)                                                        | Status | Branch |
|--------|----------|---------------------------------------------------------------------------|--------|--------|
| rrp-11 | MEDIUM   | Session native-error preferences make a successful rewrite throw; routing silently dies | `[x]`  | master (direct, 1ec00e4) |
| rrp-12 | MEDIUM   | Rebinding host path into `docker exec ... rtk ...` breaks a valid command | `[x]`  | master (direct, 0e3e34e) |
| slp-11 | MEDIUM   | Rotated audit files accumulate unbounded until disk exhaustion            | `[x]`  | master (direct, f4cdc7d) |
| slp-12 | MEDIUM   | Hard-killed server leaves job-start events reading as running forever     | `[x]`  | master (direct, df13998) |

Round-1 statuses: all ten slp rows and rrp-1..5/7/8 flipped `[x]`;
rrp-6/9/10 remain `[~]` pending the round-3 grade of their completions.

**Re-grade round 3 (codex, codex-cli 0.144.1, read-only) at head
`27f40ea`:** rrp-10, rrp-11, rrp-12, slp-12 **RESOLVED** (flipped
`[x]`). Held on sharper legs: rrp-6 (the rrp-12 wrapper-discard path
executed unrouted WITHOUT a label under forced route=rtk — completed
`c287ed5`: every unrouted outcome labeled), rrp-9 (command strings
frozen but inputs weren't — HEAD/worktree drift moves ratios
independently of routing; completed `dc1b2c4`: pinned ref `63b4cbc`,
generated fixture), slp-11 (retention enforced only at startup;
completed `4ae312e`: enforced at every rotation). Three NEW findings,
ALL ADMITTED (rrp-13 reviewer-probed live; rrp-14/15 verified against
`RunspaceHost.cs` and the probed rtk behavior):

| ID     | Severity | Impact (one line)                                                        | Status | Branch |
|--------|----------|---------------------------------------------------------------------------|--------|--------|
| rrp-13 | MEDIUM   | Rewrite child inherits server cwd; rtk reads the WRONG project's rules   | `[x]`  | master (direct, 9f06e5d) |
| rrp-14 | HIGH     | Fixed rewrite bound can exceed remaining budget; outer deadline recycles warm state | `[x]`  | master (direct, b30a1af) |
| rrp-15 | HIGH     | `Set-Alias git ...; git status` — preflight resolution lies for later segments | `[x]`  | master (direct, 0ad0769 + 6e3ef9b + 5320c13 + 38168c9; closed CONVERGED, residual disclosed) |

**Re-grade round 4 (codex, codex-cli 0.144.1, read-only) at head
`20e4084`:** rrp-9, rrp-13, rrp-14, slp-11 **RESOLVED**; NO NEW
FINDINGS — **the security plan is fully closed (all 12 slp findings
resolved).** Two interaction tails held on the rtk plan, both
coder-accepted and completed one commit each: rrp-6 (the rrp-14 budget
skip was a new unlabeled unrouted path under forced route=rtk and
contradicted "always attempted" — completed `851c125`: skip labeled,
wording qualified) and rrp-15 (a native-HEADED statement is not inert —
`true (Set-Alias git Write-Output); git status` executes the embedded
mutation; completed `6e3ef9b`: preceding natives must carry
constant-literal arguments only, counterexample added verbatim as a
regression case).

**Re-grade round 5 (codex, codex-cli 0.144.1, read-only) at head
`577aa8d`:** rrp-6 **RESOLVED** (flipped `[x]`). rrp-15 held on a
redirection-target subexpression (`true > $(Set-Alias ...); git
status`, probed live) — the literal-args enumeration lost again, so
the completion (`955f1d7`) replaced enumeration with a whole-statement
AST WHITELIST, fail-closed on unrecognized node types.

**Re-grade round 6** at head `955f1d7`: rrp-15 held again — ambient
`Set-PSBreakpoint -Command true -Action { Set-Alias git ... }` mutates
resolution from WARM STATE, outside any submission AST (probed live).
The escalation proved static preflight analysis unsound in principle;
completion `5320c13` adopted the reviewer's own round-3 mechanism:
EXECUTION-TIME re-resolution — each routed segment re-resolves its
head immediately before running and falls back to the original
verbatim; the static screen demoted to a cost optimization.

**Re-grade round 7** at head `5320c13`: rrp-15 held once more on a
different-in-kind leg (probed live): substitution SUPPRESSES
name-keyed hooks — a `git` breakpoint never fires when `rtk git` runs
instead, so hook side effects (including mutations later segments
would observe) diverge between routed and unrouted execution. This is
inherent to routing-by-substitution and is already true of the
SHIPPED single-command routing today; no guard or wording can make
`rtk git` fire a `git` breakpoint. Disposition: recorded in the plan
as an inherent, disclosed residual with a slice-4 doc duty (name-keyed
session hooks do not fire on routed segments; `route=pwsh` /
`RTK_DISABLED` is the escape) — commit `38168c9`.

**Loop CLOSED 2026-07-11 (CONVERGED):** per the recorded sd1
convergence precedent — round sizes 20 → 7 → 6 → 2 → 1 → 1 → 1, every
named repro guarded as a regression case, the mechanism genuinely
improved three times (static enumeration → AST whitelist →
execution-time guard), and the terminal tail class (name-keyed hook
divergence) is provably unfixable by any routing-by-substitution
design, including the one already shipped. All 12 slp findings and
rrp-1..14 RESOLVED by reviewer grade; rrp-15 closed by coder
disposition as inherent-disclosed-residual — **flagged for owner
ratification** (reopen or order round 8 to overrule). Both plans
remain DRAFTs awaiting owner approval; nothing in this loop authorizes
implementation. Commits unpushed pending the owner's master push go.

---

Loop run 2026-07-11 (mandatory audit + harness-scoped sessions +
internal RTK routing plan) — requested reviewers: Claude
(Claude Code 2.1.207) and Grok (grok 0.2.93). Docs-only scope:
`.agents/plans/audited-harness-sessions.md`, introduced at
`0652d93` against base `875efa0`. Reviewers are dispatched read-only,
headless, and one-shot with pinned base/head SHAs. Plan fixes follow the
established docs-loop precedent: direct to `master`, exactly one admitted
finding per commit; nothing in this loop authorizes implementation or push.

**Loop ACTIVE.** Live reviewer probes passed on 2026-07-11. An empty findings
table is a valid review result.

## Findings (audited harness sessions plan)

| ID    | Severity | Impact (one line) | Status | Branch |
|-------|----------|-------------------|--------|--------|
| ahs-1 | HIGH     | Missing RTK capture leaves compression and raw recovery mutually contradictory | `[x]` | master (direct, 5288b7b) |
| ahs-2 | HIGH     | Raw transition silently contradicts active D1 consent and shipped guards | `[x]` | master (direct, 12eddd8) |
| ahs-3 | HIGH     | Auto-Bash turns detector false positives into wrong-interpreter execution | `[x]` | master (direct, e0710eb) |
| ahs-4 | MEDIUM   | Exact-script evidence is not part of the durable pre-effect commit | `[x]` | master (direct, 098dcd3) |
| ahs-5 | MEDIUM   | Closing reserved `default` can permanently brick unqualified tools | `[x]` | master (direct, 5c458f8) |
| ahs-6 | MEDIUM   | Reconciliation omits approved routing/dialect contracts this plan replaces | `[x]` | master (direct, 61f6d53 + bca83e2) |
| ahs-7 | MEDIUM   | Warm background-session concurrency and kill semantics are undefined | `[x]` | master (direct, 2f8e419) |
| ahs-8 | LOW      | Fail-closed audit prevents `ptk_state` from reporting the audit failure | `[x]` | master (direct, 5ee1aa3) |
| ahs-9 | MEDIUM   | New Bash execution contract breaks an unnamed load-bearing heredoc refusal guard | `[x]` | master (direct, 2c5774f) |
| ahs-10 | MEDIUM  | Output-handle wording breaks four unnamed load-bearing Pester marker guards | `[x]` | master (direct, 2ca8434) |
| ahs-11 | MEDIUM  | Template-less sessions have no defined cold-background policy | `[x]` | master (direct, baf765e) |
| ahs-12 | HIGH    | Worker protocol on stdout is corruptible by FullLanguage user code | `[x]` | master (direct, 1b21005) |
| ahs-13 | HIGH    | Pre-effect audit has no immutable prepare/commit reservation protocol | `[x]` | master (direct, 05a41e6) |
| ahs-14 | MEDIUM  | Background call and job terminal events have an impossible ordering | `[x]` | master (direct, 58e7d05) |
| ahs-15 | MEDIUM  | Reused worker-local job IDs can target a new generation from a stale call | `[x]` | master (direct, 9527390) |
| ahs-16 | MEDIUM  | Retention may delete audit segments that SIEM never acknowledged | `[x]` | master (direct, a65d6f2) |
| ahs-17 | MEDIUM  | Export-checkpoint audit events can recursively generate forever | `[x]` | master (direct, 3f783b1) |
| ahs-18 | HIGH    | Hard supervisor death can leave a blocked worker or job orphaned | `[x]` | master (direct, 23043b5 + b7677da) |
| ahs-19 | HIGH    | Timeout containment is undefined for dynamically connected sessions | `[x]` | master (direct, f2f4255) |
| ahs-20 | HIGH    | Same-session lifecycle and invocation admissions are not linearized | `[x]` | master (direct, dc3d626) |
| ahs-21 | MEDIUM  | Busy `restart(force=false)` has no defined no-side-effect behavior | `[x]` | master (direct, ab31227) |
| ahs-22 | MEDIUM  | A session alias can be ambiguously rebound to another template/digest | `[x]` | master (direct, 757b994 + c9a14ba) |
| ahs-23 | MEDIUM  | Malformed catalogs and bootstrap path failures have no fail-closed contract | `[x]` | master (direct, 18aebca) |
| ahs-24 | HIGH    | Timed-out bootstrap can later yield an untracked authenticated worker | `[x]` | master (direct, 6fce3af) |
| ahs-25 | LOW     | `ptk_session list` conflicts with a schema that requires `name` | `[x]` | master (direct, c342747 + 4dd8074) |
| ahs-26 | MEDIUM  | Two .NET raw/path guards are omitted from the intentional migration inventory | `[x]` | master (direct, 1f2c9c2) |
| ahs-27 | MEDIUM  | Running-job busy semantics contradict the shipped reset-kills-job contract | `[x]` | master (direct, e36b40b) |
| ahs-28 | MEDIUM  | Output-handle ownership and lifetime across worker generations are undefined | `[x]` | master (direct, af73889) |
| ahs-29 | HIGH    | Timeout replacement has no deadlock-free transition or post-deadline grace | `[x]` | master (direct, 6294340) |
| ahs-30 | MEDIUM  | New audited reads can consume the reserve needed for terminal events | `[x]` | master (direct, 23586fb) |
| ahs-31 | MEDIUM  | Side-effect-free prepare forbids the required external `bash -n` validator | `[x]` | master (direct, e4e261d) |
| ahs-32 | MEDIUM  | Post-launch startup containment can wait forever after its deadline | `[x]` | master (direct, 70e1d39 + 7ec5d7a) |
| ahs-33 | HIGH    | Accepted calls/jobs can overbook the terminal-event reserve | `[x]` | master (direct, 69caf6c) |
| ahs-34 | MEDIUM  | Idle exit can discard an unconfirmed containment quarantine | `[x]` | master (direct, 00bb110) |
| ahs-35 | HIGH    | Unix broker death after arming can remove the hard-parent-death proof | `[x]` | master (direct, f6a20f3) |
| ahs-36 | MEDIUM  | Worker-starting lifecycle tools have no defined startup deadline function | `[x]` | master (direct, da32d9c + 1c23e1b) |
| ahs-37 | MEDIUM  | Canonical state claims Slice 0 reviews closed without durable evidence | `[x]` | master (fast-forward, 008dfa0) |

**Claude round 1 — REOPENED** (Claude Code 2.1.207, default
claude-opus-4-8, read-only), reviewed head
`90b5773086182febb6b90d678ecfc3b72c6f7cb8` against base
`875efa05b7ef6c01354466f3f93211316d30c901`,
`guard_confirmed=true`, 2026-07-11T09:50:13Z. Structured verdict and both
SHAs matched the dispatch. Eight findings returned and **ALL ADMITTED** after
independent coder verification against the cited plan, active D1 decision,
shipped dialect guards, current worker/job/reset code, prior rrp/sd1 review
evidence, and adjacent RTK tee implementation. The reviewer separately
confirmed the current-implementation inventory, owner-settled scope,
assurance limits, and slp-11/slp-12 retention/terminal-unknown handling; it
filed no finding on the deliberate busy-reset contract revision.

**Claude fix round 1 LANDED:** eight admitted findings, exactly one plan fix
per commit: `5288b7b` (ahs-1, RTK routing wins and recovery is honestly
unavailable without the upstream seam), `12eddd8` (ahs-2, raw consent
explicitly superseded while route-pwsh consent remains),
`e0710eb` (ahs-3, parse-fatal + detector + bounded `bash -n` proof),
`098dcd3` (ahs-4, exact script evidence is ordered before dispatch),
`5c458f8` (ahs-5, reserved default close/restart semantics),
`61f6d53` (ahs-6, approved routing/dialect plan reconciliation),
`2f8e419` (ahs-7, warm session jobs deferred), and
`5ee1aa3` (ahs-8, minimal unrecorded audit-health diagnostic).
All rows remain pending until Claude re-grades the revised fixed SHA.

**Claude re-grade round 2 — REOPENED** (Claude Code 2.1.207, default
claude-opus-4-8, read-only), reviewed head
`242de89d9485e4f2955d26cd1c9bb01d3cb7a3e3` against base
`875efa05b7ef6c01354466f3f93211316d30c901`,
`guard_confirmed=true`, 2026-07-11T10:09:07Z. Structured verdict and both
SHAs matched the dispatch. Claude marked ahs-1..5, ahs-7, and ahs-8
RESOLVED; those rows are now `[x]`. ahs-6 remains NOT_RESOLVED because its
reconciliation names D1 changes but still omits the D2 raw-marker/counter
clauses and the greenfield `raw=true`-unbounded clause. Three new findings
were **ALL ADMITTED** after coder verification against the cited plan and
shipped guards: ahs-9 names the heredoc guard intentionally changed by the
new parse-fatal + detector + `bash -n` execution gate; ahs-10 names the four
Pester marker guards intentionally changed by same-invocation recovery; and
ahs-11 defines `allowColdBackground` for reserved-default and template-less
dynamic sessions. The reviewer independently live-probed both Bash legs and
verified every cited guard in the current tree.

**Coder consistency audit after Claude round 2 — 14 NEW FINDINGS, ALL
ADMITTED.** Three parallel read-only passes checked the frozen head by domain:
audit/security, worker/session lifecycle, and routing/output. The
routing/output pass found no non-duplicate issue. The other two passes cited
concrete plan sections and predicted implementer-visible failures; the coder
then independently checked those sections before admitting ahs-12..ahs-25.
The findings cover protocol-stream injection, the missing prepared-plan
reservation between warm resolution and supervisor audit commit, impossible
background lifecycle order, stale job-ID reuse, unacknowledged-segment
retention, recursive checkpoint events, hard-parent-death containment,
timeout containment for dynamic connections, lifecycle admission races,
restart parity, immutable alias/template binding, catalog/path failure
behavior, timed-out startup disposal, and the list-action schema. The
template-less background finding was already ahs-11 and was not duplicated.
These are coder-admitted plan findings, not attributed to Claude; all remain
pending reviewer grade after one-finding-per-commit fixes.

**Claude/coder fix round 2 LANDED:** exactly one plan-finding fix per commit:
`bca83e2` (ahs-6, complete D1/D2/greenfield reconciliation), `2c5774f`
(ahs-9, heredoc guard migration), `2ca8434` (ahs-10, Pester marker guard
migration), `baf765e` (ahs-11, frozen default/dynamic/template background
policy), `1b21005` (ahs-12, dedicated protocol pipes), `05a41e6` (ahs-13,
prepared-plan reservation and idempotent commit), `58e7d05` (ahs-14,
background start-call versus asynchronous job lifecycle), `9527390` (ahs-15,
supervisor-nonreused public job IDs), `a65d6f2` (ahs-16, anchored backlog
retention), `3f783b1` (ahs-17, non-event checkpoint sidecar), `23043b5`
(ahs-18, platform hard-parent-death containment), `f2f4255` (ahs-19,
unconditional post-start timeout worker replacement), `dc3d626` (ahs-20,
session-slot admission leases), `ab31227` (ahs-21, restart busy parity),
`757b994` (ahs-22, immutable alias binding), `18aebca` (ahs-23,
all-or-nothing catalog/path validation), `6fce3af` (ahs-24, confirmed startup
containment), and `c342747` (ahs-25, name-free list schema). All rows remain
`[~]` until reviewer re-grade; none of these plan commits authorizes product
code.

**Claude re-grade round 3 — REOPENED** (Claude Code 2.1.207, default
claude-opus-4-8, read-only), reviewed head
`e3cbbd2f2e780e55954b2ec8916647d117501792` against base
`875efa05b7ef6c01354466f3f93211316d30c901`,
`guard_confirmed=true`, 2026-07-11T10:31:09Z. Structured verdict and both SHAs
matched the dispatch. Claude returned one evidence-backed RESOLVED comment for
each requested ID (ahs-6 and ahs-9..ahs-25); those rows were provisionally
flipped `[x]` to record its grade. It found three new MEDIUM issues, **ALL
ADMITTED** after coder verification: ahs-26 names two shipped .NET raw/path
guards omitted from the intentional migration inventory; ahs-27 captures the
running-job/reset semantic conflict exposed by the new busy predicate; and
ahs-28 settles supervisor-versus-worker ownership and generation lifetime for
`ptk_output` artifacts. The valid empty-findings condition was not met, so the
Claude loop remains open.

**Coder disposition after Claude round 3 — three grades HELD, three NEW
findings ADMITTED.** Independent fixed-head re-grades accepted Claude's closure
of ahs-6, ahs-9..17, ahs-19..21, ahs-23, and ahs-24, but did not accept three
RESOLVED labels: ahs-18 still has a spawn-to-containment race before Windows
Job Object assignment / Unix reaper arming; ahs-22 says both “first successful
open” and that failed bootstrap preserves an immutable binding; and ahs-25's
nullable reflection signature cannot enforce action-conditional field
presence or distinguish omitted fields. Those rows returned to `[~]`.

Three non-duplicate cross-boundary failures were also admitted: ahs-29 gives
post-start timeout replacement an atomic lifecycle transition, self-lease
exemption, confirmed old-worker death, and named containment grace after the
request deadline; ahs-30 prevents new audited reads from consuming the
terminal-event reserve at anchored high water; and ahs-31 orders the external
no-execution `bash -n` validator after audited commit because `prepare`
explicitly forbids process start. For ahs-27, the coder disposition is to
preserve the shipped reset-kills-running-jobs contract by removing running
jobs from the non-force busy predicate, rather than rewriting that guard to
bless an accidental compatibility regression. All nine pending rows require
one-finding-per-commit fixes and reviewer re-grade.

**Fix round 3 LANDED:** nine pending findings, exactly one plan fix per
commit: `b7677da` (ahs-18, creation-time Windows Job Object membership and
broker-gated Unix launch), `c9a14ba` (ahs-22, alias binding frozen at validated
admission before bootstrap), `4dd8074` (ahs-25, explicit conditional schema
plus raw field-presence validation), `1f2c9c2` (ahs-26, two .NET raw/path guard
migrations), `e36b40b` (ahs-27, running cold jobs remain reset-owned rather
than lifecycle-busy), `af73889` (ahs-28, supervisor-owned harness-lifetime
output artifacts), `6294340` (ahs-29, atomic timeout transition and bounded
post-deadline containment grace), `23586fb` (ahs-30, physically separate
terminal reserve and all-call admission stop), and `e4e261d` (ahs-31,
post-audit scrubbed `bash -n` validation). Rows remain `[~]` pending fixed-SHA
review; no product code is authorized.

**Claude re-grade round 4 — REOPENED** (Claude Code 2.1.207, default
claude-opus-4-8, read-only), reviewed head
`baf8ba31a508fe5e815667c0f86c0f8fa78418f2` against base
`875efa05b7ef6c01354466f3f93211316d30c901`,
`guard_confirmed=true`, 2026-07-11T10:49:13Z. Structured verdict and both SHAs
matched the dispatch. Claude returned evidence-backed RESOLVED comments for
ahs-18, ahs-22, and ahs-25..ahs-31; those rows are now `[x]`. It found one new
MEDIUM issue, **ADMITTED**: ahs-32 gives post-launch startup failure the same
bounded containment-confirmation grace, explicit `containment_unconfirmed`
terminal, quarantine, and eventual-confirmed-death recovery gate as the
post-start execution timeout path. All three parallel coder re-grades also
accepted the submitted fixes and found no other schema/lifecycle/routing
regression on that fixed head.

**Coder audit after Claude round 4 — one NEW finding ADMITTED.** ahs-33 closes
a remaining guarantee gap in ahs-30: stopping new calls at high water protects
the reserve from later reads, but nothing reserves future terminal bytes when
calls/jobs are accepted below that threshold. An arbitrary burst across
independent sessions can therefore commit side effects and later exhaust a
fixed terminal segment. The required fix atomically allocates bounded terminal
capacity before `call.accepted`; a background start also retains a separate job
terminal reservation, and live workers reserve loss/exit capacity. Admission
fails before acceptance when the appropriate reservation cannot be made.
This finding was independently checked against the bounded-record and
all-session concurrency contracts; it is not a duplicate of ahs-30.

**Fix round 4 LANDED:** `70e1d39` (ahs-32) applies the same fixed
deadline-plus-containment-grace bound to post-launch startup, returns explicit
`containment_unconfirmed`, quarantines without new admission, and permits
recovery only after later confirmed death. `69caf6c` (ahs-33) atomically
reserves each call's worst-case terminal set before `call.accepted`, retains
job/worker lifecycle reservations, and derives admission capacity from
physical free bytes minus outstanding obligations. Both rows remain `[~]`
pending fixed-SHA review; no product code is authorized.

**Claude re-grade round 5 — REOPENED** (Claude Code 2.1.207, default
claude-opus-4-8, read-only), reviewed head
`c38e0fbc8d6637e26339399117d76c6a3a29e11d` against base
`875efa05b7ef6c01354466f3f93211316d30c901`,
`guard_confirmed=true`, 2026-07-11T10:58:02Z. Structured verdict and both SHAs
matched. ahs-33 is RESOLVED: reservation covers the worst-case call set before
acceptance, persistent asynchronous job/worker obligations, atomic concurrent
admission, and the anchored physical reserve; its row is `[x]`. ahs-32 remains
NOT_RESOLVED only because Slice 7 still literally says every post-launch
startup timeout/cancel must await confirmed containment before returning,
contradicting the newly bounded lifecycle contract and acceptance test. No
other finding was returned.

**Coder audit after Claude round 5 — one NEW finding ADMITTED.** Both
independent lifecycle/audit re-grades found the same non-duplicate gap:
ahs-34. Startup/execution containment may now return and leave the slot
`quarantined` under continued death observation, but supervisor idle
suppression still names only starting/resetting, active foreground calls, and
running jobs. Idle exit can therefore discard the observer/alias quarantine
before confirmation and let a later harness overlap the unconfirmed worker.
The fix must count every quarantined/live containment observer as aggregate
live work and prove an idle interval cannot end the supervisor first.

**Fix round 5 LANDED:** `7ec5d7a` (ahs-32) replaces the stale Slice 7
wait-forever instruction with the bounded confirmed/quarantined contract, and
`00bb110` (ahs-34) makes every quarantine/containment observer aggregate live
work with a delayed-death versus idle-timer acceptance case. Both rows remain
`[~]` pending fixed-SHA review; no product code is authorized.

**Claude re-grade round 6 — ACCEPTED / CLAUDE LOOP CLOSED** (Claude Code
2.1.207, default claude-opus-4-8, read-only), reviewed head
`e77780c9d6a39d49a49f47693d04c23e5c1b1190` against base
`875efa05b7ef6c01354466f3f93211316d30c901`,
`guard_confirmed=true`, 2026-07-11T11:03:18Z. The structured result matched
both SHAs, returned explicit RESOLVED comments for ahs-32 and ahs-34, and had
an empty findings array. Those rows are `[x]`; all 34 ahs findings are now
closed by Claude/coder grade. This closes the requested Claude reviewloop only;
the independent Grok loop remains required, and any Grok-driven plan change
will receive a final Claude confirmation on the same final plan head.

**Grok round 1 — REOPENED** (grok 0.2.93, read-only sandbox), reviewed head
`c96da777bcf5e9a6ee862025b4b6a805243f08d0` against base
`875efa05b7ef6c01354466f3f93211316d30c901`,
`guard_confirmed=true`, 2026-07-11T11:09:04Z. Structured verdict and both SHAs
matched. Grok independently accepted the ahs-1..ahs-34 closures and owner
constraints, then returned two actionable findings, **BOTH ADMITTED** after
coder verification. ahs-35 defines supervisor fail-closed handling when the
Unix containment broker itself exits after its armed acknowledgment; ahs-36
defines the absolute startup deadline for open/restart/reset, template-backed
and dynamic/default starts, explicit overrides, lazy invoke starts, and the
fixed containment grace. No duplicate or stylistic finding was admitted.

**Grok fix round 1 LANDED:** `f6a20f3` (ahs-35) makes the Unix broker a
continuously monitored containment lease; unexpected exit blocks admission,
directly tears down the identity-validated process group, and faults or
quarantines the generation before recovery. `da32d9c` (ahs-36) adds
`timeoutSeconds` to lifecycle tools and freezes the shared operator-default /
positive-override cap / template-ceiling / lazy-invoke deadline function, with
containment grace only after that budget expires. Both rows remain `[~]`
pending Grok re-grade; no product code is authorized.

**Grok re-grade round 2 — ACCEPTED / GROK LOOP CLOSED** (grok 0.2.93,
read-only sandbox), reviewed head
`f401089b1a6bbbf6fa4c860d4f99394848eed6e6` against base
`875efa05b7ef6c01354466f3f93211316d30c901`,
`guard_confirmed=true`, 2026-07-11T11:15:40Z. The structured result matched
both SHAs, returned explicit RESOLVED comments for ahs-35 and ahs-36, and had
an empty findings array. Those rows are `[x]`; all 36 ahs findings are closed
by Grok/coder grade. Because Grok's fixes landed after Claude's accepted pass,
the same final plan content still requires one Claude regression confirmation
before the overall dual-review loop closes.

**Claude final regression check — REOPENED** (Claude Code 2.1.207, default
claude-opus-4-8, read-only), reviewed head
`d58dcaee1a65c0b7e8eb2d57498fe58277e4f0b4` against base
`875efa05b7ef6c01354466f3f93211316d30c901`, plan blob
`d6fc86faf9943444ce89cc7f141203048dd155e7`,
`guard_confirmed=true`, 2026-07-11T11:21:25Z. ahs-35 is RESOLVED. ahs-36 is
NOT_RESOLVED on one precise schema leg: the `open` branch says it permits only
open-time binding fields without explicitly including `timeoutSeconds`, while
close/restart name their deadline field. A strict `oneOf` implementation would
therefore reject the open override that the lifecycle deadline contract and
acceptance matrix require. The finding is admitted as an ahs-36 residual; no
new ID is needed.

**Cross-review fix LANDED:** `1c23e1b` completes ahs-36 by explicitly allowing
`timeoutSeconds` in the `open` branch of the action-conditional schema and
adding schema acceptance for open/close/restart versus list. The row remains
`[~]` until both Claude and Grok confirm the final plan content.

**DUAL REVIEWLOOP CLOSED — ACCEPTED BY CLAUDE AND GROK ON THE SAME FINAL
HEAD/PLAN.** Final reviewed head
`0b9d43d43289666f02b9d0af99d629af231dcbca`, base
`875efa05b7ef6c01354466f3f93211316d30c901`, plan blob
`bc47579b7f73231d524ff64fc5b5a9c75b332435`. Claude Code 2.1.207
(claude-opus-4-8, read-only) and grok 0.2.93 (read-only sandbox) each returned
`verdict=accepted`, `guard_confirmed=true`, exact matching SHAs, an explicit
`ahs-36 RESOLVED` comment, and an empty findings array on 2026-07-11. ahs-36
is `[x]`; all 36 admitted findings are closed. The plan remains DRAFT pending
explicit owner approval; review convergence authorizes neither product code,
decisions-log edits, nor push.

**OWNER APPROVAL — 2026-07-11:** the owner explicitly accepted
`.agents/plans/audited-harness-sessions.md` after dual-review closure. The plan
is now the canonical implementation contract. The approval message did not
request an implementation slice or authorize push; both remain separate
explicit-go actions. The decisions-log hold remains in force.

**AUDITED-HARNESS SLICE 0 CLAUDE REVIEW — REOPENED / ahs-37 ADMITTED**
(Claude Code 2.1.207, model reported as `claude-fable-5`, read-only), reviewed
head `a9fe9ecae75b712f5ab48fd7636613a3e0ffb35a` against pre-slice base
`2a83723369e2752b4d930fd57c3ae4b5f484bad9`, `guard_confirmed=true`, verdict
recorded 2026-07-11T16:53:29Z. The fixed-worktree review found one material
issue: `.agents/state.md` claims three focused Slice 0 reviews closed without
any committed review record supporting that claim. The coder independently
confirmed the claim is present at the reviewed head and the cited evidence is
absent, so the MEDIUM finding is admitted as ahs-37. Slice 1 remains blocked;
review acceptance would authorize neither merge nor push.

**ahs-37 CLAUDE RE-GRADE — ACCEPTED / SLICE 0 LOOP CLOSED** (Claude Code
2.1.207, model reported as `claude-fable-5`, read-only), reviewed head
`a6b484d269e0d046b1c1621aa8705046c4bb1c6d` against base
`2ecc417db494fbe4c077723144e5d30289f20f7b`, `guard_confirmed=true`,
2026-07-11T16:59:45Z. Claude independently reproduced the original evidence
gap, confirmed the unsupported claim is absent and both durable verdict
records are present, and passed `git diff --check`, declared-file scope,
merge-base, and clean-worktree checks. The structured envelope exited zero and
matched both SHAs exactly. ahs-37 is `[x]`; the branch is ready for an
owner-gated merge. Acceptance authorizes neither merge nor push, and Slice 1
must not start until the accepted correction lands.

**ahs-37 MERGED / LOCAL BRANCH AUTHORITY DELEGATED — 2026-07-11.** The owner
explicitly authorized autonomous local branch creation, switching, merging,
and cleanup for the remainder of the audited-harness coding work. The accepted
branch was fast-forwarded into `master` at `008dfa0`, its content arrival was
verified with an empty branch-to-master diff, and the local branch was deleted.
This authority does not include push; push remains ask-first.

**AUDIT V1 COMPLETENESS CORRECTION — CLAUDE ACCEPTED** (Claude Code 2.1.207,
model reported as `claude-fable-5`, read-only), reviewed head
`e308da9bbffc8812937246bc1cfd6a3ae6e46e5b` against base
`d70b62c528586516ec4196ffd0f55d3136dc5010`, `guard_confirmed=true`,
2026-07-11T17:55:15Z. Claude independently confirmed that the base required but
could not represent prepared plan ID, bounded permitted fallback set, and the
emergency-probe recovery summary; the corrected strict v1 envelope represents
all three without weakening behavior or changing an emitted format. It found
no material issue. Its sole LOW, explicitly non-blocking comment was to surface
the pre-release correction to the owner; that correction and rationale were
presented directly in the active owner conversation. The structured envelope
exited zero and matched both SHAs exactly. Product implementation may resume;
push remains ask-first.

---

Loop run 2026-07-11 (audited-harness Slice 1) — reviewer: Claude Code
2.1.207, fixed scope `f4dbf5a55e012984769a41dcd1f44a92dca82739..460c1061f4b4913241c536adeb52403b86a067a2`.

**SLICE 1 CLAUDE REVIEW — ACCEPTED / LOOP CLOSED** (Claude Code 2.1.207,
model reported as `claude-fable-5`, isolated disposable worktree),
`guard_confirmed=true`, verdict recorded 2026-07-11T22:48:46Z. The structured
envelope exited zero, its result and structured payloads matched, and both
fixed SHAs matched exactly.

Claude independently ran
`AuditPreEffectGuardTests.Foreground_authorization_failure_never_executes_user_script`
green for all four cases, changed only production
`AuditCallContext.AuthorizeInvocationAsync` to authorize after
`AuditUnavailableException`, and reproduced four assertion failures proving
the forbidden user marker was written. It restored the file with
`git checkout --`, reran the same guard 4/4 green, then passed the full .NET
suite, Pester suite, and audit handshake; the reviewed worktree ended clean at
the exact head.

No material finding was returned. Claude also traced each operation profile's
reservation count against its append paths, verified evidence and dispatch
flush ordering before foreground/background effects, checked protected
cross-platform storage and startup hash-chain validation, confirmed the narrow
credential-free `ptk_state` outage exception, and confirmed trusted preflight
does not re-enter user hooks before dispatch. Slice 2 SIEM export was correctly
treated as out of scope. Acceptance authorizes local landing under the owner's
delegated branch authority, not push or history rewriting.

**SLICE 1 LANDED LOCALLY — 2026-07-11.** `master` fast-forwarded to the
acceptance-record commit `286b171`; `git diff
feat/audited-harness-slice1 master --` was empty before the feature branch was
deleted. The disposable Claude worktree was clean and removed. No push was
performed.

---

**SLICE 2 EVIDENCE-ADMIN FAILURE CLASSIFICATION — CLAUDE ACCEPTED** (Claude
Code 2.1.207, model `claude-fable-5`, isolated disposable worktree), reviewed
head `adb8b4b0f02118ce20c7f75a36fe4341e28e2646` against base
`b87922189eb4c7b068c4bd9b5390d6700c040a28`, `guard_confirmed=true`,
2026-07-12T09:24:35Z. Claude independently made the absent-evidence and
post-disclosure classifiers wrong, observed the intended focused guards fail,
restored production byte-exactly, and passed focused 16/16 plus full 873/873.
No material finding remained. Detail and the non-blocking concurrent-destination
classification caveat are recorded in
`.agents/review/findings/s2-admin-evidence-failures.md`. Acceptance authorizes
local landing under the owner's delegated branch authority, not push.

---

**SLICE 2 DISPOSITION-ADMIN FAILURE CLASSIFICATION — CLAUDE ACCEPTED**
(Claude Code 2.1.207, model `claude-fable-5`, isolated disposable worktree),
reviewed head `7e982ce4a4008c2ba5fee850ed890c8a78193550` against base
`a5420993147413a60fcd9cc52ba60d920eb609b2`, `guard_confirmed=true`,
2026-07-12T09:38:37Z. Claude independently collapsed the post-completion
receipt-missing state into the broader post-checkpoint code, observed the exact
guard fail, restored production byte-exactly, and passed focused 22/22 plus
full 877/877. No material finding remained. Detail and three non-blocking
fail-closed observations are recorded in
`.agents/review/findings/s2-admin-disposition-failures.md`. Acceptance
authorizes local landing under the owner's delegated branch authority, not
push.

---

**SLICE 2 INTEGRATED REVIEW FINDING — JOB-ID AUDIT POISON OPEN** (Claude Code
2.1.207, fixed head `6cbd1d3061985f06bb0a5da8bcf2faa84a5bb826` against
`78e256ca0f3b1253aa97dd984f1d913429ea452a`, `guard_confirmed=true`, verdict
`reopened`, 2026-07-12T14:33:24Z). An omitted identifier on a job-specific
`ptk_job` action can admit `id=0`, trigger an audit schema failure, permanently
close audited admission until restart, and misstate the client-visible refusal
as completed. Canonical detail is in
`.agents/review/findings/s2-job-id-audit-poison.md`.

---

**SLICE 2 INTEGRATED REVIEW FINDING — ANCHORED TEMP RECOVERY OPEN** (Claude
Code 2.1.207, fixed head `6cbd1d3061985f06bb0a5da8bcf2faa84a5bb826`
against `78e256ca0f3b1253aa97dd984f1d913429ea452a`,
`guard_confirmed=true`, verdict `reopened`, 2026-07-12T14:33:24Z). A hard
death after protected rotation-temporary creation can make anchored startup
and `ptk-audit-admin` reject the spool forever. Canonical detail is in
`.agents/review/findings/s2-anchored-temp-recovery.md`.

The complete structured integrated-review record, independent mutations,
verification, Windows static review, and non-material observations are in
`.agents/review/slice2-integrated-claude.md`.

---

**SLICE 2 INTEGRATED CLAUDE RE-REVIEW — REOPENED** (Claude Code 2.1.207,
fixed head `49971d6ce5cb246d2283eab052163ae85a5b5c87` against
`78e256ca0f3b1253aa97dd984f1d913429ea452a`, `guard_confirmed=true`,
2026-07-12T15:35:30Z). The reviewer independently broke and restored both
previously reopened guards, confirmed those two fixes closed, then passed
.NET 926/926, Pester 134 with two platform skips, and the zero-warning
handshake in a clean disposable worktree. The structured result and payload
matched exactly. Two distinct HIGH findings reopened the integrated slice;
canonical review detail is in `.agents/review/slice2-integrated-claude.md`.

---

**SLICE 2 INTEGRATED REVIEW FINDING — TLS HANDSHAKE MISCLASSIFICATION OPEN.**
A transient TLS peer abort is durably classified as certificate configuration
failure, stopping same-identity export retries and eventually audited admission
at spool high water. Canonical detail is in
`.agents/review/findings/s2-tls-handshake-misclassification.md`.

---

**SLICE 2 INTEGRATED REVIEW FINDING — WINDOWS CHECKPOINT DURABILITY OPEN.**
The Windows handle-based atomic replacement path has no post-rename
`FlushFileBuffers` barrier before a checkpoint is treated as durable and used
to authorize retention. Canonical detail is in
`.agents/review/findings/s2-windows-checkpoint-durability.md`.

---

**SLICE 2 FINAL INTEGRATED CLAUDE REVIEW — ACCEPTED / LOOP CLOSED** (Claude
Code 2.1.207, model `claude-fable-5`, isolated disposable worktree), reviewed
head `3d3739a2efee6c2a325ba1410413b2500658cf7c` against base
`78e256ca0f3b1253aa97dd984f1d913429ea452a`, `guard_confirmed=true`,
2026-07-12T17:10:19Z. The one-shot JSON envelope exited zero; its result and
structured payload matched exactly, both fixed SHAs matched the dispatch, and
the reviewer returned no material comments after the required independent
mutation proof and restored full macOS battery. The detached review worktree
was clean and removed. The same integrated code head separately passed the
full checkout battery on macOS and Windows; canonical machine evidence is in
`.agents/machines.md`.

All four material integrated-review findings are `Verified`; their individual
red-to-green proofs and Claude verdicts remain in the canonical finding files.
Acceptance authorizes local landing under the owner's delegated branch
authority, not push.

**SLICE 2 LOCAL LANDING RECORDED.** Local `master` was fast-forwarded through
the accepted Slice 2 feature history after the fixed-head verdict. Content
arrival is verified separately from ancestry before branch cleanup. No push
was performed or authorized.

---

**SLICE 3 MIXED-GUIDANCE SUB-SLICE CLAUDE REVIEW — ACCEPTED** (Claude Code
2.1.207, model `claude-opus-4-8`, isolated disposable worktree), reviewed head
`669ce6ea47c520a9c3bb73411192630d56ed519b` against base
`f311fe2a65d1c0ec5515af0e1bb93ccd128b22a1`, `guard_confirmed=true`,
2026-07-13T00:47:57Z. The first Fable dispatch returned API status 429 before
any repository action; the playbook's single fail-closed retry used Opus. Its
one-shot JSON envelope exited zero, the result and structured payload matched,
and both fixed SHAs matched the dispatch. No material finding was returned.

Claude independently made safe mixed capture produce no guidance and observed
the two positive guards fail, then restored byte-exactly and passed the focused
9/9. It separately removed the canonical `Set-Content` identity/source checks
and observed the noncanonical-sink guard fail, then restored byte-exactly and
passed focused 9/9. The restored exact head passed 1,010/1,010 .NET tests, 139
Pester tests with two platform skips, and the stdio handshake. The detached
worktree was clean and removed. Acceptance authorizes the remaining Slice 3
verification/review work under the approved plan, not push or history rewrite.

---

**SLICE 3 FINAL INTEGRATED CLAUDE REVIEW — REOPENED** (Claude Code 2.1.207,
model `claude-opus-4-8`, isolated disposable worktree), reviewed head
`669ce6ea47c520a9c3bb73411192630d56ed519b` against base
`0c08379a02c796b8ea0e1779c196840c6a9b1269`, `guard_confirmed=true`,
2026-07-13T01:48:24Z. The structured result and payload matched, both SHAs
matched the dispatch, and the restored exact head passed 1,010/1,010 .NET,
139 Pester with two platform skips, and the zero-warning handshake. Claude's
three independent barrier/RTK/Bash mutations each failed the intended focused
guards and passed after byte-exact restoration. The detached worktree was
clean and removed.

## Findings (audited-harness Slice 3 integrated review)

| ID | Severity | Impact (one line) | Status | Branch |
|----|----------|-------------------|--------|--------|
| s3-rtk-output-bounds | HIGH | Default RTK output bypasses ANSI cleanup and all passthrough bounds | `[x]` | `fix/s3-rtk-output-bounds` |
| s3-block-fidelity | MEDIUM | Clean/dynamicparam blocks can be silently dropped by RTK routing | `[x]` | `fix/s3-block-fidelity` |
| s3-background-operator | MEDIUM | A trailing background operator becomes synchronous RTK execution | `[x]` | `fix/s3-background-operator` |
| s3-rtk-preference-isolation | HIGH | Warm native preferences can discard routed stdout and pollute `$Error` | `[x]` | `fix/s3-rtk-preference-isolation` |
| s3-wrapper-context | MEDIUM | Context-changing wrappers are routed despite the exact-original contract | `[x]` | `fix/s3-wrapper-context` |
| s3-using-statement-fidelity | MEDIUM | A top-level using statement can be omitted from routed execution | `[x]` | `fix/s3-using-statement-fidelity` |
| s3-background-bash-parity | MEDIUM | Background Bash parity is assigned to later Slice 5 | `[-]` | |

Claude returned the first two material findings. Separate coder audits
reproduced and admitted three additional non-duplicate findings before any fix
began. The background-Bash parity candidate was declined because the approved
sequence assigns that work to Slice 5. Canonical evidence, predicted failures,
and required guards are in the finding files. The remaining integrated
contracts were accepted, and the recorded installed/OS-protected and live
Windows/Bash caveats remain honest later validation obligations rather than
Slice 3 blockers. No finding authorizes push or history rewrite.

**s3-block-fidelity CLAUDE REVIEW — ACCEPTED** (Claude Code 2.1.207, model
`claude-opus-4-8`, isolated disposable worktree), reviewed head
`561c56136bfb895d7278ad1a320cfcc3c8cb9dcc` against base
`7758c8cf5864ffcaebab9dad70a1ecbd5ccf0df4`, `guard_confirmed=true`,
2026-07-13T02:50:35Z. Claude independently proved both the eligibility and
domain exclusions load-bearing, restored focused 38/38, and passed the full
1,012-test .NET/Pester/handshake battery. No material defect remained in the
named fix; its worktree was clean and removed. The review separately admitted
`s3-using-statement-fidelity`, which does not reopen this accepted finding.

**s3-using-statement-fidelity CLAUDE REVIEW — ACCEPTED** (Claude Code 2.1.207,
model `claude-opus-4-8`, isolated disposable worktree), reviewed head
`b7ab1a3c164a5aaf8957fe7725d8c9bd113f53bc` against base
`2b9b28cb4f187a72803811c252de2637bfa340ea`, `guard_confirmed=true`,
2026-07-13T03:11:12Z. The eligibility and domain checks each failed their
specific assertion when independently removed, restored focused 39/39, and
the exact head passed 1,013/1,013 .NET, 139 Pester with two skips, and the
handshake. No material issue remained; the worktree was clean and removed.

**s3-background-operator CLAUDE REVIEW — ACCEPTED** (Claude Code 2.1.207,
model `claude-opus-4-8`, isolated disposable worktree), reviewed head
`923f8a522b4e662c83ad3fa8351cb1f88e2dbd6f` against base
`66f22fac482f242e1d70c24763ef8c01eb49d97d`, `guard_confirmed=true`,
2026-07-13T03:21:14Z. Independently removing the eligibility and domain checks
failed their exact assertions; restoration passed the focused guard and the
full 1,014-test .NET/Pester/handshake battery. No material issue remained; the
worktree was clean and removed.

**s3-wrapper-context CLAUDE REVIEW — ACCEPTED** (Claude Code 2.1.207, model
`claude-opus-4-8`, isolated disposable worktree), reviewed head
`bad4287b0a102904d0ed626c250c5a3fd8f15194` against base
`4995bc02b776a90bcce1b268dd0e83078cd62a71`, `guard_confirmed=true`,
2026-07-13T03:35:21Z. Claude independently reverted the wrapper exclusion and
observed exactly the four container-exec guard failures, restored focused
44/44, and confirmed the plan's docker example plus option-prefixed forms.
The accepted scope is explicitly container `exec`, not universal wrapper
detection; its worktree was clean and removed.

**s3-rtk-output-bounds CLAUDE REVIEW — ACCEPTED** (Claude Code 2.1.207, model
`claude-opus-4-8`, isolated disposable worktree), reviewed head
`bda3562c5340619c8c1bb41404ec73bbba7c7902` against base
`89b83b78ed02142bc93ee16b1256ab31585498eb`, `guard_confirmed=true`,
2026-07-13T03:53:56Z.
Independently restoring the old host shaping gate failed on retained ANSI and
unbounded output; independently disabling only the module provenance skip
failed on a second-shaping marker. Restoration passed the focused guard and
the exact head passed 1,018/1,018 .NET tests, 139 Pester tests with two skips,
and the handshake. Direct observation produced 401 bounded lines with no ANSI
or second RTK invocation. Both review trees were clean and removed.

**s3-rtk-preference-isolation CLAUDE REVIEW — ACCEPTED** (Claude Code 2.1.207,
model `claude-opus-4-8`, isolated disposable worktree), reviewed head
`40923784601bf8063d9461188b04be3940374c7d` against base
`e766866a65469dad93384a95d572288ba96e1381`, `guard_confirmed=true`,
2026-07-13T04:55:03Z. Claude independently proved the direct host capture,
typed pre-start fallback, and tilde/wildcard argument exclusions load-bearing,
validated exact argument spelling and quoting against PowerShell 7, and
confirmed audit ordering, no retry after start, identity/deadline/stream
bounds, truthful exit state, and single-pass provenance-aware shaping. The
restored exact head passed 1,030/1,030 .NET tests, 139 Pester tests with two
platform skips, and the zero-warning handshake. The clean disposable
worktree was removed and the coder tree remained clean. Windows execution of
the new fixture remains the next slice-level validation gate.

**s3-rtk-preference-isolation WINDOWS EXACT-ARCHIVE VALIDATION — REOPENED**
(PowerShell 7.6.3/.NET 10.0.301 on `NETWATCH-01`), validated code head
`40923784601bf8063d9461188b04be3940374c7d` from local/uploaded SHA-256
`CE5707231353BABCBA096E90076513B9532A7C0B1FE9C64F337A474D8110FF2E`,
2026-07-13T05:03:51Z. The .NET battery passed 1,020/1,030; two direct-runner
guards exposed Windows stderr/timeout-marker defects and eight RTK-route
integration guards returned empty/no routed output. Pester and handshake were
correctly skipped after the required .NET gate failed. All owned local and
remote disposable artifacts were removed. The earlier macOS fixed-SHA Claude
acceptance remains historical evidence, not current branch readiness;
canonical machine-level failure evidence is in `.agents/machines.md`.

**s3-rtk-preference-isolation WINDOWS CORRECTIVE EXACT-ARCHIVE VALIDATION —
PASSED (PRE-REVIEW)** (`NETWATCH-01`), validated corrective head
`c100ba199d9854f7171733d9950b26e2a8a397ab` from local/uploaded SHA-256
`76F027844CC53919B8D2FCEE526940F9196A684337AA3BBFB0E798C5B67BF5A3`,
2026-07-13T05:29:28Z. Removing fixture stream forwarding failed exactly the
eight RTK-route integration guards; byte-exact restoration passed the full
.NET/Pester/handshake battery. All disposable artifacts were removed.
Canonical counts and machine caveats are in `.agents/machines.md`; a new
fixed-SHA Claude verdict was required and is recorded below.

**s3-rtk-preference-isolation WINDOWS CORRECTION CLAUDE REVIEW — ACCEPTED**
(Claude Code 2.1.207, model `claude-opus-4-8`, isolated macOS and Windows
worktrees), reviewed head `64eb767a826da0c8177d9fcdd2fa1ea7033a1d73`
against base `747358e9650c8cf21e95890bd827559f90395639`,
`guard_confirmed=true`, 2026-07-13T05:42:02Z. Claude independently confirmed
the range leaves production routing byte-identical, passed the macOS battery,
and used a separately hashed exact-head Windows archive. Removing only fixture
stream forwarding failed exactly the eight RTK-route guards; byte-exact
restoration passed the full Windows .NET/Pester/handshake battery. All local
and remote review artifacts were removed and the coder tree remained clean.

---

**SLICE 3 FINAL INTEGRATED CLAUDE REVIEW — ACCEPTED / LOOP CLOSED** (Claude
Code 2.1.207, model `claude-opus-4-8`, isolated disposable macOS and Windows
worktrees), reviewed head `b78d9c6f176cb42771f823a6b1bdc2b3e6561f07`
against base `0c08379a02c796b8ea0e1779c196840c6a9b1269`,
`guard_confirmed=true`, 2026-07-13T05:58:53Z. The one-shot JSON envelope
exited zero; its result and structured payload matched, and both fixed SHAs
matched the dispatch. Claude found no material defect after independently
proving the durable dispatch barrier, preference-independent RTK capture, both
RTK output provenance/bounds legs, background/wrapper AST exclusions, and
Bash validation ordering load-bearing. Every mutation failed for its intended
reason and passed after byte-exact restoration.

The restored exact head passed the full macOS battery. A separately hashed
exact-current-head archive passed the full Windows battery, and the reviewer
confirmed the final code tree is identical to the independently Windows
red-to-green-reviewed correction. Both review worktrees and every local/remote
transfer or proof artifact were removed; independent probes found no residue
and the coder tree remained clean. Canonical platform counts and residual
installed/OS-protected caveats are in `.agents/machines.md`.

All six admitted material integrated-review findings are `Verified`; their
individual guard proofs and fixed-SHA verdicts remain in the canonical finding
files. `s3-background-bash-parity` remains the recorded Slice 5 intake decline,
not an open Slice 3 finding. Acceptance authorizes local landing under the
owner's delegated branch authority, not push or history rewrite.

**SLICE 3 LOCAL LANDING RECORDED.** Local `master` was fast-forwarded through
accepted feature tip `bd34715972dfaf41d021bc2377b723ff2bf55cf8` after the
fixed-head verdict. `master` and `feat/audited-harness-slice3` had identical
content and identical tips after the fast-forward; content arrival was
verified separately from ancestry before the feature branch was deleted. No
push was performed or authorized.

---

**SLICE 4A OUTPUT-STORE CLAUDE REVIEW — ACCEPTED** (Claude Code 2.1.207,
model `claude-opus-4-8`, isolated disposable worktree), reviewed head
`bee983d76d89090755f9a7b70a7a47a48275c924` against base
`9c89abf1d7c45435dd48b56b1fde1fec3b87c9fa`, `guard_confirmed=true`,
2026-07-13T14:04:57Z. The one-shot JSON envelope exited zero; its result and
structured payload matched exactly, and both fixed SHAs matched the dispatch.
Claude found no material defect in the supervisor-owned output store,
protected quota/retention behavior, audited retrieval ordering/secrecy, or
the bounded five-field `ptk_output` schema.

Claude independently proved four guards red then green: retained artifact
identity under path substitution, mandatory durable `output.read_accessed`
before content release, UTF-8 scalar-safe artifact capping, and the audited
`maxBytes` bound. Exact restoration passed 1,048/1,048 .NET tests, 139 Pester
tests with two platform skips, and the stdio handshake. The detached review
worktree was clean at the reviewed SHA and removed.

One explicitly non-blocking reviewer observation remains: a read offset past
the artifact end uses detail `offset_not_utf8_boundary`, while search uses the
more precise `offset_past_end`. The machine state remains correctly
`invalid_offset`; no audit, quota, disclosure, or consumer behavior is
affected, so the reviewer did not reopen the sub-slice and no material
finding was admitted. Acceptance authorizes the next approved Slice 4
sub-slice, not push or history rewriting.

---

**SLICE 4B SAME-INVOCATION CAPTURE CLAUDE REVIEW — ACCEPTED** (Claude Code
2.1.207, model `claude-opus-4-8`, isolated disposable worktree), reviewed
head `347d85c2dede7241e037fe7a538b7e726952cd27` against base
`76005eb4d78cee84369bd5f475292fde7c0b544a`, `guard_confirmed=true`,
2026-07-13T18:10:50Z. The one-shot JSON envelope exited zero; its result and
structured payload matched exactly, and both fixed SHAs matched the dispatch.
Claude found no material defect in two-stage same-invocation capture,
anonymous recovery publication, passive no-user-code freezing, exact nonce
stripping, or the private renderer lifecycle.

Claude independently proved eight representative guards red then green:
duplicating the submitted user script broke the one-execution identity;
rendering before unlink broke both namespace and substitution guards; allowing
cancel to win after `Publishing` stranded the coordinator; ambient-culture
scalar conversion invoked hostile culture getters; accepting general
`PSPropertyInfo` invoked an active script property; unconditional detached-name
stripping removed a mismatched user nonce; skipping the stop join disposed the
private pipeline too early; and removing the final pre-invoke cancellation
gate started work after cancellation. Byte-exact restoration passed all 1,081
.NET tests, 141 Pester tests with two platform skips, and the stdio
handshake. The detached worktree was clean at the reviewed SHA and removed.

One explicitly non-blocking observation remains: under repeated full-suite
load on Unix, the unchanged anchored-runtime test can observe the brief window
where atomic publication has created the destination hard link but has not yet
removed the temporary hard-link name. The reviewer observed the assertion in
2 of 9 reviewed-head battery runs, proved the test and `PublishAtomically`
path are outside and byte-identical across this diff, and did not reopen Slice
4b. A separate test-stabilization slice may wait for destination-present and
temporary-absent in one eventual assertion. Windows delete-pending/link-count
proof was reviewed statically on macOS; live Windows execution remains a later
platform gate. Acceptance authorizes the remaining approved Slice 4 work, not
push or history rewriting.

---

**SLICE 4C LEGACY-RAW RETIREMENT CLAUDE REVIEW — ACCEPTED** (Claude Code
2.1.207, model `claude-opus-4-8`, isolated disposable worktree), reviewed
head `76d4f0c85252b65d20b7ecb41c3d605835a767d1` against base
`4477412546d563487f0436ccfa617acac24cce3f`, `guard_confirmed=true`,
2026-07-13T19:00:32Z. The one-shot JSON envelope exited zero; its result and
structured payload matched exactly, and both fixed SHAs matched the dispatch.
Claude found no material defect in legacy-`raw` inertness, foreground or
background dialect enforcement, `route=pwsh` consent, the ephemeral
`PowerShellDirect` background start fact, or the separate unshaped state-probe
path. The planned Slice 5 routing/persistence work and Slice 10 guidance
surfaces remain outside this sub-slice.

Claude independently proved seven policy boundaries red then green: planner
input independence from `raw`; foreground shaping/capture independence;
foreground and background dialect refusal; both the assertion and pre-start
validation teeth of the background `PowerShellDirect` fact; complete
self-formatted state-probe output; and the recovery/schema descriptions.
Exact restoration passed all 1,085 .NET tests, 141 Pester tests with two
platform skips, and the stdio handshake. A one-time restore populated the
fresh worktree's ignored build assets before the required `--no-restore`
battery. The detached review worktree was clean at the reviewed SHA and
removed.

Two explicitly non-blocking reviewer observations remain: the now-unused
public shaped `TryInvokeIfIdleAsync` surface can wait for the planned session
runtime cleanup, and two private-renderer fixtures retain a semantically inert
`raw: true` argument while using an explicit missing-module path for their
actual no-shaper setup. Neither changes production behavior or weakens the
fixtures, so no material finding was admitted. Acceptance authorizes the
final fixed-SHA integrated Slice 4 review, not Slice 5 implementation, push,
or history rewriting.

---

**SLICE 4 FINAL INTEGRATED CLAUDE REVIEW — ACCEPTED** (Claude Code 2.1.207,
model `claude-opus-4-8`, isolated disposable worktree), reviewed product head
`76d4f0c85252b65d20b7ecb41c3d605835a767d1` against Slice 4 base
`9c89abf1d7c45435dd48b56b1fde1fec3b87c9fa`, `guard_confirmed=true`,
2026-07-13T19:34:22Z. The one-shot JSON envelope exited zero; its result and
structured payload matched exactly, both fixed SHAs matched the dispatch, and
Claude found no material defect across the integrated 4a output store, 4b
same-invocation capture/recovery, and 4c legacy-`raw` retirement.

Claude independently proved eight cross-slice guards red then green:
supervisor-lifetime handle retrieval across MCP requests; durable
`output.read_accessed` before disclosure; anonymous artifact safety against a
later pathname substitute; raw-invariant foreground capture; exactly one
submitted user invocation; caller-supplied recovery hints composed inside the
module's elision marker; honest no-handle `RtkUnknown` behavior when the RTK
capture seam is absent; and foreground/background dialect routing independent
of `raw`. Each production sabotage failed for the intended observable reason,
was restored byte-exactly, and passed its focused guard afterward.

The exact restored tree passed 1,085/1,085 .NET tests, 141 Pester tests with
two platform skips, and the stdio handshake. Claude also confirmed statically
that reservation precedes user execution, the unshaped artifact seals before
private shaping, ptk_output audit protection discloses no secret material,
the state-probe exception is narrow, and the ephemeral background
`PowerShellDirect` fact does not cross into Slice 5 persistence. The detached
worktree was clean at the reviewed SHA and removed.

One explicitly non-blocking test observation remains: the new private-output
stop/join test missed its fixed two-second `stopStarting` scheduling wait once
inside a parallel full suite after its production timeout assertions had
passed; the later post-shutdown assertions stayed green in repeated isolated
runs. It passed 8/8 isolated runs and 3/3 isolated runs under CPU load; four
full-suite confirmations were clean. Claude classified this as a test
timing/CI-flake risk rather than a production concurrency defect and did not
reopen Slice 4. Host-specific runner-stall evidence is recorded in
`.agents/machines.md`.

The reviewer kept cold background planning/recovery and persisted metadata in
Slice 5, and installer/README/decisions reconciliation in Slice 10. Acceptance
authorizes the delegated local fast-forward of the feature branch, not Slice 5
implementation, push, or history rewriting.

**SLICE 4 LOCAL LANDING RECORDED.** Local `master` was fast-forwarded through
accepted feature tip `67e4043185fa2da8d22536569223f5bb29af3993` after the
fixed-head verdict. `master` and `feat/audited-harness-slice4` had identical
content and identical tips after the fast-forward; content arrival was
verified separately from ancestry before the feature branch was deleted. No
push was performed or authorized.

---

**SLICE 5A COLD-BACKGROUND ADMISSION CLAUDE REVIEW — ACCEPTED** (Claude Code
2.1.207, model `claude-opus-4-8`, isolated disposable worktree), reviewed head
`ed7c7826dfd26380e50ca6ad5ace40ba23ad71dd` against base
`1a2f3e9b64c1ba101ba1d3f71e435f3219601a7f`, `guard_confirmed=true`,
2026-07-13T20:19:30Z. The one-shot JSON envelope exited zero; its structured
payload matched the verdict schema and both fixed SHAs matched the dispatch.
Claude found no material defect in the default-on immutable admission fact,
false-policy audit lifecycle, no-effect ordering, audit-failure behavior, or
the direct-commit backstop.

Claude independently proved five guards red then green: bypassing the early
tool gate reached the cwd probe; removing the manager backstop admitted a
direct process commit; using the ordinary refusal path invented execution
validation events; weakening proved no-root coverage falsified the audit
guard; and swallowing final `call.not_started` persistence failure returned a
non-error response with a lost terminal record. Exact restoration was clean
and passed 1,094/1,094 .NET tests, 141 Pester tests with two platform skips,
and the stdio handshake. The detached review worktree was removed and the
feature worktree remained clean.

One nonblocking future-scope observation remains: ordinary events currently
carry the truthful reserved-default `allow_cold_background=true` literal.
When Slice 8 introduces named session binding, the supervisor audit context
must carry the same frozen effective policy as the runtime so a false-policy
event cannot contradict its session fact. No public session surface or later
Slice 5 routing/recovery work was pulled into this sub-slice. Acceptance
authorizes the next approved Slice 5 sub-slice, not push or history rewriting.

---

**SLICE 5B TYPED COLD-BACKGROUND DISPATCH FOUNDATION CLAUDE REVIEW —
ACCEPTED** (Claude Code 2.1.207, model `claude-opus-4-8`, isolated disposable
worktree), reviewed head `bbcb1b73adeaa8cb9a6ae24f9ef588be40de459b`
against base `ff4244792f68a802c5e57dd546d44d388eae3eed`,
`guard_confirmed=true`, 2026-07-13T22:49:00Z. The one-shot JSON envelope
exited zero; its result and structured payload matched exactly, both fixed
SHAs matched the dispatch, and Claude found no material defect in the typed
cold-background dispatch/JobManager foundation.

Claude independently proved two high-risk boundaries red then green. Removing
the fallback-first refusal let an unproved fallback reach a real process-start
attempt, and misclassifying an indeterminate start as proved-no-start broke
three no-retry/retained-outcome guards. Exact restoration passed 1,125/1,125
.NET tests and 141 Pester tests with two platform skips in the review
worktree. The coder's same-content local tree also passed the zero-warning
build and stdio handshake. The detached worktree was clean at the reviewed
SHA and removed.

Two explicitly non-blocking observations remain. The start-attempt ledger
retains one small entry per retained job, matching the existing unbounded job
table; prune them together when job-record retention arrives. A kill request
in the narrow interval after process exit but before terminal observation can
transiently report `Failed` rather than `AlreadyExited`; termination reason is
rolled back and no false side effect is claimed. Neither observation is a
present material defect.

Production invoke planning and operational fallback, persisted-provenance
polling, opaque output handles, and model-facing path removal remain later
Slice 5 work. Acceptance authorizes that next approved sub-slice, not push or
history rewriting.

---

**SLICE 5C PROVENANCE-AWARE BACKGROUND POLLING CLAUDE REVIEW — ACCEPTED**
(Claude Code 2.1.207, model `claude-opus-4-8`, isolated disposable worktree),
reviewed head `c93686698fcf0485923769fcb07816db800a426d` against base
`5d122055fef0178e64517ded7076b2a3ac0fb439`, `guard_confirmed=true`,
2026-07-13T23:46:04Z. The one-shot JSON envelope exited zero; its result and
structured payload matched exactly, both fixed SHAs matched the dispatch, and
Claude found no material defect in provenance-aware job polling or access
audit.

Claude independently proved four boundaries red then green: treating
`RtkUnknown` as direct text ran generic `rtk log` a second time and leaked
ANSI; projecting the legacy compatibility plan invented source routing;
dropping the RTK input provenance changed a cleanup-failure audit to
`direct_text`; and removing or making unconditional the seam-absent recovery
suffix broke the RTK positive or direct negative control. Exact restoration
passed the zero-warning build, 1,129/1,129 .NET tests, 141 Pester tests with
two platform skips, and the stdio handshake. The detached worktree was clean
at the reviewed SHA and removed.

Three nonblocking observations remain. The reviewer's first full .NET run hit
the unchanged 500 ms setup timing in
`RunspaceHostTests.Post_start_module_file_mutation_cannot_execute_during_repriming`;
the test passed 3/3 in isolation and the restored full suite reran clean.
Model-facing job paths remain intentionally present until the approved
handle/path-free sub-slice lands. The new Windows fixture branches were
reviewed statically on macOS and remain covered by the existing CI matrix.
Acceptance authorizes the next approved Slice 5 sub-slice, not push or history
rewriting.

---

**SLICE 5D COLD BACKGROUND PLANNING FIDELITY PRIMITIVES CLAUDE REVIEW —
ACCEPTED** (Claude Code 2.1.207, model `claude-opus-4-8`, isolated disposable
worktree), reviewed head `36e0f49fd7bc38a7af6f7fcb0b2263a49862295b`
against base `43d1307f061904e0e6a54d6d71aa303ac59637cc`,
`guard_confirmed=true`, 2026-07-14T01:29:43Z. The one-shot JSON envelope
exited zero; its result matched the verdict schema, both fixed SHAs matched
the dispatch, and Claude found no material defect in the cold planning
primitives or the platform-faithful test correction.

Claude independently changed `rawEntry.TrimStart()` to `rawEntry.Trim()`.
The live-PowerShell PATH differential failed only for `trailing_space`, with
12 resolver tests passing and one failing; exact restoration passed all 13
resolver tests and left both index and worktree clean at the reviewed SHA.
The coder's exact restored tree passed 1,173/1,173 .NET tests, 141 Pester
tests with two platform skips, and the stdio handshake.

Three reviewer comments were adjudicated as nonblocking. Windows CI remains
load-bearing for live PATHEXT/candidate-order coverage. A case-only spelling
change in a Windows target path can conservatively report target-resolution
change because the target record uses ordinal path equality; this proves no
start and selects the authorized exact-original fallback rather than changing
semantics. Claude also questioned case-insensitive PATH-directory
deduplication on case-sensitive filesystems, but the coder rechecked upstream
PowerShell: `CommandDiscovery.LookupPathCollection.Contains` and `IndexOf`
unconditionally use `StringComparison.OrdinalIgnoreCase`, so PTK matches the
behavior it models. Production invoke activation/revalidation/fallback and
output-handle wiring remain the next approved Slice 5 scope. Acceptance does
not authorize push or history rewriting.

---

**SLICE 5 PRODUCTION COLD-BACKGROUND ACTIVATION CLAUDE REVIEW — ACCEPTED**
(Claude Code 2.1.208, model `claude-opus-4-8`, isolated disposable worktree),
reviewed head `d3ff11517ed266b18b0021b7f49425e7a536e20d` against base
`79b1d3e377bc1c8383e81d836b53e49f65c99db3`, `guard_confirmed=true`,
2026-07-14T03:11:29Z. The first one-shot envelope exited zero and completed
the review, but its result wrapped the required JSON object in explanatory
prose, so the orchestrator rejected it fail-closed. The single permitted
schema-only retry exited zero and returned an exact payload: both fixed SHAs
matched, the guard flag was literal true, and the verdict was `accepted`.
Claude found no material defect in the post-cwd cold plan, split durable audit
barriers, commit-time launch revalidation, one-shot proved-no-start fallback,
deadline gates, or terminal-route transfer.

Claude independently proved two high-risk boundaries red then green. Replacing
literal command re-resolution with a hash check of only the captured target
let a changed PATH reach process start and changed the expected
`rtk_target_resolution_changed` failure to `rtk_process_start_failed`.
Removing transactional `_routing` rollback after a failed fallback append
left the unaudited `powershell_direct` projection in memory. Exact restoration
passed the zero-warning build, 1,192/1,192 .NET tests, 141 Pester tests with
two platform skips, and the stdio handshake. The detached worktree was clean
at the reviewed SHA and removed; the coder worktree remained untouched.
The coder's first canonical solution rerun hit the unchanged one-second warmup
budget in `Private_output_stop_is_joined_before_disposal_and_guard_release`;
that test passed 3/3 in isolation and the clean canonical rerun passed
1,192/1,192.

Two reviewer observations were adjudicated as nonblocking. A failure to attach
the already-started process handle after `Process.Start()` returned true can
conservatively surface `ProcessStarted=null`; it still forbids retry, and the
reviewer considered the path effectively unreachable. `AbandonFallback`
could mask a return if its invariant unexpectedly threw, but the attempt ledger
always transitions before that cleanup and terminal-lease release is protected
by an inner `finally`. Opaque output handles and path-free recovery remain the
next approved Slice 5 scope.

---

**SLICE 5 PATH-FREE BACKGROUND OUTPUT RECOVERY REVIEWS — ACCEPTED.** Claude
Code 2.1.208 (model `claude-opus-4-8`) completed at
2026-07-14T04:33:35Z; Grok 0.2.93 (`f00f96316d4b`, default configured model)
completed at 2026-07-14T04:35:04Z. Each ran headless and one-shot in a separate
isolated disposable worktree, reviewed head
`fc61be661aff876e13c022a016a39d64ccb0a48c` against base
`ee21f16218122e9e8de3b2c93deb13c82d807328`, returned an exact schema-
constrained `accepted` payload with `guard_confirmed=true`, and named the
dispatched SHAs. Claude's outer envelope and inner structured payload matched
exactly. Grok's authoritative `structuredOutput` matched the schema and had an
empty comments array. The orchestrator independently confirmed both detached
trees clean at the reviewed SHA before recording either verdict.

Claude independently proved seven production boundaries red then green:
terminal handle publication; direct-text versus seam-absent `RtkUnknown`
eligibility; poll-read path sanitization; no-start reservation cleanup; use of
the shared output-store lane for terminal sealing; bounded UTF-8 scalar
completion; and the real MCP background `ptk_job` to `ptk_output` path. It also
confirmed the fallback output-store call site with its focused no-start guard
after an initial handshake mutation correctly demonstrated that the handshake
exercises only the primary dispatch path. Every mutation was restored before
its focused guard returned green.

The exact restored Claude tree passed 1,202/1,202 .NET tests, 141 Pester tests
with two platform skips, and the expanded stdio handshake, including retrieval
of one background direct-text invocation through the path-free handle. The
coder's exact committed tree had already passed the same full battery and a
zero-warning build, with focused red-to-green proofs covering publication,
capacity cleanup and timing, anti-wedge sealing, UTF-8 boundaries, provenance,
path isolation, exception sanitization, stable/incomplete wording, kill
semantics, invoke wiring, descriptions, generated guidance, and the real MCP
path. Grok's independent schema verdict attested to the same required guard
and restored-battery gate and reported no material finding.

Two Claude observations are nonblocking and remain recorded here rather than
silently discarded. Four concurrent long-lived direct-text jobs can reserve
the default session's full 32 MB output quota (8 MB each), making later capture
truthfully unavailable until a reservation releases; execution remains exact-
once and the degraded reason is explicit, as the approved plan permits. Also,
`.agents/repo-guidance.md` retains stale 2026-07-03 test counts while this
head's observed counts are 1,202 .NET and 141 Pester; that out-of-range docs
drift belongs to a future deliberate `drift` pass. Neither reviewer found a
material observable defect. Acceptance completes Slice 5 and authorizes the
approved Slice 6 implementation next; it does not authorize push or history
rewriting.

---

**SLICE 6 `SESSIONRUNTIME` EXTRACTION CLAUDE REVIEW — ACCEPTED.** Claude Code
2.1.208 (model `claude-opus-4-8[1m]`, isolated disposable worktree) completed
at 2026-07-14T05:57:42Z. Its first one-shot envelope exited zero and returned
an exact schema-constrained `accepted` payload with `guard_confirmed=true`,
reviewed head `7999328de546c86b042e58b0ff21b38d6e97e322`, and base
`aca20a65e7441297a2763f73e27c358a80ae4eae`. The orchestrator independently
matched both fixed SHAs and the literal guard flag. Claude found no material
defect in the verbatim handler extraction, per-call audit/output capability
boundary, jobs-before-runspace shutdown, fail-closed DI construction,
runtime-local cache, reset lifetime, or unchanged MCP schemas/defaults.

Claude independently proved four product boundaries red then green: making
the available-module cache static broke two-runtime cache isolation; removing
runtime disposal left its long-running job alive; replacing the reset adapter
with a plausible constant left warm state uncleared; and clearing the module
cache during reset forced a second enumeration. Every mutation was restored.
The exact restored head passed 1,207/1,207 .NET tests, 141 Pester tests with
two platform skips, and the stdio handshake. Claude confirmed its detached
tree clean at the exact head, removed it, and left the coder tree untouched.

One nonblocking reviewer observation was evidence-checked rather than silently
accepted. A same-typed invoke `raw`/`background` swap passed the .NET suite,
and Claude suggested direct adapter tests for invoke/job/state. In a separate
clean detached tree, the coder proved the canonical stdio handshake already
guards those adapters behaviorally: that invoke swap failed the background
job assertion, a job `id`/`offset` swap failed background status, and a
constant state adapter failed the state header assertion. The focused reset
adapter test covers the only adapter the handshake does not execute. All
mutations were restored, the tree was clean at `7999328`, and it was removed.
No product or required-verification gap remains. Acceptance completes Slice 6;
it does not authorize push, history rewriting, or landing without the next
owner go.

---

**CI PORTABILITY REPAIR REVIEW — ACCEPTED.** An independent
`ci_repair_review` agent reviewed head `1066de1` against base `e775a1d` on
2026-07-14 and found no material defect. The exact range changes only the
approved plan/state and six test-harness files; it does not change production
code. The reviewer confirmed that the original behavioral assertions remain
intact, temporary RTK fixtures and environment state are restored, the
Windows owner setup uses only token-assignable capabilities, the state probe
uses the production budget, the bounded buffer workload retains the same-
buffer overwrite opportunity, and both command-resolution fixtures preserve
their original guards. `git diff --check` passed.

The restored code head passed 1,207/1,207 .NET tests, 141 Pester tests with
two expected platform skips, and the full stdio handshake. A second complete
.NET run with `PTK_RTK_PATH` forced to a missing file also passed
1,207/1,207. Independent red-to-green mutation proofs showed that removing
the output-buffer ownership copy loses the first canary and that removing the
fully-qualified cold-target guard captures the deliberately relative file.
At this reviewed head the Windows-specific branches remained provisional
until an explicitly approved push could run the hosted GitHub Actions matrix.
The hosted follow-up below records the final correction and closes that proof.

---

**CI PORTABILITY HOSTED FOLLOW-UP — ACCEPTED.** Owner-approved GitHub Actions
run `29312600445` tested `c710702` and passed Ubuntu and macOS completely. Its
Windows job improved from the original seven failures to 1,206/1,207 server
tests: the sole remaining assertion compared the fixture spelling
`fixture.EXE` with the resolver's equivalent `fixture.exe` spelling. Two
independent agents confirmed that the resolver intentionally preserves the
requested candidate spelling, production Windows path identity is case-
insensitive, and the neighboring live-PowerShell comparisons already use the
same semantics. Commit `3c61886` changes only that expected-source assertion;
no production file changes.

A deliberate mutation prioritizing the appended `.ps1` candidate made the
existing exact-extension guard fail with `ExternalScript` instead of
`Application`; restoring production ordering returned both focused tests
green. This proves the casing correction does not mask a real candidate-order
regression. Owner-approved GitHub Actions run `29313220388` then tested exact
head `3c618867adbe1c172f0b95fed53cc7425280a3f1`: Ubuntu, macOS, and Windows all
passed checkout, SDK setup, Pester, the full server suite, the stdio handshake,
and cleanup. This closes the hosted portability proof; it does not authorize
landing or history rewriting.

---

**CI MASTER-LANDING FLAKE STABILIZATION — ACCEPTED.** An independent
`ci_repair_review` agent reviewed exact range `00e74d2..d30bbf3` on
2026-07-14 and found no material defect. The range changes only the approved
plan/state and two test files. The setup warm invocation alone receives a
ten-second override; the subject retains the one-second host timeout and every
timeout, stop/join, unavailable-guard, shutdown, and eventual-release
assertion. The concurrent startup test now mirrors production request scoping,
uses one disposed scope per contender, and deterministically overlaps its
first two handlers without exceeding journal reservation capacity. Exactly two
opens, one recovery/startup, eight handlers, eight accepted/completed pairs,
and final stop ordering remain asserted. Barrier cleanup is fail-safe, and no
production file changes.

The focused Windows proofs were red then green. A 1.5-second private-output
opening delay exhausted the old setup budget but passed with the explicit
setup-only override; removing the production `StopCompleted` join still broke
the stabilized test. The old singleton audit holder reproduced immediately on
`NETWATCH-01`, and under the deterministic barrier only one of the first two
handlers entered; restored scoped ownership passed 10/10. The exact restored
head passed 1,207/1,207 local .NET tests, 141 Pester tests with two platform
skips, and the full local handshake. Direct Windows validation passed
1,207/1,207 .NET tests, 142 Pester tests with one platform skip, and the full
zero-warning handshake.

Owner-approved GitHub Actions run `29316181542` tested exact head
`d30bbf3701c484aeb81ab59616f6aa074687e95c`; Ubuntu, macOS, and Windows each
passed setup, Pester, the complete server suite, the stdio handshake, and
cleanup. This closes the master-landing flake follow-up. It does not authorize
history rewriting or unrelated work.

---

**CI FINAL-TIP SCHEDULING FOLLOW-UP — CODE ACCEPTED; SISTER FIXTURE EXPOSED.** An
independent `ci_final_review` agent reviewed exact committed range
`e3b1dfd..6193129` on 2026-07-14. The range changes only three test files plus
plan/state documentation; no production file changes. The reviewer confirmed
that the startup-repair test retains scoped per-request ownership,
deterministic two-handler overlap, and every original event/open/shutdown
assertion while reusing its original fifteen-second contender budget. Both
atomic-publication fixtures retain their start gates, eight-party barriers,
convergence or identity assertions, and temporary-file cleanup checks while
moving synchronous contenders to dedicated `LongRunning` threads on
`TaskScheduler.Default`. No code finding remained.

The guards were independently red then green. Restoring singleton ownership
admitted only one of the first two audit handlers; scoped ownership passed
10/10 on Windows. Replacing each production collision-recovery return with a
rethrow made its corresponding stabilized fixture fail immediately with the
losing `File exists` publication error, not a rendezvous timeout; exact
restoration returned both green, and the pair passed together 10/10. The
complete local battery passed 1,207/1,207 .NET tests, 141 Pester tests with two
expected skips, and the full handshake. The matching server patch passed
1,207/1,207 .NET tests, 142 Pester tests with one expected skip, and the full
zero-warning handshake on `NETWATCH-01`. Hosted run `29318333860` passed
Ubuntu and macOS but exposed the separate
concurrent-recovery singleton fixture on Windows; Slice 10 below closes it.

---

**CI CONCURRENT RECOVERY SCOPING — ACCEPTED.** An
independent `slice10_review` agent reviewed exact committed range
`24c7958..6193ae4` on 2026-07-14 and found no material issue. The range changes
only `AuditRuntimeGateTests.cs` plus plan/state documentation; no production
files. The recovery fixture now matches production's request-scoped
`AuditCallContextAccessor`, with one disposed scope for the initial failed
request and each of eight recovery contenders. Its additive first-two-handler
overlap reuses the original twenty-second contender budget, and both test-owned
gates release in `finally`. All original recovery, health, handler/event-count,
and final shutdown assertions remain.

The singleton mutation deterministically admitted only one handler and failed
the overlap checkpoint on macOS and Windows. Restored scopes passed 10/10 on
both. The complete local battery passed 1,207/1,207 .NET tests, 141 Pester
tests with two expected skips, and the full handshake. The matching Windows
patch passed 1,207/1,207 .NET tests, 142 Pester tests with one expected skip,
and the full zero-warning handshake. GitHub Actions run `29331077331` tested
exact docs descendant `ccee469`; Ubuntu, macOS, and Windows each passed Pester,
all 1,207 server tests, the stdio handshake, and cleanup. This completes the
CI portability and master-landing stabilization follow-up without production
changes.

---

**AUDITED-HARNESS SLICE 7A BOUNDED WORKER PROTOCOL — ACCEPTED.** Claude Code
2.1.209 (model `claude-opus-4-8[1m]`, isolated disposable worktree) reviewed
exact committed range `a88d6050ade9e972a0cee6cb9a22e5e188895f30..f86de26a6a2d3b3bc52928c0ee0254873aba7863`
on 2026-07-14 and returned the required structured `accepted` verdict with
`guard_confirmed=true`. The range adds the strict bounded v1 worker protocol
foundation and its tests: nine frozen message kinds, strict UTF-8 NDJSON,
duplicate/unknown/version rejection, a 1 MiB encoded-frame cap, depth 32,
incremental fragmented/coalesced reads, serialized writes, terminal writer
fault latching, and clearing of pooled buffers that may contain script data.

The reviewer independently repeated six required mutations. Frame-cap drift,
dropping coalesced read-ahead, bypassing the writer gate, returning uncleared
pooled data, relaxing the depth check, and removing the terminal writer-fault
latch each failed for its intended reason; restoring the exact reviewed head
returned the focused and complete suites green. The restored battery passed
1,248/1,248 .NET tests, 141 Pester tests with two expected skips, and the full
stdio handshake. The orchestrator independently confirmed the coder and
review trees were both clean at the exact reviewed SHA, then removed the
disposable worktree.

Payload-specific script/result/diagnostic caps remain owned by the later
worker/dispatch layers. A future single-reader worker loop must tear down on
reader cancellation or error, and dispatch remains responsible for request-id
applicability. These are non-blocking obligations for later approved Slice 7
sub-slices, not protocol-code defects. This acceptance authorizes the next
approved Slice 7 sub-slice; it does not authorize push, history rewriting, or
landing.

---

**AUDITED-HARNESS SLICE 7B SESSION OPERATIONS/LIFETIME SEAM — ACCEPTED.**
Claude Code 2.1.209 (model `claude-opus-4-8[1m]`, isolated disposable
worktree) reviewed exact committed range
`2eca287a0d9111d91a741d8ddd16c6c621d53370..e70089fd933b638fd4732c87c5b0e39ba7a423b6`
on 2026-07-14 and returned the required structured `accepted` verdict with
`guard_confirmed=true`. The range introduces the public tool-to-provider
`ISessionOperations` seam and separate internal disposable
`ISessionLifetime`, keeps audit/output capabilities supervisor-local rather
than making them protocol payloads, retargets all four session tools and
production DI, and makes the audit runtime gate own only the ordered lifetime.
It adds no worker hosting and changes no tool schema, output, reset, or audit
authority.

The reviewer independently repeated three required mutations. A concrete
`SessionRuntime` downcast failed against the fake provider; an unawaited
lifetime drain let server shutdown overtake the blocker; and inferred concrete
DI registration leaked `runtime` into the public `ptk_state` schema. An extra
mutation dropping the audit capability at the explicit interface boundary also
drove the full .NET suite red. Restoring the exact reviewed head returned
1,250/1,250 .NET tests, 141 Pester tests with two expected skips, the full
stdio handshake, and `git diff --check` green. The orchestrator independently
confirmed the coder and review trees were both clean at the exact reviewed
SHA, then removed the disposable worktree.

The future remote provider must capture each request's current audit
capability immediately, retain the output store in the supervisor, and send
only bounded protocol DTOs. Deadline-aware worker shutdown remains owned by
the later worker/containment sub-slices. These are the approved next-layer
obligations, not findings in this seam. This acceptance authorizes the next
approved Slice 7 sub-slice; it does not authorize push, history rewriting, or
landing.

---

**AUDITED-HARNESS SLICE 7C PLATFORM-NEUTRAL WORKER LIFECYCLE — ACCEPTED.**
Claude Code 2.1.209 (model `claude-opus-4-8[1m]`, isolated disposable
worktree) reviewed exact committed range
`cfaee5fce1b4b59f1672f32fec969ce72582703c..56734e3ec7793d259cda5ae7e70ba1b0e78d0522`
and returned the required structured `accepted` verdict with
`guard_confirmed=true` at 2026-07-14T16:17:16Z. The range adds a
platform-neutral lifecycle server over injected private streams: hello before
read, strict initialize-first boot/ID/payload validation, one absolute startup
deadline that includes scheduler queue time, explicit host-cancellation races,
ready/shutdown correlation, stable protocol/transport/runtime exits, and
exactly-once shutdown/disposal of every returned session lifetime. It does not
add worker mode, stdio, process launch, containment, request dispatch, audit,
or output-store capability.

The reviewer independently repeated seven required mutations. Moving runtime
construction back onto the protocol loop hid EOF; dropping buffered EOF
admission constructed a runtime; weakening the delegate-side deadline check
constructed after queue expiry; dropping late-factory drain leaked the
returned lifetime; synchronous unguarded cancellation leaked an aggregate
exception to the host; changing deadline equality published false ready; and
returning canceled before cleanup left the ready lifetime undrained. Each
named focused guard failed for its intended reason. Restoring the exact
reviewed head returned 1,280/1,280 .NET tests, 141 Pester tests with two
expected skips, the full stdio handshake, and `git diff --check` green.

The reviewer recorded three nonblocking observations. Two `run.IsCompleted`
cleanup probes are scheduling-sensitive in isolation, but the cleanup result
classification test deterministically guards the same await. The capability
reflection sweep checks the outer `WorkerServer` surface rather than nested
types; no forbidden capability exists at this head, and later nested-state
changes must preserve that source-level boundary. The post-ready loop is
single-iteration until the next dispatcher sub-slice replaces it. None is a
production defect in this range. The orchestrator independently parsed and
matched the JSON `result` and `structured_output`, verified exit zero, exact
base/head, literal guard confirmation, and clean coder/reviewer trees, then
removed the disposable worktree. This acceptance authorizes the next approved
Slice 7 sub-slice; it does not authorize push, history rewriting, or landing.

---

**AUDITED-HARNESS SLICE 7D WINDOWS CREATION-TIME CONTAINMENT — ACCEPTED.**
Claude Code 2.1.209 (model `claude-opus-4-8`, isolated disposable worktree)
reviewed exact committed range
`3348167530024d0543abad973ed3178a9c95b3e7..bbc2a0e2b116280ceebaab5442014286671aefe2`
and returned the required structured `accepted` verdict with
`guard_confirmed=true` at 2026-07-14T17:35:31Z. The range adds the deliberately
unwired Windows launch primitive: an unnamed noninheritable Job Object with
exact queried-back `KILL_ON_JOB_CLOSE`, five explicit child handles, one
`CreateProcessW`/`STARTUPINFOEX` call with exactly `JOB_LIST` plus
`HANDLE_LIST`, a sorted closed Unicode environment, proof-only suspension,
exact membership verification, and job-first rollback/ownership cleanup. It
does not add worker entry, DI/tool routing, Unix containment, or a fallback
spawn path.

The reviewer independently repeated seven required mutations. Removing the
post-membership cancellation check, changing the exact job limit, suspending a
runnable launch, dropping the environment block's final NUL, breaking trailing
backslash quoting, adding a generic `Process.Start` fallback, and adding an
alternate `CreateProcessA` import each failed only its intended guard. An extra
attribute-binding mutation demonstrated that the live `JOB_LIST` binding is a
Windows-only proof on the reviewer host. Restoring the exact reviewed head
returned 1,309/1,309 .NET tests, 141 Pester tests with two expected skips, the
full stdio handshake, `git diff --check`, and a clean worktree.

Independent direct validation on `NETWATCH-01` covered the reviewer's platform
limit. A hash-matched exact-tree archive passed all 29 focused Windows guards,
1,309/1,309 .NET tests, 142 Pester tests with one expected skip, and the full
zero-warning handshake. A separate mutation making runnable creation suspended
failed the live checkpoint after fifteen seconds, then job-first cleanup left
no process residue; exact restoration passed. Host-specific archive hashes and
cleanup evidence are in `.agents/machines.md`.

The reviewer recorded three nonblocking obligations for the wiring slice.
Cross-platform source guards pin the attribute constants but not their
call-site adjacency; live Windows integration owns that proof. Before a caller
uses cancellable native process waits, replace the borrowed nonowning wait
handle with an owning duplicated process handle so unregister/dispose cannot
race handle reuse. Before wiring this launcher, eliminate or coordinate generic
supervisor-side inheriting spawns because the five selected child ends are
temporarily inheritable process-wide. These are later integration obligations,
not defects in the unwired range.

The orchestrator independently parsed and matched the JSON `result` and
`structured_output`, verified exit zero, exact base/head, literal guard
confirmation, and clean coder/reviewer trees, then removed the disposable
worktree. Two denied redundant reviewer status probes contributed no evidence;
the orchestrator repeated those checks directly. This acceptance authorizes the
next approved Slice 7 sub-slice; it does not authorize push, history rewriting,
or landing.

---

**AUDITED-HARNESS SLICE 7 WINDOWS WAIT-OWNERSHIP PREREQUISITE — ACCEPTED.**
Claude Code 2.1.209 (model `claude-opus-4-8[1m]`, isolated disposable
worktree) reviewed exact committed range
`4578e6f6b29e89751a2940a6a94b98b7f8fe73a0..d1cca1b5fc69be61c7102843ab3ceb645cd362eb`
and returned the required structured `accepted` verdict with
`guard_confirmed=true` at 2026-07-14T18:25:25Z. The range replaces borrowed
cancellable process waits with per-wait, noninheritable `DuplicateHandle`
copies owned through `SafeWaitHandle`, with immediate Win32 error capture and
explicit ownership transfer. It adds no worker entry, launcher wiring, tool
routing, or Unix containment.

A preliminary structured review ending at
`5ea7d602a9809ab1a38f2b15ba3cfc2a87036147` also returned formal `accepted`
with `guard_confirmed=true`, but that verdict was immediately reopened and is
explicitly superseded rather than treated as final acceptance. Independent
preflight showed the guard could pass with an unused owning helper while the
active async path still borrowed the canonical handle; its two-wait live
fixture could also mask the race because the other waiter retained the
canonical handle. Corrective commit `d1cca1b` pins the compiled
`WaitForExitAsync` state machine's direct construction of
`OwnedProcessWaitHandle`, globally rejects the borrowed helper, and changes the
live Windows sequence to cancel the sole wait, dispose the contained owner
before awaiting cancellation, then observe cancellation and process death
through an independent witness.

The final reviewer independently repeated both guard mutations. Replacing the
owning constructor with the old raw borrowed handle failed the compiled direct-
call assertion; separately leaving the owning helper intact while switching
only the active wait path to a borrowed helper failed the async-state-machine
construction assertion with expected one and actual zero. Exact restoration
passed the named guard after each mutation, then passed 1,309/1,309 .NET tests,
141 Pester tests with two expected platform skips, the full stdio handshake,
scoped formatting, both diff checks, and exact-head cleanliness. The reviewer
confirmed that the sole-wait order removes the former second-wait masking
condition while correctly noting that scheduling still makes the live race a
complementary rather than deterministic ownership proof. Direct Windows
execution and cleanup evidence is canonical in `.agents/machines.md`.

Four observations were nonblocking: the source assertion for
`desiredAccess: 0` is looser than its wording, though `DUPLICATE_SAME_ACCESS`
makes that mask ignored; the audited class-body slice relies on textual
adjacency; the live test is intentionally a no-op on non-Windows review hosts;
and duplication failure now surfaces `Win32Exception` rather than the launch-
stage exception type. None is a material defect in this prerequisite. This
acceptance closes only owning wait handles; it does not authorize default-
session wiring, push, history rewriting, or landing.

---

**AUDITED-HARNESS SLICE 7E WINDOWS WORKER ENTRY — ACCEPTED.** Claude Code
2.1.209 (model `claude-opus-4-8[1m]`, isolated disposable worktree) reviewed
exact committed range
`eec7ed1ec1a17a0ae70d62895162864c637f8b42..12617ccb25a3f3ff8d9690d94ebe5cea4f141ee6`
and returned the required structured `accepted` verdict with
`guard_confirmed=true` at 2026-07-15T03:09:09Z. The range adds the Windows-only
managed `--worker` lifecycle entry before supervisor infrastructure, captures
and removes both bootstrap variables before validation or runtime creation,
replaces inherited protocol handles with validated noninheritable pipe
duplicates, and drives the existing lifecycle server through private request
and event streams. It freezes managed exits `0`, `64`, and `80..84`, emits one
bounded allow-listed stderr diagnostic only for nonzero exits, and leaves the
default MCP `ISessionOperations` registration in-process.

Independent preflight reopened preliminary code head `e2cbfb5` for two medium
guard vacuities: the concrete process-environment adapter was never exercised,
and managed exits `81..84` could write through global diagnostic destinations
without failing a test. Test-only commits `e9421cc` and `12617cc` add a real
process-environment removal guard plus entry-level global-console capture and
direct-standard-output source guards. A first formal reviewer launch was
discarded before verdict because its sandbox denied proof mutations; it
contributed no acceptance evidence.

The final reviewer independently repeated all eleven required mutations:
retaining the event bootstrap variable, neutering the concrete environment
removal, making an active duplicate inheritable, entering the runtime before
validated initialize, leaking partial bootstrap ownership, passing through a
hostile detail code, retrying or issuing a second diagnostic write, writing
separately through global `Console.Out` and `Console.Error`, opening standard
output directly, and removing the early worker return from `Program`. Each
failed only its intended guard. Blob hashes confirmed exact production-file
restoration; the restored tree passed 1,432/1,432 .NET tests, 141 Pester tests
with two expected platform skips, the full stdio handshake, scoped formatting
for every changed C# file, `git diff --check`, and exact-head cleanliness.

Independent direct validation on `NETWATCH-01` covered the Windows-only path.
The hash-matched final archive passed all 125 focused worker/bootstrap/
containment guards, 1,432/1,432 .NET tests, 142 Pester tests with one expected
platform skip, and the zero-warning handshake. A forced-build mutation
disabling the `Program` worker branch discovered exactly one live test and
failed at the first hello assertion in 254 ms. Exact source restoration left
zero mismatches and zero relevant process residue; all disposable paths were
removed. Host-specific archive, profile, and cleanup evidence is in
`.agents/machines.md`.

One reviewer observation was nonblocking and outside the reviewed range:
`BashProcessRunnerTests.Execution_pipe_drains_cannot_outlive_the_call_deadline`
is an older real-process 500 ms timing fixture that failed four of five cold
isolated probes around 550 ms, passed with a warm two-second allowance, and
passed in the final 1,432-test run. One denied initial `rtk`-prefixed status
probe also contributed no evidence; ordinary commands and the orchestrator's
independent checks confirmed both SHAs and clean trees. The orchestrator parsed
and matched the JSON `result` and `structured_output`, verified exit zero,
literal guard confirmation, and exact base/head, then removed the disposable
worktree.

Operation DTOs, dispatch/cancellation, supervisor audit/output transfer, and
the atomic default-session proxy cutover remain later Slice 7 work. This
acceptance authorizes only the next approved sub-slice; it does not authorize
push, history rewriting, landing, or default-session cutover.

---

**AUDITED-HARNESS SLICE 7F OPERATION TRANSPORT — ACCEPTED.** Claude Code
2.1.209 (model `claude-opus-4-8[1m]`, isolated disposable worktree) reviewed
exact committed range
`3580e674153c866ee22cdfa9c4b6ee8b4962125a..a9e757ecb22a4fe61c60e9076a2fac88293523b8`
and returned the required structured `accepted` verdict with
`guard_confirmed=true` at 2026-07-15T05:13:44Z. The range adds strict private
request/cancel parsers and a response codec plus a standalone 64-request
scheduler with increasing request-ID reservation, off-reader-thread dispatch,
targeted cancellation and deadlines, owned-cancellation classification, one
uncancelled terminal write attempt, fatal peer cleanup, and idempotent drain.
It remains deliberately unwired: no production executor, `SessionRuntime`,
worker server, process entry, program, DI, MCP, audit, output, reset, or proxy
path references the new transport.

A preliminary launch against the same fixed SHAs returned `invalid` with
`guard_confirmed=false` because plan-mode correctly denied every mutation and
test command. It changed nothing and contributed no acceptance evidence. The
orchestrator re-dispatched the exact review with mutation authority confined to
the already-disposable worktree; that corrected launch is the sole acceptance
verdict.

The final reviewer independently applied eight one-at-a-time production
mutations: adding a concrete production `IWorkerOperationExecutor`, permitting
`detailCode` on completed responses, moving capacity rejection before
request-ID reservation, discarding the injected initial high-water mark,
routing the admission trampoline through the inline scheduler, redirecting
explicit cancellation to another active request, weakening owned-versus-
foreign cancellation-token classification, and removing fatal peer
cancellation after writer failure. Each failed its intended guard for the
expected assertion, not compilation or unrelated infrastructure. Every edit
was explicitly reversed without reset or checkout; final production blob
hashes matched the reviewed head and the worktree was clean.

The restored reviewer tree passed the 38-test focused Slice 7f suite,
1,470/1,470 .NET tests, 141 Pester tests with two expected skips, and the full
stdio handshake. The coder's separate disposable proof applied eight
independent variants, each failed its named guard, and the restored broader
68-test worker-operation focus passed. Two denied redundant `rtk git` reviewer
probes contributed no evidence; ordinary PTK-routed checks and the
orchestrator's independent SHA/status checks confirmed the range and clean
trees. The orchestrator parsed and matched the JSON `result` and
`structured_output`, verified exit zero, exact base/head, literal guard
confirmation, and then removed the disposable worktree.

Concrete operation argument/result codecs, real runtime dispatch,
prepare/commit/abort/event handling, supervisor audit/output capability
transfer, public job IDs, reset/process replacement, and the atomic
default-session proxy cutover remain later separately owner-gated sub-slices.
This acceptance authorizes only presentation of the next boundary; it does not
authorize push, history rewriting, landing, or live routing.

---

**AUDITED-HARNESS SLICE 7G OPERATION CODECS — ACCEPTED.** Claude Code
2.1.210 (model `claude-opus-4-8[1m]`, isolated disposable worktree) reviewed
exact committed range
`a83e2e66c9eb65d4d3e103dc4da689e991865bcc..eef38cb1b1e5967fab6edf8a431639f869606fa0`
and returned the required structured `accepted` verdict with
`guard_confirmed=true` at 2026-07-15T14:32:56Z. The range adds strict,
transport-kind-neutral value codecs for foreground `invoke`, the four
job-control operations, and `state`; exact closed argument/result DTOs;
strict logical UTF-8 script/result limits; content-free stable failures; and
no production wiring.

The reviewer independently applied all ten required one-at-a-time mutations:
admitting `reset`, accepting duplicate nested fields, accepting unknown
fields, normalizing route case/whitespace, counting UTF-16 code units instead
of strict UTF-8 bytes, removing inbound and then outbound result limits,
retaining each of the two content-bearing framework exceptions, and adding a
production codec reference outside staging. Each failed its intended guard
for the expected assertion rather than compilation or unrelated
infrastructure. The coder separately applied the same ten mutation classes;
an initially weak content-redaction assertion was strengthened when it failed
to detect the retained invalid-code-unit detail, after which both independent
exception-retention mutations failed the corrected no-inner-exception guard.

Exact restoration matched codec blob
`46f5ee9152c6cb12c53539af99728e90283298d9` and unchanged `Program.cs` blob
`74e8380dd42da6ffe164ee983235e549b0cb8a3e`. The reviewer tree was clean at
the exact head and passed the 7-test focused Slice 7g boundary suite,
1,475/1,475 .NET tests, 141 Pester tests with two expected skips, the full
stdio handshake, and `git diff --check`. The coder's restored broader
worker-operation focus passed 38/38 and the same full battery. Two denied
redundant `rtk`-prefixed reviewer probes contributed no evidence; ordinary
checks supplied the recorded SHA, diff, and plan facts.

The orchestrator matched the JSON `result` and `structured_output`, verified
exit zero, exact base/head, literal guard confirmation, clean restored blobs,
and removed the disposable worktree. The reviewer noted only two nonblocking
pre-existing/minor observations: the dated example verification counts in
`.agents/repo-guidance.md` await a deliberate `drift` pass, and one defensive
null branch after a JSON string-kind guard is unreachable but harmless.

Real runtime execution, `prepare`/`commit`/`abort`/`event`, background start,
supervisor audit/output capability transfer, public job-ID allocation,
reset/process replacement, proxy wiring, and the atomic default-session
cutover remain separate owner-gated work. This acceptance authorizes the next
approved sub-slice under the standing delegated local-branch workflow; it does
not authorize push, history rewriting, or live routing.

---

**AUDITED-HARNESS SLICE 7H PREPARED-OPERATION CODECS — ACCEPTED.** Claude Code
2.1.210 (model `claude-opus-4-8[1m]`, isolated disposable worktree) reviewed
exact committed range
`1179ed03b3ca49170763b5cce83400c491fc792b..8f5c57c61b616cb1b85768619d4646902cfc9c72`
and returned the required structured `accepted` verdict with
`guard_confirmed=true`, parsed by the orchestrator at 2026-07-15T15:56:23Z.
The range adds one production staging file containing strict standalone value
codecs and distinct DTOs for foreground-invoke `prepare`, its prepared
correlation, `commit`, and `abort`; no existing production file wires them.

The reviewer confirmed closed-object validation and the frozen content-free
failure codes; canonical lowercase RFC 4122 UUIDv4 plan IDs and lowercase
SHA-256 digests; strict-UTF-8 digest recomputation on parse and create;
positive full-range signed-64-bit generations; lossless positive UTC
whole-millisecond creation deadlines; fail-closed typed correlation matching;
distinct commit and abort types; and digest/hash buffer clearing in `finally`.
It also confirmed that the extended staging guards prohibit envelope, server,
scheduler, executor, runtime, audit/output, DI, and process-lifecycle
capabilities and prohibit every production reference outside the staging file.

The reviewer independently applied all eleven required one-at-a-time
production mutations: making `Match` the enum default, admitting `state` as a
prepare operation, skipping parse-side digest matching, accepting uppercase
digest text, omitting UUID version/variant checks, narrowing generation to
32 bits, accepting non-UTC or sub-millisecond creation deadlines, ignoring
`planId` in commit correlation, removing digest-buffer zeroing and clear-on-
return, retaining the nested invalid-script exception, and adding a real codec
reference in `Program.cs`. Each failed its intended guard. Every mutation was
restored before the next; the final reviewer tree was porcelain-clean at the
exact reviewed head with an empty `git diff HEAD`.

On the restored exact head, the reviewer passed the 16-test focused Slice
7g/7h/staging suite, 1,484/1,484 .NET tests, 141 Pester tests with two expected
platform skips, the full stdio handshake, and `git diff --check`. After its
mutation sequence it again passed the focused suite, full .NET suite, and
handshake. The coder independently exercised the same eleven mutation classes,
restored exact production content, and passed the same full battery. One
denied preliminary `rtk`-prefixed status probe contributed no evidence;
ordinary shell checks supplied the exact SHA, status, diffs, plan facts, and
all verification evidence.

The orchestrator matched the outer JSON result and inner structured output,
verified exit zero, exact base/head, literal guard confirmation, and the clean
restored worktree, then removed the disposable worktree. The reviewer reported
no material issue and no other observation.

Reservation acquisition, final prepared-descriptor and response binding,
planning and command-identity revalidation, idempotent commit, abort/expiry,
runtime execution, audit/output transfer, background start and public job IDs,
job-terminal events, reset/process replacement, proxy wiring, and the atomic
default-session cutover remain separate owner-gated work. This acceptance
authorizes delegated local landing only; it does not authorize push, history
rewriting, or live routing.

---

**MCP RESILIENCE PLAN — ACCEPTED.** Claude Code 2.1.210
(`claude-fable-5`, effort `max`, isolated disposable worktree) reviewed exact
fixed range
`5ae154cbde38127c21a63a3ff5c001814c1ea244..ab54fe17c9baae5ad187aa3160e19fa930bf25a5`
and returned the schema-constrained verdict `accepted` with
`guard_confirmed=true`, exact matching SHAs, and no comments at
2026-07-15T17:25:51Z. The range is the complete resilience-plan branch through
the owner-approved fail-fast model-retry contract: no server-side call queue,
no saved-state authoring tool, retry guidance only for proved-no-start
terminals, a guardian-local state poll before a fresh audited request, and no
retry of `outcome_unknown`.

The orchestrator verified exit zero, the exact base/head, literal guard
confirmation, an empty comment list, clean fixed-range `git diff --check`, and
a clean detached reviewer worktree before removing it. The JSON envelope's
`modelUsage` reported `claude-fable-5` for the review and Claude Code's Haiku
helper only; it contained no Opus model. Reviewed the complete resilience plan;
no material issue found. This acceptance records plan quality only and does
not authorize implementation, merge, push, or history rewriting.

---

**MCP RESILIENCE PLAN, COMPLETE AMENDED RANGE — ACCEPTED.** Claude Code
2.1.210 (`claude-fable-5`, effort `max`, isolated disposable worktree)
reviewed exact fixed range
`5ae154cbde38127c21a63a3ff5c001814c1ea244..b4a2c0c02c2a7bb975eb3d9bbfadc719bdbf76d8`
and returned the schema-constrained verdict `accepted` with
`guard_confirmed=true`, exact matching SHAs, and no comments. The orchestrator
parsed the successful envelope at 2026-07-15T20:22:25Z. This range includes
the complete owner-approved retry-loop timing and readiness-gate contract,
automatic post-timeout baseline recovery, exact guardian/host binary pinning,
cold-background session gating, and the distinct outer-host containment grace.

The orchestrator verified exit zero, the exact base/head, literal guard
confirmation, an empty comment list, clean fixed-range `git diff --check`, and
clean main and detached reviewer worktrees at the exact head. The JSON
envelope's `modelUsage` reported `claude-fable-5` for the review and Claude
Code's Haiku helper only; it contained no Opus model. Earlier attempts through
the removed proxy returned no verdict and were not accepted as review
evidence. The successful replacement used the same frozen SHAs, schema,
review scope, model, and effort after that transport was removed.

Reviewed the complete amended resilience plan; no material issue found. This
acceptance records plan quality only and does not authorize implementation,
merge, push, history rewriting, or live routing.

---

**MCP RESILIENCE R0 IMPLEMENTATION — EXTERNAL REVIEW BLOCKED.** Claude Code
2.1.211 was dispatched at exact fixed range
`215e10fcfede9cf200b21b3c0cda95d4fc712ddd..c1d809f51b74b97a04a13fe32a5b72afeb4d15af`
with model `claude-fable-5`, effort `max`, a schema-constrained verdict, and an
isolated disposable-worktree requirement. A bounded smoke probe succeeded and
its `modelUsage` contained `claude-fable-5` and no Opus model. The 30-minute
review attempt and the playbook's one 45-minute retry both returned no output,
so neither supplied a verdict, matching SHAs, or `guard_confirmed=true`; both
are fail-closed and not accepted as of 2026-07-16T04:15:54Z. Each disposable
reviewer tree was clean and removed after its process exited, and the coder
tree remained clean.

The separate in-repo contract audit accepted current head and the full/native
verification is recorded in `.agents/machines.md`, but those facts do not
replace the owner-required Fable implementation review. R0 therefore remains
unmerged pending a successful fixed-SHA rerun or an explicit owner change to
the review gate. Contested/blocker detail:
`.agents/review/mcp-resilience-r0-review.contested.md`.

---

**MCP RESILIENCE R0 IMPLEMENTATION — ACCEPTED; EARLIER PROCESS BLOCKER
RESOLVED.** Claude Code 2.1.211 (`claude-fable-5`, effort `max`, isolated
disposable worktree) reviewed exact fixed range
`215e10fcfede9cf200b21b3c0cda95d4fc712ddd..c1d809f51b74b97a04a13fe32a5b72afeb4d15af`
after the compression proxy was removed. A fresh direct-endpoint smoke probe
and the review envelope each reported `claude-fable-5` plus Claude Code's Haiku
helper and no Opus model. The review exited zero and returned the exact schema-
constrained verdict `accepted`, literal `guard_confirmed=true`, and both full
SHAs matching dispatch at 2026-07-16T06:15:48Z.

Fable independently proved three representative guards red then green. It
corrupted `HostGeneration` in `sentinel-event.json` and the semantic per-column
Sentinel projection guard failed; remapped a post-write private-host loss to
`backend_lost_before_dispatch` and the fake guardian's write-started
`outcome_unknown` assertion failed; and weakened broker worker-group `SIGKILL`
to `SIGTERM`, causing the `after_release` guardian-death containment case to
miss its deadline. Every mutation was restored byte-exactly and its focused
guard reran green. The reviewer verified empty porcelain and `git diff HEAD`,
removed its disposable worktree, and the orchestrator independently confirmed
the worktree was absent and the coder tree unchanged.

The reviewer reproduced 64 Ajv strictTypes warnings, identified them as
conjunctive branch subschemas whose types are pinned at the root/definition
level, and found no accepted-invalid or rejected-valid case in 17 targeted
probes. It also recorded one LOW, non-blocking test-hygiene advisory at
`server/PtkMcpServer.Tests/Native/ptk_guardian_broker_fixture.c:1437,1500`:
abrupt testhost death before `finally` can leave the TERM-immune paused
disposable fixture tree until its fixture guardian is killed. The case cannot
create a false pass or affect an R0 product contract; a later scoped hygiene
slice may add a guardian stdin-EOF watch.

Reviewed complete R0 contracts, fake protocol/recovery fixtures, and native
feasibility containment; no material issue found. R0 is review-complete and
ready for its local owner-gated merge. This acceptance does not authorize
push, history rewriting, or R1-R7 implementation. Historical failed-attempt
detail and resolution are preserved in
`.agents/review/mcp-resilience-r0-review.contested.md`.

---

**MCP RESILIENCE R1 IMPLEMENTATION — PLATFORM COMPLETE; OPENREVIEW HELD.**
R1 code is fixed at `60eb20f37a75259c0bf246d594632bde128c109b` over exact
base `1f314a29807e7504aa04f7f14899c6bb6483248a`. Exact-head macOS, Windows,
and Linux behavior evidence is complete in `.agents/machines.md`; independent
final per-slice in-repo reviews reported no remaining material finding after
their actionable guard issues were closed.

The `openreview` playbook's bounded headless smoke succeeded with Claude Code
2.1.210, effort `max`, schema-constrained output, and `modelUsage` containing
only `claude-fable-5` and no Opus model. A disposable detached worktree was
then created at the exact head and the unprimed question was dispatched over
the pinned range. Before the reviewer returned an envelope, the owner reported
that Fable capacity was exhausted and directed the openreview to stop and all
openreviews to wait until Fable capacity returns. The process was killed, so it
supplied no verdict and is not accepted review evidence. The disposable tree
was still clean at the exact head, was removed, and the coder tree remained
clean.

The owner permits Opus or Grok for ordinary code reviews during the capacity
window, but they are not substituted for this held openreview. Re-dispatch the
same fixed range with Fable when capacity returns. R2-R7 remain unauthorized;
this record authorizes no merge, push, or history rewriting.
