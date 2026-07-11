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
| ahs-37 | MEDIUM  | Canonical state claims Slice 0 reviews closed without durable evidence | `[x]` | `fix/ahs-37-record-slice0-review` |

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
