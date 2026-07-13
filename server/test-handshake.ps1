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
$outputParent = Join-Path (
    [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) (
    '.ptk/test-handshake-output-' + [guid]::NewGuid().ToString('N'))
$psi.Environment['PTK_AUDIT_ROOT'] = $auditRoot
$psi.Environment['PTK_OUTPUT_ROOT'] = $outputParent
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

function Stop-ServerProcess {
    [OutputType([bool])]
    param(
        [Parameter(Mandatory)]
        [System.Diagnostics.Process]$Process,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if (-not $Process.HasExited) {
        try {
            # EOF is the stdio transport's graceful shutdown signal. Closing
            # the writer also proves that harness-owned resources are disposed
            # before this smoke test inspects their protected roots.
            $Process.StandardInput.Close()
        }
        catch {
            if (-not $Process.HasExited) {
                Write-Host "$Label stdin close failed: $_"
            }
        }
    }

    if ($Process.HasExited -or $Process.WaitForExit($TimeoutSec * 1000)) {
        return $true
    }

    Write-Host "$Label did not exit within ${TimeoutSec}s after stdin EOF; killing its process tree."
    try {
        $Process.Kill($true)
    }
    catch {
        if (-not $Process.HasExited) { throw }
    }
    if (-not $Process.HasExited -and -not $Process.WaitForExit($TimeoutSec * 1000)) {
        throw "$Label did not exit within ${TimeoutSec}s after the kill fallback."
    }
    return $false
}

function Assert-LiveOutputRoot {
    param(
        [Parameter(Mandatory)]
        [string]$Parent,
        [Parameter(Mandatory)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Parent -PathType Container)) {
        throw "$Label did not create its configured PTK_OUTPUT_ROOT parent"
    }
    $children = @(Get-ChildItem -LiteralPath $Parent -Force)
    $serverRoots = @($children | Where-Object {
        $_.PSIsContainer -and $_.Name -match '^server-\d+-[0-9a-f]{32}$'
    })
    if ($children.Count -ne 1 -or $serverRoots.Count -ne 1) {
        throw "$Label output parent contained $($children.Count) children and $($serverRoots.Count) valid server roots"
    }

    $serverRoot = $serverRoots[0]
    if (($serverRoot.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Label output server root was a link or reparse point"
    }
    if (-not $IsWindows) {
        $expectedMode = [System.IO.UnixFileMode]::UserRead -bor
            [System.IO.UnixFileMode]::UserWrite -bor
            [System.IO.UnixFileMode]::UserExecute
        $actualMode = [System.IO.File]::GetUnixFileMode($serverRoot.FullName)
        if ($actualMode -ne $expectedMode) {
            throw "$Label output server root mode was $actualMode instead of $expectedMode"
        }
    }
    if (@(Get-ChildItem -LiteralPath $serverRoot.FullName -Recurse -Force -File).Count -ne 0) {
        throw "$Label output server root retained a named artifact while live"
    }
}

$outputToken = 'ptk-handshake-' + [guid]::NewGuid().ToString('N')
$escapedOutputToken = [regex]::Escape($outputToken)
$quotedOutputToken = "'" + $outputToken.Replace("'", "''") + "'"
$warmSeedScript = '$warm = ' + $quotedOutputToken
$warmReadScript = '$warm'
$mainExitedGracefully = $false
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
    foreach ($required in @('ptk_invoke', 'ptk_job', 'ptk_state', 'ptk_reset', 'ptk_output')) {
        if ($names -notcontains $required) {
            throw "$required missing from tools/list; got: $($names -join ', ')"
        }
    }
    foreach ($tool in @($tools.result.tools)) {
        $inputFields = @($tool.inputSchema.properties.PSObject.Properties.Name)
        foreach ($hostOnlyField in @('auditContext', 'outputStore')) {
            if ($inputFields -contains $hostOnlyField) {
                throw "host-only $hostOnlyField leaked into the $($tool.name) MCP input schema"
            }
        }
    }
    $outputTool = @($tools.result.tools | Where-Object name -EQ 'ptk_output')
    if ($outputTool.Count -ne 1) {
        throw "tools/list returned $($outputTool.Count) ptk_output definitions"
    }
    $outputSchema = $outputTool[0].inputSchema
    if (@($outputSchema.required) -notcontains 'handle') {
        throw 'ptk_output input schema does not require handle'
    }
    $outputFields = @($outputSchema.properties.PSObject.Properties.Name | Sort-Object)
    if (($outputFields -join ',') -ne 'action,handle,maxBytes,offset,pattern') {
        throw "ptk_output input fields drifted: $($outputFields -join ', ')"
    }
    $actions = @($outputSchema.properties.action.enum)
    if (($actions -join ',') -ne 'read,search,status') {
        throw "ptk_output action enum drifted: $($actions -join ', ')"
    }
    if ($outputSchema.properties.offset.minimum -ne 0) {
        throw "ptk_output offset minimum drifted: $($outputSchema.properties.offset.minimum)"
    }
    if ($outputSchema.properties.maxBytes.minimum -ne 1 -or
        $outputSchema.properties.maxBytes.maximum -ne 65536) {
        throw 'ptk_output maxBytes bounds drifted'
    }
    if ($outputSchema.properties.action.default -ne 'read' -or
        $outputSchema.properties.offset.default -ne 0 -or
        $outputSchema.properties.maxBytes.default -ne 16384 -or
        -not $outputSchema.properties.pattern.PSObject.Properties['default'] -or
        $null -ne $outputSchema.properties.pattern.default) {
        throw 'ptk_output defaults drifted'
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
        params = @{ name = 'ptk_invoke'; arguments = @{ script = $warmSeedScript } }
    }
    [void](Read-RpcResponse -Id 4)

    Send-Rpc @{
        jsonrpc = '2.0'; id = 5; method = 'tools/call'
        params = @{ name = 'ptk_invoke'; arguments = @{ script = $warmReadScript } }
    }
    $warmText = (Read-RpcResponse -Id 5).result.content[0].text
    if ($warmText -notmatch "(?m)^$escapedOutputToken\r?$") {
        throw "ptk_invoke cross-call state failed; second call returned '$warmText'."
    }
    Write-Host 'ptk_invoke cross-call state ok: runtime token survived'

    $handleMatches = [regex]::Matches(
        $warmText,
        '(?m)^recovery=available: ptk_output handle=(ptko_[A-Za-z0-9_-]+)\r?$')
    if ($handleMatches.Count -ne 1) {
        throw "ptk_invoke returned $($handleMatches.Count) recovery handles; text was '$warmText'."
    }
    $recoveryHandle = $handleMatches[0].Groups[1].Value
    Send-Rpc @{
        jsonrpc = '2.0'; id = 6; method = 'tools/call'
        params = @{ name = 'ptk_output'; arguments = @{ handle = $recoveryHandle } }
    }
    $outputRead = (Read-RpcResponse -Id 6).result
    $outputText = $outputRead.content[0].text
    $outputHeader = ($outputText -split '\r?\n', 2)[0]
    if ($outputRead.isError -or
        $outputHeader -notmatch '^\[ptk output\] action=read state=available complete=true bytes=\d+ provenance=powershell_objects offset=0 next_offset=\d+ bytes_returned=\d+$' -or
        $outputText -notmatch "(?m)^$escapedOutputToken\r?$") {
        throw "ptk_output did not retrieve the advertised invocation: '$outputText'."
    }
    Write-Host 'ptk_output recovery ok: advertised handle returned the runtime token with PowerShell-object provenance'
    Assert-LiveOutputRoot -Parent $outputParent -Label 'main server'

}
catch {
    Write-Host "HANDSHAKE FAILED: $_"
    $failed = $true
}
finally {
    try {
        $mainExitedGracefully = Stop-ServerProcess -Process $proc -Label 'main server'
        if (-not $mainExitedGracefully) {
            Write-Host 'HANDSHAKE FAILED: main server required the kill fallback during shutdown.'
            $failed = $true
        }
    }
    catch {
        Write-Host "HANDSHAKE FAILED: main server shutdown failed: $_"
        $failed = $true
    }
    $serverError = if ($proc.HasExited) {
        $proc.StandardError.ReadToEnd()
    }
    else {
        'server process remained alive after bounded shutdown attempts'
    }
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
            if ($rawAudit.Contains($outputToken) -or
                $rawAudit.Contains($warmSeedScript) -or
                $rawAudit.Contains($warmReadScript)) {
                throw 'runtime output token or exact script text leaked into the core audit stream'
            }
            $outputAccepted = @($events | Where-Object {
                $_.event_type -eq 'call.accepted' -and $_.request.tool -eq 'ptk_output'
            })
            if ($outputAccepted.Count -ne 1) {
                throw "expected one accepted output audit event; got $($outputAccepted.Count)"
            }
            $outputCallId = $outputAccepted[0].correlation.call_id
            $outputEvents = @($events | Where-Object {
                $_.correlation.call_id -eq $outputCallId
            } | Sort-Object sequence)
            $outputEventTypes = @($outputEvents.event_type)
            if (($outputEventTypes -join ',') -ne 'call.accepted,output.read_accessed,call.completed') {
                throw "output audit lifecycle drifted: $($outputEventTypes -join ', ')"
            }
            $outputAccess = @($outputEvents | Where-Object event_type -EQ 'output.read_accessed')
            if ($outputAccess.Count -ne 1 -or
                $outputAccess[0].outcome.state -ne 'available' -or
                $outputAccess[0].outcome.bytes_returned -le 0 -or
                $outputAccess[0].outcome.next_offset -le 0 -or
                -not $outputAccess[0].request.output_handle_digest) {
                throw 'output read audit facts were incomplete or incorrect'
            }
            if ($rawAudit.Contains($recoveryHandle)) {
                throw 'raw output handle leaked into the core audit stream'
            }
            $evidence = @(Get-ChildItem -LiteralPath (Join-Path $auditRoot 'evidence') -Filter '*.script')
            if ($evidence.Count -ne 2) { throw "expected two evidence payloads; got $($evidence.Count)" }
            $payloads = @($evidence | ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName })
            if ($payloads -notcontains $warmSeedScript -or $payloads -notcontains $warmReadScript) {
                throw 'evidence payloads did not preserve the exact submitted scripts'
            }
            Write-Host 'audit ok: boundary attribution, provided fields, evidence references, and core secrecy verified'
        }
        catch {
            Write-Host "AUDIT VERIFICATION FAILED: $_"
            $failed = $true
        }
    }
    if ($mainExitedGracefully) {
        try {
            $retainedOutputFiles = if (Test-Path -LiteralPath $outputParent) {
                @(Get-ChildItem -LiteralPath $outputParent -Recurse -Force -File)
            }
            else {
                @()
            }
            if ($retainedOutputFiles.Count -ne 0) {
                throw "graceful shutdown retained $($retainedOutputFiles.Count) output artifact file(s)"
            }
            Write-Host 'output cleanup ok: graceful main exit retained no artifact files'
        }
        catch {
            Write-Host "OUTPUT CLEANUP VERIFICATION FAILED: $_"
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
    if (Test-Path -LiteralPath $outputParent) {
        Remove-Item -LiteralPath $outputParent -Recurse -Force
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
    $diagnosticOutputParent = Join-Path $diagnosticParent 'output'
    $proc = $null
    try {
        [void](New-Item -ItemType Directory -Path $diagnosticParent)
        Set-Content -LiteralPath $blocker -Value 'blocked'
        $psi.Environment['PTK_AUDIT_ROOT'] = Join-Path $blocker 'audit'
        $psi.Environment['PTK_OUTPUT_ROOT'] = $diagnosticOutputParent
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
        if (Test-Path -LiteralPath $diagnosticOutputParent) {
            throw 'diagnostic-only audit outage allocated an output-store root behind the audit gate'
        }
        Write-Host 'audit outage ok: MCP diagnostic remains available and invoke stays fail-closed'
    }
    catch {
        Write-Host "DIAGNOSTIC HANDSHAKE FAILED: $_"
        $failed = $true
    }
    finally {
        if ($null -ne $proc) {
            try {
                $diagnosticExitedGracefully = Stop-ServerProcess -Process $proc -Label 'diagnostic server'
                if (-not $diagnosticExitedGracefully) {
                    Write-Host 'DIAGNOSTIC HANDSHAKE FAILED: server required the kill fallback during shutdown.'
                    $failed = $true
                }
            }
            catch {
                Write-Host "DIAGNOSTIC HANDSHAKE FAILED: server shutdown failed: $_"
                $failed = $true
            }
            $diagnosticError = if ($proc.HasExited) {
                $proc.StandardError.ReadToEnd()
            }
            else {
                'diagnostic server remained alive after bounded shutdown attempts'
            }
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

# A hard-killed harness cannot run OutputStore.Dispose. Recovery must therefore
# use an already-unlinked artifact through its supervisor-owned open handle.
if (-not $failed) {
    $hardKillAuditRoot = Join-Path (
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) (
        '.ptk/test-handshake-hard-kill-audit-' + [guid]::NewGuid().ToString('N'))
    $hardKillOutputParent = Join-Path (
        [Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) (
        '.ptk/test-handshake-hard-kill-output-' + [guid]::NewGuid().ToString('N'))
    $hardKillToken = 'ptk-hard-kill-' + [guid]::NewGuid().ToString('N')
    $hardKillScript = "'" + $hardKillToken.Replace("'", "''") + "'"
    $proc = $null
    try {
        $psi.Environment['PTK_AUDIT_ROOT'] = $hardKillAuditRoot
        $psi.Environment['PTK_OUTPUT_ROOT'] = $hardKillOutputParent
        $proc = [System.Diagnostics.Process]::Start($psi)

        Send-Rpc @{
            jsonrpc = '2.0'; id = 201; method = 'initialize'
            params = @{
                protocolVersion = '2025-06-18'
                capabilities    = @{}
                clientInfo      = @{ name = 'ptk-handshake-hard-kill'; version = '0.0.0' }
            }
        }
        $hardKillInit = Read-RpcResponse -Id 201
        if (-not $hardKillInit.result.serverInfo.name) {
            throw 'hard-kill initialize failed'
        }
        Send-Rpc @{ jsonrpc = '2.0'; method = 'notifications/initialized' }
        Send-Rpc @{
            jsonrpc = '2.0'; id = 202; method = 'tools/call'
            params = @{
                name = 'ptk_invoke'
                arguments = @{ script = $hardKillScript; route = 'pwsh' }
            }
        }
        $hardKillInvoke = (Read-RpcResponse -Id 202).result
        $hardKillText = $hardKillInvoke.content[0].text
        $hardKillHandles = [regex]::Matches(
            $hardKillText,
            '(?m)^recovery=available: ptk_output handle=(ptko_[A-Za-z0-9_-]+)\r?$')
        if ($hardKillInvoke.isError -or
            $hardKillText -notmatch "(?m)^$([regex]::Escape($hardKillToken))\r?$" -or
            $hardKillHandles.Count -ne 1) {
            throw 'hard-kill invoke did not produce one recoverable PowerShell artifact'
        }
        $hardKillHandle = $hardKillHandles[0].Groups[1].Value
        Send-Rpc @{
            jsonrpc = '2.0'; id = 203; method = 'tools/call'
            params = @{ name = 'ptk_output'; arguments = @{ handle = $hardKillHandle } }
        }
        $hardKillRead = (Read-RpcResponse -Id 203).result
        if ($hardKillRead.isError -or
            $hardKillRead.content[0].text -notmatch "(?m)^$([regex]::Escape($hardKillToken))\r?$") {
            throw 'hard-kill guard could not read the live anonymous recovery artifact'
        }
        Assert-LiveOutputRoot -Parent $hardKillOutputParent -Label 'hard-kill server'

        $liveArtifactFiles = if (Test-Path -LiteralPath $hardKillOutputParent) {
            @(Get-ChildItem -LiteralPath $hardKillOutputParent -Recurse -Force -File |
                Where-Object Name -Like 'artifact-*.out')
        }
        else {
            @()
        }
        if ($liveArtifactFiles.Count -ne 0) {
            throw "live anonymous recovery retained $($liveArtifactFiles.Count) named artifact file(s)"
        }

        $proc.Kill($true)
        if (-not $proc.WaitForExit($TimeoutSec * 1000)) {
            throw "hard-killed server did not exit within ${TimeoutSec}s"
        }

        $remainingArtifactFiles = if (Test-Path -LiteralPath $hardKillOutputParent) {
            @(Get-ChildItem -LiteralPath $hardKillOutputParent -Recurse -Force -File |
                Where-Object Name -Like 'artifact-*.out')
        }
        else {
            @()
        }
        if ($remainingArtifactFiles.Count -ne 0) {
            throw "hard-kill retained $($remainingArtifactFiles.Count) output artifact file(s)"
        }
        Write-Host 'hard-kill output cleanup ok: anonymous live recovery left no named artifact before or after exit'
    }
    catch {
        Write-Host "HARD-KILL OUTPUT CLEANUP FAILED: $_"
        $failed = $true
    }
    finally {
        if ($null -ne $proc) {
            if (-not $proc.HasExited) {
                try { [void](Stop-ServerProcess -Process $proc -Label 'hard-kill guard server') }
                catch {
                    Write-Host "HARD-KILL OUTPUT CLEANUP FAILED: bounded process cleanup failed: $_"
                    $failed = $true
                }
            }
            $hardKillError = if ($proc.HasExited) {
                $proc.StandardError.ReadToEnd()
            }
            else {
                'hard-kill guard server remained alive after bounded cleanup attempts'
            }
            if ($failed -and -not [string]::IsNullOrWhiteSpace($hardKillError)) {
                Write-Host "hard-kill server stderr:`n$hardKillError"
            }
            $proc.Dispose()
        }
        if (Test-Path -LiteralPath $hardKillAuditRoot) {
            Remove-Item -LiteralPath $hardKillAuditRoot -Recurse -Force
        }
        if (Test-Path -LiteralPath $hardKillOutputParent) {
            Remove-Item -LiteralPath $hardKillOutputParent -Recurse -Force
        }
    }
}

if (-not $failed) { Write-Host 'HANDSHAKE PASSED' }
exit ($failed ? 1 : 0)
