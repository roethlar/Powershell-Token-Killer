# Plan: mini-SIEM discovery (decision support for the OPEN 2026-07-14 receiver question — NO BUILD AUTHORIZED)

## STOP — read this before the rest of the file

**Status: DISCOVERY ONLY. The underlying decision is OPEN** — appended by the
owner to the end of the decision queue (2026-07-14; `.agents/decisions.md`,
"Whether PTK should ship a mini SIEM receiver for external audit custody").
**Nothing in this file authorizes implementation.** Producing, reviewing, or
approving this discovery plan does not release the owner's hold on broader
decision-log reconciliation and does not reopen any settled transport decision.

Provenance: drafted 2026-07-15 on branch `plan/mini-siem-discovery`
(worktree `.claude/worktrees/mini-siem`, cut from `master` @ `5ae154c`),
deliberately independent of `plan/mcp-resilience-guardian`. The resilience
compatibility contract is consumed read-only from commit `33d6a35`
(`git show 33d6a35:.agents/plans/mcp-resilience.md`); this plan must track
that contract as it lands on master — never fork or restate it authoritatively.

Baseline evidence at draft time (this worktree, darwin), author-reported from
the drafting session — no committed transcript or machine record backs these
counts, and they are not independently recorded: Pester 141 passed /
2 Windows-only routing skips / 0 failed; `dotnet test server/PtkMcpServer.slnx`
1484/1484 passed; `pwsh -NoProfile -File server/test-handshake.ps1` →
HANDSHAKE PASSED. Re-run and capture a durable transcript tied to the tested
SHA and environment before treating these counts as branch-health evidence.
(`.agents/repo-guidance.md` Verification still records
43/43 Pester from 2026-07-03 — stale count, flagged here, not edited by this
plan.)

## Binding inputs this discovery must not re-litigate

1. **No new SIEM transport.** "Audit v3 does not create a new SIEM transport.
   Anchored mode retains the exact one-record OTLP/HTTP protobuf request,
   acknowledgment, retry, checkpoint, and durable-anchor contract" (33d6a35,
   plan lines 561-563).
   If the gated decision later chooses a PTK receiver, that receiver "must
   implement this same authenticated one-record OTLP/HTTP contract"
   (lines 583-584).
2. **Contract prohibitions 25-27** (33d6a35, lines 853-856): never change the
   OTLP wire/envelope by destination or audit schema version; never drop or
   mistype one prior or `ptk.host.*` OTLP query attribute; never let a
   vendor/mini-SIEM adapter acknowledgment replace PTK's configured durable
   OTLP anchor semantics.
3. **Anchor boundary** (`server/AUDIT-EXPORT.md`): the configured receiver is
   the observable anchor boundary; success requires durable commit under a
   separately administered principal before acknowledgment. Per the decision
   entry: "A same-user sidecar or an in-memory collector is not a meaningful
   anchor; a default in-memory OpenTelemetry pipeline therefore does not
   answer this question."
4. **Conformance seam**: "A future mini-SIEM conformance fixture can replace
   the fake receiver without changing PTK bytes. The receiver itself remains
   unimplemented and separately owner-gated" (33d6a35, lines 799-801).
   Discovery output must be expressible as criteria pluggable into that seam
   while preserving the exact "without changing PTK bytes" requirement.
5. **Owner's standing recommendation** (decisions.md, end of the OPEN entry):
   "discovery first, not implementation. Compare the smallest existing durable
   OTLP deployment against a custom receiver using the criteria above. Build
   PTK-specific receiver code only if no supportable existing shape provides
   the required external boundary at acceptable operational cost."

## The question and the options under assessment (from the OPEN entry)

When Microsoft Sentinel, Splunk, or another robust SIEM is unavailable, should
PTK ship or maintain a small external receiver so anchored audit records leave
both the PowerShell runspace and the PTK source machine, receive secure durable
custody, and remain useful for basic investigation and alerting?

1. PTK-maintained minimal OTLP receiver: durable-before-ack storage,
   authentication, chain/event validation, bounded query, retention, basic
   alerts.
2. Hardened deployment profile + validation harness for existing lightweight
   components that together provide the same external durable boundary.
3. No fallback receiver: anchored mode requires an independently operated SIEM
   or durable OTLP service.

## Discovery method: acceptance-question evidence matrix

The owner listed eleven acceptance questions "before any build." Each becomes
a matrix row; every option gets a cited answer per row — vendor documentation,
configuration excerpt, or reproducible probe transcript. No cell may be filled
by inference or model memory.

| # | Acceptance question (owner's wording, condensed) | Evidence a cell must carry |
|---|---|---|
| 1 | Threat model and separate service identity | Written threat model; identity/principal design per option |
| 2 | Durable-before-`200` semantics | Doc or probe proving ack is withheld until durable commit |
| 3 | Duplicate handling for PTK's at-least-once delivery | Documented idempotence/dedup behavior on replay |
| 4 | Event-ID / hash-chain validation | Where validation runs and what rejects a broken chain |
| 5 | Crash, disk-full, backpressure, restart behavior | Failure-mode table; disk-full must reject, never false-ack |
| 6 | mTLS or equivalent authentication | Supported authn, cert rotation story |
| 7 | Receiver host storage protection | At-rest protection and OS-level access controls |
| 8 | Retention and read authorization | Retention policy hooks; who can read, enforced how |
| 9 | Minimum useful queries/alerts | The smallest query/alert set an investigation needs |
| 10 | Upgrade/backup/recovery ownership | Named owner and procedure per option |
| 11 | Security patch burden of a network service | Patch cadence/surface added by each option |

## Work slices (documents and criteria only; any executable probe is a separately authorized change)

- **D1 — candidate survey (feeds option 2).** Enumerate the smallest existing
  deployments that plausibly provide durable-before-ack custody over the exact
  authenticated one-record OTLP/HTTP contract, and score them against the
  matrix. Must explicitly resolve the known trap with evidence: receiver-side
  acknowledgment in a stock OpenTelemetry Collector pipeline is not obviously
  gated on durable commit (exporter-side sending-queue persistence is not
  receiver-side durability — see the Collector resiliency documentation
  already cited by `server/AUDIT-EXPORT.md`). Binding Input 3's components
  are graded separately, mirroring acceptance questions 1 and 2 in
  `.agents/decisions.md:329-334`: acknowledgment ordering
  (durable-before-ack) is graded only in row 2; the separately administered
  principal/service identity is graded in row 1; associated storage
  controls are graded in row 7. A candidate failing one component fails
  only the row for that component, not row 2 wholesale.
- **D2 — threat model and identity draft (rows 1, 6, 7).** Attacker profiles:
  compromised PTK host user, network adversary, receiver-host tampering.
  Separate-principal design for the receiver identity. The existing
  `PtkAuditAdmin` separation is an out-of-band interface precedent only —
  "an interface boundary, not hostile same-user isolation"
  (`server/AUDIT-EXPORT.md:205`) — and supplies no service-identity boundary;
  the receiver's separately administered principal requires a separate
  operator login, elevation boundary, or OS policy. Storage protection
  expectations per option.
- **D3 — conformance criteria (rows 2-5).** Measurable pass/fail probes
  written against the guardian fixture seam (Binding Input 4):
  durable-before-ack with two synchronized barriers — (a) pre-commit: a
  receiver killed before durable commit must have emitted no valid,
  nonrejecting OTLP acknowledgment (a bare `200` status alone is not an
  ack; the full valid/nonrejecting response per
  `server/AUDIT-EXPORT.md:87-97` is); (b) post-ack: every observed valid
  nonrejecting OTLP acknowledgment must survive immediate receiver
  termination and restart with the record intact; duplicate replay
  idempotence;
  event-ID/hash-chain validation rejects tampering; disk-full ⇒ rejection,
  never a false `200`; backpressure signaling that PTK's existing retry
  honors; restart/recovery re-advertisement; cross-version compatibility per
  `33d6a35:.agents/plans/mcp-resilience.md:785-801` — exact v1, v2, and v3
  bodies accepted, every retained attribute name and type preserved, the
  four typed `ptk.host.*` v3 attributes intact, Unicode and timestamp
  fidelity, and PTK request bytes unchanged.
- **D4 — operational cost comparison (rows 8-11).** Compare all three options
  across the eleven acceptance criteria; option 3 ("require an external SIEM")
  carries zero PTK code cost but requires an independently operated service.
  The owner's cost gate applies only to choosing custom PTK receiver code
  (option 1) over a supportable existing shape: "Build PTK-specific receiver
  code only if no supportable existing shape provides the required external
  boundary at acceptable operational cost."
- **D5 — recommendation memo.** One page mapping matrix results to options
  1/2/3, plus a drafted decision-entry update for the owner to accept or
  reject. **Gate: owner sign-off. No build follows from this plan.**

## Non-goals

- No receiver code; no shipped deployment profile; no probe execution.
- No changes to `server/AUDIT-EXPORT.md` semantics or the OTLP wire/envelope.
- No edits to `.agents/decisions.md` while the reconciliation hold stands —
  D5 *drafts* an update; only the owner lands it.
- No edits to the resilience plan, its branch, or its conformance suite.

## Verification (for the discovery itself)

- Every matrix cell carries a citation (vendor doc, config excerpt, or probe
  transcript). Cells without evidence stay empty and are listed as unknowns in
  D5 rather than guessed.
- D3 criteria must discriminate: the guardian fake durable receiver would
  pass them; a deliberately non-durable configuration — one that returns a
  valid nonrejecting acknowledgment before durable commit — must fail the
  post-ack barrier (the record does not survive termination immediately
  after the ack). A receiver that merely dies before responding satisfies
  the pre-commit barrier vacuously, so discrimination rests on the post-ack
  barrier. Executing that check belongs to the separately authorized probe
  step, not this plan.
- The standard battery (`.agents/repo-guidance.md` → Verification) stays green
  on this branch; the branch adds documents only.

## References

- `.agents/decisions.md` — OPEN (2026-07-14) mini-SIEM entry: question,
  options, acceptance questions, standing recommendation.
- `git show 33d6a35:.agents/plans/mcp-resilience.md` — compatibility contract;
  SIEM-relevant lines 34-37, 561-584, 686-690, 778-805, 845-880.
- `server/AUDIT-EXPORT.md` — retained OTLP transport, acknowledgment, and
  anchor-boundary semantics; Collector resiliency references.
