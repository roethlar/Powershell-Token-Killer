#!/usr/bin/env pwsh
#Requires -Version 7
<#
.SYNOPSIS
Smoke-tests the PtkMcpServer stdio transport end to end: starts the server as
a child process and performs an MCP initialize / tools/list /
tools/call(ptk_state) handshake over real stdin/stdout. Three launch modes:
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
$auditRoot = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) (
    '.ptk/test-handshake-audit-' + [guid]::NewGuid().ToString('N'))
$psi.Environment['PTK_AUDIT_ROOT'] = $auditRoot
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
    foreach ($required in @('ptk_invoke', 'ptk_job', 'ptk_state', 'ptk_reset')) {
        if ($names -notcontains $required) {
            throw "$required missing from tools/list; got: $($names -join ', ')"
        }
    }
    foreach ($tool in @($tools.result.tools)) {
        if (@($tool.inputSchema.properties.PSObject.Properties.Name) -contains 'auditContext') {
            throw "host-only auditContext leaked into the $($tool.name) MCP input schema"
        }
    }
    Write-Host "tools/list ok: $($names -join ', ')"

    Send-Rpc @{
        jsonrpc = '2.0'; id = 3; method = 'tools/call'
        params = @{ name = 'ptk_state'; arguments = @{} }
    }
    $call = Read-RpcResponse -Id 3
    $text = $call.result.content[0].text
    if ($text -notmatch 'ptk server: pid \d+') {
        throw "ptk_state returned unexpected text: '$text'"
    }
    Write-Host 'ptk_state ok: server header present'

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

}
catch {
    Write-Host "HANDSHAKE FAILED: $_"
    $failed = $true
}
finally {
    if (-not $proc.HasExited) { $proc.Kill($true) }
    $proc.WaitForExit()
    $serverError = $proc.StandardError.ReadToEnd()
    if (-not $failed) {
        try {
            $segments = @(Get-ChildItem -LiteralPath (Join-Path $auditRoot 'spool') -Filter '*.jsonl' |
                Sort-Object Name)
            if ($segments.Count -eq 0) { throw 'no audit JSONL segment was created' }
            $rawAudit = ($segments | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName }) -join ''
            $events = @($segments | ForEach-Object {
                Get-Content -LiteralPath $_.FullName | ForEach-Object { $_ | ConvertFrom-Json }
            })
            if ($events[0].event_type -ne 'server.started') {
                throw "first audit event was '$($events[0].event_type)', not server.started"
            }
            for ($index = 0; $index -lt $events.Count; $index++) {
                if ($events[$index].sequence -ne ($index + 1)) {
                    throw "audit sequence gap at record $($index + 1)"
                }
            }
            $invokeAccepted = @($events | Where-Object {
                $_.event_type -eq 'call.accepted' -and $_.request.tool -eq 'ptk_invoke'
            })
            if ($invokeAccepted.Count -ne 2) {
                throw "expected two accepted invoke audit events; got $($invokeAccepted.Count)"
            }
            foreach ($accepted in $invokeAccepted) {
                if ($accepted.actor.client_name -ne 'ptk-handshake') {
                    throw 'MCP client attribution was not captured at the boundary'
                }
                if (@($accepted.request.provided_fields).Count -ne 1 -or
                    $accepted.request.provided_fields[0] -ne 'script') {
                    throw 'provided_fields did not preserve the exact invoke boundary'
                }
                if (-not $accepted.request.script_evidence_id -or
                    -not $accepted.request.original_script_digest) {
                    throw 'accepted invoke did not reference exact script evidence'
                }
            }
            if ($rawAudit.Contains('$warm = 41') -or $rawAudit.Contains('$warm + 1')) {
                throw 'exact script text leaked into the core audit stream'
            }
            $evidence = @(Get-ChildItem -LiteralPath (Join-Path $auditRoot 'evidence') -Filter '*.script')
            if ($evidence.Count -ne 2) { throw "expected two evidence payloads; got $($evidence.Count)" }
            $payloads = @($evidence | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName })
            if ($payloads -notcontains '$warm = 41' -or $payloads -notcontains '$warm + 1') {
                throw 'evidence payloads did not preserve the exact submitted scripts'
            }
            Write-Host 'audit ok: boundary attribution, provided fields, evidence references, and core secrecy verified'
        }
        catch {
            Write-Host "AUDIT VERIFICATION FAILED: $_"
            $failed = $true
        }
    }
    if ($failed -and -not [string]::IsNullOrWhiteSpace($serverError)) {
        Write-Host "server stderr:`n$serverError"
    }
    $proc.Dispose()
    if (Test-Path -LiteralPath $auditRoot) {
        Remove-Item -LiteralPath $auditRoot -Recurse -Force
    }
}

# A broken protected root must leave the stdio/MCP supervisor available for
# its narrow emergency diagnostic while keeping every runtime dependency and
# user effect behind the audit gate.
if (-not $failed) {
    $diagnosticParent = Join-Path (
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) (
        '.ptk/test-handshake-diagnostic-' + [guid]::NewGuid().ToString('N'))
    $blocker = Join-Path $diagnosticParent 'not-a-directory'
    $marker = Join-Path $diagnosticParent 'must-not-exist'
    $proc = $null
    try {
        [void](New-Item -ItemType Directory -Path $diagnosticParent)
        Set-Content -LiteralPath $blocker -Value 'blocked'
        $psi.Environment['PTK_AUDIT_ROOT'] = Join-Path $blocker 'audit'
        $proc = [System.Diagnostics.Process]::Start($psi)

        Send-Rpc @{
            jsonrpc = '2.0'; id = 101; method = 'initialize'
            params = @{
                protocolVersion = '2025-06-18'
                capabilities    = @{}
                clientInfo      = @{ name = 'ptk-handshake-diagnostic'; version = '0.0.0' }
            }
        }
        $diagnosticInit = Read-RpcResponse -Id 101
        if (-not $diagnosticInit.result.serverInfo.name) {
            throw 'diagnostic-only initialize failed'
        }
        Send-Rpc @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
        Send-Rpc @{ jsonrpc = '2.0'; id = 102; method = 'tools/list' }
        $diagnosticTools = @((Read-RpcResponse -Id 102).result.tools.name)
        if ($diagnosticTools -notcontains 'ptk_state' -or
            $diagnosticTools -notcontains 'ptk_invoke') {
            throw 'diagnostic-only tools/list was incomplete'
        }

        Send-Rpc @{
            jsonrpc = '2.0'; id = 103; method = 'tools/call'
            params = @{ name = 'ptk_state'; arguments = @{} }
        }
        $diagnosticState = (Read-RpcResponse -Id 103).result.content[0].text
        if ($diagnosticState -notmatch '(?m)^audit=unavailable$' -or
            $diagnosticState -notmatch '(?m)^unrecorded=true$' -or
            $diagnosticState -match 'ptk server:') {
            throw "diagnostic-only ptk_state leaked or omitted state: '$diagnosticState'"
        }

        $literalMarker = "'" + $marker.Replace("'", "''") + "'"
        Send-Rpc @{
            jsonrpc = '2.0'; id = 104; method = 'tools/call'
            params = @{
                name = 'ptk_invoke'
                arguments = @{ script = "Set-Content -LiteralPath $literalMarker -Value ran" }
            }
        }
        $diagnosticInvoke = (Read-RpcResponse -Id 104).result
        if (-not $diagnosticInvoke.isError -or
            $diagnosticInvoke.content[0].text -notmatch 'operation was not started' -or
            (Test-Path -LiteralPath $marker)) {
            throw 'diagnostic-only invoke was not rejected before its effect'
        }
        Write-Host 'audit outage ok: MCP diagnostic remains available and invoke stays fail-closed'
    }
    catch {
        Write-Host "DIAGNOSTIC HANDSHAKE FAILED: $_"
        $failed = $true
    }
    finally {
        if ($null -ne $proc -and -not $proc.HasExited) { $proc.Kill($true) }
        if ($null -ne $proc) {
            $proc.WaitForExit()
            $diagnosticError = $proc.StandardError.ReadToEnd()
            if ($failed -and -not [string]::IsNullOrWhiteSpace($diagnosticError)) {
                Write-Host "diagnostic server stderr:`n$diagnosticError"
            }
            $proc.Dispose()
        }
        if (Test-Path -LiteralPath $diagnosticParent) {
            Remove-Item -LiteralPath $diagnosticParent -Recurse -Force
        }
    }
}

if (-not $failed) { Write-Host 'HANDSHAKE PASSED' }
exit ($failed ? 1 : 0)
