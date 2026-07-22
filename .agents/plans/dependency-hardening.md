# Plan: dependency currency and advisory remediation

**Status:** IMPLEMENTATION ACTIVE. Owner GO received 2026-07-22. Dependency
inventory cutoff `2026-07-22T17:09:14Z` was frozen at baseline `637be3c`.
The owner approved updating through current stable major versions, including
the xUnit v2-to-v3, Coverlet 6-to-10, and SQLitePCLRaw 2-to-3 migrations. The
owner rejected any policy that makes vulnerability advisories build- or
install-blocking: users retain the choice to build when an advisory has no
patched version. This work runs before resilience R6 and does not authorize a
push, merge, release, or installed-payload update.

## Outcome

Bring every repository-managed dependency to the current stable version at a
single recorded inventory cutoff, replace deprecated dependencies with their
current stable successor, and remove the current vulnerable
`System.Security.Cryptography.Xml` graph. Preserve application behavior, test
coverage, package layout, and all existing security boundaries.

The completion claim is a point-in-time claim tied to the recorded cutoff and
exact commit. NuGet vulnerability, deprecation, and currency queries remain
operator evidence, not a permanent build gate. If a later zero-day has no
patched stable release, restore/build/install continue with the ordinary
visible warning; the warning is recorded as a residual risk rather than
suppressed or mislabeled as clean.

## Evidence and current graph

Inventory was queried from NuGet.org, the PowerShell Gallery, and the official
GitHub Actions repositories on 2026-07-22. Immediately before implementation,
record a UTC cutoff and rerun every query below. Freeze that result as the
slice's target set; do not chase releases published after the cutoff.

The server graph currently has one vulnerable transitive package:

```text
PtkMcpServer
└── Microsoft.PowerShell.SDK 7.6.3
    ├── System.Security.Cryptography.Xml 10.0.6
    ├── Microsoft.Windows.Compatibility 10.0.5
    │   └── System.Security.Cryptography.Xml 10.0.6
    └── System.ServiceModel.* 10.0.652802
        └── System.Security.Cryptography.Xml 10.0.6
```

`dotnet nuget why` confirms that this is the only route into
`System.Security.Cryptography.Xml`. NuGet reports five high-severity
advisories against 10.0.6. The mini-SIEM solution currently has no vulnerable
direct or transitive NuGet package.

The exact current-to-target inventory as of the draft is:

| Dependency | Current | Stable target | Owning surfaces |
|---|---:|---:|---|
| Microsoft.PowerShell.SDK | 7.6.3 | 7.6.4 | `PtkMcpServer` |
| System.Security.Cryptography.Xml | transitive 10.0.6 | resolved 10.0.10 | conditional direct security floor in `PtkMcpServer` |
| Microsoft.Extensions.Hosting | 10.0.9 | 10.0.10 | server and guardian |
| ModelContextProtocol | 1.4.0 | 1.4.1 | server and guardian |
| Microsoft.CodeAnalysis.CSharp | 5.0.0 | 5.6.0 | guardian architecture tests |
| xunit | 2.9.3, deprecated | replace with xunit.v3 3.2.2 | all five .NET test projects |
| xunit.runner.visualstudio | 3.1.4 | 3.1.5 | all five .NET test projects |
| Microsoft.NET.Test.Sdk | 17.14.1 | 18.8.1 | all five .NET test projects |
| coverlet.collector | 6.0.4 | 10.0.1 | server and SIEM test projects |
| SQLitePCLRaw.bundle_e_sqlite3 | 2.1.12 | 3.0.4 | SIEM receiver |
| Pester | CI minimum 5.0.0 | exact stable 6.0.1 | PowerShell test workflow |
| actions/setup-dotnet | v5 | v6 | both CI jobs |

Already-current dependencies still belong to the final audit but need no
manifest churn: Google.Protobuf 3.35.1, Grpc.Tools 2.82.0,
Microsoft.Data.Sqlite 10.0.10, and actions/checkout v7. The workflow's
`10.0.x` SDK channel remains intentionally rolling within .NET 10; this plan
does not add `global.json` or change the target framework. Pester prereleases
are excluded.

The five .NET test projects are:

1. `server/PtkGuardianArchitecture.Tests/PtkGuardianArchitecture.Tests.csproj`
2. `server/PtkMcpGuardian.Tests/PtkMcpGuardian.Tests.csproj`
3. `server/PtkMcpServer.Tests/PtkMcpServer.Tests.csproj`
4. `server/PtkMcpServer.Tests/SiemConformance/PtkMcpServer.SiemConformance.Tests.csproj`
5. `siem/PtkSiemReceiver.Tests/PtkSiemReceiver.Tests.csproj`

The standalone producer-conformance project is not a member of either
solution, so every inventory and verification pass must name it explicitly.

## Scope and invariants

- Update every direct NuGet dependency at the cutoff, including approved major
  migrations and deprecated-package replacement. A transitive dependency is
  changed only through its owning direct dependency or through one explicit
  direct security floor when upstream still resolves a vulnerable version.
- Preserve every existing `PrivateAssets` and `IncludeAssets` boundary.
  `Grpc.Tools` remains build-only/private in both consumers; test runners,
  collectors, and Roslyn remain non-product dependencies.
- Keep explicit versions in the existing project files. Do not introduce
  Central Package Management, lock files, floating NuGet versions, broad
  `Directory.Build.props` policy, or package-update automation in this slice.
- Do not add `WarningsAsErrors`, `NuGetAuditLevel`, advisory suppression,
  `NoWarn`, a restore/install refusal, or a runtime advisory gate. NuGet's
  ordinary advisory output remains visible and non-blocking.
- Use only stable releases. Do not select previews, release candidates, alpha
  packages, nightly feeds, or unpublished source builds to manufacture a clean
  audit.
- Make only compatibility edits directly required by the upgraded dependency.
  Do not change public MCP schemas, audit bytes, storage schema, recovery
  semantics, package roles, runtime defaults, or test intent.
- Preserve test discovery. The xUnit migration must not make tests disappear,
  change skipped-platform intent, or weaken assertions to accommodate new APIs.
- Do not resolve the separate ARM64 MSBuild-only `Grpc.Tools` launch failure in
  this plan. Grpc.Tools is already current at the draft cutoff; use direct x64
  Linux for clean acceptance while retaining the ARM64 blocker accurately.
- Each dependency family below is one finding and one commit. A slice is not
  committed until its focused restore, audit, build, and behavior checks pass.
  Never batch later green slices into an earlier failed migration.

## Implementation slices

### 0. Freeze the inventory cutoff

Before any manifest edit, record UTC time, NuGet sources, SDK/runtime versions,
and the exact outputs of these commands:

```text
dotnet package list --project server/PtkMcpServer.slnx --outdated --format json
dotnet package list --project server/PtkMcpServer.slnx --deprecated --include-transitive --format json
dotnet package list --project server/PtkMcpServer.slnx --vulnerable --include-transitive --format json
dotnet package list --project siem/PtkSiem.slnx --outdated --format json
dotnet package list --project siem/PtkSiem.slnx --deprecated --include-transitive --format json
dotnet package list --project siem/PtkSiem.slnx --vulnerable --include-transitive --format json
dotnet package list --project server/PtkMcpServer.Tests/SiemConformance/PtkMcpServer.SiemConformance.Tests.csproj --outdated --format json
dotnet package list --project server/PtkMcpServer.Tests/SiemConformance/PtkMcpServer.SiemConformance.Tests.csproj --deprecated --include-transitive --format json
dotnet package list --project server/PtkMcpServer.Tests/SiemConformance/PtkMcpServer.SiemConformance.Tests.csproj --vulnerable --include-transitive --format json
```

Also record the current stable Pester Gallery version and latest stable major
release for each `actions/*` reference. If the rerun differs from the draft
table, update this plan's target table before code changes. A newer stable
major remains inside the owner's approved boundary; record its compatibility
cost explicitly rather than silently retaining the older draft target.

### 1. Repair the PowerShell SDK security chain

Update `Microsoft.PowerShell.SDK` in `PtkMcpServer` to the cutoff target.
Restore, then use `dotnet nuget why` and the generated assets graph to inspect
every route to `System.Security.Cryptography.Xml`. If the SDK still resolves a
vulnerable version, add one explicit `System.Security.Cryptography.Xml`
reference at the current stable .NET 10 patch with a comment naming it as the
security floor. Do not add duplicate overrides to test projects.

The focused guard is the complete `RunspaceHostTests` class plus the
PowerShell-SDK architecture/dependency guard. Prove warm state, module loading,
serialization, cancellation, timeout, and native command behavior before the
commit. The vulnerable query must return no advisory for the repaired server
graph.

### 2. Update Microsoft.Extensions.Hosting

Update both runtime references together so guardian and host cannot publish a
mixed hosting stack. Run production composition/startup tests, the guardian
architecture dependency boundary, and the stdio handshake. No hosting API or
dependency-injection lifetime may change merely to make the package compile.

### 3. Update ModelContextProtocol

Update server and guardian references together. Run public contract/catalog
tests, MCP adapter tests, strict initialize/list/call tests, architecture
dependency tests, and the complete stdio handshake. Public tool names, schemas,
instructions, result shapes, and stdio framing must remain byte-compatible
except for package metadata that tests already treat as noncontractual.

### 4. Update the Roslyn architecture-test compiler

Update `Microsoft.CodeAnalysis.CSharp` only in the architecture-test project.
Run the complete architecture suite. If the new compiler reports syntax or
semantic facts differently, update the inventory/parser only when repository
source proves the new result correct; never weaken a forbidden-reference or
source-boundary assertion.

### 5. Migrate xUnit v2 to xUnit v3

Replace `xunit` with `xunit.v3` at the cutoff target in all five test projects
and update `xunit.runner.visualstudio` in the same commit. Preserve runner
private-asset metadata. Make the smallest source adaptations required by xUnit
v3 APIs, especially async lifetime and assertion signatures; do not rewrite
test organization or timing policy.

Run discovery before and after migration and compare fully qualified test
names, not only totals. The frozen listed-identity baseline is architecture 73,
guardian 429, server 1,861, SIEM 91, and producer conformance 6. Runtime data
enumeration produces full-suite totals of architecture 73, guardian 436,
server 1,868, SIEM 91, and producer conformance 6. Platform skips may differ
only according to their existing OS predicates. Every missing, renamed, or
newly skipped test is a blocker until explained by an intentional existing
source identity.

### 6. Update Microsoft.NET.Test.Sdk

Update the test SDK in all five test projects. Run each project explicitly and
through both solution entry points. Confirm that VSTest/Microsoft Testing
Platform selection does not change discovery, filtering, exit codes, console
failure reporting, or CI invocation semantics.

**Implemented atomically with Slice 5.** With `xunit.v3` 3.2.2 and
`xunit.runner.visualstudio` 3.1.5 installed, Test SDK 17.14.1 built all five
projects but `dotnet test` rejected the resulting assemblies before discovery.
A green xUnit-only commit therefore did not exist; Test SDK 18.8.1 is part of
the same compatibility commit. Exact sorted listed identities match the
pre-migration `cab73df` snapshot at all five counts above, and explicit plus
solution-entry-point macOS runs retain every runtime-enumerated row.

The v3 analyzers expose 1,302 distinct `xUnit1051` cancellation-token
locations. They remain visible: this migration neither suppresses them nor
blindly replaces the tests' deliberate timeout and cancellation tokens. The
concurrent solution run also proved that the cold-background PID marker can
exist before `WriteAllText` publishes its contents. The compatibility edit
keeps the existing five-second retry budget but waits for a valid positive PID;
the failing concurrent run and the subsequent green run prove that exact
synchronization boundary. At this slice, all three package graphs report zero
deprecated and zero vulnerable packages, and the outdated query reports no
test-stack package; only the separately owned Coverlet and SQLite targets
remain.

### 7. Update Coverlet collector

Update the collector in the server and SIEM test projects while preserving its
private/non-product asset behavior. Run both projects with ordinary `dotnet
test` and one scoped collection smoke per project. The ordinary suite must not
start producing coverage artifacts in tracked paths or change test discovery.

**Implemented.** Coverlet 10.0.1 preserves the existing test-only package
shape. Ordinary macOS runs pass server 1,868 and SIEM 91. Scoped `OutputStore`
and `SqliteIngestStore` collections pass 3 and 11 tests and each emit exactly
one non-empty Cobertura file to an external temporary results root (10,230,859
and 554,893 bytes); the root is removed after validation and the repository
contains no coverage artifact. Both solution graphs remain free of deprecated
and vulnerable packages. The server outdated query is empty; the SIEM query
contains only the separately owned SQLite target.

### 8. Update the SQLite native bundle

Update the SIEM receiver's explicit `SQLitePCLRaw.bundle_e_sqlite3` override to
the cutoff target and revise the existing comment to describe the current
reason for the direct reference. Do not remove the explicit bundle merely
because `Microsoft.Data.Sqlite` has a lower minimum.

Run the entire SIEM suite on macOS, x64 Linux, and Windows. The migration-open,
WAL/FULL policy, transactional ingest/quarantine faults, replay, fork, mTLS,
and `SQLITE_FULL` tests must remain green. Confirm the native SQLite library
loads from the packaged graph on every OS and that no second bundle/provider is
present in the assets file or publish output.

**Partial verification; do not commit yet.** The 3.0.4 candidate passes all 91
SIEM tests on macOS and x64 Linux `magneto`. The restored graph resolves the
bundle, config, core, and provider uniformly to 3.0.4 and has zero outdated,
deprecated, or vulnerable packages. The transferred Linux manifest matches
local SHA-256
`a87644c4a259f6ea0809c06964c60a739977e83a4dae457dd6508f959c6edb2b`;
its disposable checkout was removed. Windows remains mandatory, but the
recorded `ASHBIAMWEB1` and `NETWATCH-01` names were both unresolvable on
2026-07-22 and no matching reachable local neighbor was available. Keep the
manifest change uncommitted until a Windows host completes the 91-test suite.

### 9. Move the PowerShell test workflow to stable Pester 6

Change CI's Pester provisioner from an ambient minimum-major check to the
cutoff's exact stable version. Prefer `Microsoft.PowerShell.PSResourceGet` when
available and retain a compatible Gallery fallback for hosted images that lack
it. Import the exact selected version before invoking tests so an older ambient
module cannot win resolution.

Run all PowerShell tests under that exact version on macOS, Linux, and Windows.
The baseline is 141 passed with two Unix-platform skips, or 142 passed with one
Windows-platform skip. Do not edit product behavior or assertions merely for a
Pester compatibility change.

### 10. Update GitHub Actions runtime dependencies

Update `actions/setup-dotnet` in both jobs to the cutoff's current stable major.
Retain the rolling .NET 10 SDK channel and the already-current checkout action.
Validate workflow syntax locally. This slice remains provisionally verified
until a separately authorized push runs all six hosted jobs; do not claim the
action upgrade complete from local test execution alone.

## Verification

After every slice, rerun the affected package inventory, focused tests, and a
clean build of the changed project, then commit that slice before continuing.
After all local slices, from a clean worktree run:

```text
dotnet restore server/PtkMcpServer.slnx --force --no-cache
dotnet restore siem/PtkSiem.slnx --force --no-cache
dotnet restore server/PtkMcpServer.Tests/SiemConformance/PtkMcpServer.SiemConformance.Tests.csproj --force --no-cache
dotnet test server/PtkMcpServer.slnx --no-restore
dotnet test siem/PtkSiem.slnx --no-restore
pwsh -NoProfile -Command "Remove-Item Env:PTK_SIEM_CONFORMANCE_MODE -ErrorAction SilentlyContinue; dotnet test server/PtkMcpServer.Tests/SiemConformance/PtkMcpServer.SiemConformance.Tests.csproj --no-restore; exit $LASTEXITCODE"
pwsh -NoProfile -Command "$env:PTK_SIEM_CONFORMANCE_MODE='in-process'; dotnet test server/PtkMcpServer.Tests/SiemConformance/PtkMcpServer.SiemConformance.Tests.csproj --no-restore; exit $LASTEXITCODE"
pwsh -NoProfile -Command "Import-Module Pester -RequiredVersion <cutoff-version>; Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal -CI"
pwsh -NoProfile -File server/test-handshake.ps1
```

PowerShell must set/remove `PTK_SIEM_CONFORMANCE_MODE` without substituting an
empty string. Run all nine NuGet currency/deprecation/vulnerability queries
from Slice 0 again and record exact output. At the cutoff graph:

- no direct package is older than its frozen stable target;
- no direct or transitive package is deprecated;
- no direct or transitive package has a known advisory;
- `System.Security.Cryptography.Xml` resolves only to the safe cutoff version;
- all five test projects retain their full pre-migration test identity set;
- restore/build produces no `NU1901`-`NU1904` warning for the cutoff graph.

Repeat the clean restore, inventory, complete server/SIEM/conformance .NET
batteries, exact Pester 6 run, and handshake on:

1. local macOS;
2. direct x64 Linux (`magneto` or an equivalent clean host); and
3. direct Windows (`ASHBIAMWEB1` or an equivalent clean host).

Use disposable exact-source checkouts for remote validation and remove them
after scoped process checks. Do not modify installed PTK payloads or host
profiles. Project-level serialization is acceptable on a constrained host,
but no competing workload may be used as authoritative timing evidence.

After separate push authorization, require the GitHub Actions Ubuntu, macOS,
and Windows matrices to pass both the server and SIEM jobs at one exact commit.
Record the run ID and SHA. No local result substitutes for the workflow-action
runtime proof.

## Completion and failure handling

- Record the cutoff, exact package/action versions, audit output, platform
  contexts, test identities/counts, and hosted run (when authorized) in
  `.agents/machines.md`. Update `.agents/state.md` and this status line with
  exact code and evidence heads.
- Remove the parked NU1903 item only when the exact accepted graph is clean. If
  a new advisory appears after the cutoff and no patched stable version exists,
  keep the warning visible, record it as residual risk, and state only the
  cutoff-qualified completion claim. Never suppress it or block user builds.
- If one migration cannot compile or preserve behavior, keep previously green
  family commits, do not skip the failed dependency and claim full currency,
  and investigate within that dependency family. After two consecutive cycles
  with no verifiable delta, stop and surface the concrete compatibility
  blocker to the owner.
- Do not push, merge, rewrite history, publish a release, install a payload, or
  begin resilience R6 without the separately required authorization.
