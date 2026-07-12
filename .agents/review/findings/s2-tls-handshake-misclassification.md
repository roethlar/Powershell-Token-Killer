# s2-tls-handshake-misclassification: retry transient TLS transport failures

**Severity**: HIGH — one routine collector-side handshake interruption can
durably stop export until an operator changes configuration, eventually
closing audited admission at spool high water.
**Status**: Open
**Branch**: `fix/s2-tls-handshake-misclassification`
**Commit**: pending

## Evidence

`server/PtkMcpServer/Audit/AuditOtlpHttpExporter.cs:363-371` classifies every
`HttpRequestError.SecureConnectionError` as TLS validation failure. The send
path maps that classification to the durable configuration block at
`AuditOtlpHttpExporter.cs:175-180`. A TLS peer abort can surface under the same
HTTP request error with an inner transport `IOException`, not an
`AuthenticationException`.

## Predicted observable failure

A collector restart, load-balancer reset, or peer EOF during TLS establishment
is recorded as `tls.validation`. `AuditClosedSpoolExportPump` will not retry the
same export-configuration identity; process restart does not clear it. Export
and acknowledged-prefix retention stop, the spool reaches high water, and all
new audited calls fail closed despite valid certificates.

## What

Distinguish certificate/authentication failures from transient transport
failures inside `SecureConnectionError`. Only an `AuthenticationException`
chain may create the durable TLS-configuration block; peer-abort I/O remains a
retryable connection failure.

## Approach

Pending coder triage and implementation. Add a deterministic TLS-handshake
peer-abort guard that exercises the real exporter classification before
narrowing `IsTlsFailure`.

## Files changed

- Pending implementation.

## Guard proof

- Pending red-to-green exporter test for a peer abort during TLS handshake.

## Coder dispute (if any)

None yet; independent triage is in progress.

## Known gaps

The guard must distinguish a transport abort from the existing wrong-CA and
wrong-hostname certificate-validation cases without weakening either block.

## Reviewer comments

Claude Code 2.1.207 reviewed fixed head
`49971d6ce5cb246d2283eab052163ae85a5b5c87` against
`78e256ca0f3b1253aa97dd984f1d913429ea452a`, `guard_confirmed=true`, verdict
`reopened`, recorded 2026-07-12T15:35:30Z. It cited the broad
`SecureConnectionError` classification and the durable same-identity retry
block as a transient, model-independent path to process-lifetime export loss
and eventual fail-closed admission.
