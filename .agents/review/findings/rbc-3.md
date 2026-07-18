# rbc-3: AuditRuntimeGate TryCreateCallContext bypasses the lifecycle gate

**Severity**: MAJOR
**Status**: RESOLVED — refuted at triage 2026-07-18: the claimed missing check
exists at the reviewed head `f6a2caa` and has existed since the file's first
commit (`460c106`). No code change. Refutation independently verified and
**accepted** by external fixed-SHA review (codex, 2026-07-18T05:24:20Z).
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/Audit/AuditRuntimeGate.cs:107-146`

## Evidence

`TryCreateCallContext` returns an `AuditCallContext` after only checking
`_disposed || _stopping`, never requiring `_lifecycle?.IsStarted == true`.
This differs from `TryBeginCall` (line 217) and `CanConstructRuntimeLocked`
(line 383), both of which enforce the lifecycle gate.

Callers of `TryCreateCallContext` can therefore obtain a context and
append audit records before `server.started` is durable, bypassing the
lifecycle invariant the rest of the gate is built around.

## Predicted observable failure

Audit records are appended out of lifecycle order — before
`server.started` — making the journal chain inconsistent for any
consumer that treats `server.started` as the anchor for the session's
audited activity.

## What

Either enforce the lifecycle gate in `TryCreateCallContext` (add the
`_lifecycle?.IsStarted != true` check that `TryBeginCall` enforces), or
document at the method and every call site that this is a diagnostic-only
path that must never be used for effectful audit records. If it is only
used by `ptk_state`, enforce that at the call site.

## Scope of fix

One method in `AuditRuntimeGate.cs`, plus call-site verification. No
architectural change.

## Guard proof

Already present in the suite (verified at triage, no new test needed):

- `AuditRuntimeGateTests.cs:393`
  (`Diagnostic_only_shutdown_is_idempotent_and_never_reopens_storage`) —
  `TryCreateCallContext` refuses after failed startup + stop, i.e. with no
  durable `server.started`.
- `AuditEvidenceOrphanReconcilerTests.cs:175` and `:238` — refusal while
  startup reconciliation holds the runtime `Unavailable` (no durable
  `server.started` yet); admission at `:180`/`:245` only after the blocker
  clears and the full serialized startup — including the durable
  `server.started` append — completes inside the same call.

## Triage resolution (2026-07-18, head `a4cff13`)

The Evidence section is a misread. `TryCreateCallContext` performs **two**
`_gate` checks: the early fast-path (`_disposed || _stopping` only, lines
116–124) and a second check after `TryInitializeSerialized` (lines 133–140)
that enforces exactly the clause the finding says is absent:

    if (_disposed || _stopping ||
        (!_testOperational && _lifecycle?.IsStarted != true))

- `git show f6a2caa:…/AuditRuntimeGate.cs` confirms the clause at the
  reviewed head; `git log -L` attributes it to `460c106`
  ("feat: add fail-closed audit foundation") — it was never missing.
- The invariant is also structural: `_lifecycle`/`_journal` are published
  together (under `_gate`) only after `candidateLifecycle.EnsureStarted()`
  returns, and `AuditServerLifecycle` sets `_state = Started` only after the
  `server.started` append succeeds. There is no reachable state with a
  non-null `_journal` and an un-started lifecycle.
- `_testOperational` is only set by the `internal static`
  `CreateOperationalForTests` factory, referenced solely from
  `PtkMcpServer.Tests` (`AuditCallFilterTests.cs:375`,
  `AuditPreEffectGuardTests.cs:2049`). No production path can bypass the
  gate.

The likely source of the misread: reviewing the method's opening block
(lines 107–124) and stopping at the first `return`, missing the post-init
re-check.

## Reviewer comments

Read-only review by Hermes subagent (audit subsystem pass). Triage refutation
performed in-session against both the reviewed head and current `master`.

### External refutation review (dispatched, accepted)

Reviewer: codex / gpt-5.6-sol / high / standard
Harness: codex-cli 0.144.5 (headless `codex exec --sandbox read-only --json`,
model/effort pinned at dispatch; JSONL envelope + `--output-schema` payload)
Reviewed head SHA: 3a5667263f38b2c849087f0ef2da96b1bb6740c8
Base SHA: a4cff131711af0ac3d3f2e86307980a920f56117 (docs-only diff)
guard_confirmed: true (reviewer re-derived the git evidence itself:
`git show` at `f6a2caa` and head, `_testOperational` reference search,
cited guard tests located and read)
Verdict: **accepted** — refutation correct and complete; the reviewer
additionally cleared the alternate-path blind spot (no pre-start journal
access via `TryInitializeSerialized`, `AuditRuntimeResources.Journal`, or
any `AuditCallContext` construction site).
Timestamp: 2026-07-18T05:24:20Z
Escalation: none (T1-T5 unmatched; standard at high)
Record committed: yes (this commit)

Reviewer comments (verbatim):
- server/PtkMcpServer/Audit/AuditRuntimeGate.cs:133 — At f6a2caa and the reviewed head, the sole successful production return follows the second gate requiring IsStarted; all earlier returns refuse or throw.
- server/PtkMcpServer/Audit/AuditRuntimeGate.cs:67 — _testOperational is assigned only by the private constructor reached through CreateOperationalForTests; repository references are confined to two test files.
- server/PtkMcpServer/Audit/AuditRuntimeGate.cs:443 — TryInitializeSerialized calls EnsureStarted before publishing _journal and _lifecycle together; failure publishes neither.
- server/PtkMcpServer/Audit/AuditServerLifecycle.cs:86 — server.started is appended before state becomes Started at line 93; AuditJournal flushes the append to disk before returning.
- server/PtkMcpServer/Audit/AuditRuntimeResources.cs:33 — The only production consumer of the exposed Journal property is TryInitializeSerialized, and production AuditCallContext construction occurs only in the lifecycle-gated runtime methods; no alternate pre-start access path exists.
- server/PtkMcpServer.Tests/AuditRuntimeGateTests.cs:393 — The named diagnostic-only shutdown test exists and asserts refusal after startup failure, when no server.started was durable.
- server/PtkMcpServer.Tests/AuditEvidenceOrphanReconcilerTests.cs:175 — Local reconciliation asserts refusal while startup remains unavailable and admission at line 180 only after serialized startup can complete.
- server/PtkMcpServer.Tests/AuditEvidenceOrphanReconcilerTests.cs:238 — Anchored reconciliation likewise asserts refusal before startup proof and admission at line 245 only after the blocker clears.