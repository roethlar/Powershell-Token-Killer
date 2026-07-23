# Plan: reconcile published master into MCP resilience R1

**Status:** APPROVED 2026-07-22. The owner approved the complete local
reconciliation plan with `go`; push, merge into `master`, release, and
installed-payload changes remain unauthorized. Drafted after a fresh `origin`
fetch on 2026-07-22 against `origin/master`
`e4d8d1e8a3c156106d2da02287c2c38923c5199c` and feature content/evidence head
`96d4af28b39005135158da05121cf69c09b01039`; the plan and corresponding state
record are the only permitted documentation descendant before approval. A
push, merge into `master`, release, or installed-payload change is outside
this plan.

## Objective

Merge the current published `origin/master` into the active
`feature/mcp-resilience-r1` branch with one ordinary merge commit. Preserve
both lines' accepted behavior and complete history without rebasing, rewriting,
force-updating, or choosing either tree wholesale. Resolve only integration
conflicts caused by changes that were developed independently from common base
`1f314a29807e7504aa04f7f14899c6bb6483248a`.

The result remains a feature branch. This plan does not land the feature on
`master`; that later direction requires independent content verification and
owner approval.

## Frozen branch inventory

- Local `master` exactly matches `origin/master` at `e4d8d1e`. It needs no
  fast-forward. Its 88 history commits absent from the feature include
  mini-SIEM S3H storage protection, RBC/hot-fix review remedies, background
  process-tree and output-store hardening, Defender false-positive install
  handling, and the current governance toolkit.
- `feature/mcp-resilience-r1` is at `96d4af2`. Its published remote is at
  `68c5b34`; the only unpublished commit records the first dependency CI run
  and proposed correction. Its 173 history commits absent from master include
  resilience R1-R5, guardian/private-host composition and containment, exact
  package production, and the approved dependency migration.
- `origin/ci/dependency-hardening` also points to `68c5b34`. It is only the
  workflow-trigger branch for the first hosted dependency run, not another
  source line.
- `fix/ci-portability`, both S2 admin review branches, the three S2 Windows
  repair branches, `impl/mcp-resilience-r0`, and
  `plan/mcp-resilience-guardian` introduce no patch absent from either active
  line. The RBC and schema-fix branches introduce no patch absent from
  `master`. Do not merge or delete them in this plan.
- Three dormant branches still have one patch absent from both active lines:
  `feat/closed-prefix-reader` (`e451aaa`),
  `feat/config-retry-capability` (`1e1add9`), and
  `fix/locked-prefix-reconcile` (`f3983a8`). Their disposition is independent;
  do not fold them into this reconciliation merely because they exist.

Counts and classifications above are evidence tied to the two recorded content
tips, not permanent branch facts. Re-fetch and recompute them immediately
before merging. If `origin/master` changed, or the feature gained any change
after `96d4af2` other than this plan and state record, update this plan and stop
for renewed owner approval.

## Merge boundary

1. Require a clean feature worktree. Fetch and prune `origin`, confirm local
   `master` still equals `origin/master`, confirm the feature differs from
   recorded head `96d4af2` only by this plan and state record, and rerun a
   read-only merge preview.
2. Start `git merge --no-commit --no-ff origin/master` on
   `feature/mcp-resilience-r1`. Never rebase, squash, amend, force-push, or use
   an `ours`/`theirs` whole-tree strategy. If the conflict set expands beyond
   the recorded paths or reveals an unplanned behavioral decision, abort the
   merge and amend the plan instead of guessing.
3. Resolve the recorded conflicts according to the ledger below. Automated
   clean merges remain part of the candidate and must be reviewed for semantic
   compatibility; syntactic cleanliness is not acceptance.
4. Run the complete verification battery on the uncommitted merge result.
   Commit one ordinary merge commit only after all required checks pass. Any
   unrelated defect discovered during verification becomes a separately
   planned follow-up, not an opportunistic merge-resolution change.
5. Update `.agents/state.md`, `.agents/machines.md`, affected plans, and review
   records with the exact merge commit and evidence. Do not push without a
   separate outward-action approval.

## Conflict ledger

The read-only `git merge-tree` preview reports ten content conflicts.

### Durable records

- `.agents/machines.md`: retain master S3H platform evidence and all feature
  resilience/dependency evidence in their existing chronological sections.
  Do not duplicate counts owned by plans or erase the failed hosted-run record.
- `.agents/review/index.md`: retain the complete master RBC/hot-fix review
  ledger and the feature's resilience review acceptance. Reconcile rows by
  finding identity and exact accepted SHA; do not let a stale side reopen or
  close a finding silently.
- `.agents/state.md`: rewrite the merged current-state entry rather than
  concatenating stale snapshots. Master-owned landed fixes and governance,
  feature R1-R5/dependency status, the hosted CI blockers, and all still-valid
  blockers must remain discoverable. Push state must not be recorded.

Current toolkit-owned governance from `origin/master` controls every installed
governance artifact. Do not hand-edit `AGENTS.md`, skills, playbooks, command
wrappers, hooks, or settings during conflict resolution. Their master versions
must arrive unchanged; repo-specific merged truth belongs in the editable
`.agents/` records named above.

### Installer and frozen contracts

- `scripts/dev-install.ps1`: preserve the feature's matched guardian package,
  manifest verification, native-broker packaging, and publish-lifetime bounds;
  also preserve master's Defender quarantine detection and its fail-closed
  operator diagnostic. Neither installation path may bypass the other's gate.
- `server/Contracts/ResilienceR0/contract.json` and
  `package-manifest.example.json`: preserve the feature's MCP-compatible frozen
  session schema and master's JSON Schema 2020-12-valid `ptk_output` offset
  schema. Regenerate nothing and retain canonical LF/exact-byte policy.
- `server/PtkMcpServer.Tests/McpResilienceR0ContractTests.cs`: retain exact-byte
  and generated-schema guards for both contract changes. The final tests must
  fail if either the session compatibility or offset-schema correction is
  independently reverted.

### Runtime ownership and concurrency

- `server/PtkMcpGuardian/Audit/AuditRuntimeGate.cs`: this is a rename-aware
  conflict between master's RBC-2 repair in the former server-owned gate and
  the feature's move of that gate into the guardian. Port the master guarantee
  that a failed session drain still publishes degraded `server.stopped` into
  the guardian-owned implementation while retaining feature shutdown
  serialization and host-snapshot audit fields. Preserve the existing RBC-2
  guard and guardian lifecycle-audit guards.
- `server/PtkMcpGuardian/Output/OutputStore.cs`: this is a rename-aware
  conflict between master's RBC-7/RBC-14 repairs in the former server-owned
  store and the feature's guardian-owned transfer store. Preserve master's
  file I/O outside the store gate, post-I/O state revalidation, retained-entry
  disposal rules, retention deletion outside the gate, settle-generation
  rechecks, and wedge guards. Preserve the feature's guardian ownership,
  transferred-output ingestion, execution-capability authorization, terminal
  recovery, and tombstone behavior. Both sets of concurrency tests must run
  unchanged.
- `server/PtkMcpServer/JobManager.cs`: combine master's RBC-1/RBC-15 cold-job
  stream and process-tree containment, eager exclusive-group initialization,
  incarnation checks, terminal cleanup, and asynchronous group release with
  the feature's guardian-reserved public job IDs, capability-bound output,
  private session authority, and default-session composition. No job may lose
  containment or gain authority as an integration side effect.

## Verification

Before committing, run all checks from the uncommitted merge worktree.

1. Run `git diff --check`, parse the workflow, verify no conflict markers, and
   inspect every cleanly auto-merged file changed on both sides.
2. Run the exact contract tests and prove both independent guards by temporary
   one-at-a-time reversions of the session-schema and offset-schema changes.
   Restore the candidate after each red proof.
3. Run focused RBC-1, RBC-2, RBC-7, RBC-14, and RBC-15 tests plus the guardian
   audit, output-transfer, job-lifecycle, native containment, package-layout,
   and installer tests affected by the conflicts.
4. On macOS, run all package vulnerability/deprecation/outdated audits, the
   complete architecture, Guardian, server, SIEM, and both conformance modes,
   exact Pester, and the stdio handshake.
5. Validate the same merged source on x64 Linux `magneto` and Windows
   `NETWATCH-01`. Require the complete architecture, Guardian, server, SIEM,
   conformance, Pester, and handshake identities already frozen by the
   dependency plan. Use the established Windows ordinary-account/SYSTEM split
   only for credential-bound coverage; use xUnit v3 in-process execution where
   VSTest's known path boundary requires it.
6. Confirm no disposable roots, processes, tasks, certificates, archives, or
   validation artifacts remain. Then create the merge commit and rerun at
   least the macOS complete battery at its exact committed SHA.

The dependency plan's proposed hosted-CI correction remains a separate,
unapproved change. After this reconciliation lands locally, re-evaluate those
three findings against the merged tree and request its approval separately.

## Failure handling

- Any new conflict, lost test identity, new advisory, or behavior choice not
  settled above stops the merge. Abort back to the recorded clean feature tip,
  preserve diagnostic evidence in repo records, and amend this plan.
- Two consecutive verification cycles that bank no red-to-green delta are a
  stall. Stop and report the exact failing identities and attempted deltas.
- A successful local merge does not authorize pushing the feature, updating
  `master`, deleting branches, releasing, or changing installed payloads.
