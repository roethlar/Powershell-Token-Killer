# PtkSiemReceiver

Mini-SIEM OTLP/HTTP receiver for anchored audit export. Accepts
`/v1/logs` over mTLS, enforces endpoint-owned request custody
(bounded reads, commit/quarantine semantics), and persists events,
quarantine, and custody records to a SQLite ingest store.

## ⚠️ Deployment warning: no retention enforcement on master (rbc-11)

`RetentionMaxAgeDays` and `RetentionMaxTotalBytes` are parsed and
validated (`Configuration/SiemReceiverConfiguration.cs`) but are
**not enforced** by any code on `master`. The `events`, `quarantine`,
and `custody` tables store full `raw_request` BLOBs (up to
`MaxRequestBytes` each) and grow without bound.

**Do not deploy a master build of this receiver to an environment
where an authenticated client can send sustained traffic.** A
malicious or misbehaving client can fill the disk; once SQLite writes
fail the receiver must reject new records, losing audit custody.

Retention enforcement lives on the isolated branch
`plan/mini-siem-storage-hardening` (S3H). This warning stands until
the owner's S3H land/park decision resolves (see
`.agents/review/findings/rbc-11.md` and `.agents/decisions.md`).
Deployment guidance is gated on that decision:

- **S3H lands** → retention sweeps enforce the configured limits;
  remove this warning.
- **S3H parked** → master remains not deployable for unattended
  ingest; bound disk usage operationally (quota/monitoring) or do not
  expose the receiver to untrusted-volume clients.
