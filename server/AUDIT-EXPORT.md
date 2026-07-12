# Anchored audit export

PTK always keeps its mandatory local audit. Local-only mode is the default and
requires no SIEM, collector, endpoint, or export credentials. Anchored mode is
an explicit startup choice that copies core audit records to a remote durable
boundary; it does not replace the local journal.

## Enable anchored mode

Set `PTK_AUDIT_EXPORT_CONFIG` to an absolute path. The variable being absent
means local-only. Its presence means anchored mode, even when its value is
empty: PTK will not serve tools unless the complete configuration validates and
the anchored audit runtime opens successfully. It never silently falls back to
local-only after anchored intent has been expressed.

The configuration is strict JSON. It has exactly these seven properties, with
no comments, trailing commas, duplicate properties, or unknown properties:

```json
{
  "schema_version": "ptk.export-config/1",
  "protection_mode": "anchored",
  "endpoint": "https://audit-gateway.example.net:4318/v1/logs",
  "headers": {
    "Authorization": "Bearer REPLACE_WITH_A_REAL_SECRET"
  },
  "ca_file": null,
  "client_certificate_file": null,
  "client_private_key_file": null
}
```

This is a template, not a usable configuration. Put the real file outside the
repository. The configuration file, every referenced PEM file, and each file's
immediate parent directory must already be protected for only the PTK process
owner. PTK verifies external configuration without relaxing its permissions:
on POSIX, directories must be mode `0700` and files `0600`; on Windows, each
path must be owned by the current user and have a protected DACL containing
only one full-control ACE for that user. Symlinks and reparse points are
refused. All paths must be absolute.

For mTLS, `client_certificate_file` and `client_private_key_file` must be a
pair. `ca_file` is an optional PEM bundle that replaces system trust with the
specified custom roots for this endpoint. A certificate-only example is:

```json
{
  "schema_version": "ptk.export-config/1",
  "protection_mode": "anchored",
  "endpoint": "https://audit-gateway.example.net:4318/v1/logs",
  "headers": {},
  "ca_file": "/secure/ptk-export/gateway-ca.pem",
  "client_certificate_file": "/secure/ptk-export/ptk-client.pem",
  "client_private_key_file": "/secure/ptk-export/ptk-client-key.pem"
}
```

At least one authentication mechanism is required: one or more request headers,
a client certificate, or both. Header names and values are transmitted
verbatim, so `Authorization`, API-key, and other receiver-specific header
schemes work without PTK knowing their meaning. Keep those values secret. The
endpoint must be HTTPS, contain no credentials, query, or fragment, and is used
verbatim. PTK does not append `/v1/logs` and does not follow redirects.

PTK sends one OTLP/HTTP protobuf log record per request. The OpenTelemetry
[OTLP exporter specification](https://opentelemetry.io/docs/specs/otel/protocol/exporter/)
defines the signal endpoint convention; use the receiver's exact logs URL in
`endpoint`.

## What counts as the anchor

PTK advances its durable local checkpoint only after the configured endpoint
returns HTTP 200 with a valid OTLP protobuf response that does not reject the
record. That receiving endpoint is the anchor boundary. A downstream SIEM that
the receiver might contact later is not the boundary PTK can observe.

Configure the endpoint to return success only after the record has been
durably committed under a principal the harness cannot alter or erase. The
recommended shape is a separately administered collector, gateway, queue, or
index with persistent storage and a service identity distinct from the account
running PTK. A same-user sidecar or an in-memory proxy adds transport but not a
meaningful security boundary.

An OpenTelemetry Collector can expose the `otlp` receiver with its `http`
protocol, as shown in the official
[Collector configuration documentation](https://opentelemetry.io/docs/collector/configuration/).
Its default in-memory pipeline is not a durable anchor. If a Collector is used
as the endpoint, validate the acknowledgment behavior of the exact version and
distribution, configure persistent storage or a durable queue, and test crash,
disk-full, and restart behavior. The Collector's
[resiliency guidance](https://opentelemetry.io/docs/collector/resiliency/)
distinguishes in-memory sending queues from WAL-backed persistent storage and
documents their remaining loss cases.

## Delivery and downstream correlation

Delivery is at least once. A receiver can durably commit a record and lose its
response, or PTK can stop after the response but before its checkpoint update;
the same event is then sent again. The retry preserves the record identity and
content.

Use `ptk.audit.event_id` as the deduplication key and retain
`ptk.audit.event_hash`, `ptk.audit.previous_event_hash`,
`ptk.audit.sequence`, and `ptk.supervisor.boot_id` for chain validation.
Identical repeats of one event ID and hash are expected. The same event ID with
different content or a different hash is a high-severity integrity signal.

The OTLP body contains the core audit JSON record and useful fields are also
projected into OTLP attributes. Exact submitted script bytes are deliberately
excluded: the exported event carries only its opaque local evidence ID and
SHA-256 digest. Evidence files are sensitive and remain in the owner-only local
store. They are never included in the automatic OTLP export.

## Out-of-band audit administration

`PtkAuditAdmin` is installed beside the server under `~/.ptk/bin/` as a
separate executable, not an MCP tool. It uses the same
`PTK_AUDIT_ROOT` and optional `PTK_AUDIT_EXPORT_CONFIG` startup environment as
the server. Every evidence access durably records an intent before opening the
payload and records success or failure afterward. Core events contain the
opaque evidence ID, digest, byte count, destination kind, and protected export
path when one is used, never the script bytes themselves.

```text
PtkAuditAdmin evidence read --id <evidence-uuid>
PtkAuditAdmin evidence export --id <evidence-uuid> --output <absolute-path>
```

`read` writes the exact sensitive script bytes to stdout. `export` exclusively
creates a new file in an already owner-only protected directory and refuses an
existing path, symlink, or reparse point. Protect stdout, shell history, and
redirections as incident evidence too.

A permanent partial/data/protocol export block never clears automatically.
With the target supervisor stopped, an operator can either attest a separately
verified durable receipt or explicitly acknowledge an evidence gap:

```text
PtkAuditAdmin disposition --boot-id <uuid> --event-id <uuid> \
  --verified-receipt-digest <lowercase-sha256>

PtkAuditAdmin disposition --boot-id <uuid> --event-id <uuid> \
  --acknowledged-gap-reason <machine-code>
```

The command takes the target boot's exclusive checkpoint lease, resolves the
exact blocked record, durably persists an idempotent proof-bound disposition
intent, and only then advances that one checkpoint. The receipt digest is an
operator attestation; this executable does not query or independently verify a
SIEM. Configuration/authentication blocks still require corrected startup
configuration rather than operator disposition.

Disposition audit is a correlated sequence: `export.disposition_intent`
records the requested target boot/event and exact proof;
`export.disposition_authorized` records the durable disposition ID and frozen
blocked-record tuple before the checkpoint changes; and the completed or failed
terminal event repeats the fullest known facts. These values live in the
dedicated `operator_disposition` object in the OTLP body. Query attributes under
`ptk.disposition.*` project the disposition ID, target boot/event, proof kind,
failure class, target export-configuration identity, and the selected proof
value without replacing the complete body.

This separation is an interface boundary, not hostile same-user isolation.
An agent allowed to run arbitrary native commands under the PTK account can
invoke any installed executable that OS policy makes reachable, including
`PtkAuditAdmin`. Keep administrative invocation outside the model-controlled
session and enforce any stronger restriction with a separate operator login,
application control, elevation boundary, or equivalent OS policy. PTK cannot
manufacture that boundary while the harness and administrator share one
unrestricted identity.

## SIEM and log-routing patterns

PTK intentionally speaks only authenticated OTLP/HTTP over HTTPS. Vendor and
legacy integrations belong after the durable OTLP boundary:

| Destination | Recommended route | Boundary note |
| --- | --- | --- |
| Backend with native OTLP logs | PTK directly to its exact HTTPS logs endpoint, or through a durable OTLP gateway | Use direct delivery only when the backend's valid 200 response means durable acceptance. |
| Splunk HEC, Microsoft Sentinel ingestion, Elastic, QRadar, or another vendor API | PTK to a durable OTLP gateway, then a vendor-supported exporter or separately maintained adapter | PTK does not ship or impersonate a vendor SDK. Preserve the PTK identity and chain attributes during mapping. |
| Windows Event Log and WEF | PTK to a durable OTLP gateway, then a Windows service/adapter that writes a dedicated event channel; WEF can forward that channel | Windows Event Forwarding reads events already written to a Windows log. The Event Log write or later WEF hop is not PTK's anchor unless the OTLP endpoint durably commits before acknowledging. See Microsoft's [WEF architecture](https://learn.microsoft.com/en-us/windows/win32/wec/windows-event-collector). |
| RFC 5424 syslog | PTK to a durable OTLP gateway, then an adapter that maps fields to RFC 5424 structured data and sends over TLS | Do not reduce this audit route to unauthenticated UDP. Use [RFC 5424](https://www.rfc-editor.org/info/rfc5424/) with the [RFC 5425 TLS transport](https://www.rfc-editor.org/info/rfc5425/) and preserve stable IDs and hashes. |

For every adapter, test duplicate handling, field truncation, timestamp
precision, Unicode, maximum message size, backpressure, and loss across process
and host restarts. A format conversion that drops the event ID or chain fields
weakens incident reconstruction even when transport succeeds.

## Monitoring and alerts

At minimum, alert on:

- `ptk_state` reporting an anchored exporter that is stalled or faulted, a
  blocked event, or a last acknowledgment that stops advancing;
- sustained retry delay, rising local spool use, or exhausted effective free
  capacity;
- a sequence gap, previous-hash mismatch, or conflicting content for one
  `ptk.audit.event_id` at the receiver;
- an unexpected export-configuration identity or supervisor boot transition;
- `server.started` without a corresponding `server.stopped`, while allowing for
  a known hard kill or host failure; and
- audit-storage unavailability or protection failures on the PTK host.

Also alert on `export.disposition_intent`, especially an acknowledged gap, and
on an evidence-access intent without its matching completed/failed outcome.

Keep receiver-side retention, access control, and alert administration outside
the PTK/harness identity. Local files are protected from other identities but
are not immutable against the same account that runs PTK; their hash chain
detects inconsistency only when a trustworthy copy or remote anchor survives.
