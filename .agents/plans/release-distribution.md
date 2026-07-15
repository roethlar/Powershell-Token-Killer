# Plan: Release distribution — prebuilt binaries + one-line installer

**Status:** APPROVED by owner 2026-07-04 (in-session go after resolving the
open questions and a codex review loop on the plan text — 3 LOW
doc-consistency findings, all fixed and accepted; see
`.agents/review/index.md`). Slices execute in order, codex review loop per
code slice. The exact plan-scoped push authority is recorded under Owner
logistics; every push outside that scope remains ask-first.
Open questions resolved by owner 2026-07-04 (see Resolutions): 5 RIDs,
version v0.2.0, install root `~/.ptk` everywhere, winget as the eventual
primary Windows path with readiness built into v0.2.0. One question remains
deliberately open: the hook default in the public installer (owner: "decision
for later" — must close before slice 4 finalizes installer UX).
The owner confirmed on 2026-07-15 that nothing has shipped and this development
environment is the only current use, then delegated the resilience packaging
cutover. Slices 0-2 remain valid development evidence; slice 3 is now ordered
after resilience R7 and packages/registers only its matched guardian layout,
with no legacy migration or side-by-side public server path.
**Decision basis:** owner direction 2026-07-04 (recorded as an amendment to the
continuation decision in `.agents/decisions.md`): the current
run-from-the-repo-checkout install story (`dotnet run --project ...`) is not
acceptable for a release. The first public release has no current target date;
when authorized, it must ship as prebuilt per-platform binaries with a one-line
installer (tier 3). The publish-and-register script (tier 1) and .NET tool
packaging (tier 2) are **dev-only** paths, never the public install story.

## Goal

For the first public release, a user on Windows, macOS, or Linux installs ptk
without cloning the repo or having the .NET SDK:

```powershell
# Windows
irm https://raw.githubusercontent.com/AlsoBeltrix/PowerShell-Token-Killer/master/install.ps1 | iex
```

```sh
# macOS / Linux
curl -fsSL https://raw.githubusercontent.com/AlsoBeltrix/PowerShell-Token-Killer/master/install.sh | sh
```

The installer downloads the release asset for the platform from GitHub
Releases, verifies its checksum, lays it out under a stable install dir,
registers the public MCP guardian with Claude Code when the `claude` CLI is
present (prints the command otherwise, plus a Codex `config.toml` snippet), and
can optionally install the redirect hook. `--uninstall` reverses all of it.

Dev workflows keep two non-public paths: a publish-and-register script that
installs the current checkout's HEAD (tier 1 — resilience R7 upgrades this same
generator and layout mode before release CI reuses it), and local .NET tool
packaging (tier 2, cuttable).

ptk is a primarily-Windows tool, and **winget is ultimately the primary
Windows install path** (owner, 2026-07-04). v0.2.0 ships the scripts above;
they are written as winget's future engine, and the winget manifest is the
named follow-up once v0.2.0's assets are published (see the winget track
section).

## Grounded facts this design stands on

- **Self-contained host publish works.** Recorded 2026-07-03
  (`.agents/state.md`): a self-contained publish of the current server
  xcopy-deploys to boxes with no .NET installed; the embedded engine is
  PowerShell 7.6 (SDK 7.6.3, net10.0, ModelContextProtocol 1.4.0 per
  `server/PtkMcpServer/PtkMcpServer.csproj`). This proves the private-host
  payload baseline, not the still-unbuilt matched guardian archive.
- **Module discovery already supports an installed layout.**
  `RunspaceHost.ResolveModulePath` probes upward for
  `src/PwshTokenCompressor.psd1` — today from cwd first, then
  `AppContext.BaseDirectory` — and `PTK_MODULE_PATH` is an explicit override.
  The cwd-first order is wrong for a shipped binary (a session inside a repo
  containing that path would win over the installed copy), which is why the
  Design commitments flip the probe to binary-dir-first; after the flip,
  registration needs no env block and `PTK_MODULE_PATH` remains the explicit
  override only (see the Registration commitment — that bullet is the single
  registration contract).
- **The hook installer is layout-portable.** `scripts/ptk_init.ps1` resolves
  `ptk-hook.ps1` from its own `$PSScriptRoot`, so shipping `scripts/` inside
  the release artifact lets the installed copy register the installed hook
  path. (Verify in slice 1; expected zero code change.)
- **The handshake script is the artifact smoke test.** It originally launched
  only via `dotnet run` / `dotnet exec` of a Debug dll; the third mode that
  drives an arbitrary server binary landed as the slice-0 tooling change
  (`-ServerCommand` — see the probe results below).
- **Repo is PUBLIC** (verified 2026-07-04 via `gh repo view`): anonymous
  release-asset downloads and raw.githubusercontent.com installer URLs work.
- **No CI exists** (recorded in `.agents/repo-guidance.md`); release
  automation requires building it, and workflow iteration only runs on pushed
  refs — see Owner logistics.

## Design commitments

- **Directory-layout publish, not single-file, no trimming.** The PowerShell
  SDK is reflection-heavy and does not support trimmed or single-file publish
  reliably; the artifact is a plain publish directory in an archive. Size
  (~70–120 MB per RID) is acceptable for GitHub Releases; slice 0 measures it.
- **Every shipped artifact is smoke-tested on its own OS/arch** by the
  extended handshake script through the actual packaged guardian entry — in CI
  where a native runner exists, otherwise on owner hardware during the rc
  rehearsal (the owner has machines for every v0.2.0 RID except osx-x64, which
  is not shipped). No untested asset ships; an unverifiable RID is dropped,
  not shipped blind — and the drop is logged, never silent.
- **One ptk home: `~/.ptk`, on every platform, for every end-user install
  method.** (The dev-only tier-2 packaging rehearsal is the sole, explicit
  exemption on payload location — see slice 5; config resolution is
  install-method-independent and stays at `~/.ptk` even there.)
  Payload and config live together; no `--dir` override in v0.2.0 (an
  override is variance by another name — added later only on real demand,
  with whole-home-moves-together semantics). Inside it:
  - installer-owned, replaced wholesale on upgrade: `bin/` (the exact matched
    `PtkMcpGuardian(.exe)`, private `PtkMcpServer(.exe)`, shared dependencies,
    and required platform containment helpers), `src/` (module), `scripts/`
    (`ptk-hook.ps1`, `ptk_init.ps1`, and `dev-install.ps1` — added in slice 1 as the
    payload-local uninstall entry point the Windows ARP UninstallString
    targets, until a future separately designed installer entry hosts it),
    `VERSION`
  - user-owned, NEVER touched by install/upgrade/uninstall (only by an
    explicit `--purge`): everything else — the future `policy.psd1`
    (destructive-cmdlet allow/deny lists, design parked in
    `.agents/decisions.md`) and any later config/state land here, at the
    same path regardless of how ptk was installed.
- **The runtime payload is one matched unit.** Guardian, private host, shared
  contracts, and required containment helpers come from the same reviewed
  source/version set. Cutover or upgrade requires a new MCP session; installers
  refuse to mutate an in-use payload. They stage and validate the complete
  `bin/` tree and snapshot the prior installer-owned payload/registrations,
  activate the payload as a unit, and mutate harness registrations only as the
  final cutover step. Any activation/registration failure restores the complete
  snapshot before returning failure. Missing, stale, or mixed components fail
  before MCP initialization; installers never merge individual binaries into
  an old tree or report success after an unconfirmed rollback.
- **Package managers conform or don't ship:** any future package-manager
  distribution must deliver into `~/.ptk` (installer-type packaging driving
  our own install logic — the winget track below). Portable-type packages
  that land in a manager-owned directory fork the layout and are ruled out.
- **Module discovery becomes binary-relative.** `ResolveModulePath` flips to
  probe `AppContext.BaseDirectory` upward BEFORE cwd, so an installed server
  never silently loads a checkout's module because the session happens to sit
  in a repo containing one, and the payload is position-independent (`bin/`
  finds its sibling `src/` wherever the home is). This is the plan's one
  deliberate server-code change beyond the handshake mode; `PTK_MODULE_PATH`
  keeps its explicit-override semantics.
- **Registration** is `claude mcp add ptk --scope user` pointing at the
  absolute `PtkMcpGuardian` path (no env block needed once discovery is
  binary-relative); remove-then-add so re-installs and dev→release switches
  never collide. Registration and hook wiring are always produced by the
  scripts shipped in the payload's `scripts/` — one generator for every
  install method, nothing hand-maintained.
- **Windows installs are winget-ready from v0.2.0:** every Windows install —
  script or future winget — writes the per-user Add/Remove Programs registry
  entry (HKCU uninstall key: DisplayName, DisplayVersion = release version,
  UninstallString), and uninstall removes it. That entry is what
  `winget upgrade`/`winget uninstall` track. The install logic is written so
  a future separately designed installer entry can host it without requiring
  a separately installed PowerShell; that entry itself is post-v0.2.0.
- **Installers refuse to run elevated** (root / Administrator) with a clear
  message: ptk is a per-user tool, and the warm runspace inherits the
  harness's privileges — an elevated install invites root-owned files and an
  elevated-execution footgun. The security-posture docs gain the matching
  sentence (slice 6).
- **rtk is recommended, never bundled.** It is a separate product with its own
  installers; ptk already degrades visibly without it. The installer detects
  rtk on PATH and prints an install pointer when absent.
- **The release tag and the published release are owner actions.** CI builds
  and smoke-tests everything into a **draft** release; publishing it (and
  pushing the `v0.2.0` tag that triggers it) needs an explicit owner go, per
  `.agents/push-policy.md`. The product GO does not authorize a tag or public
  release by itself.
- **Distribution does not implement resilience.** The resilience plan owns
  guardian/host behavior and R7 cutover. This plan consumes that reviewed,
  tested matched layout after it lands; distribution-specific code remains
  limited to packaging, registration, installers, and release validation.

## Slices

0. **Probe, then freeze (evidence + test tooling only).**
   - Extend `server/test-handshake.ps1` with a `-ServerCommand <exe> [args]`
     mode (guard: both existing modes still pass; new mode passes against a
     local publish). The only code in this slice.
   - Local publish probe on this Mac (osx-arm64): publish → canonical layout →
     handshake against the binary via the new mode; measure archive size and
     cold-start latency vs `dotnet run`. Confirm the macOS apphost is ad-hoc
     signed (it is by default from dotnet publish) and runs from a
     curl-downloaded archive without Gatekeeper interference.
   - `claude mcp add --env` syntax verified against the installed CLI.
   - CI probe on a side branch: minimal workflow proving runner facts —
     .NET 10 SDK via setup action, Pester 5 availability/pinning per OS,
     macos-latest is arm64, and the ARM runners (`windows-11-arm`,
     `ubuntu-24.04-arm`) needed for the win-arm64/linux-arm64 smokes,
     artifact upload. Results recorded here before slices 2–4 are built.
1. **Discovery flip + canonical layout + dev install script (tier 1,
   dev-only).** First the server change this layout depends on: flip
   `ResolveModulePath` to probe the binary's directory upward before cwd
   (guard: dotnet test covering both orders — installed layout wins over a
   cwd checkout; cwd probe still works when nothing ships alongside the
   binary). Then `scripts/dev-install.ps1`: publish the current checkout
   self-contained for the local RID, produce the `~/.ptk` layout, register
   with `claude mcp` (and print the Codex snippet), write the Add/Remove
   Programs entry on Windows, `-Hook` optional, `-Uninstall`, `-LayoutOnly
   -OutputDir` mode that release CI reuses so dev and release artifacts are
   the same layout by construction. Install logic is factored for a future
   separately designed installer entry. Guard: handshake
   `-ServerCommand` against the installed binary; uninstall removes the
   registration, the ARP entry, and the payload while leaving non-payload
   files; existing repo-based `.mcp.json` flow untouched. This completed
   single-server layout and registration are transitional development state;
   resilience R7 evolves the same generator/layout mode to the matched guardian
   package before release slice 3 reuses it.
2. **CI workflow (tests).** `.github/workflows/ci.yml` on push/PR:
   ubuntu/windows/macos matrix running the Pester suite, `dotnet test`, and
   the handshake. Guard: green on all three OSes on the PR/branch.
3. **Release workflow.** `.github/workflows/release.yml` on `v*` tags:
   after resilience R7, per-RID publish using its matched guardian layout mode
   on the RID's native runner —
   win-x64 (windows-latest), win-arm64 (windows-11-arm), linux-x64
   (ubuntu-latest), linux-arm64 (ubuntu-24.04-arm), osx-arm64 (macos-latest);
   no osx-x64 — launch and handshake only through the packaged guardian,
   prove missing or mismatched private components refuse initialization,
   verify that direct no-argument server launch is unavailable, archive as
   `ptk-<version>-<rid>.zip|.tar.gz`, generate `SHA256SUMS`, assemble a
   **draft** GitHub Release. Version stamped from the tag via `-p:Version`
   (and into the ARP entry at install time). Guard: an rc pre-release tag
   (e.g. `v0.2.0-rc.1`) yields a complete draft with every asset
   smoke-tested and checksummed; any RID whose runner smoke cannot run is
   covered on owner hardware in slice 7 before the release publishes, or
   dropped with a logged reason.
4. **Installers.** `install.ps1` (Windows PowerShell one-liner) and
   `install.sh` (POSIX sh for macOS/Linux): detect OS/arch, refuse to run
   elevated, download the pinned-or-latest release asset, verify against
   `SHA256SUMS`, stage and validate the complete matched payload, refuse an
   in-use installation, snapshot the prior installer-owned payload and harness
   registrations, activate the payload as one unit, then register the guardian
   as the final mutation when `claude` is present (otherwise print the command).
   Preserve the already approved behavior for every other harness: generate
   only its authorized registration or guidance, and make every generated path
   point at the guardian. Any activation/registration failure restores the
   complete snapshot before the installer returns failure. Write the ARP entry
   on Windows, print the Codex snippet and an rtk recommendation when rtk is
   absent, install the hook per the deferred hook-default decision (must be
   closed before this slice ships; hook requires `pwsh` and is skipped with a
   message when missing — the matched payload never needs an installed
   PowerShell), `--uninstall` (removes payload, registrations, ARP entry; leaves
   user config; `--purge` for everything). Guard: from the rc draft release, a
   one-line install on each supported OS ends with the handshake passing
   through the installed guardian; injected failure at every activation/
   registration boundary leaves the prior payload and registrations byte-
   identical; uninstall leaves no registration, ARP entry, or payload.
5. **.NET tool packaging (tier 2, dev-only, CUTTABLE).** If retained,
   `PackAsTool` carries the same matched guardian/host/helper set and may not
   register or expose the private server directly. Ship the module inside the
   package so the binary-relative probe finds it; install only from a local
   source (`dotnet tool install -g
   --add-source`); explicitly NOT published to NuGet.org for v0.2.0. This is
   a packaging rehearsal for developers: its payload lives in the .NET tool
   store (that is how `dotnet tool` works), which is the one recorded
   exemption from the `~/.ptk` payload rule — it is never distributed to
   users, and config still resolves to `~/.ptk`. Guard: local tool install
   passes the handshake via the tool command. It remains the first thing
   dropped under release pressure because it is off the public critical path.
6. **Docs + release prep.** README "Setup" becomes the one-liner install
   (repo-checkout and dev paths move to a dev/contributor doc);
   `server/README.md` documents the `~/.ptk` layout, binary-relative
   discovery, and the elevated-harness sentence in the security posture
   (the runspace inherits the harness's privileges — root/Admin harness
   means root/Admin shell); release-notes draft; `ModuleVersion` bumped to
   0.2.0 to match the release; `.agents/repo-guidance.md` "No CI" statement
   and `.agents/repo-map.json` verification entries updated (drift fix rides
   in the same slice that creates the drift).
7. **RC rehearsal + release (owner-gated).** After every prerequisite lands:
   rc tag → draft release → one-line installs exercised on the owner's machines
   (hardware exists for every shipped RID), any runner-unsmokable ARM artifact
   verified there, friction fixed, then a separate owner go for the `v0.2.0`
   tag and public release.

Process note: the codex review loop per code slice (owner-set precedent,
2026-07-04) applies to slices 0–5; workflow YAML counts as code here.

## Slice 0 probe results (2026-07-04, this Mac — osx-arm64)

- **Handshake tooling:** `-ServerCommand <exe> [args...]` mode added to
  `server/test-handshake.ps1` (parameter sets keep the three launch modes
  mutually exclusive). Contract: a named array parameter — multi-element
  commands use PowerShell array syntax (`-ServerCommand
  dotnet,exec,PtkMcpServer.dll`, verified end-to-end) from a command
  context (CI `shell: pwsh` steps qualify); `pwsh -File` binds arguments
  literally, so from `-File` pass a single executable path. Space-separated
  tokens after `-ServerCommand` fail binding loudly by design — the
  alternative (ValueFromRemainingArguments) was probed and REJECTED: it
  silently swallows server args that prefix-match script/common parameters
  (`-v` binds to `-Verbose`), corrupting the launched command. Guard ran:
  default and `-UseRegistrationCommand` modes still pass; the new mode
  passes against a local publish.
- **Publish:** `dotnet publish server/PtkMcpServer -c Release -r osx-arm64
  --self-contained` (plain directory layout — no trimming, no single-file)
  → 558 files, 129 MB on disk; ~2 s incremental with a warm NuGet cache.
- **Canonical layout:** `bin/` (publish output) + `src/` (module) +
  `scripts/` + `VERSION` assembled in a scratch dir. Handshake passes via
  the new mode from the repo root and from a neutral cwd. From the neutral
  cwd the module resolves through the `AppContext.BaseDirectory` upward
  probe (`bin/` → home's `src/`) — verified explicitly:
  `(Get-Module PwshTokenCompressor).Name` returns the module inside
  `ptk_invoke` and no module-not-found warning appears on stderr. The
  layout is position-independent today whenever no repo checkout shadows
  it from cwd; the slice-1 probe-order flip remains required for exactly
  that shadowing case. stdout is byte-clean JSON-RPC; stderr carries
  Microsoft.Hosting info logs (harmless — MCP ignores stderr). The server
  exits 0 on stdin EOF (clean shutdown; useful fact for CI smokes).
- **Archive size:** 45 MB `.tar.gz` / 46 MB `.zip` for the full layout —
  well under this plan's ~70–120 MB working estimate, which measurement has
  now superseded: the estimate was simply high for the shipped asset (the
  unpacked directory is 129 MB; the compressed archive is what ships).
- **Cold-start:** single-sample wall times of the full handshake script
  (initialize, tools/list, ping, two invokes — measured around the whole
  `pwsh` invocation, so these are script wall times, not server start
  latency): 2.5–2.8 s (two runs) against the published binary vs 5.2 s
  (one run) via the registration `dotnet run`, whose per-launch build
  check accounts for the delta. Directional conclusion only: the published
  binary removes the build-check cost from session start.
- **Signing/Gatekeeper:** the apphost is ad-hoc signed by default
  (`codesign -dv`: `Signature=adhoc`, CodeDirectory `flags=0x2(adhoc)`).
  A curl-downloaded archive carries NO `com.apple.quarantine` (only
  `com.apple.provenance`); extract + run passes the handshake with zero
  Gatekeeper interference. Method caveat: the download was loopback plain
  HTTP — curl never applies quarantine whatever the endpoint, so this
  should hold for the real URL, but the actual GitHub-Releases download
  gets exercised once `install.sh` exists (slices 4/7). Contrast,
  supporting the Risks entry (measured on this box only — Darwin 25.5,
  osx-arm64, default Gatekeeper settings): a never-executed copy with a
  hand-written quarantine xattr is killed on exec (exit 137/SIGKILL) and
  `spctl --assess --type execute` reports `rejected` — the Risks entry's
  browser-download friction is real, not hypothetical. Whether a real
  browser download hits it also depends on the extraction tool (Archive
  Utility propagates quarantine; CLI `tar` generally does not) and macOS
  version — not probed. (Re-quarantining a copy that had already run once
  did NOT block it; approval appears to be cached, mechanism not probed.)
- **`claude mcp` CLI (Claude Code 2.1.201):** `claude mcp add [options]
  <name> <commandOrUrl> [args...]`; stdio is the default transport; env
  vars via `-e/--env KEY=value`; scope via `-s/--scope local|user|project`
  (default local); `claude mcp remove <name> [-s scope]` exists, so the
  remove-then-add registration contract is expressible verbatim. The
  registration commitment (`--scope user`, absolute guardian path, no env
  block) needs nothing the installed CLI lacks.
- **CI runner probe (run 2026-07-04, `ci/probe` branch — branch since
  deleted per the owner's no-lingering-branches condition):** all five
  planned runners exist and all jobs passed — `ubuntu-latest` (X64),
  `windows-latest` (X64), `macos-latest` (**ARM64 confirmed**),
  `windows-11-arm` (ARM64), `ubuntu-24.04-arm` (ARM64). On every one of
  the five: .NET SDK **10.0.301** resolves via `actions/setup-dotnet@v4`
  with `dotnet-version: 10.0.x`; **pwsh 7.6.3 is preinstalled** (`shell:
  pwsh` works everywhere); **Pester 5.7.1 is preinstalled** — the
  Install-Module fallback exists but was needed nowhere, so slice 2 needs
  no Pester pinning step (keep the fallback for runner-image drift);
  `actions/upload-artifact@v4` works on all five (artifacts downloaded
  and verified). Two annotations to carry into slices 2–3: the v4 action
  majors (checkout/setup-dotnet/upload-artifact) emit Node 20 deprecation
  warnings — use current action majors when writing the real workflows;
  and `macos-latest` migrates to macOS 26 from 2026-06-15 (already
  ARM64; no impact on the RID set).

## Timeline

The original 2026-07-25 calendar is superseded as of 2026-07-15 because it
predated the required guardian R0-R7 work. Slices 0-2 are complete development
evidence. Release slice 3 cannot start until resilience R7 has landed and
frozen the matched package generator/layout; no replacement release date is
approved. Slice 5 remains cuttable, and native RID verification remains a
release gate rather than a reason to ship an untested asset.

## Owner logistics (needed to execute, not design questions)

- **Pushes:** CI/workflow iteration only runs on pushed refs, and the policy
  is ask-first. Requested: a standing go, scoped to this plan, for pushes to a
  `ci/*` side branch and `v0.2.0-rc.*` pre-release tags. `master` pushes and
  the final `v0.2.0` tag stay per-explicit-go. **GRANTED 2026-07-04** with
  the owner's hard condition: no branches may linger once the coding is
  done — the agent deletes every `ci/*` branch (local and remote) as soon
  as its facts/workflows land on master. Probe branches fork from
  origin/master, not local HEAD, so unpushed master work is not published
  through a side branch.
- **Real-box installer verification** remains mandatory in slice 7; CI runner
  coverage does not replace direct validation on the owner's supported-RID
  hardware.

## Resolutions (owner, 2026-07-04)

1. **RID set:** five — `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`,
   `osx-arm64`. No `osx-x64`. The owner has hardware for all five, so every
   artifact can be exercised locally in the rc rehearsal in addition to CI
   runner smokes.
2. **Version:** `v0.2.0` (the pre-release history is the implied 0.1;
   `ModuleVersion` is bumped to match in slice 6).
3. **Hook in the public installer:** DELIBERATELY OPEN — owner: "decision for
   later"; must close before slice 4 ships. The recorded tension: default-off
   risks shipping demo-ware (0/13 unprompted-adoption evidence; MCP tools
   hidden behind ToolSearch means an unhooked server goes undiscovered) vs
   default-on shipping a global settings.json edit and a deny-redirect on
   every shell call that we ourselves will have lived with for only ~5 days
   (friction log starts ~07-20). Standing recommendation: off by default
   with the installer's closing message stating the measured adoption fact
   and the `--hook` flag; revisit for the winget release with friction-log
   data.
4. **Install root:** `~/.ptk` on every platform — one ptk home for payload
   AND config, independent of install method; no `--dir` in v0.2.0; package
   managers deliver into it or don't ship. (The earlier
   `%LOCALAPPDATA%`-on-Windows recommendation was withdrawn: the ecosystem
   ptk integrates with — `~/.claude`, `~/.codex`, `~/.dotnet`, `~/.nuget` —
   already normalized home-root dot-dirs on Windows, and the harness's
   winget precedent was winget's packaging convention, not a design choice.)

## Post-v0.2.0: the winget track (named follow-up, not v0.2.0 scope)

Winget is the eventual primary Windows install path. `winget install <id>`
must run OUR install logic (installer-type manifest — winget downloads the
package and executes it silently), never a portable-type package (winget
would own the destination and fork the `~/.ptk` layout). The pieces v0.2.0
deliberately builds toward it: the ARP uninstall entry (winget's
upgrade/uninstall tracking), version stamped there from the release tag, and
install logic hostable by a future separately designed installer entry, so
self-install runs on a machine with nothing preinstalled and winget's
zip-with-nested-installer support invokes exactly that from inside the existing
release archive. Submission to
microsoft/winget-pkgs happens once v0.2.0's assets are published (manifests
need live asset URLs and go through validation lag).

## Risks

- **PowerShell SDK publish quirks** (the reason single-file/trimming are ruled
  out) could still surface per-RID at runtime; the every-artifact-smoke-tested
  commitment is the containment.
- **GitHub runner drift** (net10 SDK availability, Pester versions, macos
  arch) — slice 0's CI probe exists to catch this before the workflows are
  designed around wrong facts.
- **Unsigned binaries.** No code signing or notarization in v0.2.0. The
  curl/irm install paths avoid browser quarantine/MOTW, and dotnet ad-hoc
  signs macOS apphosts, but a user who downloads the archive via a browser may
  hit Gatekeeper/SmartScreen friction. Documented limitation; signing is a
  post-v0.2.0 track if it bites (and a prerequisite worth revisiting at
  winget submission time).
- **Guardian dependency.** The release cannot be scheduled until resilience R7
  lands and its matched package/cutover passes review. No deadline authorizes a
  single-server artifact, skipped native validation, or a partial cutover.

## Non-goals

- Shipping ON any package manager in v0.2.0 (NuGet.org, winget, Homebrew).
  v0.2.0 is GitHub Releases + installer scripts; winget-READINESS is in
  scope (ARP entry, hostable install logic), the manifest submission is the
  named post-v0.2.0 track above.
- Code signing / notarization.
- Bundling or installing rtk.
- Implementing guardian/host resilience or any unrelated server/module behavior
  inside distribution work. This plan packages the separately reviewed R7
  result; its own runtime changes remain limited to the already-landed
  handshake launch mode and module-discovery probe-order flip.
- A `--dir` install-location override (one home: `~/.ptk`).
- Auto-update mechanics (re-running the installer — or later, winget
  upgrade — is the update path).
- Building the destructive-cmdlet policy gate (still paused; this plan only
  reserves its config location: user-owned files in `~/.ptk`).
- The universal wrapper CLI (still paused).
