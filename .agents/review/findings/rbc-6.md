# rbc-6: No SIGKILL escalation for Unix process trees after SIGTERM grace

**Severity**: MAJOR
**Status**: RESOLVED — refuted at triage 2026-07-18. The reviewed code already
uses the Unix runtime's immediate SIGKILL tree termination; the claimed
SIGTERM-only grace and predicted TERM-trap survivor do not exist. No code
change. External fixed-SHA review accepted with `guard_confirmed: true`.
Merged to `master` at `315b9db` (owner-approved, docs only).
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**Files**: `server/PtkMcpServer/Execution/BashProcessRunner.cs:736-744`,
`server/PtkMcpServer/Execution/RtkProcessRunner.cs:403-430`

## Evidence

`TryKillProcessTree` (`BashProcessRunner.cs:736`) and `KillAndDrainAsync`
(both files) call `process.Kill(entireProcessTree: true)` followed by a
bounded `WaitForExitAsync` with `ProcessStopGrace = 2s`.

On Unix, `Kill(entireProcessTree: true)` sends `SIGTERM`, not `SIGKILL`.
A child that catches `SIGTERM` (a common pattern for graceful-shutdown
handlers that hang, or a malicious process) can survive the 2-second
grace. `stopped` is then set to `false` and the result reports
`InvokeDisposition.OutcomeUnknown` with "PTK will not retry it" —
correct for the audit boundary, but the process is still running.

There is no escalation to `SIGKILL` after the grace expires.

## Predicted observable failure

A Bash/RTK descendant that traps `SIGTERM` survives the containment
window, keeps running (holding resources, network connections, or
file locks), and is reported as `OutcomeUnknown`. The supervisor moves
on, but the orphaned process persists on the host.

## What

After the `SIGTERM` grace expires, escalate to `SIGKILL` for the process
tree (or for the process group if a negative PID is available). On
Unix, `kill(-pgid, SIGKILL)` reaches the whole group; the .NET
`Process.Kill` tree-walk does not escalate. A platform-specific
follow-up kill is needed.

## Scope of fix

One helper method shared by `BashProcessRunner` and `RtkProcessRunner`.
No architectural change; the `OutcomeUnknown` audit boundary is
preserved — the escalation is best-effort containment, not a retry.

## Guard proof

No product guard is required because no product behavior changes. The triage
refutation is backed by the .NET 10 runtime source and a direct host probe,
recorded below, that exercises the exact API used by both runners.

## Triage resolution (2026-07-18, head `2b3ce1a`)

The finding's signal premise is false for the target runtime:

- Both cited production paths already call
  `Process.Kill(entireProcessTree: true)`. Their two-second
  `ProcessStopGrace` bounds observation/drain after the kill; it is not a
  SIGTERM grace period.
- The official .NET 10 Unix implementation first sends SIGSTOP while it
  discovers the live tree, then sends SIGKILL to the root and each discovered
  descendant. `Process.Kill()` itself also sends SIGKILL. Source:
  <https://github.com/dotnet/runtime/blob/release/10.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Unix.cs>
  (`Kill`, `KillTree`).
- A direct probe on Darwin arm64 with .NET SDK 10.0.302 / runtime 10.0.10
  launched a Bash root that ignored SIGTERM and held a `sleep 300` child,
  called `Kill(entireProcessTree: true)`, and observed the root exit with code
  137 and the child dead in under the five-second observation bound. The
  predicted survivor did not reproduce.

The paused uncommitted implementation on
`fix/rbc-6-unix-sigkill-escalation` addresses a different condition: a child
that daemonizes and is reparented before tree discovery. A second direct probe
confirmed that such a child can outlive a call made after its root has already
exited. That is not the filed SIGTERM-escalation defect, and the proposed
server-wide process-group tracker changes lifecycle/containment behavior well
beyond the finding's approved one-helper scope. It remains preserved as WIP
and is neither accepted nor committed by this refutation. Treat daemonized-
descendant containment as a separately scoped question; do not relabel it as
the rbc-6 fix.

## Reviewer comments

Read-only review by Hermes subagent (execution/worker subsystem pass). Triage
refutation performed by Codex against the reviewed/current code, official
.NET 10 Unix source, and the installed .NET 10.0.10 runtime.

### External refutation review (dispatched, accepted)

Reviewer: codex / gpt-5.6-sol / high / standard
Harness: codex-cli 0.144.5 (headless `codex exec --sandbox read-only --json`,
model/effort pinned at dispatch; JSONL envelope + `--output-schema` payload)
Reviewed head SHA: d50adcd85d107f9f34b8c48c02f708c0322937e9
Base SHA: e766e1923bf655fc2385a095d5eec5d4a50351a5 (docs-only diff)
guard_confirmed: true (reviewer independently inspected the reviewed/current
runner code and the official .NET 10 Unix implementation, then reproduced the
SIGTERM-resistant root/child probe on .NET 10.0.10)
Verdict: **accepted** — the filed SIGTERM-only premise is false; no product
change or product guard is required for this refutation.
Timestamp: 2026-07-18T14:56:24Z
Escalation: none (T1-T5 unmatched; standard at high)
Record committed: yes (this commit)

Reviewer comments (verbatim): none.
