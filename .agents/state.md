# Agent State

This file is the first place future agents should read for current repo state. Keep it
short and update it when important repo facts change.

## Now

- **Audited-harness Slice 5 is in progress on
  `feat/audited-harness-slice5`.** Sub-slice 5a code head `ed7c782` adds the
  audited cold-background admission barrier. Sub-slice 5b code head `bbcb1b7`
  adds the typed cold-background dispatch/JobManager foundation: immutable
  launch provenance, one-shot proved-no-start fallback, bounded containment,
  owned concurrent output capture, and separate start/root/execution/output
  certainty. Sub-slice 5c code head `c936866` makes typed-job polling and
  access audit provenance-aware: seam-absent `RtkUnknown` output stays
  ANSI-cleaned and bounded without a second `rtk log`, carries explicit unavailable
  recovery, preserves source routing through cleanup failure, and leaves
  legacy placeholder routing neutral. Sub-slice 5d code head `f7d5940`, with
  its platform-faithful test correction at reviewed head `36e0f49`, adds
  PowerShell-faithful cold PATH/PATHEXT resolution, frozen target identity and
  revalidation, target-aware native argument mode, and conservative fidelity
  gates. Claude accepted exact range `43d1307..36e0f49` with
  `guard_confirmed=true`; canonical guard and verification evidence is in
  `.agents/review/index.md`. Production invoke activation/fallback, output
  handles, and path-free recovery remain unwired.
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
  implementation/review branches without per-merge confirmation. Push remains
  separately ask-first.
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
  hold.
- **Release distribution remains approved work.** Slices 0-2 are landed;
  slice 3 is queued and `.github/workflows/release.yml` is still absent.
  The deliberately open hook-default choice blocks slice 4 only.
- **Standing GitHub authority:** the owner granted persistent permission on
  2026-07-10 to comment, close, and triage issues in this repository as
  appropriate without per-action asks. Pushes remain ask-first.

## Next

1. Continue audited-harness Slice 5 by wiring production cold planning and
   proved-no-start fallback into invoke, then add supervisor-owned output
   handles and make recovery path-free under
   `.agents/plans/audited-harness-sessions.md`.
2. Do not infer approval as a push go; push remains separately ask-first.
3. Execute release-distribution slice 3 under its approved plan. Re-present
   the hook-default choice before release-distribution slice 4.
4. When the owner releases the decisions hold, reconcile the rejected
   security mechanism, retired durable/shared staging, and PTK→RTK routing
   direction in `.agents/decisions.md`.
5. Push remains ask-first; no push is authorized by plan/review completion.

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
- Audited-session slices 0-4 are complete locally. Slice 4's exact integrated
  fixed-head Claude acceptance and independently verified local landing are
  recorded in `.agents/review/index.md`; host-specific verification records
  live in `.agents/machines.md`.

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
