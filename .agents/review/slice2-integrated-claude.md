# Audited-harness Slice 2 integrated Claude review

**Reviewer**: Claude Code 2.1.207 (`claude-fable-5`)
**Base**: `78e256ca0f3b1253aa97dd984f1d913429ea452a`
**Reviewed head**: `6cbd1d3061985f06bb0a5da8bcf2faa84a5bb826`
**Verdict**: `reopened`
**Guard confirmed**: `true`
**Recorded**: 2026-07-12T14:33:24Z

The one-shot JSON envelope exited zero. Its result and structured payload
matched, both fixed SHAs matched the dispatched values, and the payload
conformed to the reviewloop schema.

## Material findings

- `.agents/review/findings/s2-job-id-audit-poison.md`
- `.agents/review/findings/s2-anchored-temp-recovery.md`

## Independent guard proof

- Moving `EvidenceExportProgress.FinalPublished` after the injected
  post-publication hook made the exact publication-boundary guard fail with
  `evidence.storage_failed` instead of
  `audit.outcome_failed_after_publish`; restoration passed.
- Removing the post-unlink retention content comparison made the `AfterDelete`
  preopened-writer case complete silently; restoration passed.
- After byte-exact restoration, the reviewer passed 919/919 .NET tests, Pester
  134 passed with two Windows-only skips, and the zero-warning stdio handshake.
  The detached review worktree was clean and removed.

## Windows static review

The reviewer found the `FILE_RENAME_INFO` offsets and byte counts correct for
both pointer sizes, confirmed replace-plus-POSIX flags, source-handle identity
verification, direct-child confinement, protected-root checks, and no-replace
publication semantics. Live Windows evidence remained the separately executed
919/919 .NET, 136/136 Pester, zero-warning handshake, admin layout smoke, and
held-reader mutation proof at the reviewed head.

## Non-material reviewer observations

These were returned as fail-closed follow-ups, not Slice 2 acceptance blockers:

- Receiver-controlled `Retry-After` is uncapped and can stall export or exceed
  timer bounds.
- Peer-crash orphan or stray spool entries can fault the complete adoption loop.
- A crash between quota-lock creation and marker-byte commit can wedge a spool
  root.
- Unix evidence publication has a link-to-unlink crash alias without the
  control-file alias recovery path.
- Export transition records are not separately bounded against the terminal
  event reserve.
- `journal.storage` unavailability has no in-process recovery path.
- Non-ASCII export-header values remain retryable transport failures instead of
  a visible configuration block.
- Graceful shutdown is unbounded when a child cannot be killed.
- Storage-metric update failure can leak reservation state or misreport a
  durably appended record.

The reviewer also found no material checkpoint-advance bypass, secret/plaintext
leak, anchored-retention proof gap, disposition-stage misclassification, model
access to the admin surface, or duplicate job terminal.
