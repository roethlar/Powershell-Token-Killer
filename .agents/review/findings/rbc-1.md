# rbc-1: Cold PowerShell-direct background jobs lack stream redirection and CreateNoWindow

**Severity**: BLOCKER
**Status**: Fixed on `fix/rbc-1-cold-ps-job-stream-containment` (guard test green; pending owner review/merge)
**Source**: read-only codebase review 2026-07-17, head `f6a2caa`
**File**: `server/PtkMcpServer/JobManager.cs:1102-1107`

## Evidence

`CreatePowerShellStartInfo` builds the `ProcessStartInfo` with
`RedirectStandardInput = true` and `UseShellExecute = false` but does NOT set
`CreateNoWindow = true` and does NOT redirect `StandardOutput` / `StandardError`
for the cold PowerShell-direct background path. Verified directly against the
source at `JobManager.cs:1102-1107`.

This is asymmetric with the two sibling paths in the same subsystem:

- `BashProcessRunner.CreateBaseStartInfo` sets `CreateNoWindow = true` and
  redirects all three streams.
- `RtkProcessRunner.CreateStartInfo` sets `CreateNoWindow = true` and
  redirects all three streams.

The encoded PowerShell wrapper writes its own output to `plan.OutputPath`, but
a native command invoked inside the cold PS job that writes to stderr (or
stdout outside PowerShell's pipeline capture) inherits the parent's console
handles. Those handles are the MCP JSON-RPC transport (captured at
`Program.cs:94-95` before `ChildStdinGuard.DetachChildStdin()`), so leaked
bytes can corrupt the transport channel.

## Predicted observable failure

A cold background job that shells out to a native command (e.g., a build
tool that writes progress to stderr) can inject bytes into the MCP
stdio transport, corrupting the JSON-RPC channel for the owning harness
session. A child process may also flash a console window on Windows
desktop sessions.

## What

Add `CreateNoWindow = true`, `RedirectStandardOutput = true`, and
`RedirectStandardError = true` to `CreatePowerShellStartInfo`, and drain
both streams to the job's output capture (or to null if the encoded
wrapper already captures what matters). Match the containment posture
the RTK and Bash paths already enforce.

## Scope of fix

One method in `JobManager.cs`. No architectural change. The encoded
wrapper's file-based capture already handles the PowerShell pipeline
output; the fix closes the inherited-handle leak for native-command
stderr/stdout inside the cold PS job.

## Guard proof

`JobManagerTests.Cold_direct_job_host_streams_never_reach_the_transport_and_cannot_deadlock`
(`server/PtkMcpServer.Tests/JobManagerTests.cs`). Uses
`ProcessStartOverrideForTests` to capture the actual `ProcessStartInfo`
at start time and asserts `RedirectStandardOutput`,
`RedirectStandardError`, `RedirectStandardInput`, `CreateNoWindow`, and
`!UseShellExecute`. The job script writes ~96KB to each of
`[Console]::Out` and `[Console]::Error` — host-handle writes that bypass
the wrapper's `*> log` redirection exactly as a native child would —
then emits a pipeline marker. The test asserts the job exits 0 (no
>64KB pipe deadlock, proving the null-drain), the marker is captured,
and none of the host-handle bytes appear in the job's captured output
(they went to the redirected pipes, not the inherited transport
handles). Verified failing semantics against pre-fix behavior: without
redirection the bytes share the server's stdio handles; without the
drain the child blocks on a full pipe.

Fix: `CreatePowerShellStartInfo` now sets `CreateNoWindow = true` and
redirects stdout/stderr; `JobManager` drains both to null
(`DrainStreamToNullAsync`) since legitimate output flows through the
encoded wrapper's file-based capture. Full `JobManagerTests` class: 59
passed, 0 failed.

## Reviewer comments

Read-only review by Hermes subagent (execution/worker subsystem pass),
verified against source by the orchestrating session. No external
fixed-SHA review has been dispatched.