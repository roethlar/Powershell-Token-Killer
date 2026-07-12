# Audited-harness Slice 2 integrated Claude review

**Current status**: Reopened again at fixed head
`49971d6ce5cb246d2283eab052163ae85a5b5c87`; the original review below is
retained as history.

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

## Fixed-head re-review — reopened

**Reviewer**: Claude Code 2.1.207
**Base**: `78e256ca0f3b1253aa97dd984f1d913429ea452a`
**Reviewed head**: `49971d6ce5cb246d2283eab052163ae85a5b5c87`
**Verdict**: `reopened`
**Guard confirmed**: `true`
**Recorded**: 2026-07-12T15:35:30Z

The one-shot JSON envelope exited zero. Its result and structured payload
matched, both fixed SHAs matched the dispatched values, and the payload
conformed to the reviewloop schema. The reviewer created and removed its own
detached worktree; the coder tree remained clean.

### Resolved findings independently confirmed

- Removing the job-specific required-ID boundary made all three metadata cases
  and the end-to-end audit-poison guard fail; byte-exact restoration passed
  focused 5/5. The reviewer also confirmed authorization-persistence refusal is
  now recorded as failed with zero returned bytes.
- Removing only anchored crash-temporary recovery made both canonical restart
  guards fail with the former unknown-entry refusal; byte-exact restoration
  passed 2/2. The bounded retained-identity recovery was independently judged
  safe.
- After restoration, the reviewer passed .NET 926/926, Pester 134 passed with
  two Windows-only skips, and the zero-warning stdio handshake. The final tree
  was clean at the exact head and removed.

### New material findings

- `.agents/review/findings/s2-tls-handshake-misclassification.md`
- `.agents/review/findings/s2-windows-checkpoint-durability.md`

### Non-material re-review observations

- A compound crash can leave an anchoring evidence artifact after its boot's
  retirement controls are removed, making later evidence operations fail
  closed until manual repair.
- Evidence publication performs a protected identity check and SHA-256 hash of
  every retained artifact on the serialized admission path, so long-lived
  installations can accumulate linear per-call latency.
