#Requires -Version 7
<#
.SYNOPSIS
Dev-only installer (release-distribution plan, tier 1): publishes the current
checkout self-contained and installs it into the one ptk home (~/.ptk), or
produces the canonical release layout for CI. NOT the public install story —
end users get install.ps1/install.sh against GitHub Releases.

.DESCRIPTION
Default (install): publish for this machine's RID -> replace the
installer-owned payload in ~/.ptk (bin/, src/, scripts/, VERSION) wholesale,
leaving every other file (user config) untouched -> register the server with
Claude Code at user scope (remove-then-add) -> write the Add/Remove Programs
entry on Windows -> run the full per-agent init (ptk_init.ps1: hooks,
registrations, guidance for every detected harness). One command per
machine. -Uninstall reverses all of it and keeps user files. -LayoutOnly -OutputDir <dir> only
builds the layout (release CI reuses this so dev and release artifacts are
the same layout by construction); -Rid and -Version parameterize it.

Install logic lives in small functions so a future `PtkMcpServer install`
verb can host it in-process (the binary embeds the PowerShell engine).

.EXAMPLE
pwsh -File scripts/dev-install.ps1                # install current HEAD
pwsh -File scripts/dev-install.ps1 -Hook          # ... and install the hook
pwsh -File scripts/dev-install.ps1 -Uninstall
pwsh -File scripts/dev-install.ps1 -LayoutOnly -OutputDir out/ptk-layout
#>
[CmdletBinding(DefaultParameterSetName = 'Install')]
param(
    # Deprecated, accepted for compatibility: the full per-agent init
    # (hooks, registrations, guidance - every detected harness) runs by
    # DEFAULT after a successful registration; -Hook adds nothing.
    [Parameter(ParameterSetName = 'Install')]
    [switch]$Hook,

    [Parameter(ParameterSetName = 'Uninstall', Mandatory)]
    [switch]$Uninstall,

    # Build the canonical layout into -OutputDir and stop: no home install,
    # no registration. This is the mode release CI drives per RID.
    [Parameter(ParameterSetName = 'LayoutOnly', Mandatory)]
    [switch]$LayoutOnly,
    [Parameter(ParameterSetName = 'LayoutOnly', Mandatory)]
    [string]$OutputDir,
    # Target RID for -LayoutOnly (defaults to this machine's). Cross-RID
    # publish needs no target runtime installed.
    [Parameter(ParameterSetName = 'LayoutOnly')]
    [string]$Rid,

    # Version stamped into the publish (-p:Version) and the VERSION file.
    # Release CI passes the tag version; dev installs default to
    # 0.2.0-dev.g<shortsha>.
    [Parameter(ParameterSetName = 'Install')]
    [Parameter(ParameterSetName = 'LayoutOnly')]
    [string]$Version
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$ptkHome = Join-Path $HOME '.ptk'
# Everything the installer owns and replaces wholesale on upgrade; anything
# else under ~/.ptk is user-owned and never touched here.
$payloadEntries = @('bin', 'src', 'scripts', 'VERSION')
$arpKeyPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\ptk'

function Get-PtkRid {
    $arch = switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported architecture: $_" }
    }
    $os = if ($IsWindows) { 'win' } elseif ($IsLinux) { 'linux' } elseif ($IsMacOS) { 'osx' }
    else { throw 'Unsupported OS.' }
    "$os-$arch"
}

function Assert-NotElevated {
    # ptk is a per-user tool and the warm runspace inherits the harness's
    # privileges; an elevated install invites root-owned files and an
    # elevated-execution footgun (plan: Design commitments).
    $elevated = if ($IsWindows) {
        [Security.Principal.WindowsPrincipal]::new(
            [Security.Principal.WindowsIdentity]::GetCurrent()
        ).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    else {
        (id -u) -eq '0'
    }
    if ($elevated) {
        throw 'Refusing to run elevated (root/Administrator): ptk installs per-user. Re-run from a normal shell.'
    }
}

function Get-PtkVersion {
    if ($Version) {
        # Accept tag-shaped values (v0.2.0-rc.1): release CI passes the git
        # tag verbatim and MSBuild rejects a leading v as an invalid version.
        return $Version -replace '^[vV]', ''
    }
    # Get-Command first: a missing native command is a terminating
    # CommandNotFoundException that 2>$null does not suppress.
    $git = Get-Command git -ErrorAction SilentlyContinue
    $sha = if ($git) { & $git -C $repoRoot rev-parse --short HEAD 2>$null } else { $null }
    if (-not $sha -or $LASTEXITCODE -ne 0) { $sha = 'unknown' }
    "0.2.0-dev.g$sha"
}

function Assert-PtkServerNotRunning {
    # Replacing or removing bin/ under a live server half-fails on Windows
    # file locks and leaves a stale server running old code elsewhere;
    # recorded precedent: every rebuild needed Stop-Process first
    # (.agents/state.md).
    $running = @(Get-Process -Name PtkMcpServer -ErrorAction SilentlyContinue |
            Where-Object { $_.Path -and $_.Path.StartsWith($ptkHome, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -gt 0) {
        throw ("A ptk server from {0} is running (PID {1}). Stop it first " +
            "(Stop-Process -Name PtkMcpServer) or restart the harness session, then re-run.") -f
            $ptkHome, ($running.Id -join ', ')
    }
}

function Get-PtkServerBinaryName {
    param([string]$TargetRid)
    if ($TargetRid -like 'win-*') { 'PtkMcpServer.exe' } else { 'PtkMcpServer' }
}

function Get-PtkFileSha256 {
    param([Parameter(Mandatory)][string]$Path)

    $algorithm = [Security.Cryptography.SHA256]::Create()
    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::Open,
        [IO.FileAccess]::Read,
        [IO.FileShare]::Read)
    try {
        [Convert]::ToHexString($algorithm.ComputeHash($stream)).ToLowerInvariant()
    }
    finally {
        $stream.Dispose()
        $algorithm.Dispose()
    }
}

function Get-PtkPublicContractSha256 {
    $contractPath = Join-Path $repoRoot 'server' 'Contracts' 'ResilienceR0' 'public-tool-contract.json'
    [byte[]]$contractBytes = [IO.File]::ReadAllBytes($contractPath)
    if ($contractBytes.Length -lt 2 -or
        $contractBytes[-1] -ne 10 -or
        $contractBytes[-2] -eq 10 -or
        [Array]::IndexOf($contractBytes, [byte]13) -ge 0 -or
        ($contractBytes.Length -ge 3 -and
            $contractBytes[0] -eq 0xef -and
            $contractBytes[1] -eq 0xbb -and
            $contractBytes[2] -eq 0xbf)) {
        throw 'The frozen public tool contract is not strict UTF-8 with exactly one final LF.'
    }

    $hash = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $hash.AppendData([Text.Encoding]::ASCII.GetBytes("ptk.public-contract/1`0"))
        $hash.AppendData($contractBytes, 0, $contractBytes.Length - 1)
        [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $hash.Dispose()
    }
}

function Add-PtkBigEndianHashInteger {
    param(
        [Parameter(Mandatory)][Security.Cryptography.IncrementalHash]$Hash,
        [Parameter(Mandatory)][ValidateSet(4, 8)][int]$Width,
        [Parameter(Mandatory)][uint64]$Value
    )

    [byte[]]$bytes = if ($Width -eq 4) {
        [BitConverter]::GetBytes([uint32]$Value)
    }
    else {
        [BitConverter]::GetBytes($Value)
    }
    if ([BitConverter]::IsLittleEndian) {
        [Array]::Reverse($bytes)
    }
    $Hash.AppendData($bytes)
}

function Write-PtkPackageManifest {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][ValidateSet(
            'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-arm64')][string]$TargetRid,
        [Parameter(Mandatory)][string]$PayloadVersion
    )

    $isUnix = $TargetRid -notlike 'win-*'
    $suffix = if ($isUnix) { '' } else { '.exe' }
    $definitions = [Collections.Generic.List[object]]::new()
    $definitions.Add([pscustomobject]@{ Path = 'VERSION'; Role = 'version' })
    $definitions.Add([pscustomobject]@{ Path = "bin/PtkAuditAdmin$suffix"; Role = 'audit_admin' })
    if ($isUnix) {
        $definitions.Add([pscustomobject]@{ Path = 'bin/PtkContainmentBroker'; Role = 'containment_helper' })
        $definitions.Add([pscustomobject]@{ Path = 'bin/PtkGuardianBroker'; Role = 'guardian_helper' })
        $definitions.Add([pscustomobject]@{ Path = 'bin/PtkMcpGuardian'; Role = 'guardian_apphost' })
        $definitions.Add([pscustomobject]@{ Path = 'bin/PtkMcpGuardian.dll'; Role = 'guardian_managed' })
        $definitions.Add([pscustomobject]@{ Path = 'bin/PtkMcpServer'; Role = 'host_apphost' })
        $definitions.Add([pscustomobject]@{ Path = 'bin/PtkMcpServer.dll'; Role = 'host_managed' })
    }
    else {
        $definitions.Add([pscustomobject]@{ Path = 'bin/PtkMcpGuardian.dll'; Role = 'guardian_managed' })
        $definitions.Add([pscustomobject]@{ Path = 'bin/PtkMcpGuardian.exe'; Role = 'guardian_apphost' })
        $definitions.Add([pscustomobject]@{ Path = 'bin/PtkMcpServer.dll'; Role = 'host_managed' })
        $definitions.Add([pscustomobject]@{ Path = 'bin/PtkMcpServer.exe'; Role = 'host_apphost' })
    }
    $definitions.Add([pscustomobject]@{ Path = 'bin/PtkMcpServer.runtimeconfig.json'; Role = 'host_runtime' })
    $definitions.Add([pscustomobject]@{ Path = 'bin/PtkSharedContracts.dll'; Role = 'shared_contract' })
    $definitions.Add([pscustomobject]@{ Path = 'scripts/ptk_init.ps1'; Role = 'script' })
    $definitions.Add([pscustomobject]@{ Path = 'src/PwshTokenCompressor.psm1'; Role = 'module' })

    $executableRoles = @(
        'audit_admin', 'containment_helper', 'guardian_helper',
        'guardian_apphost', 'host_apphost')
    $entries = @()
    $previousPath = $null
    foreach ($definition in $definitions) {
        if ($null -ne $previousPath -and
            [StringComparer]::Ordinal.Compare($previousPath, $definition.Path) -ge 0) {
            throw 'Package manifest paths are not in strict ordinal order.'
        }
        $previousPath = $definition.Path
        $relativePlatformPath = $definition.Path.Replace('/', [IO.Path]::DirectorySeparatorChar)
        $absolutePath = Join-Path $Destination $relativePlatformPath
        if (-not (Test-Path -LiteralPath $absolutePath -PathType Leaf)) {
            throw "Package manifest artifact is missing: $($definition.Path)"
        }
        $file = Get-Item -LiteralPath $absolutePath
        $unixMode = if (-not $isUnix) {
            $null
        }
        elseif ($definition.Role -in $executableRoles) {
            493
        }
        else {
            420
        }
        if ($isUnix) {
            $mode = if ($unixMode -eq 493) {
                [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor
                    [IO.UnixFileMode]::UserExecute -bor [IO.UnixFileMode]::GroupRead -bor
                    [IO.UnixFileMode]::GroupExecute -bor [IO.UnixFileMode]::OtherRead -bor
                    [IO.UnixFileMode]::OtherExecute
            }
            else {
                [IO.UnixFileMode]::UserRead -bor [IO.UnixFileMode]::UserWrite -bor
                    [IO.UnixFileMode]::GroupRead -bor [IO.UnixFileMode]::OtherRead
            }
            [IO.File]::SetUnixFileMode($file.FullName, $mode)
        }
        $entries += [ordered]@{
            path = $definition.Path
            role = $definition.Role
            bytes = [long]$file.Length
            sha256 = Get-PtkFileSha256 -Path $file.FullName
            unix_mode = $unixMode
        }
    }

    $hostBuildRoles = @(
        'containment_helper',
        'guardian_helper',
        'host_apphost',
        'host_managed',
        'host_runtime',
        'shared_contract'
    )
    $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
    $hostBuild = [Security.Cryptography.IncrementalHash]::CreateHash(
        [Security.Cryptography.HashAlgorithmName]::SHA256)
    try {
        $hostBuild.AppendData([Text.Encoding]::ASCII.GetBytes("ptk.host-build/1`0"))
        foreach ($entry in $entries) {
            if ($entry.role -notin $hostBuildRoles) { continue }
            [byte[]]$pathBytes = $strictUtf8.GetBytes([string]$entry.path)
            Add-PtkBigEndianHashInteger -Hash $hostBuild -Width 4 -Value $pathBytes.Length
            $hostBuild.AppendData($pathBytes)
            Add-PtkBigEndianHashInteger -Hash $hostBuild -Width 8 -Value ([uint64]$entry.bytes)
            $hostBuild.AppendData([Convert]::FromHexString([string]$entry.sha256))
        }
        $hostBuildSha256 = [Convert]::ToHexString($hostBuild.GetHashAndReset()).ToLowerInvariant()
    }
    finally {
        $hostBuild.Dispose()
    }

    $manifest = [ordered]@{
        schema_version = 'ptk.package-manifest/1'
        package_version = $PayloadVersion
        rid = $TargetRid
        private_protocol_version = 1
        public_contract_sha256 = Get-PtkPublicContractSha256
        host_build_sha256 = $hostBuildSha256
        files = $entries
    }
    [byte[]]$manifestBytes = $strictUtf8.GetBytes(
        (($manifest | ConvertTo-Json -Depth 5 -Compress) + "`n"))
    [IO.File]::WriteAllBytes(
        (Join-Path $Destination 'bin' 'ptk-package-manifest.json'),
        $manifestBytes)
}

function Build-PtkUnixBroker {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Output
    )

    $compiler = Get-Command cc -CommandType Application -ErrorAction SilentlyContinue
    if (-not $compiler) {
        throw 'The native Unix broker build requires cc on PATH.'
    }
    $arguments = @(
        '-std=c17', '-O2', '-fno-common', '-fstack-protector-strong',
        '-Wall', '-Wextra', '-Werror', '-Wpedantic', '-Wshadow',
        '-Wstrict-prototypes', '-Wmissing-prototypes',
        $Source, '-o', $Output)
    & $compiler.Source @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Native Unix broker compile failed: $Source"
    }
}

# Interim mitigation for GitHub issue #7: Microsoft Defender falsely detected
# PtkMcpServer.dll (reported as Trojan:MSIL/AsyncRAT.AB!MTB) and quarantined it
# out of the build output and the installed payload. When that happens the
# publish/copy steps succeed but the file is silently missing afterwards, so
# verify the payload landed intact and fail with actionable guidance if not.
function Assert-PtkPayloadIntact {
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$TargetRid
    )
    $required = @(
        (Join-Path $Root 'bin' (Get-PtkServerBinaryName -TargetRid $TargetRid))
        (Join-Path $Root 'bin' 'PtkMcpServer.dll')
    )
    $missing = @($required | Where-Object { -not (Test-Path -LiteralPath $_ -PathType Leaf) })
    if ($missing.Count -eq 0) { return }
    Write-Warning ((@(
        'These files are missing from the freshly written payload:'
        ($missing | ForEach-Object { "  $_" })
        'An antivirus quarantine is the most likely cause: Microsoft Defender has'
        'falsely detected PtkMcpServer.dll (Trojan:MSIL/AsyncRAT.AB!MTB) and removed'
        'it immediately after install. See the false-positive tracking issue'
        'https://github.com/AlsoBeltrix/PowerShell-Token-Killer/issues/7 and the'
        'runbook .agents/plans/defender-fp-submission.md. Check the Defender'
        'protection history before restoring anything, and do not add broad'
        'exclusions.'
    ) | ForEach-Object { $_ }) -join [Environment]::NewLine)
    throw 'Install incomplete: payload files missing (possible antivirus quarantine).'
}

# Publishes every apphost and assembles the canonical layout (bin/, src/,
# scripts/, VERSION) in $Destination. The one layout generator dev installs
# and release CI share.
function New-PtkLayout {
    param(
        [Parameter(Mandatory)][string]$Destination,
        [Parameter(Mandatory)][string]$TargetRid,
        [Parameter(Mandatory)][string]$PayloadVersion
    )
    Write-Host "Publishing PtkMcpServer ($TargetRid, $PayloadVersion)..."
    dotnet publish (Join-Path $repoRoot 'server' 'PtkMcpServer') `
        -c Release -r $TargetRid --self-contained --disable-build-servers `
        -p:Version=$PayloadVersion `
        -o (Join-Path $Destination 'bin') -v q --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    Write-Host "Publishing PtkMcpGuardian ($TargetRid, $PayloadVersion)..."
    dotnet publish (Join-Path $repoRoot 'server' 'PtkMcpGuardian') `
        -c Release -r $TargetRid --self-contained --disable-build-servers `
        -p:Version=$PayloadVersion `
        -o (Join-Path $Destination 'bin') -v q --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'PtkMcpGuardian dotnet publish failed.' }

    Write-Host "Publishing PtkAuditAdmin ($TargetRid, $PayloadVersion)..."
    dotnet publish (Join-Path $repoRoot 'server' 'PtkAuditAdmin') `
        -c Release -r $TargetRid --self-contained --disable-build-servers `
        -p:Version=$PayloadVersion `
        -o (Join-Path $Destination 'bin') -v q --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'PtkAuditAdmin dotnet publish failed.' }

    $src = New-Item -ItemType Directory -Path (Join-Path $Destination 'src') -Force
    foreach ($f in 'PwshTokenCompressor.psd1', 'PwshTokenCompressor.psm1') {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'src' $f) -Destination $src.FullName
    }
    $scripts = New-Item -ItemType Directory -Path (Join-Path $Destination 'scripts') -Force
    foreach ($f in 'ptk-hook.ps1', 'ptk_init.ps1', 'dev-install.ps1') {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts' $f) -Destination $scripts.FullName
    }
    Set-Content -LiteralPath (Join-Path $Destination 'VERSION') -Value $PayloadVersion -NoNewline
    if ($TargetRid -notlike 'win-*') {
        $currentRid = Get-PtkRid
        if ($TargetRid -ne $currentRid) {
            throw "Native Unix broker builds require their target host ($TargetRid requested on $currentRid)."
        }
        Build-PtkUnixBroker `
            -Source (Join-Path $repoRoot 'server' 'PtkMcpGuardian' 'Native' 'ptk_guardian_broker.c') `
            -Output (Join-Path $Destination 'bin' 'PtkGuardianBroker')
        Build-PtkUnixBroker `
            -Source (Join-Path $repoRoot 'server' 'PtkMcpGuardian' 'Native' 'ptk_containment_broker.c') `
            -Output (Join-Path $Destination 'bin' 'PtkContainmentBroker')
    }
    Write-PtkPackageManifest `
        -Destination $Destination `
        -TargetRid $TargetRid `
        -PayloadVersion $PayloadVersion
}

# Replaces the installer-owned payload in ~/.ptk with the staged layout.
# User-owned files (anything not in $payloadEntries) are never touched.
function Install-PtkPayload {
    param([Parameter(Mandatory)][string]$Staging)
    if (Test-Path -LiteralPath $ptkHome -PathType Leaf) {
        throw "$ptkHome exists as a file; move it aside - ptk needs it as its home directory."
    }
    New-Item -ItemType Directory -Path $ptkHome -Force | Out-Null
    foreach ($entry in $payloadEntries) {
        $target = Join-Path $ptkHome $entry
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
        }
        Move-Item -LiteralPath (Join-Path $Staging $entry) -Destination $target
    }
}

function Remove-PtkPayload {
    foreach ($entry in $payloadEntries) {
        $target = Join-Path $ptkHome $entry
        if (Test-Path -LiteralPath $target) {
            Remove-Item -LiteralPath $target -Recurse -Force
            Write-Host "Removed $target"
        }
    }
    # Drop the home itself only when nothing user-owned remains. -Force:
    # dot-named directories carry the Hidden attribute on Unix, which
    # Remove-Item refuses without it.
    if ((Test-Path -LiteralPath $ptkHome) -and
        -not (Get-ChildItem -LiteralPath $ptkHome -Force | Select-Object -First 1)) {
        Remove-Item -LiteralPath $ptkHome -Force
        Write-Host "Removed empty $ptkHome"
    }
}

# Returns $true when the server actually got registered with Claude Code;
# $false when registration was left to the user (the install arm then warns,
# and the claude leg of ptk_init skips its blocking hook - mhi-6/mhi-9).
function Register-PtkServer {
    param([Parameter(Mandatory)][string]$BinaryPath)
    $claude = Get-Command claude -ErrorAction SilentlyContinue
    if (-not $claude) {
        Write-Host 'claude CLI not found - register manually:'
        Write-Host "  claude mcp add --scope user ptk `"$BinaryPath`""
        return $false
    }
    # Remove-then-add so re-installs and dev<->release switches never collide.
    claude mcp remove --scope user ptk *> $null
    claude mcp add --scope user ptk $BinaryPath | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw ("claude mcp add failed; any previous ptk user-scope registration was " +
            "already removed. Register manually: claude mcp add --scope user ptk `"{0}`"" -f $BinaryPath)
    }
    Write-Host 'Registered with Claude Code (user scope).'
    $true
}

function Unregister-PtkServer {
    $claude = Get-Command claude -ErrorAction SilentlyContinue
    if (-not $claude) { return }
    claude mcp remove --scope user ptk *> $null
    if ($LASTEXITCODE -eq 0) { Write-Host 'Removed Claude Code registration (user scope).' }
}

function Write-PtkArpEntry {
    param([Parameter(Mandatory)][string]$PayloadVersion)
    if (-not $IsWindows) { return }
    # The per-user Add/Remove Programs entry winget's upgrade/uninstall
    # tracking keys off (plan: winget-ready from v0.2.0).
    New-Item -Path $arpKeyPath -Force | Out-Null
    Set-ItemProperty -Path $arpKeyPath -Name DisplayName -Value 'PowerShell Token Killer (ptk)'
    Set-ItemProperty -Path $arpKeyPath -Name DisplayVersion -Value $PayloadVersion
    Set-ItemProperty -Path $arpKeyPath -Name Publisher -Value 'PowerShell-Token-Killer'
    Set-ItemProperty -Path $arpKeyPath -Name InstallLocation -Value $ptkHome
    Set-ItemProperty -Path $arpKeyPath -Name UninstallString -Value (
        'pwsh -NoProfile -File "{0}" -Uninstall' -f (Join-Path $ptkHome 'scripts' 'dev-install.ps1'))
    Set-ItemProperty -Path $arpKeyPath -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty -Path $arpKeyPath -Name NoRepair -Value 1 -Type DWord
    Write-Host 'Wrote Add/Remove Programs entry (HKCU).'
}

function Remove-PtkArpEntry {
    if (-not $IsWindows) { return }
    if (Test-Path -Path $arpKeyPath) {
        Remove-Item -Path $arpKeyPath -Recurse -Force
        Write-Host 'Removed Add/Remove Programs entry.'
    }
}

# True only when a REAL ptk hook entry exists: a marker-matched command
# inside hooks.PreToolUse. A raw text match on the whole settings file would
# treat 'ptk-hook.ps1' anywhere (permissions lists, other hook events) as
# hook consent (i2-1).
function Test-PtkHookEntryPresent {
    param([Parameter(Mandatory)][string]$SettingsPath)
    if (-not (Test-Path -LiteralPath $SettingsPath)) { return $false }
    $raw = Get-Content -LiteralPath $SettingsPath -Raw
    if ([string]::IsNullOrWhiteSpace($raw)) { return $false }
    try { $config = $raw | ConvertFrom-Json -AsHashtable } catch { return $false }
    if ($null -eq $config -or -not $config.ContainsKey('hooks') -or
        -not $config['hooks'].ContainsKey('PreToolUse')) { return $false }
    foreach ($entry in @($config['hooks']['PreToolUse'])) {
        if ($null -eq $entry) { continue }
        foreach ($hook in @($entry['hooks'])) {
            if ($null -ne $hook -and [string]$hook['command'] -like '*ptk-hook.ps1*') { return $true }
        }
    }
    $false
}

function Show-PtkCodexSnippet {
    param([Parameter(Mandatory)][string]$BinaryPath)
    Write-Host ''
    Write-Host 'Codex (~/.codex/config.toml):'
    Write-Host '  [mcp_servers.ptk]'
    # TOML basic string with explicit escaping: literal (single-quoted)
    # strings cannot hold an apostrophe at all, and unescaped Windows
    # backslashes are illegal escape sequences in a basic string.
    $escaped = $BinaryPath.Replace('\', '\\').Replace('"', '\"')
    Write-Host ('  command = "{0}"' -f $escaped)
}

$mode = $PSCmdlet.ParameterSetName
# Parameter-set membership alone would let an explicit -Uninstall:$false run
# a full (destructive) uninstall; honor the switch's VALUE. -LayoutOnly:$false
# with -OutputDir has no coherent meaning - refuse rather than guess.
if ($mode -eq 'Uninstall' -and -not $Uninstall) { $mode = 'Install' }
if ($mode -eq 'LayoutOnly' -and -not $LayoutOnly) {
    throw '-LayoutOnly:$false with -OutputDir is ambiguous; pass -LayoutOnly or drop -OutputDir.'
}

switch ($mode) {
    'LayoutOnly' {
        $targetRid = if ($Rid) { $Rid } else { Get-PtkRid }
        $payloadVersion = Get-PtkVersion
        if ((Test-Path -LiteralPath $OutputDir) -and
            (Get-ChildItem -LiteralPath $OutputDir -Force | Select-Object -First 1)) {
            throw "OutputDir '$OutputDir' is not empty - refusing to clobber."
        }
        New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
        New-PtkLayout -Destination $OutputDir -TargetRid $targetRid -PayloadVersion $payloadVersion
        Assert-PtkPayloadIntact -Root $OutputDir -TargetRid $targetRid
        Write-Host "Layout ready: $OutputDir ($targetRid, $payloadVersion)"
    }
    'Uninstall' {
        Assert-NotElevated
        Assert-PtkServerNotRunning
        # Per-agent init reversal first (needs a ptk_init.ps1), then Claude
        # registration, ARP, payload. ptk_init -Uninstall reverses every
        # SUPPORTED leg - not just detected ones (mhi-10) - (hook + guidance
        # blocks, codex/grok registrations, agy plugin) and no-ops safely
        # where nothing is installed.
        $init = Join-Path $ptkHome 'scripts' 'ptk_init.ps1'
        if (-not (Test-Path -LiteralPath $init)) { $init = Join-Path $PSScriptRoot 'ptk_init.ps1' }
        if (Test-Path -LiteralPath $init) {
            try { & $init -Uninstall | Out-Host }
            catch { Write-Warning "Per-agent uninstall failed (continuing): $_" }
        }
        elseif (Test-PtkHookEntryPresent -SettingsPath (Join-Path $HOME '.claude' 'settings.json')) {
            Write-Warning ('A ptk hook entry exists in the user settings but no ptk_init.ps1 was ' +
                'found; run ptk_init.ps1 -Uninstall from a checkout to remove it.')
        }
        Unregister-PtkServer
        Remove-PtkArpEntry
        Remove-PtkPayload
        Write-Host 'ptk uninstalled. User-owned files under ~/.ptk (if any) were kept.'
    }
    default {
        Assert-NotElevated
        Assert-PtkServerNotRunning
        $targetRid = Get-PtkRid
        $payloadVersion = Get-PtkVersion
        $staging = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-stage-{0}" -f ([guid]::NewGuid()))
        New-Item -ItemType Directory -Path $staging | Out-Null
        try {
            New-PtkLayout -Destination $staging -TargetRid $targetRid -PayloadVersion $payloadVersion
            Install-PtkPayload -Staging $staging
        }
        finally {
            Remove-Item -LiteralPath $staging -Recurse -Force -ErrorAction SilentlyContinue
        }
        $binaryPath = Join-Path $ptkHome 'bin' (Get-PtkServerBinaryName -TargetRid $targetRid)
        Assert-PtkPayloadIntact -Root $ptkHome -TargetRid $targetRid
        $registered = Register-PtkServer -BinaryPath $binaryPath
        Write-PtkArpEntry -PayloadVersion $payloadVersion
        if ($Hook) {
            Write-Host 'NOTE: -Hook is deprecated - the full per-agent init runs by default.'
        }
        if (-not $registered) {
            # The claude leg guards itself now: with the claude CLI absent it
            # skips the blocking hook (mhi-6) and installs guidance only. The
            # codex/grok/agy legs never depended on Claude state, so the init
            # must still run for them (mhi-9).
            Write-Warning (('ptk is not registered with Claude Code (claude CLI not found); ' +
                'the claude leg installs guidance only, no blocking hook. Register manually, ' +
                'then re-run: pwsh -File "{0}"') -f (Join-Path $ptkHome 'scripts' 'ptk_init.ps1'))
        }
        # The end-state process: one command produces the complete
        # per-harness state (hooks, registrations, guidance) for every
        # detected harness, and re-targets any stale registration at the
        # fresh payload (issue #2).
        & (Join-Path $ptkHome 'scripts' 'ptk_init.ps1') | Out-Host
        Show-PtkCodexSnippet -BinaryPath $binaryPath
        Write-Host ''
        Write-Host "Installed ptk $payloadVersion to $ptkHome ($targetRid)."
    }
}
