#Requires -Version 7
<#
.SYNOPSIS
Per-agent ptk init: installs (or inspects/removes) ptk's redirect hook and
guidance nudge for each supported agent harness, mirroring `rtk init`
semantics: user-level by default, -Show / -Uninstall / -DryRun, idempotent
marker-owned entries.

.DESCRIPTION
Multi-harness init (.agents/plans/multi-harness-init.md): three independent
layers per harness - registration (the MCP server), enforcement (a blocking
pre-tool hook, only where live-verified), nudge (a marker-delimited guidance
block in the harness's user-level instructions file). This script ships the
framework plus the claude leg; the codex/grok/agy legs are planned (slices
2-4) and currently report themselves as not implemented.

claude leg: checks the installed payload (~/.ptk) and refuses the hook when
it is missing - a redirect hook without a server steers every shell call at
a tool that cannot answer; run scripts/dev-install.ps1 first. Installs one
PreToolUse entry (matcher "Bash|PowerShell") running scripts/ptk-hook.ps1
(deny-with-guidance; PTK_DIRECT in a command is the escape hatch), and with
-Nudge also maintains the ptk guidance block in ~/.claude/CLAUDE.md.
Existing hooks - including rtk's own Bash rewrite hook - are preserved.
Idempotent: re-running replaces ptk-owned entries instead of duplicating
them. Takes effect at the next session start.

BREAKING CHANGE vs the single-harness version of this script: installs are
USER-LEVEL BY DEFAULT (~/.claude/settings.json). The old project-local
default is behind -Local, which warns: repo-file writes collide with
content-tracking tooling.

.EXAMPLE
pwsh -File scripts/ptk_init.ps1                     # detected agents, user level
pwsh -File scripts/ptk_init.ps1 -Agent claude -Nudge
pwsh -File scripts/ptk_init.ps1 -Show
pwsh -File scripts/ptk_init.ps1 -Uninstall
#>
[CmdletBinding()]
param(
    # Agent legs to run; default is every supported agent detected on this
    # machine, 'all' forces every supported leg. Validated manually (not
    # ValidateSet) because `pwsh -File` passes "codex,grok" as one literal
    # string; comma-separated values are split and accepted.
    [string[]]$Agent,

    # Claude-only opt-in: patch the project-local .claude/settings.json (the
    # pre-multi-harness default) instead of the user-level file.
    [switch]$Local,

    # Deprecated: user-level is the default now. Accepted so older callers
    # (an installed dev-install.ps1 from a previous payload) keep working.
    [switch]$Global,

    # Also maintain the ptk guidance block in the agent's user-level
    # instructions file (claude: ~/.claude/CLAUDE.md). -Uninstall removes
    # the block regardless of this switch.
    [switch]$Nudge,

    [switch]$Show,
    [switch]$Uninstall,
    [switch]$DryRun,

    # Explicit targets / test seams. -SettingsPath is claude-only;
    # -NudgePath applies to whichever SINGLE leg is selected (default
    # claude). Providing either restricts the run to exactly one leg.
    [string]$SettingsPath,
    [string]$NudgePath,
    # Where the installed payload lives (test seam).
    [string]$PtkHome = (Join-Path $HOME '.ptk')
)
$ErrorActionPreference = 'Stop'

$supportedAgents = @('claude', 'codex', 'grok', 'agy')

# Normalize -Agent: split comma-joined values (`pwsh -File` hands
# "codex,grok" over as one string), trim, lowercase, validate.
$Agent = @($Agent |
    ForEach-Object { [string]$_ -split ',' } |
    ForEach-Object { $_.Trim().ToLowerInvariant() } |
    Where-Object { $_ })
foreach ($name in $Agent) {
    if ($name -notin ($supportedAgents + 'all')) {
        throw ("Unknown agent '{0}'. Supported: {1}, all." -f $name, ($supportedAgents -join ', '))
    }
}

# The marker every ptk-owned settings entry is recognized by (install, show,
# uninstall).
$hookMarker = 'ptk-hook.ps1'
$hookScript = Join-Path $PSScriptRoot 'ptk-hook.ps1'
$hookCommand = 'pwsh -NoProfile -File "{0}"' -f $hookScript

# Markers delimiting the ptk-owned block in a nudge (guidance) file. The
# wording is harness-neutral and conditional ("when available") so the same
# block is safe on machines where ptk is not registered, and tool names carry
# no per-harness prefix (three schemes exist - docs/harness-support.md).
$nudgeBegin = '<!-- ptk-guidance -->'
$nudgeEnd = '<!-- /ptk-guidance -->'
$nudgeBlock = @"
$nudgeBegin
When the ptk MCP server is available, use ptk_invoke for shell commands
instead of the built-in shell tools: one warm PowerShell session (imports,
connections, variables persist across calls), compressed output. Long
stateless work: background=true, then poll ptk_job; long work that needs
the warm session: raise timeoutSeconds. ptk_state diagnoses session drift;
ptk_reset restores factory state; raw=true returns full uncompressed
output. When the ptk tools are not available in this session, use the
normal shell tools.
$nudgeEnd
"@

function Test-PtkAgentPresent {
    param([Parameter(Mandatory)][string]$Name)
    if (Get-Command -Name $Name -ErrorAction SilentlyContinue) { return $true }
    # Claude Code is configured through files; an existing ~/.claude is as
    # good a detection signal as the CLI.
    ($Name -eq 'claude') -and (Test-Path -LiteralPath (Join-Path $HOME '.claude'))
}

function Read-PtkSettings {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) {
        $raw = Get-Content -LiteralPath $Path -Raw
        if (-not [string]::IsNullOrWhiteSpace($raw)) {
            return $raw | ConvertFrom-Json -AsHashtable
        }
    }
    @{}
}

function Test-PtkEntry {
    param([object]$Entry)
    foreach ($hook in @($Entry['hooks'])) {
        if ($null -ne $hook -and [string]$hook['command'] -like "*$hookMarker*") { return $true }
    }
    $false
}

# Text with the ptk-owned block removed. Surgical (mhi-7): strips exactly
# the shape the installer writes - an optional blank-line separator pair,
# the marker block, one trailing newline - so user content and its
# whitespace stay byte-identical and install/uninstall round-trips. Repeated
# cycles are stable: each install's strip removes exactly what the previous
# install added.
function Get-PtkNudgeStripped {
    param([string]$Text)
    $pattern = '(?s)(?:\r?\n\r?\n)?{0}.*?{1}(?:\r?\n)?' -f
        [regex]::Escape($nudgeBegin), [regex]::Escape($nudgeEnd)
    [regex]::Replace([string]$Text, $pattern, '')
}

function Install-PtkNudgeBlock {
    param([Parameter(Mandatory)][string]$Path)
    $existing = (Test-Path -LiteralPath $Path) ? (Get-Content -LiteralPath $Path -Raw) : ''
    $kept = Get-PtkNudgeStripped $existing
    $nl = [Environment]::NewLine
    $content = ($kept ? ($kept + $nl + $nl) : '') + $nudgeBlock.Trim() + $nl
    if ($DryRun) {
        Write-Host "DRY RUN - would write ${Path}:"
        Write-Host $content
        return
    }
    $dir = Split-Path -Parent $Path
    if ($dir -and -not (Test-Path -LiteralPath $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }
    Set-Content -LiteralPath $Path -Value $content -NoNewline
    Write-Host "[claude] ptk guidance block installed in $Path"
}

function Uninstall-PtkNudgeBlock {
    param([Parameter(Mandatory)][string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $existing = Get-Content -LiteralPath $Path -Raw
    if ($existing -notlike "*$nudgeBegin*") { return }
    if ($DryRun) {
        Write-Host "DRY RUN - would remove the ptk guidance block from $Path"
        return
    }
    Set-Content -LiteralPath $Path -Value (Get-PtkNudgeStripped $existing) -NoNewline
    Write-Host "[claude] ptk guidance block removed from $Path"
}

function Invoke-PtkClaudeLeg {
    $target = $SettingsPath
    if (-not $target) {
        $target = $Local ?
            (Join-Path (Get-Location) '.claude' 'settings.json') :
            (Join-Path $HOME '.claude' 'settings.json')
    }
    $nudgeTarget = $NudgePath ? $NudgePath : (Join-Path $HOME '.claude' 'CLAUDE.md')

    $settings = Read-PtkSettings -Path $target
    $preToolUse = @()
    if ($settings.ContainsKey('hooks') -and $settings['hooks'].ContainsKey('PreToolUse')) {
        $preToolUse = @($settings['hooks']['PreToolUse'])
    }
    $installed = @($preToolUse | Where-Object { Test-PtkEntry $_ }).Count -gt 0
    $payloadPresent = Test-Path -LiteralPath (Join-Path $PtkHome 'bin')
    $nudgePresent = (Test-Path -LiteralPath $nudgeTarget) -and
        ((Get-Content -LiteralPath $nudgeTarget -Raw) -like "*$nudgeBegin*")

    if ($Show) {
        Write-Host "[claude] settings: $target"
        Write-Host ("[claude] ptk hook: {0}" -f ($installed ? 'INSTALLED' : 'not installed'))
        Write-Host "[claude] hook script: $hookScript $((Test-Path -LiteralPath $hookScript) ? '' : '(MISSING)')"
        Write-Host ("[claude] nudge block: {0} in {1}" -f ($nudgePresent ? 'INSTALLED' : 'not installed'), $nudgeTarget)
        Write-Host ("[claude] installed payload: {0} ({1})" -f
            ($payloadPresent ? 'present' : 'MISSING - run scripts/dev-install.ps1'), $PtkHome)
        return $true
    }

    if ($Local) {
        Write-Warning (('-Local targets {0}, which lives inside a repo: anything tracking that file ' +
            'by content (governance refresh tooling, dotfile managers) will flag the edit as an ' +
            'owner modification forever after. The user-level default avoids this.') -f $target)
    }
    elseif (-not $SettingsPath -and -not $Uninstall) {
        Write-Host ('NOTE: ptk_init now installs USER-LEVEL by default (~/.claude/settings.json); ' +
            'the old project-local behavior is -Local.')
    }

    if (-not $Uninstall -and -not $payloadPresent) {
        # Registration gate: enforce only where the steered-to tool can
        # answer. A hook without an installed server denies every shell call
        # while pointing at nothing.
        Write-Warning (('No installed ptk payload at {0}. Run scripts/dev-install.ps1 first - it ' +
            'publishes the server to ~/.ptk and registers it with Claude Code - then re-run ' +
            'this script.') -f $PtkHome)
        if (-not $DryRun) { return $false }
        Write-Host 'DRY RUN - continuing to show what an install would write.'
    }

    if ($Uninstall -and -not $installed) {
        # Nothing ptk-owned in the settings: skip the write entirely rather
        # than create or reformat a file just to remove nothing.
        Write-Host "[claude] no ptk hook entry in $target - nothing to remove."
    }
    else {
        # Remove OUR hook surgically: strip it from each entry's hooks array
        # and drop an entry only when nothing remains, so a foreign hook
        # sharing an entry with ours (one matcher, several hooks) survives
        # install and uninstall.
        $preToolUse = @(foreach ($entry in $preToolUse) {
            $kept = @(@($entry['hooks']) | Where-Object {
                $null -ne $_ -and [string]$_['command'] -notlike "*$hookMarker*"
            })
            if ($kept.Count -gt 0) {
                $entry['hooks'] = $kept
                $entry
            }
        })

        if (-not $Uninstall) {
            $preToolUse += @{
                matcher = 'Bash|PowerShell'
                hooks   = @(@{ type = 'command'; command = $hookCommand })
            }
        }

        if (-not $settings.ContainsKey('hooks')) { $settings['hooks'] = @{} }
        if ($preToolUse.Count -gt 0) {
            $settings['hooks']['PreToolUse'] = $preToolUse
        }
        else {
            $settings['hooks'].Remove('PreToolUse')
            if ($settings['hooks'].Count -eq 0) { $settings.Remove('hooks') }
        }

        $json = $settings | ConvertTo-Json -Depth 32
        if ($DryRun) {
            Write-Host "DRY RUN - would write ${target}:"
            Write-Host $json
        }
        else {
            $dir = Split-Path -Parent $target
            if ($dir -and -not (Test-Path -LiteralPath $dir)) {
                New-Item -ItemType Directory -Path $dir -Force | Out-Null
            }
            Set-Content -LiteralPath $target -Value $json -NoNewline
            Write-Host ("[claude] ptk hook {0} in {1} (takes effect next session)" -f
                ($Uninstall ? 'removed' : 'installed'), $target)
        }
    }

    if ($Uninstall) {
        Uninstall-PtkNudgeBlock -Path $nudgeTarget
    }
    elseif ($Nudge) {
        Install-PtkNudgeBlock -Path $nudgeTarget
    }
    $true
}

function Invoke-PtkStubLeg {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][int]$PlannedSlice
    )
    Write-Host ("[{0}] leg not implemented yet (multi-harness-init slice {1}) - nothing changed." -f
        $Name, $PlannedSlice)
    $true
}

# --- resolve which legs run ---------------------------------------------

$explicitTargets = [bool]$SettingsPath -or [bool]$NudgePath
$resolvedAgents =
    if ($explicitTargets -or $Local) {
        $foreign = @($Agent | Where-Object { $_ -and $_ -ne 'claude' })
        if ($foreign.Count -gt 0) {
            throw (('-Local/-SettingsPath/-NudgePath are claude-leg targets; drop them or use ' +
                '-Agent claude (got: {0}).') -f ($foreign -join ', '))
        }
        @('claude')
    }
    elseif (-not $Agent) {
        $detected = @($supportedAgents | Where-Object { Test-PtkAgentPresent -Name $_ })
        if ($detected.Count -eq 0) {
            Write-Host ('No supported agent harness detected (claude/codex/grok/agy); defaulting ' +
                'to the claude leg. Pass -Agent to choose explicitly.')
            $detected = @('claude')
        }
        $detected
    }
    elseif ($Agent -contains 'all') { $supportedAgents }
    else { @($Agent | Select-Object -Unique) }

$failedLegs = @()
foreach ($name in $resolvedAgents) {
    $ok = switch ($name) {
        'claude' { Invoke-PtkClaudeLeg }
        'codex' { Invoke-PtkStubLeg -Name 'codex' -PlannedSlice 2 }
        'grok' { Invoke-PtkStubLeg -Name 'grok' -PlannedSlice 3 }
        'agy' { Invoke-PtkStubLeg -Name 'agy' -PlannedSlice 4 }
    }
    if (-not $ok) { $failedLegs += $name }
}
if ($failedLegs.Count -gt 0) {
    Write-Warning ('ptk_init: leg(s) failed: {0}' -f ($failedLegs -join ', '))
    exit 1
}
