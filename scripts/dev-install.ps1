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
entry on Windows -> optionally install the redirect hook (-Hook). -Uninstall
reverses all of it and keeps user files. -LayoutOnly -OutputDir <dir> only
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
    # Install the Claude Code redirect hook (user level) after installing -
    # invokes the INSTALLED copy of ptk_init.ps1, so the hook points at the
    # installed payload, not the checkout. Claude leg only; the multi-harness
    # path is ptk_init.ps1 itself, and a future -InitAgents will chain the
    # full per-agent init here (multi-harness-init plan, slice 5).
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

# Publishes the server and assembles the canonical layout (bin/, src/,
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
        -c Release -r $TargetRid --self-contained `
        -p:Version=$PayloadVersion `
        -o (Join-Path $Destination 'bin') -v q --nologo | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    $src = New-Item -ItemType Directory -Path (Join-Path $Destination 'src') -Force
    foreach ($f in 'PwshTokenCompressor.psd1', 'PwshTokenCompressor.psm1') {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'src' $f) -Destination $src.FullName
    }
    $scripts = New-Item -ItemType Directory -Path (Join-Path $Destination 'scripts') -Force
    foreach ($f in 'ptk-hook.ps1', 'ptk_init.ps1', 'dev-install.ps1') {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts' $f) -Destination $scripts.FullName
    }
    Set-Content -LiteralPath (Join-Path $Destination 'VERSION') -Value $PayloadVersion -NoNewline
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
# $false when registration was left to the user (-Hook gates on this - a
# blocking hook must not ship while the steered-to tool is unregistered).
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
        Write-Host "Layout ready: $OutputDir ($targetRid, $payloadVersion)"
    }
    'Uninstall' {
        Assert-NotElevated
        Assert-PtkServerNotRunning
        # Hook first (needs a ptk_init.ps1), then registration, ARP, payload.
        # Only touch the global settings file when a ptk hook entry actually
        # exists in it: ptk_init -Uninstall rewrites the file even with
        # nothing to remove (and creates it when missing), which would
        # violate the leave-non-payload-files-alone contract.
        $settingsPath = Join-Path $HOME '.claude' 'settings.json'
        $hookPresent = Test-PtkHookEntryPresent -SettingsPath $settingsPath
        if ($hookPresent) {
            # Prefer the installed copy; fall back to this script's sibling so
            # a partially deleted home still gets its hook entry removed.
            $init = Join-Path $ptkHome 'scripts' 'ptk_init.ps1'
            if (-not (Test-Path -LiteralPath $init)) { $init = Join-Path $PSScriptRoot 'ptk_init.ps1' }
            if (Test-Path -LiteralPath $init) {
                try { & $init -Global -Uninstall | Out-Host }
                catch { Write-Warning "Hook uninstall failed (continuing): $_" }
            }
            else {
                Write-Warning ("A ptk hook entry exists in {0} but no ptk_init.ps1 was found; " +
                    "run ptk_init.ps1 -Global -Uninstall from a checkout to remove it.") -f $settingsPath
            }
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
        $registered = Register-PtkServer -BinaryPath $binaryPath
        Write-PtkArpEntry -PayloadVersion $payloadVersion
        if ($Hook) {
            if ($registered) {
                Write-Host ('NOTE: -Hook covers the Claude Code leg only; for other harnesses run ' +
                    'scripts/ptk_init.ps1 (multi-harness init).')
                & (Join-Path $ptkHome 'scripts' 'ptk_init.ps1') -Agent claude | Out-Host
            }
            else {
                # A payload exists but ptk_invoke is not registered: installing
                # the redirect hook now would deny every shell call toward a
                # tool the harness cannot see.
                Write-Warning (('Skipping the hook install: the server is not registered with ' +
                    'Claude Code (claude CLI not found). Register manually, then run: ' +
                    'pwsh -File "{0}" -Agent claude') -f (Join-Path $ptkHome 'scripts' 'ptk_init.ps1'))
            }
        }
        else {
            # No -Hook, but an existing hook registration is prior consent:
            # refresh it against this install so it targets the fresh payload
            # - a registration left pointing at a moved/removed path fails
            # open silently on every shell call (issue #2).
            $globalSettings = Join-Path $HOME '.claude' 'settings.json'
            if ($registered -and (Test-PtkHookEntryPresent -SettingsPath $globalSettings)) {
                Write-Host 'Existing ptk hook registration found - refreshing it against this install.'
                & (Join-Path $ptkHome 'scripts' 'ptk_init.ps1') -Agent claude | Out-Host
            }
        }
        Show-PtkCodexSnippet -BinaryPath $binaryPath
        Write-Host ''
        Write-Host "Installed ptk $payloadVersion to $ptkHome ($targetRid)."
    }
}
