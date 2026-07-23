# rbc-8: WorkerServer initialize handshake is a fragile multi-arm Task.WhenAny

**Severity**: MINOR (downgraded from MAJOR at 2026-07-19 triage)
**Status**: Triaged 2026-07-19 — downgraded to MINOR and deferred. No demonstrated defect: the `queuedEnvelope` carry with post-initialize replay (`eor_during_initialize`/`eor_after_initialize`) already handles the feared interleaving structurally. Targeted drain-replay guard test queued to the worker-subsystem pass; no refactor. Contested by maintaining agent (owner-delegated triage); codex consensus AGREE (thread `019f7cb9-c587-79c1-994b-a28e8d7b1ba1`, turn 1/3).
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/Worker/WorkerServer.cs:121-324`

## Evidence

The `RunProtocolAsync` loop has multiple `Task.WhenAny` races between
`ownership.PendingRead`, `ownership.DeadlineTask`,
`ownership.HostCancellation`, and `ownership.FactoryTask`. The
`PendingRead` is reassigned at lines 132, 163, and consumed via
`TakePendingReadAsync` (line 137, 170, 232, 291).

The logic at lines 168-178 checks `if (ownership.PendingRead.IsCompleted)`
and calls `TakePendingReadAsync`. After the factory completes
(line 258-263), the loop re-checks `PendingRead.IsCompleted` at line 289.

The current logic appears correct — a read that completes during factory
construction is either drained inside the loop or caught by the
post-loop block at 289-299. But the correctness depends on the exact
ordering of `WhenAny` arms, and any future edit that reorders them risks
losing an envelope (a queued request frame that the worker never
processes).

## Predicted observable failure

A future refactor that reorders the `WhenAny` arms or changes the
`PendingRead` reassignment timing drops a queued request envelope
silently. The client sees a request that never gets a response, and
the worker moves on as if the queue is empty.

## What

Extract the handshake into a single state machine with explicit states
(initializing, awaiting-read, factory-running, draining) instead of
interleaved `WhenAny` races, OR add a comprehensive test that covers
every interleaving (factory completes while read pending, read
completes while factory running, both complete simultaneously, host
cancellation during factory) and pin it as a regression guard.

## Scope of fix

`WorkerServer.cs` — either a refactor (larger) or a test-only guard
(smaller). Depends on the owner's appetite for a protocol-layer change.

## Guard proof

Not yet written. A guard should cover every `WhenAny` interleaving and
assert no envelope is lost. The state-machine refactor would make the
guard structural rather than exhaustive.

## Reviewer comments

Read-only review by Hermes subagent (execution/worker subsystem pass).
No external fixed-SHA review has been dispatched.