# s2-windows-checkpoint-durability: flush replaced checkpoint contents

**Severity**: HIGH — a Windows power loss can revert a checkpoint already used
to authorize retention, causing duplicate export or permanent chain-adoption
failure after acknowledged data was deleted.
**Status**: In progress
**Branch**: `fix/s2-windows-checkpoint-durability`
**Commit**: `e4e5a697d3f65731bdf5356c814525e50214e181`

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
Checkpoint snapshots keep their strict `FileShare.Read | FileShare.Delete`
boundary and retry only Windows `ERROR_SHARING_VIOLATION` for a bounded
one-second window with capped backoff. Every attempt repeats the protected
root/file checks; all other storage, protection, and parse failures remain
immediate and fail closed.

## Files changed

- `server/PtkMcpServer/Audit/SecureAuditStorage.cs`
- `server/PtkMcpServer.Tests/SecureAuditStorageTests.cs`
- `server/PtkMcpServer/Audit/AuditExportCheckpointStore.cs`
- `server/PtkMcpServer.Tests/AuditExportCheckpointStoreTests.cs`

## Guard proof

- At exact Windows head `e56d9f2d1b5efc7366be5809d4355b6c3ba6c47f`,
  the new post-flush ordering/held-reader guard passed. A disposable mutation
  removed only the `FlushRenamedFile` call; the same guard failed because the
  destination callback observed that no flush had completed. The mutation tree
  and branch were removed after proof.
- The Windows ordering/held-reader guard and concurrent no-gap reader guard
  passed together 2/2. They exercise the real `FlushFileBuffers` P/Invoke and
  `GENERIC_WRITE` access combination.
- Final integrated validation exposed the retained write-handle sharing race.
  The deterministic correction guard holds that exact post-flush handle open,
  observes the strict reader's retry, releases it, and requires the complete
  intended checkpoint. Disabling only the error-32 classifier made the guard
  fail immediately with the reader task faulted instead of a retry.
- At `e4e5a697d3f65731bdf5356c814525e50214e181`, the Windows focused durability,
  held-reader, concurrent no-gap, and snapshot-retry suite passed 4/4, and the
  full Windows .NET suite passed 927/927. The full macOS suite also passed
  927/927; one unrelated anchored-runtime timing failure passed in isolation
  and on the complete rerun.

## Coder dispute (if any)

The finding is valid, but two reviewer details were corrected during intake.
The normal checkpoint commit is at lines 358-392 rather than line 1164, and
spool/control roots cannot be configured on separate volumes. Neither
correction removes the missing durability barrier or rollback risk.

## Known gaps

The test cannot simulate power loss directly. It proves the documented
durability primitive and its pre-commit ordering while retaining the separate
open-reader and concurrent no-gap replacement guards. A foreign same-identity
writer can delay a checkpoint read for at most the one-second retry window;
expiry rethrows the exact sharing violation and remains fail closed.

## Reviewer comments

Claude Code 2.1.207 reviewed fixed head
`49971d6ce5cb246d2283eab052163ae85a5b5c87` against
`78e256ca0f3b1253aa97dd984f1d913429ea452a`, `guard_confirmed=true`, verdict
`reopened`, recorded 2026-07-12T15:35:30Z. It traced the missing Windows
post-rename flush into checkpoint rollback after retention and classified the
result as a cross-volume crash-durability and availability failure.

Claude Code 2.1.207 reviewed fixed head
`346829b4e6b00ec1fb8d0bcc5f9e092b09cfac3d` against
`0c9f430b71f14ac40c89aad6ad7da712aa2fc47e`, `guard_confirmed=true`, verdict
`accepted`, recorded 2026-07-12T16:09:47Z. In its own disposable Windows
worktree it passed the ordering/held-reader and concurrent no-gap guards 2/2,
removed only the production flush call and observed the intended assertion
fail, restored byte-exact source, and passed 2/2 again. It also passed local
.NET 926/926, Pester 134 with two platform skips, and the zero-warning
handshake. Static review confirmed write access, retained-handle ordering, and
fail-closed checkpoint recovery before retention authorization; all local and
remote review artifacts were removed.

Post-merge exact-head Windows validation at
`32cd67e236260a064389ed21de6dc642f84e5628` reopened this finding after the
accepted review. The full suite failed
`Concurrent_readers_observe_only_complete_atomic_checkpoints`: a reader opening
the newly published checkpoint with `FileShare.Read | FileShare.Delete` raced
the retained `GENERIC_WRITE` flush handle and received
`ERROR_SHARING_VIOLATION`. This is a product integration regression, not a
fixture failure; the durability barrier must coexist with the checkpoint's
strict read-sharing contract.
