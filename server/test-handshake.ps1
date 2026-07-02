#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
Smoke-tests the PtkMcpServer stdio transport end to end: builds the server,
starts it as a child process, and performs an MCP initialize / tools/list /
tools/call(ptk_ping) handshake over real stdin/stdout.

Exits 0 on success, 1 on failure. Used as part of slice verification alongside
`dotnet test`.
#>
[CmdletBinding()]
param(
    [int]$TimeoutSec = 30
)

$ErrorActionPreference = 'Stop'
$serverDir = Split-Path -Parent $PSCommandPath
$proj = Join-Path $serverDir 'PtkMcpServer'

Write-Host 'Building server...'
dotnet build $proj -v q --nologo | Out-Host
if ($LASTEXITCODE -ne 0) { Write-Error 'Build failed.'; exit 1 }

$dll = Join-Path $proj 'bin/Debug/net10.0/PtkMcpServer.dll'
if (-not (Test-Path $dll)) { Write-Error "Built assembly not found at $dll"; exit 1 }

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = 'dotnet'
$psi.ArgumentList.Add('exec')
$psi.ArgumentList.Add($dll)
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
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
