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
| mhi-10 | MEDIUM   | Uninstall skips legs whose CLI left PATH; stale cross-harness state      | `[ ]`  |        |
| mhi-11 | MEDIUM   | Pre-existing agy hooks.json survives an install that reports no hook     | `[ ]`  |        |
