# Plan: CI portability repair after audited-harness Slice 6

**Status:** REOPENED 2026-07-16 for owner-approved Slices 13-16 after GitHub
Actions run `29536074900` exposed four independent pre-existing test-harness
races at exact head `af8189c`. Slices 11-12 remain implemented and directly
verified at repair head `f658f21`; the follow-up run proved that their six R0
checkout failures and nested-Job marker failure are closed. Slices 1-10 remain
completed at test-only code head `6193ae4`, with green hosted evidence in run
`29331077331` at docs descendant `ccee469`. The owner approved exactly four
separate test-only commits, the complete battery, and a hosted rerun. This work
does not change production runtime behavior, install RTK into ordinary unit-
test jobs, or decide whether a future PTK release bundles a pinned RTK binary.

## Evidence and problem

GitHub Actions run `29310719880` tested `69bd0d5` on Ubuntu, macOS, and
Windows. Checkout, SDK setup, and Pester passed on every runner. The .NET
server suite failed, so the handshake step was skipped:

- Ubuntu and macOS each failed only the two data rows of
  `Forced_rtk_fallback_metadata_is_raw_invariant`. The test expected
  `RtkIneligibleShape`, but a clean runner with no RTK binary correctly
  planned `RtkExecutableUnavailable`. The same failure existed at the
  pre-Slice-6 head `aca20a6`; locally, forcing `PTK_RTK_PATH` to a missing
  file reproduces it.
- Windows also exposed five independent test-harness assumptions: an
  unassignable `BuiltinUsersSid` used as an alternate owner, a real module
  enumeration constrained to a test-only 60-second host budget, a
  20,000-line `cmd.exe` timing workload constrained to 15 seconds, a
  supposedly relative path that becomes absolute across Windows volumes,
  and a live `Get-Command` probe that does not select one result before
  serializing it.

The production code is not changed merely to satisfy these failures. Each
test must establish its own preconditions and retain the behavioral assertion
that originally guarded the accepted audited-harness slice.

## Master-landing follow-up evidence

Master run `29314404462` tested `00e74d2`: Ubuntu and macOS passed, while
Windows failed two tests. Exact tree comparison shows `3c61886..00e74d2`
changes only `.agents/` documentation; the entire `server/` tree and workflow
are identical to green run `29313220388`.

- `Private_output_stop_is_joined_before_disposal_and_guard_release` failed in
  its setup-only warm invocation before installing any stop/join hooks. The
  test gives cold runspace readiness, execution, and recovery-renderer startup
  one second. The same failure is already recorded in review history, followed
  by repeated isolated and full-suite passes.
- `Concurrent_startup_repair_opens_and_publishes_one_runtime` shares one
  singleton `AuditCallContextAccessor` across eight synthetic concurrent
  requests even though production registers that mutable holder per request.
  Overlap makes one request observe another's current context and return the
  exact `audit_boundary_invalid` seen in this run and an earlier Windows run.
  The failure reproduced immediately in the clean `NETWATCH-01` checkout at
  exact master head `00e74d2`.

## Slices

Each numbered slice is one finding and one commit.

1. **Hermetic forced-RTK fallback metadata.** Provision the existing native
   cross-platform RTK stub inside the theory, set `PTK_RTK_PATH` for the
   operation, and restore the environment and fixture in `finally`. Preserve
   both `raw` rows and every existing plan assertion. Guard proof: with the
   fixture change temporarily absent and RTK forced missing, both rows fail;
   restored, both pass under the same missing ambient RTK condition.
2. **Capability-faithful Windows owner setup.** Always verify that the audit
   factory leaves the root, spool, host identity, and segment owned by the
   current user with one protected explicit full-control ACE. For the
   alternate-owner precondition, use only a distinct owner SID exposed by the
   current token as assignable; do not assume `BuiltinUsersSid` is eligible.
   Keep the always-valid ACL assertion even when the token offers no distinct
   alternate owner.
3. **Production-faithful module-enumeration budget.** Run the real
   `listAvailable` integration check with the production default host budget
   instead of the test-only 60-second budget. Do not weaken the shipped-module
   assertion or alter the runtime-local cache.
4. **Bounded output-buffer reuse workload.** Retain the blocked first write,
   delayed foreign write, buffer overwrite opportunity, and canary
   assertions, but replace the 20,000-line shell loop with one delayed foreign
   canary. The test must exercise the ownership boundary without making
   success depend on hosted-runner console throughput.
5. **Actually relative cold target fixture.** Supply an explicit relative
   source string to `TryCapture`; do not derive one between paths that can live
   on different Windows volumes. Preserve the subsequent PATH re-resolution
   and content-identity assertions.
6. **Singular live PowerShell resolution probe.** Select the first live
   `Get-Command` result before serializing command type and source, and compare
   Windows source paths with Windows path-identity casing semantics. Preserve
   all three candidate-order comparisons against the cold resolver.
7. **Separate setup warm-up from the tested timeout.** Give only the setup
   warm invocation an explicit ten-second call budget. Keep the host default
   and the subject invocation at one second so the test still drives the
   timeout, blocked stop, joined cleanup, and guard-release path it names.
   Prove the old budget fails under a deterministic delayed private-output
   opening, the new setup reaches the subject, and removing the production
   stop join still makes the stabilized test fail.
8. **Use production-faithful request scopes in the startup race.** Register
   `AuditCallContextAccessor` as scoped, create and dispose one service scope
   per contender, and hold the first two handlers at a barrier so request
   overlap is deterministic without exhausting the test journal's concurrent
   reservation capacity. Release that pair before all eight calls complete.
   Preserve the exactly-two open attempts, single recovery and startup, eight
   handler calls, eight accepted/completed pairs, and final stop assertions.
   Mutating the accessor back to singleton must fail under the barrier;
   restoring scoped ownership must pass on Windows.
9. **Remove ThreadPool admission from atomic-publication rendezvous tests.**
   Launch the eight synchronous contenders in both affected fixtures with
   `TaskCreationOptions.LongRunning` and `TaskScheduler.Default` instead of
   `Task.Run`. Preserve each start gate, eight-party collision barrier,
   convergence assertion, identity validation, and temporary-file cleanup
   assertion. Both files are one finding because they use the same blocking
   rendezvous pattern and failed together under parallel xUnit load. Prove the
   stabilized fixtures still reject broken collision convergence, then run
   both focused tests together before the complete battery.
10. **Use production-faithful request scopes in concurrent evidence
    recovery.** Register `AuditCallContextAccessor` as scoped, create and
    dispose one service scope for the initial failed request and each recovery
    contender, and deterministically overlap only the first two handlers.
    Reuse the fixture's existing twenty-second contender budget for the
    overlap checkpoint and total completion; do not introduce a tighter
    intermediate deadline. Preserve all eight requests, one degraded/recovered
    pair, handler and accepted/completed counts, healthy final state, and final
    stop assertion. Mutating the accessor back to singleton must fail the
    two-handler checkpoint; restored request scopes must pass on Windows.
11. **Preserve exact R0 contract bytes across Git checkout.** Add a root
    `.gitattributes` policy that pins only `*.json`, `*.jsonl`, and `*.yaml`
    under `server/Contracts/ResilienceR0/` to LF. Do not normalize, trim, or
    weaken the exact-byte tests. In a disposable checkout with
    `core.autocrlf=true`, the parent must reproduce the six Windows contract
    failures with `w/crlf`; the repaired tree must report `w/lf` with the LF
    attribute and pass the complete R0 contract-test class.
12. **Publish nested-Job fixture markers only after close.** Write each marker
    to a sibling pending path, close the writer, then atomically move it to the
    final marker name. Preserve the final exact bytes and every containment,
    identity, and deadline assertion. Prove the guard on Windows by holding
    the marker write handle open before close: direct-final publication must
    reproduce the sharing violation while pending-file publication passes
    under the same hold. Remove the hold, run the focused integration test ten
    times, then run the complete battery.
13. **Synchronize the scheduler peer before counting executions.** Add a
    run-continuations-asynchronously `peerEntered` signal to
    `Expired_request_never_executes_and_active_deadline_targets_only_it`, set it
    immediately when the peer request enters the executor, and await it after
    the two terminal responses before asserting the execution count. Preserve
    the expired request's no-execution proof, the active request's targeted
    deadline, the peer's lack of a terminal response before release, and every
    final response assertion. The signal must strengthen the proof: once the
    peer is known to have entered, exactly two executor calls proves that the
    already-expired request did not. Run the focused test repeatedly.
14. **Separate reprime setup mutation from the tested timeout.** Give only the
    authorized module-file mutation call in
    `Post_start_module_file_mutation_cannot_execute_during_repriming` an
    explicit ten-second budget. Keep the host's 500-millisecond default and
    the later `Start-Sleep` call unchanged so `timeout-rebuild` still exercises
    the intended timeout and rebuild. Preserve authorization, exact on-disk
    mutation, all three reprime paths, final refusal, and sentinel absence.
    Under a deterministic delay of private output startup, the old setup
    budget must fail before the subject while the explicit setup budget reaches
    and preserves the subject assertion.
15. **Treat Unix escalation as an absolute deadline, not a scheduler SLA.** In
    both normal and deliberately stalled guardian-broker tests, keep the exact
    two-second TERM-to-KILL configuration and require that KILL never occurs
    before it, but remove the unprovable 100-millisecond upper scheduler bound.
    Retain the exact ten-second containment deadline, completion bound, direct
    host and worker hard-kill checks, identity polling, group-death, survivor,
    and zombie assertions. Strengthen the native-source guard so the absolute
    `sleep_until`, timestamp, and direct-host `SIGKILL` statements must remain
    adjacent in that exact order. A temporary broker deschedule across the
    deadline must exceed the old ceiling while still satisfying the corrected
    containment proof.
16. **Release the retained Windows flush handle in the retry callback.** In
    `Windows_snapshot_retries_the_retained_post_flush_write_handle`, record the
    sharing-violation observation and synchronously release the retained write
    handle from that callback. Await the observation, replacement, and read
    directly; remove the `Task.WhenAny` identity race and the scheduler-
    dependent intermediate incompletion assertion. Keep the real Windows
    handle, exact error-32 classifier, fixed production one-second retry
    window, and final sequence-1 checkpoint assertion unchanged. Temporarily
    disabling only the production error-32 classifier must make the corrected
    guard fail before restoration; run it repeatedly on Windows.

## Verification

After each slice, run its focused test and commit before beginning the next.
After all slices:

1. `dotnet test server/PtkMcpServer.slnx`
2. `pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"`
3. `pwsh -NoProfile -File server/test-handshake.ps1`
4. Review the exact repair range for weakened assertions, production changes,
   and unproved platform assumptions.
5. Push only after separate owner approval. The Windows branches remain
   provisionally verified until the pushed matrix is green; record the exact
   run rather than claiming local macOS proves Windows behavior.

Completed evidence: owner-approved GitHub Actions run `29313220388` passed all
three matrix jobs at exact head `3c618867adbe1c172f0b95fed53cc7425280a3f1`.

For slices 7-8, run each focused red/green proof on `NETWATCH-01` from a clean
exact checkout, then repeat both focused tests there before the complete local
battery and a new owner-approved hosted matrix. Commit the two findings
separately. A green rerun without the stabilizing changes is insufficient
because it merely resamples both races.

Completed follow-up evidence: direct `NETWATCH-01` validation and owner-
approved GitHub Actions run `29316181542` passed exact head
`d30bbf3701c484aeb81ab59616f6aa074687e95c`. Canonical mutation, battery, and
independent-review details are recorded in `.agents/review/index.md` and
`.agents/machines.md`.

Final-tip run `29316766579` tested `e3b1dfd`, whose complete `server/` and
workflow trees are identical to `d30bbf3`. macOS passed. Windows failed before
either handler entered because Slice 8's new intermediate overlap checkpoint
allowed five seconds even though the original contender completion budget was
fifteen seconds. Local commit `adaffd2` reuses the original fifteen-second
budget at both checkpoints without changing the two-handler scope guard.

Ubuntu failed two older eight-way atomic-publication tests before either
reached the publication primitive. Each synchronously blocks eight
`Task.Run` workers at a test-only barrier, and both classes can run in
parallel. On a low-core hosted runner, delayed ThreadPool injection can leave
participants queued until earlier barrier participants time out. The approved
repair is one new test-only slice replacing those synchronous `Task.Run`
contenders with dedicated `TaskCreationOptions.LongRunning` tasks while
preserving all eight participants, the collision barrier, convergence checks,
and temporary-file assertions. The owner approved this exact scope on
2026-07-14.

Completed direct evidence for the reopened work: the corrected Slice 8
singleton mutation admitted only one of two handlers, while scoped ownership
passed 10/10 on Windows. For Slice 9, independently disabling each production
collision-recovery return made its corresponding fixture fail immediately
with the losing atomic-publish `File exists` error; restoration returned each
fixture green, and both passed together 10/10. The exact committed range
`e3b1dfd..6193129` changes only three test files plus plan/state documentation
and was independently accepted with no code finding. The complete local
battery passed 1,207 .NET tests, 141 Pester tests with two expected skips, and
the full handshake. The matching server patch passed 1,207 .NET tests, 142
Pester tests with one expected skip, and the full zero-warning handshake on
`NETWATCH-01`. Hosted matrix evidence is still required before completion.

Hosted run `29318333860` tested exact head `24c7958`: Ubuntu and macOS passed
Pester, all 1,207 server tests, the handshake, and cleanup. Windows failed only
`Concurrent_evidence_recovery_closes_one_outage_without_resurrecting_it` when
one of eight results returned `audit_boundary_invalid`. That fixture has
shared one singleton `AuditCallContextAccessor` since `460c106`, while
production has always registered the mutable holder per request. The exact
failure follows when one overlapping call observes another call's `Current`.
Direct Windows focused runs passed 10/10 in isolation, confirming the same
load-sensitive harness race previously corrected in Slice 8 rather than a
deterministic runtime-recovery defect. The owner approved Slice 10's scoped
fixture and deterministic two-handler guard on 2026-07-14.

Completed direct Slice 10 evidence: changing only the fixture registration
back to singleton made its two-handler overlap checkpoint fail with exactly
one admitted handler on both macOS and Windows. Restored request scopes passed
10/10 on each machine. The exact committed range `24c7958..6193ae4` changes
only this test plus plan/state documentation and was independently accepted
with no material finding. The complete local battery passed 1,207 .NET tests,
141 Pester tests with two expected skips, and the full handshake. The matching
patch passed 1,207 .NET tests, 142 Pester tests with one expected skip, and the
full zero-warning handshake on `NETWATCH-01`. GitHub Actions run `29331077331`
passed Ubuntu, macOS, and Windows at exact head `ccee469`; every matrix job
completed Pester, all 1,207 server tests, the stdio handshake, and cleanup.

## Reopened Slices 11-12 evidence

GitHub Actions run `29520427103` tested exact head `72c6103`. All three SIEM
jobs passed receiver and producer-conformance checks. Ubuntu and macOS passed
Pester, all 1,532 server tests, and the handshake. Windows passed Pester and
the complete SIEM job, then failed 7 of 1,532 server tests and skipped the
handshake.

Six failures were exact consequences of Windows checkout converting canonical
LF R0 contract artifacts to CRLF because the repository had no attribute
policy. A disposable `core.autocrlf=true` checkout reproduced the same six
tests, hashes, retained carriage returns, and `w/crlf` state. The direct
Windows archive battery had stayed green because `git archive` preserves blob
bytes and does not perform checkout conversion.

The seventh failure was a test-fixture publication race. The descendant's
final marker filename became visible while `File.WriteAllText` still held its
write handle. The existence-only wait returned, and `ReadAllTextAsync` then
hit Windows sharing violation. Host and worker markers avoid this ordering
only because their pipe events occur after their writes return; the descendant
ready event is independent of marker-write completion.

Slice 11 landed at `f77d99a`. A disposable `core.autocrlf=true` checkout of
the parent reported `w/crlf` and reproduced all six R0 failures; the repaired
checkout reported `w/lf` with `text eol=lf`, and the complete R0 contract class
passed 14/14 without changing any contract artifact or exact-byte assertion.

Slice 12 landed at `f658f21`. On Windows, a temporary direct-final mutation
held the descendant marker's writer open and deterministically reproduced the
same sharing violation. Writing the same bytes to the pending sibling under
the same hold, then closing and moving it, passed. The exact committed fixture
then passed its focused integration test 10/10.

The exact repair head passed the complete macOS battery: 1,532 server tests,
141 Pester passes with two expected platform skips, and the stdio handshake.
On `NETWATCH-01`, the complete server suite passed 1,532/1,532 under a
transient `SYSTEM` task, while the documented no-profile Pester command passed
142 tests with one expected skip and the handshake passed under
`NETWATCH-01\\michael`. The split identity is required because this host's
key-authenticated SSH identity cannot use current-user DPAPI for the suite's
persisted PKCS#12 imports. The service profile's legacy Pester was not counted
as product evidence. No validation certificate, process, task, checkout,
bundle, script, or log remained after cleanup.

Independent read-only review of `72c6103..f658f21` found no material issue,
production change, weakened assertion, or uncovered current R0 artifact. Its
only residual test-hygiene note is that the deterministic held-handle delay was
a temporary mutation proof; the ordinary Windows integration guard remains
committed. Hosted matrix evidence remains required.

## Reopened Slices 13-16 evidence

GitHub Actions run `29536074900` tested exact head `af8189c`. Pester passed on
Ubuntu, macOS, and Windows, and all three SIEM jobs passed receiver and live
producer-conformance checks. The server suite then failed four pre-existing
test-harness assumptions and skipped each handshake. Exact comparison against
the preceding head shows that none of the four affected tests or their product
implementations changed in Slices 11-12.

- Ubuntu observed one executor call after two terminal responses in
  `Expired_request_never_executes_and_active_deadline_targets_only_it`.
  Those responses belong to the already-expired and immediately-deadlined
  requests; they do not prove that the independently admitted peer's executor
  trampoline has run. An explicit peer-entry signal closes that ordering gap
  while strengthening the no-execution proof for the expired request.
- Ubuntu also failed the setup mutation in
  `Post_start_module_file_mutation_cannot_execute_during_repriming` before it
  reached the named timeout-rebuild behavior. The authorized user pipeline
  completed, but its newly mandatory private output shaping contended on the
  process-wide runspace creation lock and exceeded the test host's 500 ms
  default. A ten-second budget belongs only to that setup operation; the actual
  timeout/rebuild leg remains at 500 ms.
- macOS recorded `killAtMs=2128` against a hard-coded 2,000-2,100 ms window.
  Every containment outcome otherwise passed. The fixture schedules an
  absolute two-second escalation but cannot require a non-real-time hosted
  process to run within 100 ms. Direct sampling remained near 2,000 ms; a
  temporary broker deschedule reproduced a later timestamp with both groups
  gone and zero survivors or zombies.
- Windows completed the real sharing-violation callback in
  `Windows_snapshot_retries_the_retained_post_flush_write_handle`, but its
  run-continuations-asynchronously notification lost a `Task.WhenAny` race to
  the synchronous reader exhausting the fixed one-second retry window. Release
  must occur synchronously in the callback; enlarging the production retry
  window would hide the harness race and is out of scope.

## Non-goals

- Bundling, vendoring, downloading, or compiling upstream RTK for users.
- Adding a real-RTK compatibility matrix. That is a separate distribution and
  integration-test design decision.
- Changing `ExecutionPlanner`, audit storage, session-runtime cache ownership,
  cold command resolution, or output-capture production behavior.
