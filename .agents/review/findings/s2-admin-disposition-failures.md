# s2-admin-disposition-failures: classify audited disposition failures

**Severity**: HIGH — a generic failure record could conceal whether a loss
disposition advanced its checkpoint or published completion authority.
**Status**: Verified
**Branch**: `fix/s2-admin-disposition-failure-classification`
**Commit**: `7e982ce4a4008c2ba5fee850ed890c8a78193550`

## Evidence

`server/PtkMcpServer/Audit/AuditAdminOperations.cs` previously emitted
`operation.failed` for every disposition failure, before and after durable
checkpoint effects.

## Predicted observable failure

Local-log and SIEM investigators could not distinguish invalid input/mode,
live or invalid target controls, absent/ineligible blocks, proof conflicts,
invalid durable intent/outcome controls, checkpoint advance without terminal
audit, or terminal audit without the durable completion receipt.

## What

Disposition administration now uses typed failure categories, explicit
operation stages, and checkpoint/completion/receipt flags. Classification never
parses exception messages.

## Approach

The fix audits semantically invalid target UUIDs as a no-control-access
rejection, moves valid local-only attempts behind a durable intent, preserves
typed intent/outcome control failures, and distinguishes the post-checkpoint
and post-completion-receipt windows. Existing idempotent retry and fail-closed
checkpoint rules remain authoritative.

## Files changed

- `server/PtkMcpServer/Audit/AuditAdminDispositionFailure.cs` — categories/codes.
- `server/PtkMcpServer/Audit/AuditAdminOperations.cs` — stages and effect flags.
- `server/PtkMcpServer/Audit/AuditOperatorDispositionIntent.cs` — typed controls.
- `server/PtkMcpServer/Audit/AuditOperatorDispositionOutcome.cs` — typed receipts.
- `server/PtkMcpServer.Tests/AuditOperatorDispositionTests.cs` — all-code guards.

## Guard proof

- Mapping `BlockAbsent` to `target_control_invalid` made the retired-target guard
  fail; restoration passed.
- Mapping `CheckpointAdvancedReceiptMissing` to
  `AuditOutcomeFailedAfterCheckpoint` made
  `Failure_after_completed_append_is_audited_and_retry_commits_the_receipt`
  fail; restoration passed focused 22/22 and full 877/877.

## Coder dispute (if any)

None.

## Known gaps

None material. Eligible failure classes are checked both at the admin boundary
and checkpoint authority; drift can misclassify a future enum value but cannot
authorize an ineligible checkpoint advance.

## Reviewer comments

Claude Code 2.1.207 (`claude-fable-5`), reviewed
`7e982ce4a4008c2ba5fee850ed890c8a78193550` against
`a5420993147413a60fcd9cc52ba60d920eb609b2`,
`guard_confirmed=true`, verdict `accepted`, 2026-07-12T09:38:37Z. Claude
independently reproduced the post-completion classification failure, restored
production byte-exactly, passed focused 22/22 and full 877/877, and removed its
clean disposable worktree. No material finding was returned. Its non-blocking
comments note the duplicated eligibility set, the shared invalid/incomplete
outcome-control code, and an unverifiable post-replace checkpoint read window;
all remain fail-closed and recover idempotently.
