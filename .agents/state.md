# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- **Reconciliation of published `origin/master` into
  `feature/mcp-resilience-r1` is proposed and awaits owner approval.** A fresh
  fetch found local `master` exactly synchronized with `origin/master` at
  `e4d8d1e`, while the active feature is at `96d4af2` and published only
  through `68c5b34`. At those exact tips, master has 88 history commits absent
  from the feature and the feature has 173 absent from master. A read-only
  merge preview reports ten conflicts spanning durable records, installer and
  frozen contracts, guardian audit/output ownership, and server job
  containment. The self-contained preservation and cross-platform validation
  rules are in `.agents/plans/master-feature-reconciliation.md`. Do not merge,
  resolve product code, push, or fold the three separately unlanded dormant
  branch patches into this work without the approvals named there.
- **Dependency hardening is locally complete at code head `d1d24e8` on
  `feature/mcp-resilience-r1`; the first hosted run proved the action runtime
  but exposed three CI portability findings whose corrective amendment awaits
  owner approval.** The
  frozen inventory, PowerShell security
  chain, Hosting, MCP, Roslyn, .NET test-platform, coverage-collector, SQLite,
  Pester, and GitHub Actions runtime slices are committed. Test SDK 17.14.1
  could build xUnit v3 but not run its assemblies, so xUnit 3.2.2, the 3.1.5
  VSTest adapter, and Test SDK 18.8.1 correctly land together. Exact listed
  identities match the
  pre-migration snapshot in all five projects; explicit and solution macOS
  runs pass architecture 73, guardian 436, server 1,868, SIEM 91, and
  conformance 6.
  The solution-level cold-background marker race now waits for a valid PID
  within its unchanged five-second budget. All three package graphs have zero
  deprecated and zero vulnerable packages, and no test-stack package is
  outdated. The 1,302 new `xUnit1051` analyzer locations remain visible and
  unsuppressed because blindly replacing deliberate test cancellation tokens
  is outside this migration. Coverlet ordinary runs pass server 1,868 and SIEM
  91; scoped collection produces one non-empty external Cobertura artifact per
  project and leaves no repository artifact. SQLitePCLRaw 3.0.4 resolves one
  uniform bundle/config/core/provider graph, has no outdated, deprecated, or
  vulnerable package, and passes SIEM 91/91 on macOS, x64 Linux, and Windows.
  A fresh external publish contains one bundle and one provider identity; all
  disposable validation artifacts and SYSTEM-profile development-certificate
  residue were removed. CI now provisions and imports exact Pester 6.0.1 via
  PSResourceGet with a Gallery-compatible fallback; exact-version runs pass
  141 with two expected skips on macOS/Linux and 142 with one expected skip on
  Windows. Both CI jobs now pin setup-dotnet v6 while retaining checkout v7
  and the rolling .NET 10 SDK channel. At exact full code head
  `d1d24e8738fe145d473d0ed3c1de98c2acf96cf3`, final cold acceptance on macOS,
  x64 Linux `magneto`, and x64 Windows `NETWATCH-01` found all nine package
  audits empty and no `NU1901`-`NU1904` warning. The complete architecture 73,
  Guardian 436, server 1,868, SIEM 91, both conformance 6-test modes, exact
  Pester, and handshake identities are covered on every platform; Windows
  used authoritative TRX matching across its established ordinary-account and
  SYSTEM identity split. All disposable final-validation residue was removed.
  GitHub Actions run `29967333249` at exact documentation descendant `68c5b34`
  successfully loaded `setup-dotnet@v6` in all six jobs, passed all three
  Pester steps and all three SIEM jobs, then failed each product job in server
  tests: fortified Linux compilation rejects three discarded native `read`
  results, hosted macOS temp paths traverse a symlink rejected by secure
  storage, and Windows VSTest reproduces its known long-path failures while
  also exposing two project-host state-observation failures. The proposed
  plan amendment preserves product security behavior, consumes the native
  read result, supplies a physical macOS test temp root, and runs the three
  xUnit v3 product assemblies in-process and sequentially. No corrective code
  change or further push is authorized; merge, release, and installed-payload
  changes remain unauthorized.
- **mini-SIEM S1-S3 are complete and incorporated on local `master`; the S3 durable
  store head is `eb51f2e` and its producer-conformance compatibility head is
  `9f53831`.** S1 supplies the solution skeleton and strict startup config; S2
  supplies the independently compiled canonical proto, bounded exact
  one-record validator, real Kestrel mTLS, and frozen response table. S3 replaces
  the fail-closed placeholder with a startup-migrated SQLite store: one
  serialized nondeferred transaction re-reads and conditionally advances the
  producer chain, stores exact raw request evidence, and atomically appends the
  versioned custody ledger under asserted WAL/FULL writer policy. Byte-identical
  replay is idempotent even after head advance; duplicate mismatch, chain
  failure, and strict-validator failure commit quarantine evidence before a
  permanent response; any commit/quarantine failure remains retryable and can
  never false-ack. The SQLite package's vulnerable native minimum is overridden
  to its patched bundle, and the dependency audit is clean. The isolated
  producer conformance project remains source-compatible and its ordinary fake
  receiver path remains unchanged. Producer-owned exact v1/v2 fixtures exist at
  `1f6d485`; the serialized v3 OTLP request fixture remains absent and is never
  invented from R0's JSONL vector. Local evidence and guard proofs are recorded
  in `.agents/machines.md`. Combined local verification of merge `374f164`
  passed on macOS, exact integration tip `1ad195e` passed direct Windows
  checkout validation, and exact snapshot `a473ca3` passed the direct Linux
  behavior battery after manual generation around an ARM64 MSBuild-only
  `protoc` crash; the exact commands, contexts, counts, and clean-build caveat
  are recorded there. Hosted run `29520427103` at `72c6103` passed all three
  SIEM jobs and the complete Ubuntu/macOS product batteries; its Windows
  server failure is addressed by the CI portability follow-up below.
- **Audited-harness Slices 7a-7f and the Windows wait-ownership prerequisite
  are complete and landed on local `master`; Slice 7f code head is
  `a9e757e`.**
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
  vacuous; only the corrected fixed-SHA acceptance is final. Code head
  `12617cc` then adds the Windows-only managed `--worker` lifecycle entry,
  exact bootstrap-variable removal and noninheritable pipe ownership, stable
  managed exit mapping, and bounded allow-listed abnormal diagnostics while
  leaving default MCP operations in-process. Independent preflight reopened
  preliminary head `e2cbfb5` for two guard vacuities; test-only commits
  `e9421cc` and `12617cc` close the concrete environment-removal and global
  diagnostic-destination gaps. Claude accepted exact range
  `eec7ed1..12617cc` with `guard_confirmed=true` after eleven independent
  mutations and the full battery; the exact tree also passed direct
  `NETWATCH-01` lifecycle and containment validation. Code head `a9e757e` then
  adds strict private request/cancel parsing, response encoding, and a bounded
  standalone scheduler with targeted cancellation, exactly-one terminal
  ownership, and fatal peer cleanup while remaining deliberately unwired from
  production.
  Claude accepted exact range `3580e67..a9e757e` with
  `guard_confirmed=true` after independent mutation proof and the full battery.
  Completion records are committed at `83ca3b1`; the accepted feature history
  was fast-forwarded to local `master`, content arrival was verified by direct
  branch diff, and the feature branch was removed. Canonical review and Windows
  evidence is in `.agents/review/index.md` and `.agents/machines.md`.
- **Audited-harness Slice 7g is complete and landed on local `master` at code
  head `eef38cb`.** It adds only strict transport-neutral value codecs for
  foreground invoke, job controls, and state, with strict logical UTF-8
  script/result limits. It does not bind invoke to ordinary request transport
  or add runtime execution, prepare/commit, background start, audit/output
  transfer, job-ID allocation,
  reset, proxy wiring, or MCP behavior. Claude Code 2.1.210 accepted exact
  range `a83e2e6..eef38cb` with `guard_confirmed=true` after ten independent
  mutations and the full battery. Completion records are committed at
  `8428f17`; the accepted feature history was fast-forwarded to local `master`,
  content arrival was verified by direct branch diff, and the feature branch
  was removed. Canonical evidence is in the audited-harness plan and
  `.agents/review/index.md`.
- **Audited-harness Slice 7h is complete and landed on local `master` at code
  head `8f5c57c`.** It adds only strict unwired prepare,
  prepared-correlation, commit, and abort values for foreground invoke. It
  freezes canonical UUIDv4 plan ID, exact strict-UTF-8 script digest, worker
  generation, and original absolute deadline correlation without adding
  reservation behavior, execution, the final prepared descriptor, audit/output
  transfer, background IDs, server wiring, or MCP behavior. Claude Code
  2.1.210 accepted exact range
  `1179ed0..8f5c57c` with `guard_confirmed=true` after eleven independent
  mutations and the full battery. Completion records are committed at
  `c07a958`; the accepted feature history was fast-forwarded to local `master`,
  content arrival was verified by direct branch diff, and the feature branch
  was removed. Canonical evidence is in the audited-harness plan and
  `.agents/review/index.md`.
- **The owner-approved two-layer MCP resilience sequence is implemented and
  directly validated through R5 on `feature/mcp-resilience-r1`; the current
  code head is `225b5fc`, with exact platform evidence at Windows `195e7e6`,
  macOS `225b5fc`, and Linux code/test content `225b5fc`. R0-R5 are complete.
  R5 owns real Windows and Unix private-host launch,
  containment, monitoring, exact-generation restart, lifecycle audit,
  delivery truth, declared-state restoration, job/output tombstones, and
  recovery-aware state. Real apphost tests cover startup/replacement crashes,
  request/effect/response barriers, native descendant and cold-background
  cleanup, guardian-liveness EOF, idle persistence, lost jobs, and ambiguous
  reset requiring explicit repair. The complete repository battery, Pester,
  and stdio handshake pass at the macOS and Linux heads; exact machine
  evidence, guard proofs, test counts, host-load caveat, and current package
  advisories are in `.agents/machines.md`. The separately recorded ARM64
  MSBuild-only `protoc` crash remains an architecture-specific clean-build
  blocker, not an R5 Linux behavior blocker. The
  owner authorized R2-R7 on 2026-07-17 and directed implementation to continue
  without waiting for held Fable reviews.** The target keeps one
  public stdio guardian alive while it
  restarts an exact-version private host, and makes a healthy host replace an
  unexpectedly lost session worker. It never replays ambiguous work, changes
  generation on every replacement, recreates only frozen declared bootstrap
  state, and keeps guardian-local health/output surfaces usable. The canonical
  draft is `.agents/plans/mcp-resilience.md`; it narrowly supersedes the older
  explicit-restart target without weakening audit, containment, or session
  isolation. The owner additionally accepted the guardian crash boundary and
  fail-fast model-retry contract: no server queue or saved-state authoring tool
  in this stage; only proved-no-start errors direct the model to poll state and
  submit a new request, while `outcome_unknown` is never retried. The owner
  requested a fresh Claude Fable 5 maximum-effort review of that update.
  Claude Code 2.1.210 accepted the complete exact range
  `5ae154c..b4a2c0c` with `guard_confirmed=true` and no comments; its result
  metadata reported `claude-fable-5` plus CLI helper usage and no Opus model.
  Canonical review evidence is in `.agents/review/index.md`. For packaging,
  the owner confirmed nothing
  has shipped and only this development environment is in use, then delegated
  the choice: keep the current registration usable through R6, perform one R7
  cutover to the matched guardian package, and preserve no direct-server
  migration or compatibility layer. An eligible alias also recovers
  automatically to its fresh declared baseline after an execution-timeout
  terminal and confirmed old-tree death; the timed-out call is never replayed.
  Retryable recovery refusals carry phase, attempt, poll delay, and an exact
  readiness gate: delay expiry permits only `ptk_state`, and a fresh operation
  waits for readiness plus pre-write dispatch revalidation. After private
  write begins, the existing `outcome_unknown`/no-replay boundary still wins.
  Exact R0 verification and direct macOS/Windows evidence are recorded in
  `.agents/machines.md`. The in-repo independent audit found no R0 blocker.
  After the compression proxy was removed, Claude Code 2.1.211 routed directly
  to `claude-fable-5` at maximum effort and accepted exact range
  `215e10f..c1d809f` with matching SHAs and `guard_confirmed=true`. Its model
  metadata reported Fable plus the Haiku helper and no Opus model. It proved
  the Sentinel projection, post-write `outcome_unknown`, and Unix hard-
  containment guards red-to-green, restored its clean detached tree, and
  reported only one non-blocking fixture-hygiene advisory. Canonical review
  evidence is in `.agents/review/index.md`; the earlier fail-closed attempts
  and their resolution remain in
  `.agents/review/mcp-resilience-r0-review.contested.md`.
  R1 extracts typed shared contracts and guardian-owned audit, output, frozen
  catalog, recovery-manifest, and public-ID ownership without adding guardian
  lifecycle behavior or changing the public MCP schema. Exact platform
  evidence is in `.agents/machines.md`. The held Fable attempt returned no
  verdict; its smoke metadata proved Fable at maximum effort with no Opus
  model, and its clean disposable tree was removed. Opus or Grok may be used
  for ordinary code reviews while openreviews wait for Fable capacity.
- **CI portability Slices 11-16 are complete at final test-only head
  `5642376`; hosted run `29541559607` passed all six jobs at exact pushed
  descendant `7bc08aa`.** Run
  `29536074900` proved Slices 11-12 closed their R0 checkout and marker races,
  then exposed four older scheduler, reprime-setup, Unix deadline, and Windows
  retry-notification assumptions. Each repair changes only its test; the
  review closure additionally freezes the native deadline helper without
  restoring a scheduler-latency ceiling. Focused red/green proofs, the complete
  macOS battery, direct Linux/Windows batteries at the runtime-equivalent
  preceding head, exact final-head platform guards, and independent review all
  passed, followed by green Ubuntu/macOS/Windows product and SIEM jobs. Slices
  1-10 remain complete with green hosted evidence. Canonical details are in
  `.agents/plans/ci-portability-repair.md` and
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
  hold. The owner explicitly authorized two narrow mini-SIEM exceptions: the
  open receiver question on 2026-07-14 and its S0 Option 1 implementation
  decision on 2026-07-15. Neither releases the broader hold.
- **Release distribution remains approved work.** Slices 0-2 are landed;
  slice 3 is blocked behind resilience R7 and `.github/workflows/release.yml`
  is still absent. The old 2026-07-25 calendar is superseded with no replacement
  date approved. The deliberately open hook-default choice also blocks slice 4.
- **Standing GitHub authority:** the owner granted persistent permission on
  2026-07-10 to comment, close, and triage issues in this repository as
  appropriate without per-action asks.

## Next

1. Finish `.agents/plans/dependency-hardening.md` with the frozen package
   audits and direct macOS/Linux/Windows acceptance. Remove
   `PTK_SIEM_CONFORMANCE_MODE` with `Remove-Item Env:` before ordinary tests.
   Advisories stay visible and non-blocking; do not add warning-as-error,
   suppression, or runtime gating. After dependency hardening, begin approved
   R6 without folding the separate ARM64 MSBuild `protoc` investigation into
   resilience work. Do not merge, rewrite history, push, or publish a release
   without separate authorization.
2. Implement the owner-approved mini-SIEM S3H amendment in
   `.agents/plans/mini-siem-implementation.md`: startup filesystem hardening
   under `siem/` only. Do not begin S4-S6 or modify PTK runtime code.
3. Hold mini-SIEM at the S4 fixture gate recorded under `## Open / Parked`.
   When producer-owned v3 request bytes land, execute S4 from the complete
   producer corpus; do not substitute receiver-authored fixtures.
4. Release-distribution slice 3 is ordered after resilience R7 and consumes
   only its matched guardian layout; there is no legacy migration path. Do not
   execute it before R7 lands. Re-present the hook-default choice before release
   slice 4.
5. When the owner releases the decisions hold, reconcile the rejected
   security mechanism, retired durable/shared staging, and PTK→RTK routing
   direction in `.agents/decisions.md`.

## Open / Parked

- Mini-SIEM S4's fixture gate remains intentionally closed: producer-owned
  exact v1/v2/v3 OTLP byte corpora must all exist before S4 begins. The
  producer-side serializer for current v1/v2 records is landed at `1f6d485`,
  but R0 supplies only JSONL v3 contract vectors, not a producer-owned
  serialized v3 OTLP request; do not synthesize one in the receiver or treat
  its hand-authored S2 structural test as a golden producer fixture.
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
- A pre-existing `AuditAnchoredRuntimeTests` assertion can observe the short
  interval between the final evidence-file publication and removal of its
  `.anchoring.*.script` temporary. It passed an isolated 10/10 and a clean
  complete rerun; repair the test synchronization in a separate scoped slice,
  not by weakening R0.
- Fable's accepted R0 review noted one non-blocking test-fixture hygiene risk:
  if the testhost dies before its `finally`, the Unix guardian broker fixture's
  TERM-immune paused process group can remain until its fixture guardian is
  killed. This cannot make the guard falsely pass or affect an R0 product
  contract; consider an stdin-EOF guardian watch in a later scoped test-hygiene
  slice.
## Blockers

- **Direct ARM64 Linux clean-build validation is blocked by a host-specific
  `Grpc.Tools` launch failure.** On the Ubuntu 26.04 ARM64 VM, the bundled
  `Grpc.Tools` 2.82.0 `protoc` succeeds when invoked directly with the exact
  generated command but exits 139 only when MSBuild launches it. Manually
  generating the identical intermediate files allowed every Linux behavior
  suite to pass. This does not invalidate that behavior evidence, but a clean
  ARM64 build must not be claimed until the launch failure is resolved or
  independently disproved; see `.agents/machines.md`.

- **Mini-SIEM startup filesystem enforcement is approved but not yet
  implemented.** The S3H amendment in
  `.agents/plans/mini-siem-implementation.md`, approved by the owner on
  2026-07-16, corrects the former S1/S3
  scheduling inconsistency for current config, TLS, database, and sidecar
  paths while leaving the not-yet-existent custody-checkpoint path with its
  later owning slice. The current receiver still lacks this enforcement. Do
  not claim receiver-host storage protection or full product acceptance until
  S3H's cross-platform negative matrix lands and the later checkpoint path is
  protected before first use.

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
  decided GO, and the shared-runspace idea still assumes the rejected policy
  gate. Explicit owner calls, uncontested decisions, and live repo evidence
  named above control.

## Verification

- Automated verification entry point: `.agents/repo-guidance.md`
  (Verification). Review-loop evidence lives in `.agents/review/index.md`;
  do not duplicate volatile counts here.
- Audited-session Slices 0-6, Slices 7a-7h, and the Windows wait-ownership
  prerequisite are complete locally. Canonical fixed-head acceptance evidence
  lives in `.agents/review/index.md`; host-specific verification records live
  in `.agents/machines.md`.

## Active Sources

- `AGENTS.md`
- `.agents/repo-guidance.md`
- `.agents/decisions.md`
- `.agents/plans/security-layer.md`
- `.agents/plans/rtk-rewrite-routing.md`
- `.agents/plans/audited-harness-sessions.md`
- `.agents/plans/mcp-resilience.md`
- `.agents/plans/release-distribution.md`
- `.agents/plans/warm-runspace-mcp-server.md`
- `.agents/plans/shared-persistent-runspace.md`
- `.agents/plans/mini-siem-discovery.md`
- `.agents/plans/mini-siem-implementation.md`
- `.agents/review/index.md`
- `.agents/machines.md`

## Unrecorded Repo Memory

- None known.

History: rotated entries live verbatim in `docs/history/state-archive.md`.
