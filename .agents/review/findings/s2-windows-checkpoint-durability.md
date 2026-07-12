# s2-windows-checkpoint-durability: flush replaced checkpoint contents

**Severity**: HIGH — a Windows power loss can revert a checkpoint already used
to authorize retention, causing duplicate export or permanent chain-adoption
failure after acknowledged data was deleted.
**Status**: In progress
**Branch**: `fix/s2-windows-checkpoint-durability`
**Commit**: `e56d9f2d1b5efc7366be5809d4355b6c3ba6c47f`

## Evidence

`server/PtkMcpServer/Audit/SecureAuditStorage.cs:263-301` replaces protected
files atomically, but the Windows `SetFileInformationByHandle(FileRenameInfoEx)`
path at `SecureAuditStorage.cs:1238-1282` does not call `FlushFileBuffers` and
the durability helper flushes the directory only on non-Windows.
`server/PtkMcpServer/Audit/AuditExportCheckpointStore.cs:358-392` treats the
successful replacement and reload as durable before installing the advanced
checkpoint state that authorizes acknowledged-prefix deletion and
evidence-anchor finalization. Line 1164 is the uncertain-replacement recovery
path, not the normal commit.

## Predicted observable failure

Power loss shortly after an acknowledged checkpoint replacement can restore
the older checkpoint. The minimum result is duplicate export. If retention
already deleted the prefix proved by the newer checkpoint, restart can reject
the retained spool floor permanently. The spool is a protected child of the
same configured root, so this is an unsupported persistence-order assumption
within Windows filesystems, not the reviewer's claimed cross-volume layout.

## What

Restore an explicit Windows content-durability barrier after the handle-based
atomic rename, without weakening the held-reader atomicity fix or introducing a
delete gap.

## Approach

Open the already identity-verified retained source handle with the write access
required by `FlushFileBuffers`. After `SetFileInformationByHandle` publishes
that exact file, flush through the same retained handle before returning to the
existing destination-replaced commit callback. A post-flush test seam proves
the ordering without reopening the published path or adding a TOCTOU gap.

## Files changed

- `server/PtkMcpServer/Audit/SecureAuditStorage.cs`
- `server/PtkMcpServer.Tests/SecureAuditStorageTests.cs`

## Guard proof

- At exact Windows head `e56d9f2d1b5efc7366be5809d4355b6c3ba6c47f`,
  the new post-flush ordering/held-reader guard passed. A disposable mutation
  removed only the `FlushRenamedFile` call; the same guard failed because the
  destination callback observed that no flush had completed. The mutation tree
  and branch were removed after proof.
- The Windows ordering/held-reader guard and concurrent no-gap reader guard
  passed together 2/2. They exercise the real `FlushFileBuffers` P/Invoke and
  `GENERIC_WRITE` access combination.
- The full macOS .NET suite passed 926/926; the Windows-only guard compiles but
  returns early there, so the exact Windows proof is authoritative for the new
  behavior.

## Coder dispute (if any)

The finding is valid, but two reviewer details were corrected during intake.
The normal checkpoint commit is at lines 358-392 rather than line 1164, and
spool/control roots cannot be configured on separate volumes. Neither
correction removes the missing durability barrier or rollback risk.

## Known gaps

The test cannot simulate power loss directly. It proves the documented
durability primitive and its pre-commit ordering while retaining the separate
open-reader and concurrent no-gap replacement guards.

## Reviewer comments

Claude Code 2.1.207 reviewed fixed head
`49971d6ce5cb246d2283eab052163ae85a5b5c87` against
`78e256ca0f3b1253aa97dd984f1d913429ea452a`, `guard_confirmed=true`, verdict
`reopened`, recorded 2026-07-12T15:35:30Z. It traced the missing Windows
post-rename flush into checkpoint rollback after retention and classified the
result as a cross-volume crash-durability and availability failure.
