# Machine State

Machine-specific, nonportable facts only. Date each verification; prune stale
entries during a `drift` pass.

## `nagatha.local` — Michael's Mac

_Last verified 2026-07-11 against repo base `78779b0`._

- The live server runs from `~/.ptk/bin/PtkMcpServer`; installed version is
  `0.2.0-dev.g6db333c`.
- Installed `ptk_init.ps1`, `ptk-hook.ps1`, and
  `PwshTokenCompressor.psm1` hash-match the checkout. No product file
  changed between `6db333c` and `78779b0`.
- The Claude and Codex guidance blocks contain the current PowerShell-dialect
  and raw-recovery wording, and the Claude hook points at the installed copy.
  No dev-install or `ptk_init` rerun is pending on this Mac.
- On 2026-07-11 at plan base `2a83723`, disposable Darwin fork/pipe probes
  proved the audited-session Unix topology at the mid-creation, armed/gated,
  and released-with-descendant barriers. The broker observed supervisor
  liveness EOF, prevented pre-gate work, terminated each process group, and
  left no survivor. This proves the Darwin primitives/topology, not the later
  .NET implementation.

### Audited-harness Slice 1 checkout validation

_Verified 2026-07-11 at `460c106`; this was checkout validation, not an
installed-payload update._

- The full .NET suite passed 371/371; the PowerShell module suite passed
  134/136 with two Windows-only skips; the stdio handshake passed, including
  boundary attribution, exact evidence references, core-event secrecy, and
  the fail-closed audit-outage path. The handshake build had zero warnings.
- Real Darwin extended-ACL sabotage proved the owner-mode check alone was
  insufficient. The final storage implementation strips an existing extended
  ACL and rejects post-creation ACL reintroduction; both guards were proved
  red before the fix and green after it.

### Audited-harness Slice 2 checkout validation

_Verified 2026-07-12 at final reviewed code head `3d3739a`; this was checkout
validation, not an installed-payload update._

- The exact final head passed 927/927 .NET tests. The PowerShell module suite
  passed 134/136 with its two Windows-only skips, and the stdio handshake
  passed with a zero-warning build. The separately built audit-administration
  executable returned its exact usage contract and expected exit code 2.

- At `8470b4b`, the full .NET suite passed 390/390, the PowerShell module suite
  passed 134/136 with its two Windows-only skips, and the stdio handshake
  passed with a zero-warning build.
- At `5238984`, the full .NET suite passed 402/402. The export identity has an
  independent literal byte-vector guard, forced eight-publisher collision
  coverage, strict-UTF-8 rejection, corrupt/missing-key refusal, and
  crash-temp/link guards; each new behavior was proved red before restoration.
- At `eb0060f`, the full .NET suite passed 455/455. Strict configuration,
  read-only protection checks, endpoint noncanonicalization, and Schannel PEM
  client-certificate loading passed focused red→green guards.
- At `815a3f1`, the full .NET suite passed 464/464, the PowerShell module suite
  passed 134/136 with its two Windows-only skips, and the stdio handshake
  passed with a zero-warning build. Atomic replacement guards rejected
  delete-gap-move publication, post-commit destination deletion, and removed
  owner-only protection checks.
- At `93ebb4c`, the full .NET suite passed 536/536, the PowerShell module suite
  passed 134/136 with its two Windows-only skips, and the stdio handshake
  passed with a zero-warning build. At `56e30b0`, the full .NET suite passed
  543/543 with the same PowerShell/handshake battery; at `9651941`, it passed
  552/552 with that battery again.
- At `480713d`, the focused `FileAuditJournalSinkTests` suite passed 35/35;
  crash-safe macOS compaction guards covered publication, clone/copy failure,
  missing paths, allocation metadata, and repeated startup.
- At `0684af5`, the stdio handshake passed with a zero-warning build after the
  shared strict spool-record codec landed. At `1ce5900`, the exact-head full
  .NET suite passed 587/587. Later focused record-validation guards rejected
  malformed prior hashes, noncanonical boot IDs, and empty intermediate
  segments.

### Audited-harness Slice 3 checkout validation

_Verified 2026-07-13 at final integrated head `b78d9c6` in Claude's disposable
worktree; this was checkout validation, not an installed-payload update._

- The exact head passed 1,030/1,030 .NET tests, 139 PowerShell module tests
  with two platform skips, and the stdio handshake with a zero-warning build.
- Claude independently proved five integrated guard classes load-bearing:
  durable dispatch, preference-independent RTK capture, RTK provenance/bounds,
  AST fidelity, and Bash validation ordering. Every mutation failed for its
  intended reason, restored byte-exactly, and passed afterward.
- The clean disposable worktree and all proof artifacts were removed; an
  independent probe found no local residue and the coder tree remained clean.
- This checkout battery does not replace the later installed/OS-protected live
  validation needed for executable check/start races, dynamic dependencies,
  ACL/xattr mutation, and other properties a source snapshot cannot bind.

### Audited-harness Slice 4 checkout validation

_Verified 2026-07-13 at final integrated product head `76d4f0c` in Claude's
disposable worktree; this was checkout validation, not an installed-payload
update._

- The exact restored head passed 1,085/1,085 .NET tests, 141 PowerShell module
  tests with two platform skips, and the stdio handshake.
- Claude independently proved eight integrated guard classes load-bearing:
  supervisor-lifetime handles, audited pre-disclosure reads, anonymous
  path-substitute safety, raw-invariant capture, exactly-once execution,
  in-marker recovery hints, honest seam-absent RTK output, and raw-independent
  foreground/background dialect routing. Every mutation failed for its
  intended reason, restored byte-exactly, and passed afterward.
- The private-output stop/join test missed its fixed two-second scheduling wait
  once in a parallel full suite after its timeout assertions had passed. Its
  later post-shutdown assertions stayed green in 8/8 isolated runs and 3/3
  isolated runs under deliberate CPU load; four full-suite confirmations were
  clean. This is recorded as a non-blocking test timing/CI-flake risk, not a
  production failure.
- Two additional full-suite processes parked idle in managed waits beyond five
  minutes and were terminated; immediate exact-tree retries completed green.
  This host-specific runner instability is not a passing result and does not
  replace the clean batteries above.
- The clean review worktree and temporary prompt were removed; no installed
  payload or persistent host configuration changed. Live Windows validation
  of Slice 4 remains a later platform gate.

### MCP resilience R0 contract and containment validation

_Verified 2026-07-15 at exact corrected code head
`46ff7fb96d2b9947576e9f22627665ac763459d2`; this was checkout validation,
not an installed-payload update._

- The host reported macOS 26.5.2 build 25F84 on ARM64, Apple clang 21.0.0
  targeting `arm64-apple-darwin25.5.0`, .NET SDK 10.0.301, and PowerShell
  7.6.3.
- The native Darwin guardian-broker integration set passed 11/11. It exercised
  the pending/armed/released process-group boundaries, guardian-liveness EOF,
  identity-held containment, direct-host reaping, and the macOS prohibition on
  waiting for nonchildren.
- The exact head passed 1,531/1,531 .NET tests in 2m23s, 141 PowerShell tests
  with two platform skips, and the complete stdio handshake with a zero-warning,
  zero-error build. The focused fake guardian/private protocol/R0 contract set
  also passed 34/34.
- One separate exact-head full run observed a transient pre-existing
  `AuditAnchoredRuntimeTests` assertion while an `.anchoring.*.script` rename
  was still in flight. Its isolated guard passed 10/10 and the clean full rerun
  above passed; this is not counted as a passing run or as R0 behavior evidence.

### MCP resilience R5 macOS containment validation

_Verified 2026-07-22 at exact code head `300cbf6`; this was checkout
validation, not an installed-payload update._

- The host reported macOS 26.5.2 on ARM64, Apple clang 21.0.0, .NET SDK
  10.0.302, and PowerShell 7.6.3. The production C17 outer broker compiled
  with the package's strict warning set and the matched `osx-arm64` layout
  loaded both exact native helper roles with the required Unix modes. The
  R6-owned containment-helper role remains an intentional fail-closed exit-78
  placeholder with empty stdout/stderr.
- Direct native tests proved the gated host had `PGID == PID` before release,
  the broker reaped only its direct host, TERM/KILL covered the ordinary child
  and grandchild, and containment confirmation followed group disappearance.
  Closing only the guardian-owned liveness writer, without a stop command,
  contained the same tree. The restored direct containment case passed 20/20
  repeated runs after a buffered-event shutdown race was fixed.
- A real production guardian kept one public connection open across a hard-
  killed private host, killed an ordinary `tail` descendant and a live cold
  background PowerShell job, confirmed containment, advanced host generation
  from 1 to 2, reported warm-state loss, and successfully invoked through the
  replacement. Public EOF also left no guardian output contamination or native
  fixture processes.
- Removing process-group signaling made both the direct descendant guard and
  the real cold-background recovery guard fail; restoring it returned both to
  green. Separately ignoring guardian-liveness EOF made the liveness-only guard
  time out; restoration returned it to green. These were intentional local
  mutations and were not committed.
- The final exact tree passed 73/73 guardian architecture tests, 436/436
  guardian tests, and 1,868/1,868 server tests. The PowerShell module suite
  passed 141 tests with two platform skips, and the complete stdio handshake
  passed. The .NET runs used the physical
  `/private/var/folders/...` temp root: the unmodified macOS `TMPDIR` begins
  with the `/var` symlink, which the protected-storage guard correctly rejects.
  An initial uncached Unix layout publish exceeded the new fixture's original
  three-minute deadline; its Unix-only cold-publish allowance is now ten
  minutes and subsequent matched-layout runs passed.
- Linux validation later exposed a build-server lifetime hang in that same
  canonical publish path. Follow-up code head `225b5fc` adds the official
  `--disable-build-servers` switch to all three apphost publishes without
  changing native containment. The fixed package guard passed locally in nine
  seconds; the complete macOS rerun again passed architecture 73/73, guardian
  436/436, server 1,868/1,868, Pester 141 with two platform skips, and the full
  stdio handshake.
- Restore/build still emits the separately parked NU1903 high-severity
  advisories for `System.Security.Cryptography.Xml` 10.0.6. No dependency
  change was folded into R5.

### MCP resilience R5 Linux containment validation (`magneto`)

_Verified 2026-07-22 against the exact code/test content committed at
`225b5fc`; this was a disposable checkout validation, not an installed-payload
update._

- `magneto` reported Arch Linux x86_64, kernel 7.1.3, .NET SDK 10.0.110,
  PowerShell 7.6.3, and GCC 16.1.1. The base source was a `git archive` of
  `300cbf6` with SHA-256
  `c2201341cfb6e3732afd3e4ae48656b9b07d55dca7140159824cee66904567f0`;
  local and remote hashes matched. The only later code/test delta was the
  canonical-publish fix committed at `225b5fc` and copied with a matching
  SHA-256. A clean nonincremental solution build passed before behavior
  validation.
- The focused Linux R5 set passed 6/6: creation-time process-group ownership,
  direct descendant cleanup, guardian-liveness EOF cleanup, the frozen native
  boundary, real private-host startup, same-public-connection hard-kill
  recovery with ordinary descendant and cold-background cleanup, and the
  matched canonical Unix layout. The real recovery advanced host generation
  and resumed work through the replacement without closing public MCP.
- The original canonical layout run published the server and guardian, then
  left its childless PowerShell installer waiting in `futex_do_wait` until the
  fixture's ten-minute deadline. Publishing all three apphosts with the .NET
  CLI's official `--disable-build-servers` switch completed, and adding that
  switch to each canonical publish made the previously failing package guard
  pass in 32 seconds. Removing the fix reproduces the timeout; restoring it
  returns the guard to green.
- Independently changing native containment from process-group signaling to
  direct-host-only signaling made
  `Native_broker_owns_creation_time_group_and_confirms_descendant_death` time
  out at its all-processes-gone assertion after 32 seconds. The restored native
  source matched SHA-256
  `d62973708647dc96a8a6fdbfcaa5777928bf597b68c3957a8d8302486ede7492`
  locally and remotely, and the same test then passed in two seconds. The
  mutation was not committed.
- An intentionally non-authoritative parallel battery saturated the four-CPU
  host to load 22 and produced timing failures in both R5 composition tests
  and two established runspace timing tests; all four had passed focused or on
  the later isolated path. The authoritative solution run used one MSBuild
  project at a time and no competing workload: architecture passed 73/73,
  guardian passed 436/436, and server passed 1,868/1,868. Pester 6.0.1 was
  imported only from the disposable validation tree because the host had no
  installed Pester module; 141 tests passed with two platform skips. The
  complete stdio handshake passed every invoke, output, recovery, audit,
  outage, graceful-cleanup, and hard-kill check.
- The disposable root `/tmp/ptk-r5-linux-DXL2sF6b`, including the staged Pester
  module and source archive, was removed after a scoped process check; no host
  profile, installed payload, or unrelated process was changed. Restore/build
  continued to emit only the separately parked NU1903 advisories for
  `System.Security.Cryptography.Xml` 10.0.6.

### Dependency-hardening inventory cutoff (macOS, 2026-07-22)

_Frozen at `2026-07-22T17:09:14Z` from clean baseline `637be3c`; inventory
only, before any dependency manifest edit._

- All three managed .NET surfaces were queried separately against NuGet.org:
  `server/PtkMcpServer.slnx`, `siem/PtkSiem.slnx`, and the standalone producer-
  conformance project. Each ran `--outdated`, `--deprecated
  --include-transitive`, and `--vulnerable --include-transitive` in JSON mode.
  One parallel conformance deprecation query saw a truncated NuGet HTTP
  response; the immediate isolated rerun completed and returned the expected
  result.
- The only vulnerable graph was `Microsoft.PowerShell.SDK 7.6.3` resolving
  `System.Security.Cryptography.Xml 10.0.6` directly and through
  `Microsoft.Windows.Compatibility`/`System.ServiceModel.*`. NuGet reported the
  five high-severity advisories already recorded for R5. The server, server-
  test, and producer-conformance graphs exposed that same package; both SIEM
  projects remained free of known direct or transitive advisories.
- The only deprecated direct family was `xunit 2.9.3` in all five .NET test
  projects, with NuGet naming `xunit.v3` as its replacement. Frozen stable
  targets were PowerShell SDK 7.6.4, Hosting 10.0.10, MCP 1.4.1, Roslyn 5.6.0,
  xUnit v3 3.2.2, xUnit runner 3.1.5, Test SDK 18.8.1, Coverlet 10.0.1, and
  SQLitePCLRaw bundle 3.0.4. Google.Protobuf 3.35.1, Grpc.Tools 2.82.0, and
  Microsoft.Data.Sqlite 10.0.10 were already current.
- The PowerShell Gallery reported Pester 6.0.1 as current stable; 6.1 was only
  prerelease. Official GitHub releases reported actions/checkout v7.0.1 and
  actions/setup-dotnet v6.0.0, so the workflow's checkout major was current and
  setup-dotnet required v5-to-v6 migration. These are point-in-time targets,
  not a permanent update or build-blocking policy.

## `NETWATCH-01` — Michael's Windows machine

_Verified 2026-07-11 for audited-session slice 0 at repo base `2a83723`._

- SSH ran as ordinary identity `NETWATCH-01\michael` on Windows NT
  10.0.26200.0 x64, Windows PowerShell 5.1.26100.8737, and .NET SDK 10.0.301
  / runtime 10.0.9. The probe did not read or modify the existing `F:\dev`
  repositories.
- A disposable native probe used
  `CreateProcessW`/`STARTUPINFOEX`/`PROC_THREAD_ATTRIBUTE_JOB_LIST`, queried
  back exact `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` (`0x2000`), and proved
  specific worker membership before `ResumeThread`. Its source SHA-256 was
  `ace621f6a2f27bc2a634d4d54c43150aa6c6df1ad93efd95d2a9b025616aeb4c`;
  the executed assembly SHA-256 was
  `89d7f134667257e603d4bf32789bbbe91f1800ed7d88ca7eaf1bf7f6bab39b90`.
- The gated case proved no workload signal before release and worker death
  after raw supervisor-only termination. The released case proved an ordinary
  descendant was alive and in the same exact job, then both held process
  handles signaled after the supervisor's sole noninherited job handle closed.
  The probe contained no `AssignProcessToJobObject` or `TerminateJobObject`
  symbol/path.
- No `PtkWindowsJobProbe` process existed before or after the final run. Its
  temporary directory was removed, and no persistent host configuration was
  changed.

### Audited-harness Slice 1 checkout validation

_Verified 2026-07-11 at `460c106` in a disposable copy under `F:\dev`; the
installed Windows payload was not changed._

- The full .NET suite passed 371/371; the PowerShell module suite passed
  136/136; the stdio handshake passed with the same audit/evidence/outage
  checks as the Mac run and a zero-warning build.
- Windows-specific journal tests verified that protected directories and
  files are owned by the current SID, have inheritance disabled, and expose a
  single current-user FullControl allow rule. Alternate-owner sabotage failed
  closed.
- Live command-resolution probes exposed extensionless PATHEXT misses and a
  `.ps1`/`.exe` precedence error in the first correction. The final C# and
  PowerShell guards each failed under an Application-only sabotage and passed
  after restoring joint ExternalScript/Application enumeration. The updated
  source hashes matched the Mac checkout and the validation tree contained no
  AppleDouble files.
- All four disposable validation directories and their four transfer archives
  were removed from `F:\dev` after local landing; no installed payload or
  persistent host configuration was changed.

### Audited-harness Slice 2 checkout validation

_Verified 2026-07-12 at final reviewed code head `3d3739a` in disposable copies
under `F:\dev`; the installed Windows payload and existing repositories were
not changed._

- The exact final head passed 927/927 .NET tests, 136/136 PowerShell module
  tests, and the stdio handshake with a zero-warning build. A separate
  disposable checkout built the audit-administration executable and confirmed
  its exact usage contract and expected exit code 2.
- The corrected checkpoint durability branch independently passed its focused
  four-test Windows guard, the full suite, a disabled-retry red mutation, and a
  post-rename-flush red mutation. Claude's accepted one-finding re-review also
  ran the focused guards plus ten repeated concurrency iterations.
- Every transfer bundle and disposable validation directory was removed after
  use. Access used `10.1.10.177` with the pinned SSH host alias
  `netwatch-01`; no existing checkout or installed payload was modified.

- The full .NET suite passed 390/390; the PowerShell module suite passed
  136/136; the stdio handshake passed with a zero-warning build.
- The transfer archive and disposable validation directory were removed after
  the run.
- At `eb0060f`, the full .NET suite passed 455/455. Focused configuration/ACL
  and Schannel mTLS guards also passed. The exact Windows Pester and handshake
  checks were not rerun at that historical commit.

### Audited-harness Slice 3 checkout validation

_Verified 2026-07-12 at fixed code head `669ce6e` in a disposable copy under
`F:\dev`; the installed Windows payload and existing repositories were not
changed._

- A `git archive` ZIP of the exact code head was transferred with strict host
  key checking and verified on Windows at SHA-256
  `d472a9a6e64b1dfc57168722a62e52cc25a05468a52c740e0b4a3d955e77f0bf`.
- The full .NET suite passed 1,010/1,010; the PowerShell module suite passed
  140 tests with one skip; and the stdio handshake passed with a zero-warning
  build.
- The passing .NET count is not live Windows Bash coverage where `/bin/bash`
  is absent; the Darwin battery exercises the real Bash leg. Full live Windows
  session checks remain the plan's later validation slice.
- The GUID-named validation directory, uploaded archive, and local transfer
  archive were all removed after the battery. No persistent configuration or
  installed payload changed.

#### Failed checkout validation — 2026-07-13

Code head `40923784601bf8063d9461188b04be3940374c7d` is **not verified on
Windows**. An exact `git archive` ZIP was uploaded to a collision-checked
GUID-named path under `F:\dev`; the local and Windows copies matched SHA-256
`CE5707231353BABCBA096E90076513B9532A7C0B1FE9C64F337A474D8110FF2E` before
expansion.

- `dotnet.exe test server/PtkMcpServer.slnx` passed 1,020/1,030 and failed the
  following ten tests:
  - `RtkProcessRunnerTests.Deadline_stops_the_started_root_without_retrying_it`
    — the expected `starts.txt` marker did not exist.
  - `RtkProcessRunnerTests.Direct_capture_preserves_stdout_stderr_and_nonzero_exit`
    — captured stderr was `DIRECT_STDERR  ` rather than `DIRECT_STDERR`.
  - `InvokeToolTests.Single_native_command_routes_through_rtk`
  - `InvokeToolTests.Relative_rtk_override_is_bound_before_the_warm_cwd_changes`
  - `InvokeToolTests.Rtk_routed_output_is_not_shaped_by_rtk_a_second_time`
  - `InvokeToolTests.Authorization_observes_exact_rtk_preparation_before_exit_reset_and_execution`
  - `InvokeToolTests.Rtk_capture_ignores_warm_native_error_preferences_and_preserves_error_state`
  - `InvokeToolTests.Operator_pinned_rtk_identity_survives_warm_environment_poisoning`
  - `InvokeToolTests.Rtk_install_nag_is_filtered_but_real_stderr_survives`
  - `InvokeToolTests.Routed_command_exit_code_is_reported`
- The eight `InvokeToolTests` failures all observed empty/no routed output from
  the new Windows RTK fixture. Pester and the stdio handshake did not run after
  the required .NET gate failed.
- A preliminary validation-harness attempt stopped before expansion because it
  used an unsupported `New-Item -LiteralPath` parameter; its `finally` cleanup
  succeeded. The corrected harness produced the test evidence above.
- The failed run's disposable checkout, uploaded archive, and local transfer
  archive were confirmed removed. Existing repositories, installed payloads,
  and persistent host configuration were unchanged.

#### Corrective checkout validation — 2026-07-13

_Verified at exact corrective head
`c100ba199d9854f7171733d9950b26e2a8a397ab` in a disposable copy under
`F:\dev`; the installed Windows payload and existing repositories were not
changed._

- A `git archive` ZIP of the exact head was transferred after collision checks;
  the local and Windows copies matched SHA-256
  `76F027844CC53919B8D2FCEE526940F9196A684337AA3BBFB0E798C5B67BF5A3`.
- Removing only the fixture's explicit stdout/stderr forwarding made exactly
  the eight RTK-route integration guards fail: 1,022/1,030 passed. The script
  required three named failures, restored `Program.cs` byte-exactly, verified
  its SHA-256, and reran the same full suite successfully at 1,030/1,030.
- The PowerShell module suite passed 140 tests with one platform skip. The
  stdio handshake passed with a zero-warning, zero-error build.
- The GUID-named checkout, uploaded archive, and local transfer archive were
  confirmed removed. Existing repositories, installed payloads, and
  persistent configuration were unchanged. The checkout proof still does not
  replace later installed/OS-protected live validation for the recorded
  check/start and dependency caveats.
- Claude independently repeated the exact-head proof at review head
  `64eb767a826da0c8177d9fcdd2fa1ea7033a1d73` from archive SHA-256
  `C3949B5474FE9427BBFEEE2767F95C8C6FCD7900F11524635CEB2028AC2D5874`.
  The same eight-test mutation failed and byte-exact restoration passed the
  full Windows battery. The reviewer corrected one no-effect cleanup-command
  quoting error, then it and the coder independently confirmed zero remaining
  `ptkrev-*` artifacts under `F:\dev`; no persistent host state changed.
- The final integrated head `b78d9c6f176cb42771f823a6b1bdc2b3e6561f07`
  passed a separately hashed exact-current-head Windows checkout at SHA-256
  `8C547113682979E02F0CF0FA0C05A722DEBCC8A5CB388645FF1415C1F4B5DA90`:
  1,030/1,030 .NET, 140 Pester with one skip, and the zero-warning handshake.
  `src/`, `server/`, and `tests/` are byte-identical from corrective head
  `c100ba1` through the final head, so the independent forwarding red-to-green
  proof covers this final code tree. All review archives, scripts, and the
  remote checkout were removed; independent probes found no residue.

### CI flake stabilization validation — 2026-07-14

_Verified in the existing clean `F:\dev\PowerShell-Token-Killer` checkout at
exact follow-up head `d30bbf3701c484aeb81ab59616f6aa074687e95c`._

- The host reported Windows NT 10.0.26200.0, PowerShell 7.6.3, and .NET SDK
  10.0.301. The checkout was fast-forwarded from `00e74d2` with the owner's
  explicit permission; the installed PTK payload was not changed.
- At the old head, the singleton audit accessor race reproduced on the first
  focused run. A deterministic first-two-handler barrier admitted only one
  handler under the singleton mutation; restored scoped ownership passed
  10/10 with all original recovery, event-count, and shutdown assertions.
- A deterministic 1.5-second private-output opening delay made the old setup
  warm-up budget fail before the named invariant. The explicit setup-only
  budget passed the same delay. Removing the production `StopCompleted` join
  then made the stabilized test fail; exact restoration passed.
- The final direct battery passed 1,207/1,207 .NET tests, 142 Pester tests with
  one platform skip, and the complete stdio handshake with a zero-warning,
  zero-error build. The checkout was clean at the exact head afterward.

#### Final-tip scheduling follow-up — 2026-07-14

_Verified from the existing `F:\dev\PowerShell-Token-Killer` checkout using
the exact stable server patch from `d30bbf3..6193129`; local and Windows patch
IDs matched `ce3374eb1781fd1d3a94770214e5df4c606a7522`, and only the three
intended test files differed._

- The host reported Windows 11 Pro 10.0.26200, PowerShell 7.6.3, .NET SDK
  10.0.301, and Pester 5.8.0. `git diff --check` passed and no untracked files
  were present.
- The three corrected focused tests passed together. The complete battery then
  passed 1,207/1,207 .NET tests in 1m32s, 142 Pester tests with one expected
  platform skip, and the full stdio handshake with zero build warnings and
  errors.

#### Concurrent evidence recovery scoping — 2026-07-14

_Verified from the clean `F:\dev\PowerShell-Token-Killer` checkout by applying
the exact stable server patch from `24c7958..6193ae4`; local and Windows patch
IDs matched `a2a198e539ce0c6a494e42508751e50bf3e7116a`, and only
`AuditRuntimeGateTests.cs` differed._

- Temporarily changing the target fixture from scoped back to singleton made
  its deterministic overlap guard fail after twenty seconds with exactly one
  of the first two handlers admitted. Exact restoration passed the focused
  test 10/10 at roughly 439-445 ms each.
- The complete battery passed 1,207/1,207 .NET tests in 1m32s, 142 Pester tests
  with one expected platform skip, and the full stdio handshake with zero
  build warnings and errors. `git diff --check` passed and no unrelated or
  untracked files were present.

### Audited-harness Slice 7d Windows containment validation

_Verified 2026-07-14 for exact code tree `bbc2a0e2b116280ceebaab5442014286671aefe2`
in disposable GUID-named directories under `F:\dev`; no installed payload or
existing checkout changed._

- The final 274-file archive matched SHA-256
  `1F521058547342CC3C9F0798FEF77F09A751D6797C4A6CFC313CABD834068EF9`
  before expansion and contained no AppleDouble sidecars. The focused Windows
  launcher/containment set passed 29/29, the complete .NET suite passed
  1,309/1,309, Pester passed 142 with one expected platform skip, and the full
  stdio handshake passed with zero build warnings or errors.
- The live runnable fixture proved the production mode is not suspended, NUL
  stdin and private request/event plus diagnostic pipes are mapped, Unicode
  quoting and the caller-frozen environment survive while a post-freeze ambient
  variable does not leak. The proof-mode fixture observed no marker before
  exact-job membership verification and explicit resume; the returned owner
  survived forced GC, then closing its sole job handle killed both worker and
  ordinary descendant while independent process witnesses remained open.
- A separate archive with SHA-256
  `ACBE3C61921F32C7B6567DE84D19F7B66630C5FE5A187B4C2E404B5C1D0CC405`
  changed only runnable flags to include `CREATE_SUSPENDED`. The live runnable
  guard failed at its intended fifteen-second read checkpoint. Cleanup found no
  matching process residue; the local production file was restored to its
  pre-mutation SHA-256 before the final archive and green runs.
- One preliminary transfer was stopped before execution when macOS archive
  metadata doubled the extracted file count. It and every later mutation/final
  archive, disposable directory, and matching process were removed. No
  persistent host configuration changed.

#### Owning-wait prerequisite follow-up — 2026-07-14

_Verified for exact code tree `d1cca1b5fc69be61c7102843ab3ceb645cd362eb`
in a disposable GUID-named directory under `F:\dev`; no installed payload or
existing checkout changed._

- The 274-file exact-head archive matched SHA-256
  `7D60831481B56D72D5640D6C01F5EB548AB7ABE9EB98490628A6632179AD434A`
  before expansion.
- The focused Windows launcher/containment set passed 29/29, the complete .NET
  suite passed 1,309/1,309, Pester passed 142 tests with one expected platform
  skip, and the stdio handshake passed with a zero-warning, zero-error build.
- The live case canceled its sole outstanding native wait while the independent
  process witness remained alive, disposed the contained owner before awaiting
  the canceled task, then observed cancellation and worker exit.
- The final residue check found zero matching processes. The remote checkout
  and archive plus the local transfer archive were removed.

### Audited-harness Slice 7e Windows worker-entry validation

_Verified 2026-07-14 for exact code tree
`12617ccb25a3f3ff8d9690d94ebe5cea4f141ee6` in a disposable GUID-named
directory under `F:\dev`; no installed PTK payload or existing checkout
changed._

- The exact-tree archive matched SHA-256
  `CF6CCEF45D7E28682FF1FA649E5366B1981CFFBFB0D2E877662B87D7EDF595E2`,
  contained 319 ZIP entries representing 284 source files, and contained no
  AppleDouble sidecars.
- A forced-build mutation disabling the early `Program` worker branch
  discovered exactly one live lifecycle test and failed it at the first hello
  `Assert.NotNull` in 254 ms. An earlier unforced mutation attempt returned
  green and was rejected as vacuous; it contributed no proof and cleaned its
  disposable paths before the forced-build rerun.
- Exact restoration and rebuild passed 125/125 focused worker/bootstrap/
  containment guards, 1,432/1,432 .NET tests, 142 Pester tests with one
  expected platform skip under `pwsh -NoProfile`, and the full stdio handshake
  with zero build warnings or errors.
- Post-validation comparison found zero source-file mismatches and zero
  relevant `dotnet`, `testhost`, or PTK worker process residue. The remote
  archive and directory plus the local transfer archive were removed.
- A prior profile-bearing SSH shell could not run the canonical Pester command
  because PSProfile defines `source` as `Import-PythonVenv`; the required
  no-profile run passed. During the earlier candidate validation, the host's
  first .NET invocation also reported installing an ASP.NET Core HTTPS
  development certificate. It was not removed because certificate-wide
  cleanup could affect unrelated host state.

### MCP resilience R0 contract and containment validation

_Verified 2026-07-15 at exact corrected code head
`46ff7fb96d2b9947576e9f22627665ac763459d2` in a disposable GUID-named
directory under `F:\dev`; no installed payload or existing checkout changed._

- The host reported Windows NT 10.0.26200.0 x64, .NET SDK 10.0.302, and
  PowerShell 7.6.3. The exact 366-entry archive had SHA-256
  `F785AC44B435BB9073DD1B486E2B28FFD066C569C329A3083499B8174F3D6EC0`
  and contained no AppleDouble metadata.
- The real nested Job Object proof passed 2/2. It confirmed the noninheritable
  outer kill-on-close Job, creation-time nested host/worker membership before
  resume, ordinary descendant membership, held process identities, and death
  of host, worker, and descendant after closing the guardian's sole Job handle.
- The focused fake guardian/private protocol/R0 contract set passed 34/34.
  The original candidate had failed its single recovery diagnostic assertion
  because the test expected LF while `Console.Error.WriteLine` emits CRLF on
  Windows; exact correction `46ff7fb` made that same set green.
- The PowerShell module suite passed 142 tests with one platform skip. The full
  stdio handshake passed with zero build warnings or errors. Post-run checks
  found no PTK fixture/server/testhost processes, and the remote archive,
  harness, and disposable directory were absent.
- A broader pre-correction Windows run passed 1,513/1,531 .NET tests. Besides
  the corrected R0 newline assertion, the other 17 failures were pre-existing
  certificate/HTTPS fixtures failing during PKCS#12 import or client-certificate
  parsing on this SDK. That failed run is not claimed as a green Windows
  battery; R0's required direct Windows topology and protocol evidence is the
  focused exact-head evidence above.

## Disposable Ubuntu 26.04 ARM64 validation

_Focused verification 2026-07-11 for the Slice 1 secure-storage implementation
that landed in `460c106`._

- Raw open calls confirmed Linux ARM64 uses the architecture-specific
  `O_DIRECTORY=0x4000` and `O_NOFOLLOW=0x8000` values. The focused audit
  storage/evidence suite passed 48/48 after replacing the x86-only constants;
  the pre-fix run failed with `EINVAL`.

## Windows payload/install status

_Not verified by the 2026-07-11 containment probe._

- Current Windows payload and guidance status remains unknown, including on
  `NETWATCH-01`; the probe deliberately did not inspect `F:\dev` or installed
  PTK files. The former combined Mac/Windows reinstall claim was falsified on
  the Mac and is not evidence for any Windows host; verify the target payload
  directly before taking install action.

## mini-SIEM S1 baseline (Mac, 2026-07-15)

_Fresh transcripted baseline of the existing battery, captured at S1 start of
`.agents/plans/mini-siem-implementation.md` (plan Verification; the
predecessor's author-reported counts are not baseline evidence)._

- Head `8b6d3bb`, worktree `mini-siem`, branch `plan/mini-siem-discovery`,
  clean tree. Darwin 25.5.0 arm64, dotnet 10.0.301, pwsh 7.6.3.
- Exact repo-guidance commands: Pester 141 passed / 0 failed / 2 skipped;
  `dotnet test server/PtkMcpServer.slnx` 1484/1484 passed; handshake
  `HANDSHAKE PASSED`. All three exit codes 0.
- Full normalized transcript:
  `.agents/review/baselines/2026-07-15-mini-siem-s1.txt` (tracked copy of the
  ptk job log with terminal color escapes removed).

## mini-SIEM S2 verification (Mac, 2026-07-15)

_Local verification of S2 receiver code `e761b75` plus producer conformance
head `1f6d485` on Darwin 25.5.0 arm64, dotnet 10.0.301, pwsh 7.6.3._

- `dotnet test siem/PtkSiem.slnx`: 77/77 passed, including live TLS negatives
  for missing certificate, wrong CA, expired certificate, wrong EKU, explicit
  online-revocation refusal, and old/new CA rotation.
- Producer conformance project: 2/2 passed both with the override unset and
  with `PTK_SIEM_CONFORMANCE_MODE=in-process`; the latter drove the real
  exporter into the live SIEM Kestrel endpoint. The scoped formatter checks
  for the SIEM solution, conformance project, and changed producer integration
  file passed.
- Existing battery: Pester 141 passed / 0 failed / 2 skipped;
  `dotnet test server/PtkMcpServer.slnx` 1485/1485 passed; handshake
  `HANDSHAKE PASSED`.
- Required guard discrimination was exercised and restored independently:
  false-ack production default, optional rather than required client cert,
  skipped event-hash recomputation, non-200 success response, and raw-JSON
  rather than OTLP golden output each made its focused test fail. Restored
  trees passed the complete local battery above.
- One pre-existing scheduler timing assertion failed in an intermediate full
  run while an extra redundant TLS test was present. The redundant load was
  removed without touching scheduler code; the exact scheduler test then
  passed 20/20 focused runs and the final complete 1485-test run passed.
- Hosted ubuntu/windows/macos CI was not run from this local branch; the SIEM
  job is configured to exercise both receiver and live producer conformance
  legs on all three when the branch is published.

## mini-SIEM S3 verification (Mac, 2026-07-15)

_Local verification of S3 durable-store head `eb51f2e` plus producer
conformance compatibility head `9f53831` on Darwin 25.5.0 arm64, dotnet
10.0.301, pwsh 7.6.3._

- `dotnet test siem/PtkSiem.slnx`: 91/91 passed. Coverage includes migration
  reopen, effective WAL/FULL writer assertions, exact raw event evidence,
  deterministic custody framing and chaining, post-head duplicate replay,
  mismatched duplicate quarantine, a controlled simultaneous fork, durable
  strict-validator quarantine, unique `(boot_id, sequence)` backstop, simulated
  `SQLITE_FULL`, interrupted event/quarantine transactions, and live mTLS
  `200`/`400`/`503` storage paths.
- `dotnet list siem/PtkSiem.slnx package --vulnerable --include-transitive`
  reported no vulnerable direct or transitive package in either project. The
  explicit native SQLite bundle override resolved to 2.1.12 throughout.
- Producer conformance passed 2/2 both with the override absent and with
  `PTK_SIEM_CONFORMANCE_MODE=in-process`; the compatibility adapter preserves
  the original S2 fake-committer seam while production uses receipt metadata.
- Existing battery: Pester 141 passed / 0 failed / 2 skipped;
  `dotnet test server/PtkMcpServer.slnx` 1485/1485 passed; handshake
  `HANDSHAKE PASSED` with a zero-warning build.
- Six independent guard mutations failed for the intended reason and were
  restored: `synchronous=OFF` tripped startup policy; moving the injected fault
  after commit exposed a persisted partial transaction; rejecting a replay
  broke post-head idempotence; bypassing quarantine left no attempt evidence;
  removing the boot/sequence uniqueness allowed a fork insert; omitting remote
  endpoint bytes changed the frozen custody digest. The restored tree passed
  the complete battery above.
- Hosted ubuntu/windows/macos CI was not run from this local branch. The plan's
  separate startup filesystem-protection matrix remains unimplemented and is
  recorded as a scheduling conflict in `.agents/state.md`; these results do not
  claim acceptance row 7.

## R0 + mini-SIEM S1-S3 integration verification (Mac, 2026-07-16)

_Combined local verification of merge `374f164` on Darwin arm64. The worktree
also contained unrelated, unstaged governance-only changes; those changes were
excluded from the merge and did not alter the tested product or test sources._

- `dotnet test server/PtkMcpServer.slnx`: 1532/1532 passed.
- `dotnet test siem/PtkSiem.slnx`: 91/91 passed.
- Producer conformance: 2/2 passed with
  `PTK_SIEM_CONFORMANCE_MODE=in-process`, and 2/2 passed with the override
  absent.
- Existing PowerShell battery: 141 passed / 0 failed / 2 skipped; handshake
  reported `HANDSHAKE PASSED` with zero warnings and zero errors.
- `dotnet list siem/PtkSiem.slnx package --vulnerable --include-transitive`
  reported no vulnerable direct or transitive packages.
- This is local macOS evidence at merge `374f164`; later direct platform and
  hosted evidence is recorded in its own sections and in the CI portability
  plan. Mini-SIEM S4 remains gated on the absent producer-owned serialized v3
  OTLP request fixture, and startup filesystem owner/mode/DACL plus
  symlink/reparse enforcement remains unimplemented.

## R0 + mini-SIEM S1-S3 integration verification (Windows, 2026-07-16)

_Direct validation of exact integration tip `1ad195e` on `NETWATCH-01`, Windows
NT 10.0.26200.0 x64, PowerShell 7.6.3, .NET SDK 10.0.302/runtime 10.0.10._

- A `git archive` ZIP of the exact commit was transferred to a collision-checked
  disposable directory under `F:\dev`. Local and Windows SHA-256 matched
  `9A18367007DA43E2D4535B90CC17265F7329A4607897EEEC4DFE13E00E2450FD`.
- The .NET/TLS checks ran under a transient `SYSTEM` scheduled task because the
  key-authenticated OpenSSH logon cannot use current-user DPAPI on this host:
  `ProtectedData.Protect(..., CurrentUser)` returned `Access is denied`, causing
  persisted PKCS#12 imports to fail before test behavior. In the service
  context, `dotnet test server/PtkMcpServer.slnx` passed 1532/1532 and
  `dotnet test siem/PtkSiem.slnx` passed 91/91.
- Producer conformance passed 2/2 both with
  `PTK_SIEM_CONFORMANCE_MODE=in-process` and with the variable truly absent.
  The handshake reported `HANDSHAKE PASSED` after a zero-warning, zero-error
  build.
- The documented PowerShell command was reproduced in a nested `-NoProfile`
  process under the ordinary `NETWATCH-01\michael` account: 142 passed / 0
  failed / 1 skipped. The outer SSH shell loads `PSProfile`, whose real `source`
  alias intentionally changes one dialect-detector expectation, so that shell
  is not equivalent to the documented no-profile battery.
- Two preliminary service-runner attempts incorrectly set the conformance mode
  to an empty string while trying to remove it; the contract tests correctly
  rejected that set-but-invalid value. A final runner asserted a null process
  value before launching the green server and default-conformance batteries.
- The scheduled task, archive, checkout, helper scripts, and logs were removed.
  The isolated SYSTEM profile's first .NET invocation created ASP.NET
  development certificate thumbprint
  `35346EB43A9DCC683647EB3FEC9366AD60B8C57D`; cleanup verified its marker and
  removed that exact certificate with its private key. Final task/path/wildcard
  residue counts were zero. No existing checkout or installed PTK payload was
  changed.
- This validates the committed Windows source in a service context; it does not
  satisfy the still-unimplemented dedicated receiver-account filesystem/DACL
  and symlink/reparse acceptance matrix. Mini-SIEM S4 remains gated on
  producer-owned serialized v3 OTLP request bytes.

## R0 + mini-SIEM S1-S3 integration verification (Linux, 2026-07-16)

_Direct validation of exact snapshot `a473ca3` as ordinary user `michael` on a
disposable Ubuntu 26.04 ARM64 checkout at `192.168.64.5`: kernel 7.0.0-27,
.NET SDK 10.0.110/runtime 10.0.10, PowerShell 7.6.3, and Pester 5.8.0._

- A `git archive` ZIP of the exact commit was transferred to a collision-checked
  disposable directory. Local and Linux SHA-256 matched
  `C8615684E1AC6680DBD19EB7EFA7A7D7B4C472AE3F840B2EDD4BD4BCCCC53DF6`.
- The full server suite passed 1532/1532. The PowerShell module suite passed
  141 / 0 failed / 2 platform skips. The complete stdio handshake reported
  `HANDSHAKE PASSED`, including warm cross-call state, foreground/background
  output recovery, audit attribution and outage refusal, and cleanup checks.
- The mini-SIEM suite passed 91/91. Producer conformance passed 2/2 with
  `PTK_SIEM_CONFORMANCE_MODE` truly absent and 2/2 in `in-process` mode.
- A clean build exposed a Linux ARM64 tooling caveat: `Grpc.Tools` 2.82.0's
  bundled `linux_arm64/protoc` exited 139 only when spawned by MSBuild. The same
  binary reported `libprotoc 35.0` and completed the exact generated command
  successfully when invoked directly; disabling build servers and interposing
  transparent wrappers did not change the MSBuild-only crash. The test battery
  above used that bundled binary to generate the normal intermediate files,
  then ran the ordinary projects with `--no-restore`. No product or test source
  was modified, no system `protoc` was installed, and this evidence therefore
  validates behavior but not a clean ARM64 build path.
- The archive, checkout, wrapper, diagnostic logs, and test-created temporary
  job directories were removed. Process and scoped path checks found no PTK
  residue. The only HTTPS development certificate in the user store predates
  this validation (created 2026-07-11) and was preserved. No installed PTK
  payload or existing checkout changed.
- The receiver filesystem protection matrix remains unimplemented, and
  mini-SIEM S4 remains gated on producer-owned serialized v3 OTLP request
  bytes.

## CI portability Slices 11-12 verification (Mac and Windows, 2026-07-16)

_Verified at exact repair head `f658f212da64d2ad08c94c0341844d301f0eac4c`; this
was disposable-checkout validation and did not update an installed payload._

- On `nagatha.local`, a `core.autocrlf=true` parent checkout reproduced the six
  byte-contract failures with `w/crlf`. The repaired checkout reported `w/lf`
  and `text eol=lf`, and the R0 contract class passed 14/14. The exact repair
  head then passed 1,532/1,532 server tests, 141 Pester tests with two expected
  platform skips, and the complete stdio handshake.
- On `NETWATCH-01`, the checked-out R0 artifacts likewise reported `w/lf` with
  the LF attribute. A temporary direct-final marker writer held open for one
  second reproduced the hosted sharing violation; the pending-file version
  passed with the same hold, and the exact committed Windows integration test
  passed 10/10 after the hold was removed. The R0 contract class passed 14/14.
- The complete Windows server suite passed 1,532/1,532 under a transient
  `NT AUTHORITY\\SYSTEM` scheduled task. The documented no-profile PowerShell
  suite passed 142 tests with one expected skip under
  `NETWATCH-01\\michael`, and the stdio handshake passed with a zero-warning,
  zero-error build. The service identity was required for .NET because this
  host's key-authenticated SSH identity cannot use current-user DPAPI for the
  persisted PKCS#12 test imports; the service profile's legacy Pester was not
  counted as product evidence.
- The service run created no certificate. All exact validation tasks,
  processes, checkouts, bundles, scripts, logs, and local proof directories
  were removed, with zero scoped residue afterward. Existing checkouts,
  installed payloads, and unrelated host configuration were unchanged.

## CI portability Slices 13-16 verification (Mac, Linux, and Windows, 2026-07-16)

_The four runtime-equivalent repairs were verified at exact head
`a031556034ec41adf3f97de4248e6553f28ac90d`; the independent-review source-
guard closure was verified at final head
`5642376563fe9b0d1eb8c2512291fec45cf29bc6`._

- On `nagatha.local`, final head `5642376` passed 1,532/1,532 server tests,
  141 Pester tests with two expected platform skips, and the complete stdio
  handshake with zero warnings and errors. Homebrew PowerShell 7.6.3 was
  installed but not linked into the ordinary command path, so the passing
  battery explicitly prepended `/opt/homebrew/opt/powershell/bin`. The first
  diagnostic run without that directory failed 58 job-dependent tests solely
  because `pwsh` was unresolved; it is not counted, and its fifteen exact
  test-created job directories were removed. The SDK's first-use text referred
  to its feature-band sentinel: the only local ASP.NET development identity
  predates this validation and was preserved.
- On the Ubuntu 26.04 ARM64 VM at `192.168.64.5`, exact head `a031556` passed
  1,532/1,532 server tests, 141 Pester tests with two expected platform skips,
  and the full zero-warning handshake. The final `5642376` deadline-helper
  source guard then passed 1/1 from a new exact checkout. Both runs required
  the already-recorded direct-protoc generation around the host's MSBuild-only
  ARM64 `protoc` exit 139; they validate behavior, not a clean ARM64 build.
  All scoped checkouts, job directories, and matching processes were removed.
- On `NETWATCH-01`, disabling only the production Windows error-32 classifier
  made the corrected checkpoint test fail at the retained-handle timeout. The
  production file restored to its committed SHA-256 and the focused test then
  passed 10/10. A first full `SYSTEM` run passed 1,531/1,532; its sole
  non-counted failure transiently observed the pre-existing journal quota lock
  before its owner-only DACL. A clean rerun passed 1,532/1,532. Under nested
  no-profile `NETWATCH-01\michael`, Pester 5.8.0 passed 142 tests with one
  expected skip and the full zero-warning handshake passed. The final
  `5642376` deadline-helper source guard separately passed 1/1 under that
  ordinary identity.
- Windows production/test source hashes matched the committed bytes. The
  `SYSTEM` certificate store was empty before and after the full run; one
  pre-existing Michael ASP.NET development certificate was preserved. Final
  checks found zero scoped tasks, processes, checkouts, archives, scripts, or
  logs on either remote host. No existing repository or installed PTK payload
  changed.

## MCP resilience R1 verification (Mac, Windows, and Linux, 2026-07-17)

_Exact R1 code head `60eb20f37a75259c0bf246d594632bde128c109b`,
based on `1f314a29807e7504aa04f7f14899c6bb6483248a`. Remote validation used the
exact `git archive` whose SHA-256 was
`A0DE86C1C81E46371ED1A74D164BF5E4020C2D4AE2632F6E072A5900414DC862`._

- On `nagatha.local` (macOS 26.5.2 arm64, .NET SDK 10.0.302, PowerShell
  7.6.3), the PowerShell suite passed 141 / 0 failed / 2 skipped,
  `dotnet test server/PtkMcpServer.slnx` passed Guardian 26/26,
  Architecture 68/68, and Server 1,578/1,578, and the full stdio handshake
  passed after a zero-warning, zero-error build. Three earlier broad runs
  during the audit-seam slice had produced different process-start deadline
  failures while the host had roughly 1,000 processes, 19 `PtkMcpServer`
  processes, and load averages near 6.5; every named failure passed in isolation.
  At the final exact-head run the load average had fallen to 2.10 and the one
  complete battery passed. The preliminary failures were not reproduced once
  load fell, which is consistent with host-load diagnostics but does not prove
  load was their cause.
- On `NETWATCH-01` (Windows 11 Pro 10.0.26200 x64, .NET SDK 10.0.302/runtime
  10.0.10, Pester 5.8.0), the ordinary no-profile PowerShell suite passed
  142 / 0 failed / 1 platform skip, Shared contracts passed 29/29, Guardian
  passed 26/26, Architecture passed 68/68, the 232 directly changed R1 server
  tests passed 232/232, and the full handshake passed. The SSH identity's
  broad server run passed 1,561/1,578; all 17 failures were the host's known
  current-user DPAPI/empty-password PKCS#12 reload problem. A fresh exact-base
  tree reproduced one representative failure from each path, and the affected
  helper/configuration blobs were unchanged by R1. The definitive transient
  `NT AUTHORITY\\SYSTEM` run of `dotnet test server\\PtkMcpServer.slnx` then
  passed Guardian 26/26, Architecture 68/68, and Server 1,578/1,578 with exit
  zero. The complete SYSTEM CurrentUser certificate inventory was byte-identical
  before and after (1,503 entries; SHA-256
  `0994185A79DDC0488D64D0B603249F35214FE1A61F86DA800ED6A755E31FCD82`),
  and cleanup found zero matching tasks, processes, paths, archives, or logs.
- On the Ubuntu 26.04 ARM64 VM at `192.168.64.5` (kernel 7.0.0-27, .NET SDK
  10.0.110/runtime 10.0.10, PowerShell 7.6.3, Pester 5.8.0), the PowerShell
  suite passed 141 / 0 failed / 2 skips. The recorded Grpc.Tools 2.82.0
  MSBuild-only failure reproduced: bundled `linux_arm64/protoc` exited 139
  under MSBuild, while the exact emitted command ran directly with libprotoc
  35.0 and generated both expected intermediates. With those ordinary
  intermediates and `--no-restore`, Shared built with zero warnings/errors,
  Guardian passed 26/26, Architecture passed 68/68, Server passed
  1,578/1,578, and the full handshake passed. This is exact behavior evidence,
  not a clean ARM64 build claim. Archive comparison found no source-content
  changes, and the scoped tree/process cleanup was clean.

R1 therefore has exact-head behavior evidence on all three target platforms.
The required fixed-SHA Fable openreview remains a separate acceptance gate;
the owner subsequently authorized R2-R7 and held that review for Fable
capacity rather than blocking implementation.

## MCP resilience R2 verification (Mac, 2026-07-17)

_Exact R2 code head `eaef85f` on `feature/mcp-resilience-r1`._

- On `nagatha.local`, the focused session-recovery suite passed 99/99, the
  Guardian suite passed 196/196, the Guardian architecture suite passed 68/68,
  and the complete server suite passed 1,693/1,693. Ten independent mutation
  points made the intended guards fail before their exact bytes were restored.
- The repository PowerShell suite passed 141 / 0 failed / 2 skipped, and the
  complete stdio handshake passed after a zero-warning, zero-error build.
- R2 remains deliberately unwired. Direct Windows and Unix process validation
  resumes in the later slices that connect this state machine to live workers.

## MCP resilience R3 verification (Mac, Windows, and Linux, 2026-07-17)

_Exact R3 apphost code/test head
`1eb69d64f6475dec79e3e4f4f36a38283df0a473`, tree
`40447c97f6b54ffc60f047dff7687155a60dae61`; the macOS full-battery test-
synchronization descendant is `d238a80`._

- On `nagatha.local`, the real copied `PtkMcpGuardian` apphost process test
  passed 2/2. It performed one initialize/initialized sequence, verified the
  exact six-tool catalog, called guardian job/state surfaces, proved JSON-only
  stdout with empty stderr, closed stdin, and observed exit zero; the invalid
  `--fake-host extra` mode proved exit 64, empty stdout, and exact bounded
  stderr. The full Guardian suite passed 264/264 and Guardian architecture
  passed 69/69. One first complete server run at `1eb69d6` passed 1,761/1,762:
  an older recovery test assumed a scheduled shutdown would publish state
  within 20 ms. Its two theory cases then passed 20/20 in isolation. Test-only
  `d238a80` replaced that scheduler sleep with a condition-based bounded wait;
  the exact descendant passed Guardian 264/264, Architecture 69/69, and Server
  1,762/1,762. The PowerShell battery passed 141 / 0 failed / 2 skipped, and
  the complete stdio handshake passed after a zero-warning, zero-error build.
- On `NETWATCH-01` (Windows 11 Pro 10.0.26200 build 26200, win-x64, .NET SDK
  10.0.302/runtime 10.0.10), a disposable exact archive matched SHA-256
  `E5CC17D04C1693B0EEE62400D5346942C79F502B4A2E5A1BFC51F4D3FCEF64E3`
  locally and remotely. The apphost process test passed 2/2, full Guardian
  passed 264/264, and Guardian architecture passed 69/69. All commands exited
  zero; the archive and disposable checkout were removed without changing an
  existing checkout or installed payload.
- On the disposable Ubuntu 26.04 ARM64 VM at `192.168.64.5` (kernel
  7.0.0-27, .NET SDK 10.0.110/runtime 10.0.10, VSTest 18.0.2), the transferred
  source reproduced the exact commit and tree above; its tar SHA-256 was
  `e558f7b3851d3cbf8e2549c017b0a7555b2fb62f5fb78bb199afff45bda18d65`.
  The apphost process test passed 2/2, full Guardian passed 264/264, and
  Guardian architecture passed 69/69, including the same stdout/stderr, EOF,
  and invalid-mode assertions. The existing ARM64 clean-build blocker
  reproduced: Grpc.Tools 2.82.0's bundled `protoc` (`libprotoc 35.0`) exits 139
  only when MSBuild invokes it through an `@responsefile`; the exact direct
  arguments exit zero. Manually generating `AuditOtlp.cs` and its protodep
  allowed the ordinary `--no-restore` behavior suites to pass. No OS package is
  missing, but this remains behavior evidence rather than a clean ARM64 build
  claim. The disposable tree was removed and the VM returned to stopped state.

R3 therefore has direct same-source apphost behavior evidence on macOS,
Windows, and Linux. Hosted CI remains unrun because this authorized local
implementation sequence does not itself authorize a push; the owner-held
fixed-head Fable review also remains separate from continued R4 work.

## MCP resilience R4 audit-transfer verification (ASHBIAMWEB1, 2026-07-21)

_Exact clean committed feature head `a21b7a1`; detached proof worktree at the
same SHA on Windows host `ASHBIAMWEB1`._

- Focused `AuditCallMetadataTests` passed 15/15 after metadata capture moved
  byte-for-byte into the guardian assembly. Guardian passed 366/366 and the
  exact guardian architecture set passed 70/70. Exact predecessor `118111c`
  passed the complete solution with Guardian 366/366, architecture 70/70, and
  server 1862/1862.
- Three exact `dotnet test server/PtkMcpServer.slnx` runs at `a21b7a1` each
  passed Guardian 366/366 and architecture 70/70, but each failed a different
  server timing/environment-sensitive assertion at 1861/1862:
  `StateToolTests.Path_drift_reports_an_entry_level_diff`,
  `SessionOperationAuthorityTests.Job_output_shaping_inherits_the_exact_wire_deadline`,
  then
  `SessionOperationAuthorityTests.Wire_deadline_bounds_cold_planning_even_when_timeout_is_huge`.
  The first two passed immediately when rerun alone; the third was not rerun
  after the repo's three-cycle stall threshold fired. These runs continued to
  report only the known NU1903 advisories for
  `System.Security.Cryptography.Xml` 10.0.6 outside the timing failures.
- No product or test guard was changed in response. The feature and detached
  proof worktrees were clean at `a21b7a1`, with no lingering `dotnet` process.
  This checkpoint needs one exact full pass after external machine load/state
  changes, or a stable reproduction and separately scoped diagnosis, before a
  complete-battery claim.
- A later exact run from clean handoff descendant `589362d` (identical
  code/test content to `a21b7a1`) passed Guardian 366/366 and architecture
  70/70, then exposed stale working-tree materialization rather than a product
  failure: all eight tracked `SiemConformance/*.base64` files physically ended
  in CRLF despite the committed `text eol=lf` attribute, so the server corpus
  guard failed at 1861/1862. Their raw bytes were mechanically normalized to
  the exact committed blob hashes and the index stat cache was refreshed with
  `git add --renormalize`; no tracked diff remained. The isolated corpus guard
  then passed 1/1.
- The immediately following exact `dotnet test server/PtkMcpServer.slnx` run
  at `589362d` passed Guardian 366/366, architecture 70/70, and server
  1862/1862. This closes the R4 checkpoint stall without changing a product or
  test guard. The run continued to report only the known NU1903 advisories for
  `System.Security.Cryptography.Xml` 10.0.6.
- The owner confirmed that the local Gitea mirror is unreachable from this
  machine and will be synchronized later from another environment; do not
  treat its absence from this clone's configured remotes as a work blocker.

## MCP resilience R5 Windows verification (ASHBIAMWEB1, 2026-07-22)

_Exact clean code/test head
`195e7e6d9fc1fafabb203213a7c13dc7aa87c16a` on
`feature/mcp-resilience-r1`; Windows Server 10.0.20348 x64, .NET SDK 10.0.302,
PowerShell 7.6.3, and Pester 5.7.1._

- Direct production-apphost tests preserve one public MCP connection while a
  real Windows-contained private host dies and recovers. They cover initial
  startup, replacement death during startup, pre-write loss, possible-write
  loss, terminal-decoded loss, effect self-termination, warm-state loss,
  ordinary native descendant cleanup, started-job terminal ownership, sealed
  output, lost-job tombstones, and an open-pipe idle interval longer than the
  injected one-second transitional watchdog. The final guardian suite passed
  431/431.
- An ambiguous real `ptk_reset` now returns nonretryable `outcome_unknown`,
  leaves the recovered default alias in `recovery_unknown`, refuses ordinary
  work, and becomes ready only after a fresh explicit reset returns an
  authoritative terminal. Three independent mutations made the direct state,
  fake-supervisor, and real-process guards fail before exact restoration.
- The first complete solution run exposed stale exact architecture inventories
  from earlier R5 commits: `GuardianHostLifecycleAudit.cs` and the
  `OpenProcess`/`WaitForSingleObject` containment-observation imports were not
  listed. Updating those exact inventories uncovered a load-bearing ban on
  the `System.Diagnostics.Process` assembly: framework `SafeProcessHandle`
  introduced that reference. The guardian retained the ban and replaced it
  with its own native safe process handle. The exact architecture suite then
  passed 72/72 and the Windows launcher suite passed 4/4.
- That first combined run also timed out one fake-host mutation case and saw a
  lifecycle-audit assertion before its record appeared while projects ran in
  parallel. The four affected cases passed immediately in isolation. The
  clean-head complete rerun then passed Guardian 431/431, architecture 72/72,
  and server 1,868/1,868. Pester passed 142 with zero failures and one expected
  platform skip. The complete stdio handshake passed every invoke, output,
  background recovery, audit, graceful cleanup, outage, and hard-kill check.
- Restore/build emitted five current NU1903 advisories for
  `System.Security.Cryptography.Xml` 10.0.6; no package change was made in the
  R5 scope. The exact advisory IDs were `GHSA-23rf-6693-g89p`,
  `GHSA-8q5v-6pqq-x66h`, `GHSA-cvvh-rhrc-wg4q`, `GHSA-g8r8-53c2-pm3f`, and
  `GHSA-mmjf-rqrv-855v`.
- Unix production containment remains unvalidated and unimplemented here.
  `wsl.exe --status` exits 1 with
  `WSL_E_WSL_OPTIONAL_COMPONENT_REQUIRED`; `gcc`, `clang`, and `docker` are
  absent from the command path. The production composition deliberately
  throws until the approved native `PtkGuardianBroker` launcher exists, so
  this machine cannot support a Unix R5-completion claim.

## Dependency hardening SQLite 3.0.4 verification (macOS/Linux/Windows, 2026-07-22)

_SQLite slice candidate based on committed head `5b4eea5`; all required
platform evidence was accepted before the family commit._

- On macOS with .NET SDK 10.0.302, `SQLitePCLRaw.bundle_e_sqlite3` 3.0.4
  restored and built, and the complete SIEM solution passed 91/91. The bundle,
  config, core, and provider all resolve to 3.0.4 in both receiver and test
  graphs. SIEM currency, deprecation, and vulnerability queries are empty.
- The exact candidate manifest has SHA-256
  `a87644c4a259f6ea0809c06964c60a739977e83a4dae457dd6508f959c6edb2b`.
  It matched byte-for-byte after transfer to `magneto` (Arch Linux x86_64,
  .NET SDK 10.0.110). A clean disposable restore/build succeeded and the full
  native SIEM suite passed 91/91. Both local and remote temporary artifacts
  were removed; neither installed payload nor an existing checkout changed.
- The same manifest hash matched the disposable source on `NETWATCH-01`
  (Windows x64, .NET SDK 10.0.302). Ordinary-account restore/build succeeded;
  its full test run reproduced the machine's established certificate-private-
  key limitation at 77/91. A transient SYSTEM scheduled task therefore ran the
  exact already-built test assembly through VSTest 18.6.0. Its 137,149-byte TRX
  recorded 91 total, 91 executed, 91 passed, zero failed, and zero skipped in
  four seconds, exercising the packaged Windows native SQLite library.
- The first incomplete SYSTEM proof attempt had created one ASP.NET development
  certificate in the SYSTEM profile. A guarded audit identified exact
  thumbprint `18562DBC043997331DF813B5DAC565F4FA89AC46`, verified the localhost
  subject, marker OID, and recent creation time, then removed only that
  certificate. The final pre-run and post-run recent-marker counts were zero.
  The exact scheduled task, Windows staging root, and transfer archive were
  removed; wildcard residue checks found no SQLite validation task, staging
  root, or local archive.
- A fresh macOS external publish resolved exactly one bundle and one provider
  identity, both 3.0.4, with only that bundle's RID-specific native files. The
  temporary publish tree was removed and no repository artifact remained.

## Dependency hardening Pester 6.0.1 verification (macOS/Linux/Windows, 2026-07-22)

_Candidate workflow based on committed SQLite head `a92bb19`. The transferred
source archive SHA-256 was
`311a7130c9341cf6b6a8f90d79aa41f2011745c1aef8e8c0ea6077960c8126bb`;
the shared test-file SHA-256 was
`c7ef98af12985c5a0a0e472ec697bdd445f06c54fdaf561a6e2b7f24505eadd8`._

- macOS loaded exact Pester 6.0.1 and passed 141/143 with the two expected
  Unix-platform skips, zero failures, and zero not-run tests.
- `magneto` (Arch Linux x64, PowerShell 7.6.3) matched both hashes, saved
  Pester 6.0.1 into a disposable module root, and passed 141/143 with the same
  two expected skips and zero failures/not-run tests. The source, module, and
  archive paths were removed.
- `NETWATCH-01` (Windows 10.0.26200 x64, PowerShell 7.6.3) matched both
  hashes, saved Pester 6.0.1 into a disposable module root, and passed 142/143
  with the one expected Windows-platform skip and zero failures/not-run tests.
  Pester's loaded DLL held the first same-process cleanup attempt open; after
  that process exited, a fresh scoped cleanup removed the exact source/module
  root and archive and proved no matching residue.
- Ruby Psych parsed `.github/workflows/ci.yml`. Structural checks found one
  exact 6.0.1 provisioner, one PSResourceGet version pin, one PowerShellGet
  required-version fallback, one exact-version import, and no stale 5.0.0
  minimum-version check. No hosted workflow result is claimed before push.

## Dependency hardening setup-dotnet v6 local verification (2026-07-22)

_Candidate workflow based on committed Pester head `64859bf`; hosted action
execution remains unauthorized and unclaimed._

- Both CI jobs resolve `actions/setup-dotnet@v6`; neither retains v5. Both
  keep `actions/checkout@v7` and the rolling `dotnet-version: 10.0.x`
  channel.
- Ruby Psych parsed the complete workflow. Structural checks counted two v6
  setup references, zero v5 references, two checkout v7 references, and two
  rolling .NET 10 declarations. A separately authorized push and one exact
  six-job hosted run remain mandatory for runtime acceptance.

## Dependency hardening final local acceptance (macOS/Linux/Windows, 2026-07-22)

_Exact clean code head `d1d24e8738fe145d473d0ed3c1de98c2acf96cf3`;
the exact-source ZIP SHA-256 was
`e0088304fe0694f6b7da882d80cc661f6961728b8d40a26ba5181740115806b8`._

- On `nagatha` (macOS 26.5.2 arm64, PowerShell 7.6.3, .NET SDK 10.0.302),
  three cold restores succeeded. All nine currency, deprecation, and
  vulnerability queries were empty. Architecture passed 73/73, Guardian
  436/436, server 1,868/1,868, SIEM 91/91, and producer conformance 6/6 with
  the mode absent plus 6/6 in-process. Exact Pester 6.0.1 passed 141 tests
  with two expected Unix skips, and the complete stdio handshake passed.
- On `magneto` (Arch Linux x64, PowerShell 7.6.3, .NET SDK 10.0.110), the
  archive hash matched, three cold restores succeeded, all nine audits were
  empty, Architecture passed 73/73, and server passed 1,868/1,868. The
  solution-level Guardian run observed the already-known publication-order
  test race once while projects contended: 435/436 passed and
  `Ready_and_recovered_hosts_are_durably_distinguished` read the recovered
  state before its audit line. The exact case passed 1/1 immediately in an
  isolated project, then the complete isolated Guardian project passed
  436/436. SIEM passed 91/91, both conformance modes passed 6/6, exact Pester
  passed 141 with two expected skips, and the handshake passed.
- On `NETWATCH-01` (Windows 10.0.26200 x64, PowerShell 7.6.3, .NET SDK
  10.0.302), three cold restores and all nine empty audits completed under the
  ordinary SSH identity; exact Pester passed 142 with one expected Windows
  skip and the handshake passed. Architecture passed 73/73 and Guardian
  436/436 under the established transient `SYSTEM` identity. The xUnit v3
  in-process runner covered the complete 1,868-test server identity set:
  1,851 passed under `NETWATCH-01\\michael`, whose 17 failures were exclusively
  the established current-user DPAPI/PKCS#12 limitation; a `SYSTEM` selection
  passed 82/82 and authoritative TRX display-name matching proved that it
  included every one of those 17 cases. The in-process runner also avoided
  VSTest testhost's legacy path boundary and passed the seven evidence-path
  cases that a preliminary direct-VSTest diagnostic could not run from the
  long service profile. SIEM passed 91/91 and both conformance modes passed
  6/6 under `SYSTEM`.
- No final log contained `NU1901`-`NU1904`. The Windows SYSTEM certificate
  audit was empty before and after the accepted run and created no certificate.
  Guarded cleanup removed the two ordinary-profile and one SYSTEM-profile
  anchored-runtime directories left by preliminary diagnostics. Final checks
  found zero scoped processes, validation roots, transfer archives, scheduled
  tasks, or exact Linux retry paths; the local transfer archive was removed.
- This is complete local cross-platform acceptance for the frozen dependency
  graph. It is not hosted proof of `actions/setup-dotnet@v6`: an authorized
  push and one six-job green GitHub Actions run at one exact SHA remain
  mandatory before that runtime claim.

## Dependency hardening first hosted run (GitHub Actions, 2026-07-22)

_Run `29967333249` at exact pushed SHA
`68c5b3495c688704e47a4b60cc2ebcd8f9339b4e`; conclusion `failure`._

- Every checkout and every `actions/setup-dotnet@v6` invocation succeeded.
  Exact Pester 6.0.1 succeeded in all three product jobs. All three SIEM jobs,
  including producer conformance, completed successfully. This directly proves
  the v6 action loads and provisions the rolling .NET 10 SDK on the current
  Ubuntu, macOS, and Windows hosted images.
- Ubuntu architecture passed 73/73 and server passed 1,868/1,868. Guardian
  failed five native-broker compilation consumers because the hosted fortified
  `read(2)` declaration rejects an ignored return value under the existing
  `-Werror` policy. The production source contains the same discarded read at
  each of its three guardian-liveness poll stages; the plan amendment requires
  consuming those results without weakening compilation or containment.
- macOS architecture passed 73/73. Guardian failed 101/436 and server failed
  3/1,868 because `Path.GetTempPath()` resolved beneath the hosted runner's
  symlink-traversing temp root. `SecureAuditStorage` and transferred-output
  storage correctly rejected that protected path. The plan amendment supplies
  a unique physical `/private/tmp` root only to the CI test step and leaves the
  product's fail-closed path validation unchanged.
- Windows architecture passed 73/73. Guardian failed two real-composition
  state observations, and server failed the same seven evidence-path cases
  that direct VSTest could not run from the long service profile on
  `NETWATCH-01`. The accepted xUnit v3 in-process Windows run covered those
  seven identities; the plan amendment builds once and invokes architecture,
  Guardian, and server assemblies directly and sequentially instead of using
  concurrent solution-level VSTest hosts.
- All three product handshakes were skipped after `Server tests` failed. No
  green six-job hosted acceptance is claimed. The corrective plan amendment is
  proposed but not approved, and a repaired exact-SHA push requires separate
  authorization.
## Mini-SIEM S3H storage-hardening verification (Mac, Windows, and Linux, 2026-07-16)

_Verified at S3H code head `c726a33` (base `1f314a2` plus the exact source
snapshots named below). S4-S6 and PTK runtime sources were not changed._

- On `nagatha.local` (Darwin arm64, .NET SDK 10.0.302/runtime 10.0.10), the
  restored final tree passed 243/243 SIEM tests with zero skips.
  `dotnet format siem/PtkSiem.slnx --no-restore --verify-no-changes` formatted
  zero files, and the direct/transitive package vulnerability audit reported
  no vulnerable package in either SIEM project. The unchanged server suite
  exited zero, and the documented Pester suite passed 141 with two platform
  skips.
- Eight independent guard mutations failed for their intended reason and were
  restored: masking away POSIX special bits; bypassing the protected config
  read; reopening TLS paths after verification; disabling the lexical
  ancestor walk; removing protected-file/storage identity intersection;
  removing final WAL namespace verification; removing both live-open database
  identity proofs; and, on Windows, accepting a one-ACE DACL with inheritance
  flags. The final restored SIEM tree passed again.
- On `NETWATCH-01` (Windows 10.0.26200.8875, .NET SDK 10.0.302/runtime
  10.0.10), an exact 355-file archive had SHA-256
  `CBD0756703DCF3837F153C864B931A1263E89FF6BB73390399657DEA6AA5BEF6`.
  Under a transient `NT AUTHORITY\\LOCAL SERVICE` scheduled task, the full
  SIEM suite passed 243/243. Removing only the `AceFlags.None` predicate made
  the inheritance-flags theory case fail while the wrong-rights case remained
  green, proving that exact-DACL branch. All disposable tasks, processes,
  archives, directories, and local transfer files were removed; scoped
  residue counts were zero, and no existing checkout or installed payload was
  touched.
- On the Ubuntu 26.04 ARM64 VM at `192.168.64.5` (kernel 7.0.0-27, .NET SDK
  10.0.110/runtime 10.0.10), the refreshed source manifest was
  `ef91ee5d470ea5d8416e6aa26d83ba67f85a4dd03b1d901fbe32690ab632b5de`
  and archive SHA-256 was
  `c36bc4d63472dfe1ecb3507af7db28ea45e95a0bd2b306821934398f9a1ab81d`.
  The ordinary build reproduced the already-recorded MSBuild-only bundled
  `protoc` exit 139. Direct invocation of that same `libprotoc 35.0` generated
  the normal intermediates, after which the unmodified suite passed 243/243
  with zero skips. All scoped directories, archives, and processes were
  removed; no existing checkout or payload changed.
- The local stdio handshake was diagnostic-only for this SIEM-only slice and
  did not pass: two default 30-second runs and one discriminating 120-second
  run received no initialize response from the unchanged server and required
  their bounded process-tree kill fallback. `server/`, `src/`, and `tests/`
  had no diff, no PTK/DOTNET override was present, the server suite exited
  zero, and the Pester suite was green, so no out-of-scope runtime repair was
  attempted. Two empty/test-content handshake roots discovered under
  `/Users/michael/.ptk` predated this run (creation 2026-07-14) and were
  preserved; the failed runs created no new retained root.

## Master-to-feature reconciliation acceptance (Mac, Windows, and Linux, 2026-07-22)

_Verified for ordinary merge commit
`0b3b0deb543eadf614f581afd680a6882b462db9`, with parents feature
`91ccf9defd688c327f916f1ccc0b49bc1d48ecea` and published master
`e4d8d1e8a3c156106d2da02287c2c38923c5199c`. Exact tree
`f0b5afcbc52fc7366f773cef980c86b629f5f524` was transferred as a
2,105,934-byte ZIP whose SHA-256 was
`1535ca9e9db4fe2cd89006a57eb5c20cef935605efbf744781a86636e7da75aa`._

- On `nagatha.local` (Darwin arm64, .NET SDK 10.0.302/runtime 10.0.10), all
  nine package queries were empty; builds were green; Architecture passed
  73/73, Guardian 442/442, server 1,917/1,917, SIEM 247/247, and both
  conformance modes 6/6. Exact Pester 6.0.1 passed 141 with two expected
  platform skips and the stdio handshake passed. The same complete battery
  passed again after commit at exact SHA `0b3b0de`. One precommit server run
  found the escaped-orphan fixture's child already absent and left two exact
  disposable PIDs; guarded cleanup terminated those PIDs, the identity passed
  20/20 focused iterations, and the full server retry passed 1,917/1,917.
- On `magneto` (Arch Linux x86_64, .NET SDK 10.0.110/runtime 10.0.10), the
  transferred archive hash matched. All nine package queries and both builds
  were green. Architecture passed 73/73, Guardian 442/442, server
  1,917/1,917, SIEM 247/247, and both conformance modes 6/6. The audit-snapshot
  identity that had failed with concurrent `List` enumeration passed 25/25
  focused iterations before the full Guardian pass; the adjacent bounded
  containment identity also passed. Pester 6.0.1 was saved only beneath the
  disposable validation root, passed 141 with two expected skips, and the
  handshake passed.
- On `NETWATCH-01` (Windows 10.0.26200 x64, .NET SDK 10.0.302/runtime
  10.0.10), the transferred archive hash matched and every package query was
  clean. Under `NETWATCH-01\\michael`, Pester passed 142 with one expected
  skip, the handshake passed, Architecture passed 73/73, Guardian passed
  442/442, and the direct server run executed all 1,917 identities: 1,900
  passed and the only 17 failures were the established current-user
  DPAPI/PKCS#12 cases. A transient `NT AUTHORITY\\SYSTEM` task passed an
  83-test selection and authoritative TRX matching proved coverage of every
  one of those 17 identities. SYSTEM also passed SIEM 247/247 and both
  conformance modes 6/6. The SYSTEM certificate delta was zero.
- Cross-history validation exposed five integration defects and proved each
  red-to-green: Windows Job Object escalation retained ownership after a
  failed kill; conformance fixtures protected their TLS material; the
  independent containment observer reacted without a duplicate grace delay;
  Windows wrong-owner tests selected a SID foreign to the actual runner; and
  the in-memory audit sink serialized state and returned line snapshots. The
  last fix changed only the deterministic in-memory/fake composition, not the
  production durable audit spool.
- Guarded cleanup shut down scoped build servers and removed every enumerated
  validation root, archive, result directory, log, transient module, and
  scheduled task. Final local, Linux, and Windows probes each reported zero
  scoped artifacts and zero scoped processes; Windows also reported zero
  matching tasks. No installed payload or pre-existing checkout was changed.
