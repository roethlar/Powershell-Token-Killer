# rel0-1: -ServerCommand's documented [args...] form fails binding for space-separated tokens

**Severity**: LOW — the mode's primary consumers (release-artifact smokes) pass a
single binary path, but the documented multi-arg contract was unusable as written.
**Status**: Verified
**Branch**: master (direct, per this repo's recorded codex-loop precedent)
**Commit**: `c80dfbf`

## Evidence
`server/test-handshake.ps1` declared `-ServerCommand` as a named `[string[]]`
parameter; `pwsh -NoProfile -File server/test-handshake.ps1 -TimeoutSec 1
-ServerCommand dotnet exec <dll>` fails before launch with "A positional
parameter cannot be found that accepts argument 'exec'." (Reviewer ran the
repro; coder reproduced it.)

## Predicted observable failure
A release or CI smoke driving a server command with arguments via the
documented form exits in PowerShell parameter binding before Process.Start —
the artifact handshake is never exercised.

## What
The doc comment promised `-ServerCommand <exe> [args...]` in a shape PowerShell
named-array binding cannot deliver from space-separated tokens.

## Approach
Documented the real contract rather than loosening binding: multi-element
commands use PowerShell array syntax (`-ServerCommand dotnet,exec,X.dll`) from
a command context (CI `shell: pwsh` steps qualify); `pwsh -File` passes a
single executable path. The obvious alternative — `Position=0 +
ValueFromRemainingArguments` — was probed in a stub and REJECTED: positionally
passed dash-args that prefix-match script/common parameters are silently
swallowed (`-v` binds to `-Verbose`), corrupting the launched command; the
`--` end-of-parameters marker does not work under `pwsh -File` either. Loud
binding failure beats silent command corruption in smoke tooling.

## Files changed
- `server/test-handshake.ps1` — synopsis + param comment document the array
  contract and the loud-failure behavior
- `.agents/plans/release-distribution.md` — probe-results bullet records the
  contract and the VFRA rejection rationale

## Guard proof
Behavioral contract, not a new test: (a) array form verified end-to-end —
`-ServerCommand dotnet,exec,server/PtkMcpServer/bin/Debug/net10.0/PtkMcpServer.dll`
→ HANDSHAKE PASSED; (b) the reviewer's repro still fails loudly (documented
behavior); (c) default mode still passes. VFRA stub transcript demonstrated the
rejected alternative's silent `-v` swallow.

## Coder dispute (if any)
None — finding admitted; the fix chose contract documentation over binding
change, with the rejection rationale recorded.

## Known gaps
`pwsh -File` callers cannot express a multi-element server command at all
(documented limitation; single-token covers the release-artifact case). If a
future consumer needs it, revisit with full awareness of the VFRA hazard.

## Reviewer comments
codex (codex-cli 0.142.5), reviewed 26fd66f against base baef818,
2026-07-04 (UTC): 1 finding (this one), no_findings=false. Re-grade at
c80dfbf: pending.
