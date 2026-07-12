# s2-anchored-temp-recovery: recover bounded crash-left spool temporaries

**Severity**: HIGH — a crash during rotation can permanently prevent anchored
startup and its out-of-band administration path.
**Status**: In progress
**Branch**: `fix/s2-anchored-temp-recovery`
**Commit**: `622c4c88750a2c8b24f0189479f32a65171f5a2f`

## Evidence

`server/PtkMcpServer/Audit/FileAuditJournalSink.cs` creates protected
`.allocating` rotation temporaries and macOS compaction temporaries before
atomic publication. Local-only startup enables bounded temporary recovery, but
anchored writer preparation disables it. `server/PtkMcpServer/Audit/
AuditAnchoredWriterPreparation.cs` rejects every non-segment spool entry as
unknown before the writer or `ptk-audit-admin` can open.

## Predicted observable failure

A hard process death after creating a valid protected rotation temporary but
before publication leaves that name in the spool. Every anchored restart and
the audit administration executable then fail preflight until a human manually
deletes a file inside the protected audit root.

## What

Teach anchored preflight to recognize and safely recover only the same bounded,
canonical crash-left allocation and compaction temporaries that the journal
sink itself can create. Unknown, malformed, linked, or unprotected entries must
continue to fail closed.

## Approach

Refactor local and anchored startup onto one bounded temporary-recovery path.
Inventory and validate the complete spool before deleting anything, retain the
canonical temporary identities under the quota handle, then delete only valid,
protected, zero-length allocation temporaries or compaction temporaries whose
protected source segment still exists. Anchored startup invokes that recovery
while the root and quota identities remain pinned, before topology validation.

## Files changed

- `server/PtkMcpServer/Audit/FileAuditJournalSink.cs`
- `server/PtkMcpServer/Audit/AuditAnchoredWriterPreparation.cs`
- `server/PtkMcpServer.Tests/AuditAnchoredWriterPreparationTests.cs`

## Guard proof

- At `622c4c88750a2c8b24f0189479f32a65171f5a2f`, temporarily removing the
  anchored preflight recovery call made both new canonical allocation and
  compaction restart tests fail at the former unknown-entry refusal. Restoring
  the call made the focused startup suite pass.
- Full macOS .NET verification passed 926/926.
- The exact committed SHA passed the focused anchored writer and journal sink
  suite on Windows 72/72 in a disposable worktree.

## Coder dispute (if any)

None.

## Known gaps

Recovery must not turn the anchored preflight into a general spool cleanup
mechanism or delete any possibly published segment.

## Reviewer comments

Claude Code 2.1.207 (`claude-fable-5`) reviewed fixed head
`6cbd1d3061985f06bb0a5da8bcf2faa84a5bb826` against
`78e256ca0f3b1253aa97dd984f1d913429ea452a`, `guard_confirmed=true`, verdict
`reopened`, recorded 2026-07-12T14:33:24Z. The reviewer traced a hard death
between protected temporary creation and atomic publication into a permanent
anchored preflight refusal shared by normal and administrative startup.
