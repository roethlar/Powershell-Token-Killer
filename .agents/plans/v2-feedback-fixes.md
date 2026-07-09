# Plan: v2 feedback fixes — live-use friction from the first heavy session

**Status:** APPROVED in direction by owner 2026-07-08 ("probe away.
everything else is agreed and approved to revise the plan & loop w/
codex"), given together with the go/no-go GO decision (recorded in
`.agents/decisions.md` / archive). Process: codex review loop per slice,
as on the greenfield build. Slice 0 (the probe) ran the same day; results
are frozen below.

**Source evidence:** owner-shared live-use notes
(`F:\notes\PTK\vela_session_notes.md`, machine-local; essentials recorded
in `.agents/state.md` 2026-07-08). The note's "long work has no story"
item is already solved by greenfield D3 and needs no work here.

## Slice 0 — Probe (DONE 2026-07-08; results frozen)

Method: the built server spawned console-less (`CreateNoWindow`) over real
stdio pipes — replicating the harness spawn — driven via MCP; plus a
no-ptk console-less pwsh control. All on this Windows box, cargo/rustc =
rustup shims at `~/.cargo/bin`.

Results:

| Case | Outcome |
| --- | --- |
| `git --version` (route=pwsh) | works — control |
| `cargo --version` / `rustc --version`, route=pwsh, auto, AND raw | ALL fail: `error: command failed: 'cargo': The handle is invalid. (os error 6)` |
| `cargo --version` as a background job | works |
| console-less pwsh child running cargo (no ptk) | works |
| `'x' | cargo --version` (pipeline input → real stdin pipe) | **works** |
| `cmd /c "cargo --version"` (inherits our stdin) | fails identically |
| `cmd /c "cargo --version < nul"` (fresh NUL stdin) | **works** (the live workaround) |

**Diagnosis: the child stdin handle installed by `ChildStdinGuard` is not
usable by children.** `File.OpenHandle("NUL", ...)` returns a
NON-INHERITABLE handle; `SetStdHandle` stores its value, and children
launched by the warm runspace receive that value without the handle
existing in their handle table. Programs that never touch stdin (git)
run fine; rustup shims duplicate their std handles at startup and die
with os error 6. Every foreground path fails (shaping/routing are not
involved — raw fails too); jobs are unaffected (their stdin is a real
redirected pipe). The existing ChildStdinGuard e2e never caught this
because it asserts the stdin-reading call RETURNS (no hang), not that it
SUCCEEDS — an instant invalid-handle failure also returns fast.

## Slice 1 — Inheritable NUL stdin (the os-error-6 class)

Make the guard's NUL handle inheritable on Windows
(`SetHandleInformation(handle, HANDLE_FLAG_INHERIT, HANDLE_FLAG_INHERIT)`
after opening, before `SetStdHandle`; the Unix `open`/`dup2` leg already
yields inheritable fds and never showed the bug).

Guard: extend the spawned-server e2e to assert the stdin-reading native
(`cmd /c sort` / `cat`) **succeeds with no error text**, not merely
returns — this is exactly the assertion whose absence hid the bug, and it
needs no rust toolchain on CI. Prove red against the non-inheritable
handle, green with the fix. Manual check on this box: `cargo --version`
via ptk_invoke route=pwsh and route=auto both return the version string.

## Slice 2 — UTF-8 native output decoding (mojibake)

Live symptom: `ΓÇö` for em-dash — the hosted, console-less server decodes
native stdout with the OEM codepage (cp437/850) while modern tools emit
UTF-8. Pin the decoding at the host: set `Console.OutputEncoding` (and
the runspace's `$OutputEncoding` for the stdin side) to UTF-8 (no BOM) in
`RunspaceHost` — process-wide and idempotent there, so in-proc tests
exercise it, not only the spawned server. Trade-off accepted: tools that
emit genuine OEM output would now mojibake instead — modern toolchains
emit UTF-8, and `raw=true`/jobs (own-file logs, already UTF-8) remain
escape hatches.

Guard: a native child emitting a known non-ASCII sequence (a pwsh child
printing an em-dash as UTF-8) round-trips intact through `ptk_invoke`;
red under OEM decoding, green with the pin.

## Slice 3 — Teach and noise polish

- **Timeout message addendum:** after a recycle, command/PATH resolution
  may differ in the fresh runspace — say so and point at `ptk_state`
  (live use lost a debugging detour to this exact surprise).
- **rtk nag strip:** drop `[rtk] /!\ ...` banner lines from shaped
  output (they rode along in routed results; pure noise after first
  sight).
- **stderr-swallow probe (timeboxed):** live use reported route=auto
  sometimes losing a failed native's stderr where route=pwsh showed it.
  The slice-0 probe saw rtk SURFACE the error (`[rtk: The handle is
  invalid...]`), so this may be shape-dependent; probe, fix only if a
  concrete repro emerges, otherwise record the null result here.

Guards: message-text test (red with the old string), nag-strip test with
a banner-emitting rtk stub; probe results recorded in this plan.

## Slice 4 — Greenfield D5 (CLI-face retirement), now unblocked

The GO decision closes the window D5 was deferred behind; it executes
under the already-approved greenfield plan (retire `ptk` CLI verbs and
`docs/usage.md`, prune their tests, README single-surface rewrite, close
the decision bookkeeping). Listed here only for sequencing: after slices
1–3.

## Out of scope here

Shared multi-client host (+ signals): parked on its own measured-pain
criterion — see the Open Decision. Warm-runspace slice 7 (AD/EMS/EXO
validation) and release-plan slice 3: their own tracks; owner sequences.

## Verification

Per slice: the recorded battery (Pester, `dotnet test`, handshake when
server-facing), guard proofs (red→green), codex loop to NO FINDINGS or
convergence, one commit per slice / per finding.
