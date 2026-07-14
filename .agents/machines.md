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
