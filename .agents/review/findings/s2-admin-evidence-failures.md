# s2-admin-evidence-failures: classify audited evidence administration failures

**Severity**: HIGH — generic failure records could conceal whether protected
script bytes were absent, disclosed, or published.
**Status**: Verified
**Branch**: `fix/s2-admin-evidence-failure-classification`
**Commit**: `adb8b4b0f02118ce20c7f75a36fe4341e28e2646`

## Evidence

`server/PtkMcpServer/Audit/AuditAdminOperations.cs` previously emitted generic
failure detail codes for evidence-storage, destination, and post-effect audit
failures. `ScriptEvidenceStore.ReadExact` also wrapped destination consumer
failures as evidence-storage failures.

## Predicted observable failure

Local-log and SIEM investigators could not distinguish invalid input, absent or
invalid protected evidence, destination refusal/collision, partial disclosure,
complete disclosure, or publication followed by terminal-audit failure.

## What

Evidence administration now uses closed typed failure categories and explicit
write/flush/publication state. It never classifies by exception-message text.

## Approach

The fix adds exhaustive detail-code mapping, preserves typed evidence control
and storage failures, keeps the external byte consumer outside the storage
exception wrapper, and stages protected-export failures by destination and
publication state. Failure records carry known digest/byte facts without core
audit plaintext.

## Files changed

- `server/PtkMcpServer/Audit/AuditAdminFailure.cs` — closed categories and codes.
- `server/PtkMcpServer/Audit/AuditAdminOperations.cs` — effect-state classifier.
- `server/PtkMcpServer/Audit/ScriptEvidenceStore.cs` — typed evidence failures.
- `server/PtkMcpServer.Tests/AuditAdminEvidenceAccessTests.cs` — outcome guards.

## Guard proof

- Mapping `EvidenceAbsent` to `evidence.storage_failed` made
  `Missing_evidence_records_failure_after_intent_without_writing_bytes` fail;
  restoration passed.
- Removing the post-flush branch made
  `Read_failure_after_flush_records_failed_audit_outcome_after_disclosure` fail;
  restoration passed the focused 16/16 and full 873/873 suites.

## Coder dispute (if any)

None.

## Known gaps

None material. A destination created concurrently with an unrelated publish I/O
failure can cause the best-effort `destination_exists` classification because
the platform exposes no stronger no-message-parsing distinction.

## Reviewer comments

Claude Code 2.1.207 (`claude-fable-5`), reviewed
`adb8b4b0f02118ce20c7f75a36fe4341e28e2646` against
`b87922189eb4c7b068c4bd9b5390d6700c040a28`,
`guard_confirmed=true`, verdict `accepted`, 2026-07-12T09:24:35Z. Claude
independently reproduced both mutations, restored byte-exact production state,
passed focused 16/16 and full 873/873, and removed its clean disposable
worktree. No material finding was returned.
