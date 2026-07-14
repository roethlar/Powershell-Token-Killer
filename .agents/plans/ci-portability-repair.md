# Plan: CI portability repair after audited-harness Slice 6

**Status:** APPROVED by owner 2026-07-14 ("continue" after the exact
`69bd0d5` CI diagnosis and the proposed test-only repair scope). This plan
repairs the failing verification harness only. It does not change production
runtime behavior, install RTK into the ordinary unit-test jobs, or decide
whether a future PTK release bundles a pinned RTK binary.

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
   `Get-Command` result before serializing command type and source. Preserve
   all three candidate-order comparisons against the cold resolver.

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

## Non-goals

- Bundling, vendoring, downloading, or compiling upstream RTK for users.
- Adding a real-RTK compatibility matrix. That is a separate distribution and
  integration-test design decision.
- Changing `ExecutionPlanner`, audit storage, session-runtime cache ownership,
  cold command resolution, or output-capture production behavior.
