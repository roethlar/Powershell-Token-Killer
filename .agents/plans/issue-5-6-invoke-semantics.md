# Plan: issues #5/#6 — stderr labeling + timeout/queue semantics

**Status:** APPROVED by owner 2026-07-10 (in-session). The approved scope,
in the owner's terms: (1) stop marking successful commands' side-messages
as errors; (2) make timeouts real — a call always comes back within its
limit, and the 2026-07-10 incident (a `timeoutSeconds=900` call that never
responded at all; server found dead) is root-caused FIRST, as slice 0,
before the semantics work; (3) the status check always answers, even
mid-call; (4) one total timeout number, not separate queue/execution
knobs. Owner triage 2026-07-09 (recorded in `.agents/state.md`): #5/#6 are
the next batch after shell-dialect. The issue texts are the spec; both
were verified against current source before drafting.

## Slice 0 (added at approval) — root-cause the never-returned call

Live incident, this box, 2026-07-10: a foreground `ptk_invoke` running a
long native child (`codex exec`) with `timeoutSeconds=900` produced no
response for 2018s wall-clock, at which point the MCP client aborted; a
subsequent `ptk_state` also never responded (aborted at 1817s); the
server process was later absent from the process table.

**ROOT CAUSE (investigated 2026-07-10, evidence: the harness MCP log
`mcp-logs-ptk/2026-07-10T04-58-12-892Z.jsonl` and `pmset -g log`):
system sleep, not a server defect.** The dispatch landed at 06:59:52
local inside a 45-second maintenance DarkWake; the machine re-slept 17
seconds later and stayed asleep (short DarkWake slivers aside) until the
lid opened at 08:25:39. `Task.Delay` runs on a monotonic clock that
stops during sleep, so the 900s execution timeout accumulated only the
few awake minutes and legitimately never came due; the MCP client's
1800s patience is wall-clock, so it gave up first; `ptk_state` then
queued behind the genuinely-held gate (the #6b defect, observed live);
the orphaned codex child wrote its last bytes during the 08:20 DarkWake.
No timer failed and nothing crashed — the server promises seconds, the
caller experiences wall time, and sleep drives them apart.

**Fix (folds into slices 2-3):** the call budget is a WALL-CLOCK
deadline (`DateTimeOffset`), re-checked when timers fire — the
IdleWatchdog's existing recompute-on-wake loop is the model — so a call
that slept past its deadline answers promptly on the next wake instead
of silently extending. Slice 3 is the diagnostic half: a zero-wait busy
report answers within a DarkWake sliver. Guard: a deadline computed in
the past (or a clock-jump simulation via an injected clock/short
deadline) must produce the timeout response on the next timer check;
exact seam chosen at implementation.

## Problem

Two field reports from live macOS use, both server-side
(`server/PtkMcpServer`), module untouched:

**#5 — successful native stderr labeled `[errors]`.**
`RunspaceHost.InvokeAsync` stringifies every `ps.Streams.Error` record into
the flat `InvokeResult.Errors` (`RunspaceHost.cs:569`), and
`InvokeTool.Invoke` renders any nonempty collection under `[errors]`
(`InvokeTool.cs:112-117`). Native programs write ordinary progress and
diagnostics to stderr while exiting 0 (`python -m unittest -v` is the
canonical case), so an exit-0 call reads as failed and an agent diagnoses or
reruns a defect that does not exist. The issue's follow-up probe confirms
the split is feasible upstream: native stderr arrives as records whose
FullyQualifiedErrorId is `NativeCommandError` (first record) /
`NativeCommandErrorMessage` (continuations), distinguishable from genuine
`Write-Error`/exception records before stringification — metadata the
current `CollectErrors(ps.Streams.Error.Select(e => e.ToString()))`
discards.

**#6 — `timeoutSeconds` excludes queue wait; `ptk_state` blocks behind a
busy runspace.** `InvokeAsync` awaits the serialization gate
(`RunspaceHost.cs:473`) before the timeout machinery starts
(`RunspaceHost.cs:514`), so a queued call's wall-clock exceeds its requested
budget without bound and still executes afterward. `StateTool.State` runs
its probe through the same gate (`StateTool.cs:39`), so the advertised
health diagnostic blocks behind exactly the workload it exists to diagnose.
Serialization itself is intentional and stays.

## Design

**#5 — partition, don't reinterpret.** The error stream is split by record
identity, not guessed from exit codes: exit code alone is insufficient
because non-terminating PowerShell errors legitimately coexist with
success (issue text). Native stderr keeps a neutral label; real PowerShell
error records keep `[errors]`.

**#6 — one budget, total wall-clock.** Of the issue's two options, this
plan picks (1): `timeoutSeconds` (and the server default) is a total
wall-clock budget covering queue wait plus execution. Rationale: one number
an agent can reason about ("this call costs at most N seconds of my turn"),
it matches the measured complaint (wall-clock exceeded the requested
budget), and it adds no second knob to document. A queued call whose budget
expires fails fast without executing and without touching the active
caller's runspace. The owner can veto for option (2) (separate queue/exec
budgets) at approval time.

## Slices

1. **#5 — neutral stderr channel.** `InvokeResult` gains a `Stderr`
   collection; `InvokeAsync` partitions error records before
   stringification, on both the success and the `RuntimeException`
   paths: native-stderr records → `Stderr`, everything else → `Errors`.
   The predicate is compound provenance, not FullyQualifiedErrorId
   alone: the FQID is forgeable — verified live in this session,
   `Write-Error -ErrorId NativeCommandError -Message boom` produces FQID
   exactly `NativeCommandError` (Exception type `WriteErrorException`) —
   so a forged record would silently lose its `[errors]` label under an
   FQID-only split. Exception type is NOT a valid second factor either:
   `Write-Error -Exception ([RemoteException]::new(...))` forges the
   native record's exception type just as freely. The discriminator must
   be invocation provenance, which Write-Error's parameters cannot
   reach: a real native record carries
   `InvocationInfo.MyCommand.CommandType -eq Application` (verified live
   in a hosted runspace: native = FQID `NativeCommandError` +
   `RemoteException` + CommandType `Application`; a Write-Error record's
   MyCommand is the cmdlet), re-probed at implementation time. Both
   collision cases — forged `-ErrorId` and forged `-Exception` — go in
   the test matrix. The rtk-nag filter
   (`CollectErrors`) moves with the banner to the stderr channel (the nag
   IS native stderr). `InvokeTool` renders `[stderr]` as its own section;
   `[errors]` is reserved for genuine error records. The labels attach at
   the tool layer after raw/shaped handling, so raw=true/false and
   route=pwsh/auto behave consistently by construction — asserted anyway.
   `StateTool`'s probe-error and cache gates key on `Errors` and are
   unaffected by design (a native-stderr line must no longer be able to
   block the listAvailable cache or flag the state probe).
   The documented output contract moves with the code: the
   `server/README.md` section that enumerates the labeled sections
   (`[exit]`/`[errors]`/`[warnings]` today) gains `[stderr]`, its meaning
   (native stderr, neutral — not a failure signal), and its position.
   The README is the canonical location for that contract; the completed
   greenfield-design plan's label list is a historical record and is not
   maintained (one-canonical-location invariant).
   The `RuntimeException` catch path today never reads `LASTEXITCODE`
   (only the success path does, `RunspaceHost.cs:565`), so under
   `$PSNativeCommandUseErrorActionPreference = $true` a native command
   that writes stderr and exits nonzero would keep its diagnostics but
   drop `[exit] N`, failing the matrix below — the catch path captures
   the exit code too.
   Regression matrix (from the issue): exit-0 native stderr → `[stderr]`,
   no `[errors]`; nonzero exit → `[stderr]` alongside `[exit] N` —
   including via the terminating-native-preference catch path (i56p-6);
   `Write-Error` → `[errors]`, including with a forged
   `-ErrorId NativeCommandError`, with a forged
   `-Exception RemoteException`, and with BOTH forged in one call —
   `Write-Error -ErrorId NativeCommandError -Exception
   ([System.Management.Automation.RemoteException]::new('boom'))` must
   render `[errors]` and no `[stderr]`, or an FQID+exception classifier
   passes the isolated cases while missing the combined forgery (i56p-5,
   i56p-9, i56p-11); consistency across raw and route values.

2. **#6a — queue wait inside the budget.** `InvokeAsync` starts the budget
   clock at entry: the gate wait becomes a bounded
   `WaitAsync(budget)`; expiry before acquisition returns a fast labeled
   failure — script never executed, warm state untouched, active caller
   not canceled or recycled — whose message names the busy cause and both
   recovery paths (retry / background=true), distinct from the existing
   ran-too-long recycle message. After acquisition, the remaining budget
   (total minus queue wait) bounds everything the call runs while holding
   the gate — the preflight pipelines (dialect check, `ResolveScript`,
   exit-code bookkeeping, `RunspaceHost.cs:489,500,506`) as well as the
   main pipeline: preflight uses blocking `ps.Invoke()` today, and a
   session-shadowed helper or a wedged rtk child can hang it exactly like
   the fixed ShapeTextAsync wedge (d3-1). A preflight overrun is a wedged
   warm pipeline and recycles like an execution timeout; the
   recycle-on-timeout behavior is otherwise unchanged. `ShapeTextAsync`
   (job polls hold the same gate) gets the same bounded wait and returns
   the text unshaped on expiry — shaping must never fail a poll. The
   request budget is established at the tool boundary, BEFORE the
   foreground/background branch: the background pre-start steps — the
   cold-table dialect check and the warm-gate cwd probe
   (`InvokeTool.cs:85,89`), which today run under the server default
   regardless of the caller's `timeoutSeconds` — run under the same
   remaining budget, so a background retry after a queue expiry cannot
   itself block for the 300s default, and a request whose budget expires
   pre-start returns the fast busy failure with no job started. The cwd
   probe's busy expiry must fail the request, never degrade to a
   null-cwd start: `JobManager` sets `WorkingDirectory` only when it gets
   a path (`JobManager.cs`), so a silent fallback would run a queued
   build in whatever directory the server process happened to start in —
   the wrong project — contradicting the documented session-cwd contract
   in `server/README.md`. A failed start with retry guidance is
   recoverable; a wrong-directory build is not.
   The model-visible texts move with the semantics, or they become
   false: the ptk_invoke tool description currently states that a call
   exceeding the timeout "is aborted and the runspace recycled"
   (`InvokeTool.cs:23-25`) — after this slice that is true only of
   execution overrun, and an agent reading it after a queue expiry would
   wrongly rebuild warm connections it never lost. The tool description,
   the `timeoutSeconds` parameter description (`InvokeTool.cs:50-53`),
   and the `server/README.md` timeout section all state the total
   wall-clock semantics and distinguish the two outcomes: queue expiry =
   never executed, warm state intact; execution timeout = the owning
   call's runspace recycled.
   Regression coverage (from the issue): a queued call whose budget
   expires never executes later (observable side effect asserted absent);
   execution timeout still recycles only the call that owns the runspace;
   queue-expiry leaves the active call's warm state intact; a
   slow-shadowed preflight helper cannot hold a small-budget call past
   its deadline (i56p-1); a busy-expired background request starts no job
   at all — in particular none in the server process cwd (i56p-4).

3. **#6b — prompt `ptk_state` under load.** `RunspaceHost` maintains a
   busy snapshot around the gate (active-call start time, waiter count)
   and exposes a zero-wait try-acquire path for state probes. `StateTool`
   never queues: each of its runspace probes — the main probe and the
   uncached `listAvailable` probe (`StateTool.cs:39,95`) — runs only if
   the try-acquire wins the gate atomically; a failed try-acquire IS the
   busy signal, not a separate check racing a separate wait (a
   snapshot-read-then-queue design would let a long call slip in between
   and block ptk_state for the default budget). On busy it reports the
   host-level facts that need no runspace (pid, uptime, shaping, raw
   count, jobs, env drift) plus a
   `runspace: busy (active call running Xs, N waiting)` line and marks
   runspace-dependent details unavailable. When the try-acquire wins,
   current behavior. A served busy report is user activity: the fast
   path stamps `LastActivityUtc` even though it never enters
   `InvokeAsync` (today the stamp lives there,
   `RunspaceHost.cs:472`), or the idle watchdog could stop the server
   right after it answered — the same class as the fixed sd2-3
   background-refusal skip. Regression: a busy-path state call refreshes
   the idle clock. Queue-wait and execution time become independently
   observable: the busy line carries the active call's age, and slice 2's
   fast-fail message carries the queue wait it spent. Regression: state
   returns promptly with the busy indicator both when dispatched during
   an established long call and when the long call wins the gate between
   two state probes (the listAvailable leg).

Out of scope: `JobManager` internals (background jobs are already the
escape hatch for long stateless work; job-log stderr handling is a
different surface), any module-side change, any change to the intentional
one-pipeline-at-a-time serialization, and bounding `ResetAsync`'s gate
wait (ptk_reset behind a busy call is rare and semantically "after the
current call" — reconsider only if field use contradicts).

## Verification

dotnet suite with guard tests per slice, red-leg proven per the playbook
(`.agents/playbooks/reviewloop.md`); Pester battery re-run (module
untouched — counts must stay canonical); handshake script (server-facing
change). Live checks mirroring the issue repros over real MCP stdio:
python-style exit-0 stderr renders `[stderr]`, and the issue-6 controlled
repro (long call + concurrent ptk_state + 1s-budget invoke) shows the
prompt busy report and the fast queue failure. Codex loop per the recorded
process; issues #5 and #6 get fix references and closure after the owner
pushes.
