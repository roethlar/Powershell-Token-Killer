# Plan: issues #5/#6 — stderr labeling + timeout/queue semantics

**Status:** DRAFT awaiting owner approval. Owner triage 2026-07-09 (recorded
in `.agents/state.md`): #5/#6 are the next batch after shell-dialect. The
issue texts are the spec; both were verified against current source before
drafting.

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
   collection; `InvokeAsync` partitions error records by
   FullyQualifiedErrorId (`NativeCommandError`/`NativeCommandErrorMessage`
   → `Stderr`, everything else → `Errors`) before stringification, on both
   the success and the `RuntimeException` paths. The rtk-nag filter
   (`CollectErrors`) moves with the banner to the stderr channel (the nag
   IS native stderr). `InvokeTool` renders `[stderr]` as its own section;
   `[errors]` is reserved for genuine error records. The labels attach at
   the tool layer after raw/shaped handling, so raw=true/false and
   route=pwsh/auto behave consistently by construction — asserted anyway.
   `StateTool`'s probe-error and cache gates key on `Errors` and are
   unaffected by design (a native-stderr line must no longer be able to
   block the listAvailable cache or flag the state probe).
   Regression matrix (from the issue): exit-0 native stderr → `[stderr]`,
   no `[errors]`; nonzero exit → `[stderr]` alongside `[exit] N`;
   `Write-Error` → `[errors]`; consistency across raw and route values.

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
   recycle-on-timeout behavior is otherwise unchanged. `ShapeTextAsync` (job polls hold the
   same gate) gets the same bounded wait and returns the text unshaped on
   expiry — shaping must never fail a poll. Consequence accepted:
   `TryGetCurrentLocationAsync` (background job start) inherits the fast-
   fail and starts the job with the cold default cwd instead of blocking
   indefinitely behind a long foreground call.
   Regression coverage (from the issue): a queued call whose budget
   expires never executes later (observable side effect asserted absent);
   execution timeout still recycles only the call that owns the runspace;
   queue-expiry leaves the active call's warm state intact; a
   slow-shadowed preflight helper cannot hold a small-budget call past
   its deadline (i56p-1).

3. **#6b — prompt `ptk_state` under load.** `RunspaceHost` maintains a
   lock-free busy snapshot around the gate (busy flag, active-call start
   time, waiter count). `StateTool` consults it first: when busy, it
   reports the host-level facts that need no runspace (pid, uptime,
   shaping, raw count, jobs, env drift) plus a
   `runspace: busy (active call running Xs, N waiting)` line and marks
   runspace-dependent details unavailable — it does not queue. When idle,
   current behavior. Queue-wait and execution time become independently
   observable: the busy line carries the active call's age, and slice 2's
   fast-fail message carries the queue wait it spent.

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
