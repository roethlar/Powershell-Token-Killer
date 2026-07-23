# rbc-4: AuditOtlpHttpExporter TLS revocation disabled by default with no opt-in

**Severity**: MAJOR
**Status**: Fixed, merged to master (merge `685d34c`)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/Audit/AuditOtlpHttpExporter.cs:432`

## Evidence

`ConfigureCustomTrustPolicy` sets `RevocationMode = X509VerificationFlags.NoCheck`,
silently skipping certificate revocation checks. For an audited security
pipeline that explicitly forbids auto-redirect, disables cookies, and pins
custom roots, this is a meaningful downgrade. An attacker who obtains a
revoked-but-still-valid server cert could establish TLS and inject malicious
audit acknowledgments.

There is no configuration option to opt into a stricter revocation mode and
no comment justifying the `NoCheck` choice.

## Predicted observable failure

A compromised OTLP endpoint with a revoked certificate (revoked by the CA
but not yet expired) establishes TLS with the PTK exporter, receives audit
records, and returns forged 200 acks that cause PTK to advance its
checkpoint as if the records were durably received by a legitimate
collector.

## What

At minimum, add an explicit comment justifying `NoCheck` (e.g., air-gapped
OTLP, private CA without CRL distribution). Preferably, make revocation
mode configurable (default `Online` or `Offline` with a CRL cache) and
document the tradeoff. If `NoCheck` is intentional for the current
deployment model, record that decision in `.agents/decisions.md`.

## Scope of fix

One property in `AuditOtlpHttpExporter.cs`, plus a configuration option
in `AuditExportConfiguration` if made configurable. No architectural
change.

## Resolution

Revocation posture is now an explicit, required operator decision — there
is no implicit default and no silent fallback:

- `AuditExportConfiguration` requires a `revocation_check_mode` field
  (`ParseRevocationMode`, exact-case: `"NoCheck"` | `"Online"` |
  `"Offline"`; absent or unrecognized values throw
  `AuditExportConfigurationException`). Mirrors the SIEM receiver's
  `revocation_check_mode` contract.
- `AuditOtlpHttpExporter.ConfigureCustomTrustPolicy` now takes the parsed
  `X509RevocationMode` and sets `policy.RevocationMode` from it on the
  custom-trust (pinned root) validation path.
- On the system-trust path, `HttpClientHandler.CheckCertificateRevocationList`
  is set from the mode; since the handler only exposes a boolean online
  check, `Offline` is elevated to an online check rather than silently
  degraded to no check (documented in a code comment referencing rbc-4).
- `revocation_check_mode` participates in `ExportConfigurationIdentity`
  (HMAC-framed via `AppendFramedString`), so changing the revocation
  posture changes the config identity and cannot alias a prior identity.

## Guard proof

Written. Guards assert:
- config parsing accepts exactly `"NoCheck"`/`"Online"`/`"Offline"` and
  rejects absent/unknown/wrong-case values with
  `AuditExportConfigurationException` (`AuditExportConfigurationTests`),
- the handler enables `CheckCertificateRevocationList` for
  `Online`/`Offline` and disables it only under explicit `NoCheck`, and
  the custom-trust chain policy carries the configured mode
  (`AuditOtlpHttpExporterTests`),
- identity derivation includes the revocation mode and rejects material
  with an empty mode (`ExportConfigurationIdentityTests`).

Full test suite green after the change.

## Reviewer comments

Read-only review by Hermes subagent (audit subsystem pass). No external
fixed-SHA review has been dispatched. Fix implemented in-session; owner
review of the changed default (explicit required field is a breaking
config change for existing configs lacking `revocation_check_mode`)
still pending.