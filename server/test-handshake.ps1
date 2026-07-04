#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
Smoke-tests the PtkMcpServer stdio transport end to end: starts the server as
a child process and performs an MCP initialize / tools/list /
tools/call(ptk_ping) handshake over real stdin/stdout. Three launch modes:
default builds this checkout and drives the built dll; -UseRegistrationCommand
drives the exact `dotnet run` command the .mcp.json registration spawns;
-ServerCommand drives an arbitrary server binary (e.g. a published
self-contained build, for release-artifact smoke tests) — multi-element
commands use array syntax: -ServerCommand dotnet,exec,PtkMcpServer.dll.

Exits 0 on success, 1 on failure. Used as part of slice verification alongside
`dotnet test`.
#>
[CmdletBinding(DefaultParameterSetName = 'BuiltDll')]
param(
    [int]$TimeoutSec = 30,
    # Drive the server with `dotnet run` — the exact command the .mcp.json
    # registration spawns (build-on-launch, quiet stdout) — instead of
    # dotnet exec against a prebuilt dll.
    [Parameter(ParameterSetName = 'Registration', Mandatory)]
    [switch]$UseRegistrationCommand,
    # Drive an arbitrary server binary instead of building this checkout:
    # first element is the executable, remaining elements are its arguments.
    # Named array parameter: multi-element commands need PowerShell array
    # syntax (-ServerCommand dotnet,exec,Server.dll) from a command context.
    # Space-separated tokens after -ServerCommand do NOT collect (binding
    # errors loudly), and `pwsh -File` binds literally — from -File, pass a
    # single executable path. The child runs in this script's current
    # PowerShell location (pinned below), so relative paths resolve where
    # the caller expects.
    [Parameter(ParameterSetName = 'ServerCommand', Mandatory)]
    [ValidateNotNullOrEmpty()]
    [string[]]$ServerCommand
)

$ErrorActionPreference = 'Stop'
$serverDir = Split-Path -Parent $PSCommandPath

$mode = $PSCmdlet.ParameterSetName
# An explicit -UseRegistrationCommand:$false means the default built-dll mode,
# as it did before parameter sets were introduced (set membership alone would
# select the Registration branch regardless of the switch's value).
if ($mode -eq 'Registration' -and -not $UseRegistrationCommand) { $mode = 'BuiltDll' }

$psi = [System.Diagnostics.ProcessStartInfo]::new()
switch ($mode) {
    'ServerCommand' {
        Write-Host "Starting via server command: $($ServerCommand -join ' ')"
        $exe = $ServerCommand[0]
        # Resolve path-shaped executables against this script's PowerShell
        # location: Process.Start resolves a relative FileName against the
        # process-wide cwd (not $PWD), even when WorkingDirectory is set.
        # A bare command name (no separator) passes through to PATH lookup.
        if ($exe -match '[\\/]') {
            $exe = (Resolve-Path -LiteralPath $exe).ProviderPath
        }
        $psi.FileName = $exe
        foreach ($a in ($ServerCommand | Select-Object -Skip 1)) {
            $psi.ArgumentList.Add($a)
        }
    }
    'Registration' {
        Write-Host 'Starting via dotnet run (registration command; builds on launch)...'
        $psi.FileName = 'dotnet'
        foreach ($a in @('run', '-v', 'q', '--project', (Join-Path $serverDir 'PtkMcpServer'))) {
            $psi.ArgumentList.Add($a)
        }
    }
    default {
        $proj = Join-Path $serverDir 'PtkMcpServer'
        Write-Host 'Building server...'
        dotnet build $proj -v q --nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { Write-Error 'Build failed.'; exit 1 }

        $dll = Join-Path $proj 'bin/Debug/net10.0/PtkMcpServer.dll'
        if (-not (Test-Path $dll)) { Write-Error "Built assembly not found at $dll"; exit 1 }

        $psi.FileName = 'dotnet'
        $psi.ArgumentList.Add('exec')
        $psi.ArgumentList.Add($dll)
    }
}
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
# Pin the child to this script's PowerShell location: Process.Start would
# otherwise resolve against the process-wide cwd, which an interactive
# session's Set-Location does not change.
$psi.WorkingDirectory = (Get-Location).ProviderPath
$proc = [System.Diagnostics.Process]::Start($psi)

function Send-Rpc {
    param([hashtable]$Message)
    $json = $Message | ConvertTo-Json -Depth 12 -Compress
    $proc.StandardInput.WriteLine($json)
    $proc.StandardInput.Flush()
}

function Read-RpcResponse {
    # Reads stdout lines until a message with the given id arrives; skips
    # notifications (messages without an id) the server may emit in between.
    param([int]$Id)
    while ($true) {
        $task = $proc.StandardOutput.ReadLineAsync()
        if (-not $task.Wait($TimeoutSec * 1000)) {
            throw "Timed out after ${TimeoutSec}s waiting for response id=$Id."
        }
        $line = $task.Result
        if ($null -eq $line) { throw "Server closed stdout while waiting for id=$Id." }
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        $msg = $line | ConvertFrom-Json
        if ($msg.PSObject.Properties['id'] -and $msg.id -eq $Id) { return $msg }
        Write-Host "  (skipped notification: $($msg.method))"
    }
}

$failed = $false
try {
    Send-Rpc @{
        jsonrpc = '2.0'; id = 1; method = 'initialize'
        params = @{
            protocolVersion = '2025-06-18'
            capabilities    = @{}
            clientInfo      = @{ name = 'ptk-handshake'; version = '0.0.0' }
        }
    }
    $init = Read-RpcResponse -Id 1
    if (-not $init.result.serverInfo.name) {
        throw "initialize failed: $($init | ConvertTo-Json -Depth 12 -Compress)"
    }
    Write-Host "initialize ok: $($init.result.serverInfo.name) $($init.result.serverInfo.version)"

    Send-Rpc @{ jsonrpc = '2.0'; method = 'notifications/initialized' }

    Send-Rpc @{ jsonrpc = '2.0'; id = 2; method = 'tools/list' }
    $tools = Read-RpcResponse -Id 2
    $names = @($tools.result.tools.name)
    foreach ($required in @('ptk_ping', 'ptk_invoke', 'ptk_modules', 'ptk_reset')) {
        if ($names -notcontains $required) {
            throw "$required missing from tools/list; got: $($names -join ', ')"
        }
    }
    Write-Host "tools/list ok: $($names -join ', ')"

    Send-Rpc @{
        jsonrpc = '2.0'; id = 3; method = 'tools/call'
        params = @{ name = 'ptk_ping'; arguments = @{} }
    }
    $call = Read-RpcResponse -Id 3
    $text = $call.result.content[0].text
    if ($text -ne 'pong') { throw "ptk_ping returned '$text', expected 'pong'." }
    Write-Host 'ptk_ping ok: pong'

    # Two ptk_invoke calls sharing state prove the warm runspace end to end.
    Send-Rpc @{
        jsonrpc = '2.0'; id = 4; method = 'tools/call'
        params = @{ name = 'ptk_invoke'; arguments = @{ script = '$warm = 41' } }
    }
    [void](Read-RpcResponse -Id 4)

    Send-Rpc @{
        jsonrpc = '2.0'; id = 5; method = 'tools/call'
        params = @{ name = 'ptk_invoke'; arguments = @{ script = '$warm + 1' } }
    }
    $warmText = (Read-RpcResponse -Id 5).result.content[0].text
    if ($warmText -notmatch '\b42\b') {
        throw "ptk_invoke cross-call state failed; second call returned '$warmText'."
    }
    Write-Host 'ptk_invoke cross-call state ok: 42'

    Write-Host 'HANDSHAKE PASSED'
}
catch {
    Write-Host "HANDSHAKE FAILED: $_"
    $failed = $true
}
finally {
    if (-not $proc.HasExited) { $proc.Kill($true) }
    $proc.Dispose()
}

exit ($failed ? 1 : 0)
