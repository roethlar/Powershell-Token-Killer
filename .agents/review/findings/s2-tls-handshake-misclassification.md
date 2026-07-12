# s2-tls-handshake-misclassification: retry transient TLS transport failures

**Severity**: HIGH — one routine collector-side handshake interruption can
durably stop export until an operator changes configuration, eventually
closing audited admission at spool high water.
**Status**: In progress
**Branch**: `fix/s2-tls-handshake-misclassification`
**Commit**: `cd0f0a49f67e640b84c8f9de0ad7acf72ad59ab0`

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

Classify a nested `AuthenticationException`, not the ambiguous
`SecureConnectionError` category, as the durable TLS-configuration failure.
The deterministic guard gives authentication and peer-abort failures the same
outer category and distinguishes their inner causes; existing real wrong-CA
and wrong-hostname integration guards remain unchanged.

## Files changed

- `server/PtkMcpServer/Audit/AuditOtlpHttpExporter.cs`
- `server/PtkMcpServer.Tests/AuditOtlpHttpExporterTests.cs`

## Guard proof

- Before the production change, the exact
  `SecureConnectionError -> IOException` case failed because it returned
  `Blocked` instead of `Retry`. Removing the category-only check made it pass
  while the same category with `AuthenticationException` remained blocked.
- The wrong-CA and wrong-hostname integration guards plus the new classifier
  guard passed 3/3 on macOS and Windows at the exact code commit.
- The full macOS .NET suite passed 926/926.

## Coder dispute (if any)

None. Independent live probes on macOS and Windows .NET 10.0.9 produced
`SecureConnectionError -> IOException -> SocketException` for peer reset and
`SecureConnectionError -> AuthenticationException` for certificate rejection.

## Known gaps

None identified. Ambiguous TLS-handshake failures remain retryable without
advancing a checkpoint; authenticated certificate failures remain durably
blocked.

## Reviewer comments

Claude Code 2.1.207 reviewed fixed head
`49971d6ce5cb246d2283eab052163ae85a5b5c87` against
`78e256ca0f3b1253aa97dd984f1d913429ea452a`, `guard_confirmed=true`, verdict
`reopened`, recorded 2026-07-12T15:35:30Z. It cited the broad
`SecureConnectionError` classification and the durable same-identity retry
block as a transient, model-independent path to process-lifetime export loss
and eventual fail-closed admission.
