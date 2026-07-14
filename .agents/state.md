# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- **Audited-harness Slices 7a-7d and the first Windows wiring prerequisite are
  complete locally at code head `d1cca1b`.**
  The strict bounded v1 worker protocol freezes all nine wire kinds, enforces
  strict UTF-8 NDJSON with duplicate/unknown/version rejection, caps encoded
  frames at 1 MiB and JSON depth at 32, preserves fragmented and coalesced
  input, serializes writes, latches ambiguous writer failure, and clears
  pooled script-bearing buffers. Claude Code 2.1.209 accepted exact range
  `a88d605..f86de26` with `guard_confirmed=true` after six independent
  mutations and the full battery. Code head `e70089f` then adds the
  behavior-preserving tool-to-provider operations seam and the separately
  disposable ordered lifetime seam; Claude accepted exact range
  `2eca287..e70089f` with `guard_confirmed=true` after four mutations and the
  full battery. Code head `56734e3` then adds the platform-neutral worker
  lifecycle core with strict boot/phase/correlation checks, deadline and host
  cancellation races, and exactly-once lifetime cleanup; Claude accepted
  exact range `cfaee5f..56734e3` with `guard_confirmed=true` after seven
  independent mutations and the full battery. Code head `bbc2a0e` then adds
  the deliberately unwired Windows creation-time containment primitive:
  exact `KILL_ON_JOB_CLOSE` Job Object configuration, one five-handle
  `CreateProcessW`/`STARTUPINFOEX` launch with `JOB_LIST` and `HANDLE_LIST`,
  closed Unicode environment, proof-only suspension, job-first rollback, and
  direct runnable/pre-resume/tree-death Windows fixtures. Claude accepted exact
  range `3348167..bbc2a0e` with `guard_confirmed=true` after seven mutations
  and the full battery; the exact tree also passed direct `NETWATCH-01`
  validation. Code head `d1cca1b` closes cancellable-wait ownership using a
  per-wait noninheritable duplicated process handle, with guards covering both
  the owning constructor and active async call path. The preliminary `5ea7d60`
  review was reopened after independent preflight found its active-path guard
  vacuous; only the corrected fixed-SHA acceptance is final. Canonical review
  and Windows evidence is in `.agents/review/index.md` and
  `.agents/machines.md`.
- **CI portability repair is complete at test-only code head `6193ae4`.**
  GitHub Actions run `29316766579` at docs-only descendant `e3b1dfd` failed
  Windows at Slice 8's newly introduced five-second overlap checkpoint and
  failed Ubuntu in two older eight-way rendezvous fixtures; its server and
  workflow trees are identical to green `d30bbf3`. The Windows checkpoint now
  shares the original fifteen-second contender budget; the singleton mutation
  failed and scoped ownership passed 10/10 on `NETWATCH-01`. The Ubuntu pair
  is one blocking-ThreadPool test-harness flaw; Slice 9 uses dedicated
  `LongRunning` contenders in both fixtures while preserving their eight-way
  collision and cleanup assertions. Both collision mutations failed for the
  intended losing publish, the paired tests passed 10/10, the complete local
  and direct Windows batteries passed, and independent review found no code
  issue. Hosted run `29318333860` then passed Ubuntu and macOS at `24c7958`
  but failed one Windows result in a second concurrent recovery fixture that
  still shared a singleton request context. Production has always scoped that
  holder. Slice 10 now gives every synthetic request its own scope and
  deterministically overlaps two handlers using the existing completion
  budget. Singleton ownership failed the guard on macOS and Windows; restored
  scopes passed 10/10, both complete batteries passed, and independent review
  found no material issue. GitHub Actions run `29331077331` passed Ubuntu,
  macOS, and Windows at docs descendant `ccee469`, including Pester, all 1,207
  server tests, the handshake, and cleanup. No production files changed.
  Canonical evidence is in
  `.agents/plans/ci-portability-repair.md`, `.agents/review/index.md`, and
  `.agents/machines.md`; RTK distribution remains a separate decision.
- **Audited-harness Slice 6 is complete locally.** Code
  head `7999328` moves invoke/job/state/reset behavior and session-lifetime
  caches behind one owning `SessionRuntime`, leaves audit and output
  capabilities per operation, keeps MCP adapters thin and schema-compatible,
  and preserves jobs-before-runspace audited shutdown. Claude Code 2.1.208
  accepted exact range `aca20a6..7999328` with `guard_confirmed=true` in a
  clean detached worktree after independent cache-isolation, ownership,
  adapter, and reset-lifetime mutation proofs plus the full battery. Completion
  records are committed at `67b900d`. The accepted feature history was
  fast-forwarded to local `master`, content arrival was verified independently,
  and the feature branch was removed. Canonical evidence is in
  `.agents/review/index.md`.
- **Audited-harness Slice 5 is complete locally.** Final code head
  `fc61be6` completes audited cold-background planning, typed exactly-once
  dispatch/fallback, provenance-
  aware polling, and path-free output recovery. Eligible direct-text jobs
  reserve output capacity before start, seal one immutable bounded supervisor
  artifact, and publish its opaque handle before terminal notification;
  seam-absent RTK jobs remain explicitly unrecoverable, and model-facing job
  surfaces expose no internal path. Claude Code 2.1.208 and Grok 0.2.93 each
  accepted exact range `ee21f16..fc61be6` with `guard_confirmed=true` in clean
  detached worktrees after independent guard proofs and the full verification
  battery. Completion records are committed at `bbb1742`. The accepted
  feature history was fast-forwarded through handoff tip `de8dc53` to local
  `master`, content arrival was verified independently, and the feature branch
  was removed. Canonical evidence is in `.agents/review/index.md`.
- **Audited-harness Slice 4 is complete locally.** Final product
  head `76d4f0c` integrates the supervisor-owned output store and audited
  `ptk_output`, bounded two-stage same-invocation capture/recovery, anonymous
  retained artifacts, truthful recovery hints, behaviorally inert legacy
  `raw`, explicit `route=pwsh` consent, and the narrow unshaped state probe.
  Claude accepted exact integrated range `9c89abf..76d4f0c` with
  `guard_confirmed=true` after eight independent cross-slice red-to-green
  proofs and the full local battery. The accepted feature history was
  fast-forwarded to local `master`, content arrival was verified independently,
  and the feature branch was removed. Canonical review evidence is in
  `.agents/review/index.md`; host-specific evidence is in `.agents/machines.md`.
- **Audited-harness Slice 3 is complete locally.** Final
  integrated code head `b78d9c6` completes structured foreground routing,
  audited RTK/Bash dispatch, bounded preference-independent RTK capture,
  exact-original pre-start fallback, and post-success mixed-domain guidance.
  Claude accepted the exact fixed head with `guard_confirmed=true`; all six
  admitted material findings are closed. The accepted feature history was
  fast-forwarded to local `master`, content arrival was verified independently,
  and the feature branch was removed. Canonical review and platform evidence
  is in `.agents/review/index.md` and `.agents/machines.md`.
- **Audited-harness Slice 2 is complete locally.** The final
  integrated code head `3d3739a` completes job/control audit, local-only and
  anchored OTLP export, evidence administration and retention, permanent
  operator disposition, and the strict `ptk.audit/2` extension. Claude
  accepted the exact fixed head with `guard_confirmed=true`; all four material
  integrated-review findings are closed, and the accepted feature history was
  fast-forwarded to local `master`. Canonical review and platform evidence are
  in `.agents/review/index.md` and `.agents/machines.md`.
- **Local branch management is delegated for the remaining audited-harness
  implementation** (owner, 2026-07-11): create, switch, merge, and delete local
  implementation/review branches without per-merge confirmation.
- **Audited-harness slices 0-4 are complete locally.** Slice 1 commit
  `460c106` adds the mandatory current-server audit foundation, exact-script
  evidence, capacity reservation, protected local storage, and fail-closed
  pre-effect guards. Claude accepted the fixed-SHA implementation after an
  independent red→green guard proof; canonical review evidence is in
  `.agents/review/index.md` and platform evidence is in `.agents/machines.md`.
  `.agents/plans/audited-harness-sessions.md` combines
  mandatory PTK-owned/SIEM-exportable audit, private harness-scoped
  process-per-session workers, internal PTK→RTK routing, same-invocation
  output recovery, and no-retry mixed-domain handling. Its frozen slice-0
  results record the absent RTK capture seam, local-only/anchored OTLP
  contracts, exact profile/protocol bounds, carried routing guards, and live
  passing Windows/Unix parent-death probes. Both plan reviewers accepted the
  same approved pre-implementation content. Canonical pre-implementation
  review evidence and fixed-head guards live in `.agents/review/index.md`.
- **Prior security/routing shapes remain evidence, not implementation
  authority.** The declarative policy gate and secret redaction are rejected;
  `.agents/plans/security-layer.md` is prior-art context. The closed review
  findings in `.agents/plans/rtk-rewrite-routing.md` remain regression
  evidence, while its broad rewrite implementation is not approved.
- **`.agents/decisions.md` is UNDER HOLD** (owner, 2026-07-10: do not
  update it until the discussion is complete). The security reframe and RTK
  routing direction still need durable entries after the owner releases the
  hold. On 2026-07-14 the owner explicitly authorized only appending the new
  mini-SIEM receiver question to the end of its open queue; that scoped addition
  does not release the broader hold.
- **Release distribution remains approved work.** Slices 0-2 are landed;
  slice 3 is queued and `.github/workflows/release.yml` is still absent.
  The deliberately open hook-default choice blocks slice 4 only.
- **Standing GitHub authority:** the owner granted persistent permission on
  2026-07-10 to comment, close, and triage issues in this repository as
  appropriate without per-action asks.

## Next

1. Continue Slice 7 on its feature branch. The owner approved a separate
   Windows-only `--worker` lifecycle-entry sub-slice on 2026-07-14, with the
   existing default MCP tools left in-process until the later atomic cutover.
   Managed exit codes are now frozen: `0` for cleaned-up shutdown/EOF/cancel,
   `64` for malformed worker invocation, and `80..84` for bootstrap,
   initialize, protocol, transport, and runtime/cleanup failure; supervisor
   lifecycle state, not zero alone, decides whether a stop was expected. The
   next owner decision is pending: capture and immediately remove both private
   handle variables; accept only canonical unsigned-decimal, distinct valid
   pipe handles; duplicate them as noninheritable owned handles; close the
   inherited originals; construct no runtime until both streams succeed; and
   close everything on partial failure. If approved, record that contract,
   present the bounded abnormal-diagnostic decision, then implement without
   operation DTOs or default routing. Before real wiring coverage,
   isolate Windows containment tests from parallel process-spawning tests. The
   final default-session cutover must keep supervisor-owned audit/output,
   worker-owned runtime/process creation, and no in-process fallback. Keep the
   Unix broker behind its separately recorded contract blockers. Each sub-
   slice still requires fixed-SHA acceptance before the next begins.
2. Execute release-distribution slice 3 under its approved plan. Re-present
   the hook-default choice before release-distribution slice 4.
3. When the owner releases the decisions hold, reconcile the rejected
   security mechanism, retired durable/shared staging, and PTK→RTK routing
   direction in `.agents/decisions.md`.

## Open / Parked

- Warm-backend slice 7 is unblocked open work, currently unscheduled, and
  remains owner-run Windows validation: AD native import/warm reuse; Exchange
  implicit remoting with first-vs-repeated `Get-Queue` latency; EXO/Graph
  unattended certificate auth. Its plan status still needs correction; see
  `## Blockers`.
- Durable checkout and shared runspaces are removed from the candidate build
  scope by the owner's 2026-07-11 direction. Their older open-decision entry
  remains stale while `.agents/decisions.md` is under hold; the idea plan is
  retained as history/evidence, not current implementation direction.
- GitHub issue #3 remains open (verified 2026-07-11): item 1 landed; items
  2-4 are an unplanned follow-up candidate, while its permission-bypass
  concern belongs to the open security track.

## Blockers

- **Managed worker-entry/dispatch contract is incomplete.** The owner approved
  the Windows-only lifecycle-entry staging boundary on 2026-07-14, but exact
  bootstrap handle parsing/ownership/cleanup and abnormal diagnostic
  termination remain open before that code. The worker-exit-to-process-exit
  mapping is frozen in `.agents/plans/audited-harness-sessions.md`; the concrete
  bootstrap recommendation in `## Next` is awaiting owner approval.
  Operation DTO/dispatch/cancel/response and supervisor audit/output transfer
  remain a later contract required before default-session cutover. At
  `d1cca1b`, `Program.cs` still has no `--worker` branch and constructs the
  supervisor-side `SessionRuntime`; `WorkerServer` accepts only initialize/
  shutdown after ready.
- **Windows wiring requires a hard supervisor/worker role cutover.**
  `Program.cs`, `BashProcessRunner`, `RtkProcessRunner`, and `JobManager` still
  permit supervisor-side runtime or generic process creation. Those paths
  cannot race the Windows launcher's temporarily inheritable selected handles.
  The wired supervisor must not retain an in-process user-runtime or generic-
  spawn fallback.
- **Live wired Windows tests need process-spawn isolation.**
  `WindowsContainmentIntegrationTests` can currently overlap other xUnit
  fixtures that call `Process.Start`. Use a dedicated nonparallel process/
  project or an assembly-level parallelism rule before exercising the wired
  launcher.
- **Unix containment contract is incomplete; Windows work is not blocked:**
  the canonical audited-harness plan fixes `START_FAILED` as a stage byte plus
  errno but does not enumerate stage values, and it requires bounded broker
  TERM/KILL/reap without fixing those intervals or their relationship to
  `timeoutContainmentGrace`. It also does not pin native macOS/Linux build
  baselines, so moving runners could emit authenticated but incompatible
  binaries. Freeze these values before the Unix broker/build sub-slices; do
  not invent them during implementation. The independent Windows Job Object
  sub-slice can proceed without them.
- **Decision-log conflict, correction blocked by the owner hold:**
  `.agents/decisions.md` still describes the policy-file gate as the open
  response after its criterion fires, while the later explicit owner call in
  `.agents/plans/security-layer.md` rejects that response. Its shared-host
  entry stages durable GUID sessions followed by sharing, while the owner's
  later direction removes both from the candidate build. Do not implement
  either stale direction; preserve the decision-log conflict until the hold
  is released.
- **Plan-record drift, reported but not edited in this narrow state pass:**
  the warm-runspace plan still says slice 7 is paused behind the already
  decided GO; the release plan retains stale CI/push/policy references; and
  the shared-runspace idea still assumes the rejected policy gate. Explicit
  owner calls, uncontested decisions, and live repo evidence named above
  control.

## Verification

- Automated verification entry point: `.agents/repo-guidance.md`
  (Verification). Review-loop evidence lives in `.agents/review/index.md`;
  do not duplicate volatile counts here.
- Audited-session slices 0-6 are complete locally. Slice 4's exact integrated
  fixed-head Claude acceptance and independently verified local landing are
  recorded in `.agents/review/index.md`; host-specific verification records
  live in `.agents/machines.md`. Slice 5's final path-free output-recovery
  acceptance by both Claude and Grok and Slice 6's `SessionRuntime` acceptance
  by Claude are recorded in the same review index.

## Active Sources

- `AGENTS.md`
- `.agents/repo-guidance.md`
- `.agents/decisions.md`
- `.agents/plans/security-layer.md`
- `.agents/plans/rtk-rewrite-routing.md`
- `.agents/plans/audited-harness-sessions.md`
- `.agents/plans/release-distribution.md`
- `.agents/plans/warm-runspace-mcp-server.md`
- `.agents/plans/shared-persistent-runspace.md`
- `.agents/review/index.md`
- `.agents/machines.md`

## Unrecorded Repo Memory

- None known.

History: rotated entries live verbatim in `docs/history/state-archive.md`.
