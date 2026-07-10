Set-StrictMode -Version Latest

$script:DefaultMaxItems = 40
$script:DefaultWidth = 140
$script:PtcTableMaxColumnWidth = 50
$script:PtcPassthroughMaxLines = 400
$script:PtcPassthroughMaxChars = 40KB

function Remove-PtcAnsi {
    param([AllowNull()][string]$Text)
    if ($null -eq $Text) { return '' }
    return $Text -replace "`e\[[0-9;?]*[ -/]*[@-~]", ''
}

function Limit-PtcText {
    param(
        [AllowNull()][string]$Text,
        [int]$MaxLines = 80,
        [int]$Width = $script:DefaultWidth
    )

    $clean = Remove-PtcAnsi $Text
    $lines = @($clean -split "`r?`n")
    $shown = foreach ($line in $lines | Select-Object -First $MaxLines) {
        if ($line.Length -gt $Width) {
            $line.Substring(0, [Math]::Max(0, $Width - 1)) + '...'
        } else {
            $line
        }
    }

    if ($lines.Count -gt $MaxLines) {
        $shown += "[{0} more lines]" -f ($lines.Count - $MaxLines)
    }

    $shown -join [Environment]::NewLine
}

function Format-PtcSize {
    param([Nullable[long]]$Bytes)
    if ($null -eq $Bytes) { return '' }
    if ($Bytes -ge 1GB) { return '{0:n1}G' -f ($Bytes / 1GB) }
    if ($Bytes -ge 1MB) { return '{0:n1}M' -f ($Bytes / 1MB) }
    if ($Bytes -ge 1KB) { return '{0:n1}K' -f ($Bytes / 1KB) }
    return '{0}B' -f $Bytes
}

function Join-PtcLines {
    param([string[]]$Lines)
    ($Lines | Where-Object { $_ -ne $null }) -join [Environment]::NewLine
}

function Get-PtcDisplayProperties {
    param(
        [object]$Object,
        [int]$MaxProperties = 6
    )

    $preferred = @(
        'Name', 'DisplayName', 'Status', 'State', 'Id', 'ProcessName',
        'Path', 'FullName', 'LineNumber', 'Length', 'LastWriteTime',
        'CommandType', 'Version', 'Source'
    )

    $names = @($Object.PSObject.Properties |
        Where-Object { $_.MemberType -in 'NoteProperty', 'Property', 'AliasProperty' } |
        Select-Object -ExpandProperty Name)

    $ordered = @()
    foreach ($name in $preferred) {
        if ($names -contains $name) { $ordered += $name }
    }
    foreach ($name in $names) {
        if ($ordered -notcontains $name) { $ordered += $name }
    }

    $ordered | Select-Object -First $MaxProperties
}

function ConvertTo-PtcScalar {
    param([AllowNull()][object]$Value)
    if ($null -eq $Value) { return '' }
    if ($Value -is [datetime]) { return $Value.ToString('yyyy-MM-dd HH:mm:ss') }
    if ($Value -is [bool]) { return $Value.ToString().ToLowerInvariant() }
    if ($Value -is [System.Collections.IEnumerable] -and $Value -isnot [string]) {
        return '[{0}]' -f @($Value).Count
    }
    Limit-PtcText -Text ([string]$Value) -MaxLines 1 -Width 80
}

function Get-PtcPropertyValue {
    param(
        [object]$Object,
        [string]$Name
    )

    $property = $Object.PSObject.Properties[$Name]
    if ($null -eq $property) { return $null }
    $property.Value
}

function Test-PtcHasProperty {
    param(
        [object]$Object,
        [string[]]$Name
    )

    foreach ($candidate in $Name) {
        if ($null -eq $Object.PSObject.Properties[$candidate]) { return $false }
    }
    $true
}

# Index-based head slice. Never use Select-Object -First on object rows in
# shaping code: it stamps 'Selected.*' into the live TypeNames of
# PSObject-wrapped items (PSCustomObject, any Deserialized.* from remoting
# or Import-Clixml), and the mutation persists on the caller's objects
# across warm-session calls (i1-2).
function Select-PtcFirst {
    param(
        [object[]]$Items,
        [int]$Count
    )
    $all = @($Items)
    if ($all.Count -eq 0 -or $Count -le 0) { return @() }
    @($all[0..([Math]::Min($Count, $all.Count) - 1)])
}

function Format-PtcTable {
    param(
        [object[]]$Rows,
        [string[]]$Properties,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    if ($Rows.Count -eq 0) { return @() }
    # Slice by index, never Select-Object: piping PSObject-wrapped rows (any
    # PSCustomObject) through Select-Object -First stamps 'Selected.*' into
    # their LIVE TypeNames, and the mutation persists on the caller's
    # objects across warm-session calls.
    # A non-positive take must yield ZERO rows ($Rows[0..-1] wraps around to
    # first+last), matching Select-Object -First 0 semantics (i1-3).
    $take = [Math]::Min($MaxItems, $Rows.Count)
    $sliced = ($take -gt 0) ? @($Rows[0..($take - 1)]) : @()
    if ($Properties.Count -eq 0) {
        $lines = @($sliced | ForEach-Object { [string]$_ })
        if ($Rows.Count -gt $MaxItems) { $lines += '+{0} more' -f ($Rows.Count - $MaxItems) }
        return $lines
    }

    $visible = $sliced
    $widths = @{}
    foreach ($prop in $Properties) {
        $max = $prop.Length
        foreach ($row in $visible) {
            $value = ConvertTo-PtcScalar (Get-PtcPropertyValue -Object $row -Name $prop)
            if ($value.Length -gt $max) { $max = [Math]::Min($value.Length, $script:PtcTableMaxColumnWidth) }
        }
        $widths[$prop] = $max
    }

    $lines = @()
    $header = ($Properties | ForEach-Object { $_.PadRight($widths[$_]) }) -join '  '
    $lines += $header.TrimEnd()
    $lines += (($Properties | ForEach-Object { ('-' * $widths[$_]) }) -join '  ')

    foreach ($row in $visible) {
        $cells = foreach ($prop in $Properties) {
            $value = ConvertTo-PtcScalar (Get-PtcPropertyValue -Object $row -Name $prop)
            if ($value.Length -gt $script:PtcTableMaxColumnWidth) { $value = $value.Substring(0, $script:PtcTableMaxColumnWidth - 1) + '...' }
            $value.PadRight($widths[$prop])
        }
        $lines += (($cells -join '  ').TrimEnd())
    }

    if ($Rows.Count -gt $MaxItems) {
        $lines += '+{0} more' -f ($Rows.Count - $MaxItems)
    }

    $lines
}

function Compress-PtcFileSystem {
    param(
        [object[]]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $items = @($InputObject)
    $dirs = @($items | Where-Object { [bool](Get-PtcPropertyValue -Object $_ -Name 'PSIsContainer') })
    $files = @($items | Where-Object { -not [bool](Get-PtcPropertyValue -Object $_ -Name 'PSIsContainer') })
    $totalBytes = ($files | ForEach-Object { [long](Get-PtcPropertyValue -Object $_ -Name 'Length') } | Measure-Object -Sum).Sum
    if ($null -eq $totalBytes) { $totalBytes = 0 }

    $lines = @("fs: {0} dirs, {1} files, {2}" -f $dirs.Count, $files.Count, (Format-PtcSize $totalBytes))

    $dirRows = @((Select-PtcFirst @($dirs | Sort-Object Name) $MaxItems) | ForEach-Object {
        [pscustomobject]@{
            Type = 'dir'
            Name = $_.Name + '\'
            Modified = $_.LastWriteTime
        }
    })

    $fileRows = @((Select-PtcFirst @($files | Sort-Object Name) $MaxItems) | ForEach-Object {
        [pscustomobject]@{
            Type = 'file'
            Name = $_.Name
            Size = Format-PtcSize (Get-PtcPropertyValue -Object $_ -Name 'Length')
            Modified = $_.LastWriteTime
        }
    })

    $rows = @($dirRows + $fileRows)
    if ($rows.Count -gt 0) {
        $lines += Format-PtcTable -Rows $rows -Properties @('Type', 'Name', 'Size', 'Modified') -MaxItems $MaxItems
    }

    $omitted = $items.Count - $rows.Count
    if ($omitted -gt 0) { $lines += '+{0} more filesystem items' -f $omitted }
    Join-PtcLines $lines
}

function Compress-PtcMatchInfo {
    param(
        [object[]]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $matches = @($InputObject)
    $groups = @($matches | Group-Object Path | Sort-Object Name)
    $lines = @('{0} matches in {1} files' -f $matches.Count, $groups.Count)

    foreach ($group in (Select-PtcFirst $groups $MaxItems)) {
        $lines += ''
        $lines += '[file] {0} ({1})' -f $group.Name, $group.Count
        foreach ($match in (Select-PtcFirst @($group.Group) 8)) {
            $text = Limit-PtcText -Text ([string]$match.Line).Trim() -MaxLines 1 -Width 110
            $lines += '  {0,5}: {1}' -f $match.LineNumber, $text
        }
        if ($group.Count -gt 8) { $lines += '  +{0}' -f ($group.Count - 8) }
    }

    if ($groups.Count -gt $MaxItems) {
        $lines += ''
        $lines += '+{0} more files' -f ($groups.Count - $MaxItems)
    }

    Join-PtcLines $lines
}

function Compress-PtcProcess {
    param(
        [object[]]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $items = @($InputObject)
    $rows = @((Select-PtcFirst @($items | Sort-Object CPU -Descending) $MaxItems) | ForEach-Object {
        [pscustomobject]@{
            ProcessName = $_.ProcessName
            Id = $_.Id
            CPU = if ($null -eq $_.CPU) { '' } else { '{0:n1}' -f $_.CPU }
            WS = Format-PtcSize $_.WorkingSet64
        }
    })

    Join-PtcLines (@("processes: {0}" -f $items.Count) + (Format-PtcTable -Rows $rows -Properties @('ProcessName', 'Id', 'CPU', 'WS') -MaxItems $MaxItems))
}

function Compress-PtcService {
    param(
        [object[]]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $items = @($InputObject)
    $status = @($items | Group-Object Status | Sort-Object Name | ForEach-Object { '{0}={1}' -f $_.Name, $_.Count })
    $rows = @((Select-PtcFirst @($items | Sort-Object Status, Name) $MaxItems) | ForEach-Object {
        [pscustomobject]@{
            Status = $_.Status
            Name = $_.Name
            DisplayName = $_.DisplayName
        }
    })

    Join-PtcLines (@("services: {0} ({1})" -f $items.Count, ($status -join ', ')) + (Format-PtcTable -Rows $rows -Properties @('Status', 'Name', 'DisplayName') -MaxItems $MaxItems))
}

function Compress-PtcGenericObject {
    param(
        [object[]]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $items = @($InputObject)
    if ($items.Count -eq 0) { return '(empty)' }
    if ($items.Count -eq 1 -and $items[0] -is [string]) {
        return Limit-PtcText -Text $items[0] -MaxLines $MaxItems
    }

    $stringCount = @($items | Where-Object { $_ -is [string] }).Count
    if ($stringCount -eq $items.Count) {
        return Limit-PtcText -Text ($items -join [Environment]::NewLine) -MaxLines $MaxItems
    }
    if ($stringCount -gt 0) {
        # Mixed stream containing strings: the text is the medium (issue #1 —
        # a String+MatchInfo repro rendered a Length-only table and lost the
        # payload). Render every item by its string form: strings are
        # themselves, MatchInfo.ToString() is path:line:content.
        return Limit-PtcText -Text (@($items | ForEach-Object { [string]$_ }) -join [Environment]::NewLine) -MaxLines $MaxItems
    }

    # Index, don't Select-Object: piping a PSObject through Select-Object
    # -First stamps 'Selected.*' into its live TypeNames - the header would
    # name a wrapper type, and the mutation leaks onto the caller's objects
    # (visible across warm-session calls).
    $first = $items[0]
    $props = @(Get-PtcDisplayProperties -Object $first)
    $typeNames = @($items | ForEach-Object { $_.PSObject.TypeNames[0] } | Select-Object -Unique)
    $header = if ($typeNames.Count -gt 1) {
        # Bound the type list too: a stream of many distinct types must not
        # grow the header line without limit (i1-1).
        $shown = @($typeNames | Select-Object -First 3) -join ', '
        $suffix = ($typeNames.Count -gt 3) ? (', +{0} more' -f ($typeNames.Count - 3)) : ''
        "objects: {0} (mixed: {1}{2})" -f $items.Count, $shown, $suffix
    }
    else {
        "objects: {0} ({1})" -f $items.Count, $typeNames[0]
    }
    $lines = @($header) + (Format-PtcTable -Rows $items -Properties $props -MaxItems $MaxItems)
    if ($typeNames.Count -gt 1) {
        # The table's columns come from the FIRST item only, so on a
        # type-heterogeneous stream it misrepresents the rest; carry some
        # payload so a summary never needs a raw re-run (issue #1 guardrail).
        $lines += 'samples:'
        for ($i = 0; $i -lt [Math]::Min(3, $items.Count); $i++) {
            $lines += '  ' + (Limit-PtcText -Text ([string]$items[$i]) -MaxLines 1 -Width 110)
        }
    }
    Join-PtcLines $lines
}

function Compress-PtcObject {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline)]
        [AllowNull()]
        [object]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    begin {
        $items = [System.Collections.Generic.List[object]]::new()
    }
    process {
        if ($null -ne $InputObject) { $items.Add($InputObject) }
    }
    end {
        $array = @($items)
        if ($array.Count -eq 0) {
            '(empty)'
            return
        }

        # Route by type name, but only when EVERY item carries a matching type
        # name AND the properties the specialized compressor needs. Projections
        # and Clixml round-trips (e.g. Select-Object) keep the source type name
        # while dropping properties, so type name alone is not enough to
        # dispatch on; and shape alone is not enough either — one genuine item
        # must not drag look-alike shapes of other types into a specialized
        # compressor. Heterogeneous streams fall through to the generic path,
        # whose property access is null-safe. Each guard must list every
        # property its compressor dereferences directly; a property that is
        # legitimately absent on real objects is guarded conditionally instead:
        # DirectoryInfo has no Length, but a *file* without a known Length is a
        # projection whose size is unknown, not zero, so it goes generic.
        $typeNames = @($array | ForEach-Object { $_.PSObject.TypeNames[0] } | Select-Object -Unique)
        $allMatchType = {
            param([string[]]$Pattern)
            foreach ($typeName in $typeNames) {
                $matched = @($Pattern | Where-Object { $typeName -like $_ }).Count -gt 0
                if (-not $matched) { return $false }
            }
            $true
        }
        $allHaveProperties = {
            param([string[]]$Name)
            foreach ($item in $array) {
                if (-not (Test-PtcHasProperty -Object $item -Name $Name)) { return $false }
            }
            $true
        }

        $allFileSystemShaped = {
            foreach ($item in $array) {
                if (-not (Test-PtcHasProperty -Object $item -Name 'PSIsContainer', 'Name', 'LastWriteTime')) { return $false }
                if (-not [bool](Get-PtcPropertyValue -Object $item -Name 'PSIsContainer') -and
                    $null -eq (Get-PtcPropertyValue -Object $item -Name 'Length')) { return $false }
            }
            $true
        }

        if ((& $allMatchType '*System.IO.DirectoryInfo*', '*System.IO.FileInfo*') -and
            (& $allFileSystemShaped)) {
            Compress-PtcFileSystem -InputObject $array -MaxItems $MaxItems
            return
        }
        if ((& $allMatchType '*Microsoft.PowerShell.Commands.MatchInfo*') -and
            (& $allHaveProperties 'LineNumber', 'Path', 'Line')) {
            Compress-PtcMatchInfo -InputObject $array -MaxItems $MaxItems
            return
        }
        if ((& $allMatchType '*System.Diagnostics.Process*') -and
            (& $allHaveProperties 'Id', 'ProcessName', 'CPU', 'WorkingSet64')) {
            Compress-PtcProcess -InputObject $array -MaxItems $MaxItems
            return
        }
        if ((& $allMatchType '*ServiceController*') -and
            (& $allHaveProperties 'Status', 'Name', 'DisplayName')) {
            Compress-PtcService -InputObject $array -MaxItems $MaxItems
            return
        }

        Compress-PtcGenericObject -InputObject $array -MaxItems $MaxItems
    }
}

# Heuristic: does this text look like a log? (timestamped lines and/or level tags
# across most of the first 40 lines). Ported from the experiment/ptk-router spike.
function Test-PtcLogShaped {
    param([string]$Text)
    $lines = @($Text -split "`r?`n" | Where-Object { $_.Trim() }) | Select-Object -First 40
    if (@($lines).Count -lt 5) { return $false }
    $levelHits = @($lines | Where-Object {
        $_ -match '\[(INFO|WARN|WARNING|ERROR|FATAL|DEBUG|TRACE)\]' -or
        $_ -match '\b(INFO|WARN|ERROR|FATAL)\b.*:' -or
        $_ -match '^\d{4}-\d{2}-\d{2}[T ]\d{2}:\d{2}'
    }).Count
    return ($levelHits / @($lines).Count) -ge 0.5
}

# An explicitly set PTK_RTK_PATH wins outright: if it points at nothing, rtk is
# treated as absent rather than silently falling back to a different binary on
# PATH, so a misconfiguration stays visible.
function Get-PtcRtkCommand {
    if (Test-Path env:PTK_RTK_PATH) {
        if ($env:PTK_RTK_PATH -and (Test-Path -LiteralPath $env:PTK_RTK_PATH)) { return $env:PTK_RTK_PATH }
        return $null
    }
    # Intrinsics resolver: a PATH miss must not leave a hidden entry in the
    # caller's $Error (routing probes this on every shaped call).
    $cmd = $ExecutionContext.InvokeCommand.GetCommand(
        'rtk', [System.Management.Automation.CommandTypes]::Application)
    if ($cmd) { return $cmd.Source }
    return $null
}

function Invoke-PtcRtkLog {
    param([string]$Text)
    $rtk = Get-PtcRtkCommand
    if (-not $rtk) {
        return "[ptk:log rtk not found - returning raw text.]`n$Text"
    }
    # rtk is a native command, so invoking it overwrites the caller's
    # $LASTEXITCODE in this runspace. Shaping must not affect the call
    # (Compress-PtcOutput's contract), so restore the snapshot on the way out.
    # Snapshot the value, not the PSVariable: Get-Variable returns the live
    # variable object, whose .Value would mutate when rtk overwrites it.
    $exitCodeVariable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    $hadExitCode = $null -ne $exitCodeVariable
    $savedExitCode = if ($hadExitCode) { $exitCodeVariable.Value } else { $null }
    $tmp = [System.IO.Path]::GetTempFileName()
    try {
        Set-Content -LiteralPath $tmp -Value $Text -NoNewline
        $result = & $rtk log $tmp 2>$null
        $ok = $?
        if (-not $ok -or @($result).Count -eq 0) {
            return "[ptk:log rtk failed - returning raw text.]`n$Text"
        }
        "[ptk:log via rtk]`n" + (@($result) -join [Environment]::NewLine)
    } finally {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        if ($hadExitCode) {
            Set-Variable -Name LASTEXITCODE -Scope Global -Value $savedExitCode
        } else {
            Remove-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
        }
    }
}

# Routing for the unified shell surface (unified-shell-routing plan): a script
# that is exactly one bare native-application command with constant arguments
# is rewritten to run through rtk, whose per-command filters compress output at
# the source and whose passthrough keeps unknown commands intact (verified in
# the plan's slice-0 probe). Everything else - pipelines, chains, variables,
# cmdlets, aliases, redirections, parse errors - returns unchanged and runs as
# PowerShell: natives inside chains execute with correct semantics there, and
# their text output still gets the log-shaped rtk leg in Compress-PtcOutput.
# With no rtk binary the script also returns unchanged (same execution as
# before routing existed; unfiltered, never a failure).
function Resolve-PtcInvokeScript {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Script,
        # 'pwsh' always returns the script unchanged; 'rtk' skips the
        # Application resolution check but still needs the single-command
        # constant-args shape to rewrite safely.
        [ValidateSet('auto', 'pwsh', 'rtk')]
        [string]$Route = 'auto'
    )

    if ($Route -eq 'pwsh') { return $Script }

    $rtk = Get-PtcRtkCommand
    if (-not $rtk) { return $Script }

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Script, [ref]$tokens, [ref]$parseErrors)
    if (@($parseErrors).Count -gt 0) { return $Script }
    if ($null -ne $ast.ParamBlock -or $null -ne $ast.BeginBlock -or $null -ne $ast.ProcessBlock) { return $Script }
    if ($null -eq $ast.EndBlock) { return $Script }
    $statements = @($ast.EndBlock.Statements)
    if ($statements.Count -ne 1) { return $Script }
    $pipeline = $statements[0]
    if ($pipeline -isnot [System.Management.Automation.Language.PipelineAst]) { return $Script }
    if (@($pipeline.PipelineElements).Count -ne 1) { return $Script }
    $command = $pipeline.PipelineElements[0]
    if ($command -isnot [System.Management.Automation.Language.CommandAst]) { return $Script }
    if ($command.InvocationOperator -ne [System.Management.Automation.Language.TokenKind]::Unknown) { return $Script }
    if (@($command.Redirections).Count -gt 0) { return $Script }

    $elements = @($command.CommandElements)
    if ($elements[0] -isnot [System.Management.Automation.Language.StringConstantExpressionAst]) { return $Script }
    $name = $elements[0].Value
    if ([System.IO.Path]::GetFileNameWithoutExtension($name) -eq 'rtk') { return $Script }
    foreach ($element in ($elements | Select-Object -Skip 1)) {
        # StringConstantExpressionAst derives from ConstantExpressionAst, so
        # one check covers bare words, quoted literals, and numbers. A
        # parameter like -m is constant unless its attached argument is not
        # (-flag:$var).
        $isConstant = $element -is [System.Management.Automation.Language.ConstantExpressionAst] -or
            ($element -is [System.Management.Automation.Language.CommandParameterAst] -and
                ($null -eq $element.Argument -or
                 $element.Argument -is [System.Management.Automation.Language.ConstantExpressionAst]))
        if (-not $isConstant) { return $Script }
    }

    if ($Route -ne 'rtk') {
        # Resolve in this runspace: aliases, cmdlets, and functions shadow
        # native binaries here exactly as they would at execution time (on
        # Windows, ls is an alias and `rtk ls` fails - slice-0 probe). The
        # intrinsics API resolves without polluting $Error the way
        # Get-Command -ErrorAction SilentlyContinue does on a miss.
        $resolved = $ExecutionContext.InvokeCommand.GetCommand(
            $name, [System.Management.Automation.CommandTypes]::All)
        if ($null -eq $resolved -or $resolved.CommandType -ne [System.Management.Automation.CommandTypes]::Application) { return $Script }
        # Batch shims (npm.cmd, npx.cmd) get special argument quoting from
        # PowerShell that an rtk.exe re-invocation would not reproduce -
        # empty-string and embedded-quote args could reach the shim
        # differently. Semantics win over filtering: keep them on the
        # PowerShell path.
        if ([System.IO.Path]::GetExtension($resolved.Source) -in '.cmd', '.bat') { return $Script }
    }

    "& '{0}' {1}" -f $rtk.Replace("'", "''"), $command.Extent.Text
}

# Shell-dialect detector (shell-dialect plan D1, slice 1; every shape below
# was probed 2026-07-09 and frozen in .agents/plans/shell-dialect.md "Slice
# 0 results"). Agents feed this harness bash one-liners, and PowerShell
# either refuses them with errors that never name the real problem (a
# heredoc dies as "Missing file specification after redirection operator")
# or - the worst probed case - silently changes their meaning (echo `date`
# prints the literal text and exits 0). This names the construct so the
# invoke path can refuse fast with honest guidance (slice 2) instead of
# letting the misdiagnosis reach the agent. Detection is deliberately
# narrow: parse-fatal shapes key on the parser's own error id AND the bash
# text shape; clean-parse shapes key on exact AST forms; the plan's
# false-positive set must never trip. An over-match breaks a legitimate
# script while a miss merely falls back to today's behavior, so precision
# wins over recall - trailing-\ line continuation is excluded for exactly
# that reason (a legal Windows path ending; accepted miss, frozen in the
# plan). Returns a short label naming the construct, or $null.
function Get-PtcShellDialectFinding {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Script
    )

    $tokens = $null
    $parseErrors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseInput($Script, [ref]$tokens, [ref]$parseErrors)

    # sd1-1 (rounds 2-4): the ambient GetCommand guard cannot see
    # definitions carried by the submitted script itself (function export
    # { ... }; export X=1 executes fine - re-grade round 1), so
    # script-local definitions count as resolved too: function definitions
    # AND Set-Alias/New-Alias (round 3, plan slice 1(iii)) - but only for
    # uses they lexically PRECEDE (execution is sequential) or that sit
    # INSIDE their own definition's body (recursion, round 4). Alias
    # spellings beyond the positional and -Name forms are accepted
    # residuals, recorded in the finding. Collected before the parse-fatal
    # branch because keyword evidence consults it too (sd1-7 round 4).
    $localDefs = [System.Collections.Generic.List[object]]::new()
    foreach ($definition in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
        $localDefs.Add(@{ Name = $definition.Name; Start = $definition.Extent.StartOffset; End = $definition.Extent.EndOffset })
    }
    foreach ($aliasCommand in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true)) {
        $aliasElements = @($aliasCommand.CommandElements)
        if ($aliasElements[0] -isnot [System.Management.Automation.Language.StringConstantExpressionAst] -or
            $aliasElements[0].Value -notin @('Set-Alias', 'New-Alias')) { continue }
        $aliasName = $null
        for ($j = 1; $j -lt $aliasElements.Count; $j++) {
            $element = $aliasElements[$j]
            if ($element -is [System.Management.Automation.Language.CommandParameterAst]) {
                if ($element.ParameterName -eq 'Name') {
                    if ($element.Argument -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                        $aliasName = $element.Argument.Value
                    } elseif ($j + 1 -lt $aliasElements.Count -and
                        $aliasElements[$j + 1] -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                        $aliasName = $aliasElements[$j + 1].Value
                    }
                }
                # Any other leading named parameter: bail rather than guess
                # which later element is the name.
                break
            }
            if ($element -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
                $aliasName = $element.Value
                break
            }
        }
        if ($aliasName) { $localDefs.Add(@{ Name = $aliasName; Start = $aliasCommand.Extent.StartOffset; End = $aliasCommand.Extent.EndOffset }) }
    }

    if (@($parseErrors).Count -gt 0) {
        # Parse-fatal bash shapes. PowerShell already refuses these, but the
        # errors misdirect (slice-0 probe table), so the id alone is not
        # enough: an unrelated script hitting the same id must stay
        # unflagged, hence the paired text-shape checks.
        # sd1-3: shape evidence must come from code, not comments or string
        # literals, so those token extents are blanked (offsets preserved)
        # before matching. Token streams beside parse errors can be
        # partial; blanking only ever removes evidence, so degradation is
        # toward a miss (the pre-detector behavior), never an over-match.
        $blankKinds = @(
            [System.Management.Automation.Language.TokenKind]::Comment,
            [System.Management.Automation.Language.TokenKind]::StringLiteral,
            [System.Management.Automation.Language.TokenKind]::StringExpandable,
            [System.Management.Automation.Language.TokenKind]::HereStringLiteral,
            [System.Management.Automation.Language.TokenKind]::HereStringExpandable
        )
        # sd1-6: the filler must never let a shape regex match ACROSS a
        # blanked span - space fill satisfied \s* and turned foo 'bar'() {
        # into a bash function shape the raw text never contained (re-grade
        # round 1). [char]1 belongs to no character class the shape
        # patterns use, so erased spans break matches instead of bridging
        # them.
        $blankChar = [char]1
        $chars = $Script.ToCharArray()
        foreach ($token in @($tokens)) {
            if ($blankKinds -contains $token.Kind) {
                $end = [Math]::Min($token.Extent.EndOffset, $chars.Length)
                for ($i = $token.Extent.StartOffset; $i -lt $end; $i++) { $chars[$i] = $blankChar }
                continue
            }
            # sd1-3 (rounds 2+3): a quoted fragment glued to bareword text
            # folds into ONE Generic-kind string token (x"<<EOF" - re-grade
            # round 1), which the kind list above never sees. Blank just the
            # quoted spans; the bareword remainder stays as evidence. The
            # fragment patterns honor pwsh escapes (round 3): `" does not
            # close a double-quoted fragment, '' does not close a
            # single-quoted one.
            if ($token.Kind -eq [System.Management.Automation.Language.TokenKind]::Generic -and
                ($token -is [System.Management.Automation.Language.StringLiteralToken] -or
                 $token -is [System.Management.Automation.Language.StringExpandableToken])) {
                foreach ($fragment in [regex]::Matches($token.Text, '(?s)"(`.|[^"`])*"|''(''''|[^''])*''')) {
                    $start = $token.Extent.StartOffset + $fragment.Index
                    $end = [Math]::Min($start + $fragment.Length, $chars.Length)
                    for ($i = $start; $i -lt $end; $i++) { $chars[$i] = $blankChar }
                }
            }
        }
        $scanText = [string]::new($chars)
        # sd1-7: the error and the shape evidence must be ASSOCIATED, not
        # merely co-present anywhere in the script - Write-Output then; if
        # $true paired an if-statement error with an unrelated EARLIER
        # argument (re-grade round 1). A match counts only when it overlaps
        # the error extent (inclusive bounds - error extents are often empty
        # points) or begins at/after it (bash then/do/done trail the keyword
        # whose parse error fires). Probed offsets for every frozen IN case
        # satisfy this rule.
        $shapeNearError = {
            param([string]$ErrorId, [string]$Pattern)
            foreach ($parseError in $parseErrors) {
                if ($parseError.ErrorId -ne $ErrorId) { continue }
                foreach ($match in [regex]::Matches($scanText, $Pattern)) {
                    if ($match.Index -ge $parseError.Extent.StartOffset) { return $true }
                    if ($match.Index -le $parseError.Extent.EndOffset -and
                        $parseError.Extent.StartOffset -le ($match.Index + $match.Length)) { return $true }
                }
            }
            return $false
        }
        if (& $shapeNearError 'MissingFileSpecification' '<<') {
            # Bare << - the quoted-terminator form (<<'EOF') lexes its
            # terminator as a string literal, which blanking removes. <<
            # outside strings/comments is itself never valid pwsh, and the
            # error-id pairing still gates it.
            return 'a bash heredoc (<<WORD ... WORD)'
        }
        # sd1-7 (round 3): bash then/do/done arrive in COMMAND position when
        # pwsh error-recovers the genuine constructs (probed: then@17 and
        # fi@31 parse as commands in if/then/fi; done@28 in for/do/done),
        # while a keyword in argument position (if $true; Write-Output
        # then) never does. Keyword evidence is therefore a command NAME,
        # still paired with its error id and the same locality predicate.
        $parseCommands = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))
        $keywordCommandNearError = {
            param([string]$ErrorId, [string[]]$Keywords)
            foreach ($parseError in $parseErrors) {
                if ($parseError.ErrorId -ne $ErrorId) { continue }
                foreach ($commandAst in $parseCommands) {
                    $first = @($commandAst.CommandElements)[0]
                    if ($first -isnot [System.Management.Automation.Language.StringConstantExpressionAst] -or
                        $first.StringConstantType -ne [System.Management.Automation.Language.StringConstantType]::BareWord -or
                        $first.Value -notin $Keywords) { continue }
                    # sd1-7 (round 4): a keyword that is a REAL command here
                    # is not bash evidence - locally defined before this use
                    # (function then { ... }) or resolving in the session
                    # (re-grade round 3).
                    $keywordDefined = $false
                    foreach ($definition in $localDefs) {
                        if ($definition.Name -eq $first.Value -and
                            $definition.End -le $commandAst.Extent.StartOffset) {
                            $keywordDefined = $true
                            break
                        }
                    }
                    if ($keywordDefined -or $null -ne $ExecutionContext.InvokeCommand.GetCommand(
                            $first.Value, [System.Management.Automation.CommandTypes]::All)) { continue }
                    $start = $commandAst.Extent.StartOffset
                    if ($start -ge $parseError.Extent.StartOffset) { return $true }
                    if ($start -le $parseError.Extent.EndOffset -and
                        $parseError.Extent.StartOffset -le $commandAst.Extent.EndOffset) { return $true }
                }
            }
            return $false
        }
        if (& $keywordCommandNearError 'MissingOpenParenthesisInIfStatement' @('then')) {
            return 'a bash if/then/fi statement'
        }
        if (& $keywordCommandNearError 'MissingOpenParenthesisAfterKeyword' @('do', 'done')) {
            return 'a bash do/done loop'
        }
        if (& $shapeNearError 'MissingTypename' '(^|[\s;&|(])\[{1,2}\s') {
            return 'a bash test expression ([ ... ] or [[ ... ]])'
        }
        if (& $shapeNearError 'ExpectedExpression' '\w+\s*\(\s*\)\s*\{') {
            return 'a bash function definition (name() { ... })'
        }
        if (& $shapeNearError 'RedirectionNotSupported' '<\(') {
            return 'bash process substitution (<(...))'
        }
        return $null
    }

    # Clean-parse shapes: these run and fail late as CommandNotFound, or -
    # the worst case - succeed with silently changed meaning. Only
    # CommandAst nodes are walked, so anything inside a quoted string
    # (bash -lc 'local x=1') never enters.
    $commands = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))
    foreach ($command in $commands) {
        $elements = @($command.CommandElements)
        $name = $null
        if ($elements[0] -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
            $elements[0].StringConstantType -eq [System.Management.Automation.Language.StringConstantType]::BareWord) {
            $name = $elements[0].Value
        }

        # sd1-1: these classifiers predict a CommandNotFound failure. When
        # the name actually resolves in this session (a user-defined
        # function or alias shadowing the bash builtin), that failure
        # cannot happen and flagging it would refuse legitimate PowerShell
        # - so the same intrinsic guard Resolve-PtcInvokeScript uses gates
        # the return. set stays unguarded below BY DESIGN: it resolves
        # (Set-Variable alias) in every pwsh, which is exactly why it is
        # keyed to its argument shape instead.
        $label = $null
        if ($name -match '^[A-Za-z_][A-Za-z0-9_]*=') {
            $label = "a bash environment-variable prefix ($name ...)"
        } elseif ($name -in @('export', 'local') -and $elements.Count -ge 2 -and
            $elements[1].Extent.Text -match '^[A-Za-z_][A-Za-z0-9_]*(=|$)') {
            $label = "the bash '$name' builtin"
        } elseif ($name -eq 'source' -and $elements.Count -eq 2 -and
            $elements[1] -is [System.Management.Automation.Language.StringConstantExpressionAst]) {
            $label = "the bash 'source' builtin"
        }
        if ($label) {
            $isLocallyDefined = $false
            foreach ($definition in $localDefs) {
                if ($definition.Name -ne $name) { continue }
                if ($definition.End -le $command.Extent.StartOffset) {
                    $isLocallyDefined = $true
                    break
                }
                # sd1-1 (round 4): recursion - a use INSIDE its own
                # definition's body only ever executes after the definition
                # exists (re-grade round 3), so it is defined too.
                if ($definition.Start -le $command.Extent.StartOffset -and
                    $command.Extent.EndOffset -le $definition.End) {
                    $isLocallyDefined = $true
                    break
                }
            }
            if (-not $isLocallyDefined -and
                $null -eq $ExecutionContext.InvokeCommand.GetCommand(
                    $name, [System.Management.Automation.CommandTypes]::All)) {
                return $label
            }
        }
        if ($name -eq 'set' -and $elements.Count -ge 2 -and
            $elements[1] -is [System.Management.Automation.Language.CommandParameterAst]) {
            # Keyed to the probed full argument shape: set always resolves
            # (stock alias -> Set-Variable), so shape is the discriminator.
            $flagsOnly = $true
            foreach ($element in ($elements | Select-Object -Skip 1)) {
                $isFlag = $element -is [System.Management.Automation.Language.CommandParameterAst] -and
                    $null -eq $element.Argument -and
                    $element.ParameterName -match '^[euxo]{1,4}$'
                $isPipefail = $element -is [System.Management.Automation.Language.StringConstantExpressionAst] -and
                    $element.Value -eq 'pipefail'
                if (-not ($isFlag -or $isPipefail)) { $flagsOnly = $false; break }
            }
            if ($flagsOnly) {
                # sd1-4 (owner unparked 2026-07-09, amending the slice-0
                # exemption): flag only while set still MEANS the stock
                # Set-Variable alias. A session or preceding script-local
                # re-pointing makes set -e the user's own working command -
                # the uniform rule applies: a name that works here is never
                # bash evidence. An unresolvable set is treated as stock
                # (the runtime failure the finding predicts still happens).
                $setOverridden = $false
                foreach ($definition in $localDefs) {
                    if ($definition.Name -ne 'set') { continue }
                    if ($definition.End -le $command.Extent.StartOffset -or
                        ($definition.Start -le $command.Extent.StartOffset -and
                         $command.Extent.EndOffset -le $definition.End)) {
                        $setOverridden = $true
                        break
                    }
                }
                if (-not $setOverridden) {
                    $resolvedSet = $ExecutionContext.InvokeCommand.GetCommand(
                        'set', [System.Management.Automation.CommandTypes]::All)
                    $setOverridden = $null -ne $resolvedSet -and
                        -not ($resolvedSet.CommandType -eq [System.Management.Automation.CommandTypes]::Alias -and
                              $resolvedSet.Definition -eq 'Set-Variable')
                }
                if (-not $setOverridden) { return "bash 'set' shell options (set -e/-u/-x/-o pipefail)" }
            }
        }

        # Paired backticks = bash command substitution: parses clean, prints
        # the literal text, exits 0 - no error at all (worst probed case).
        # Extent-bounded pairing (sd1-2): a pair is either a single element
        # whose extent is exactly `word...`, or an opener extent (`letter,
        # no further backtick) closed by a LATER element extent ending with
        # its only backtick. The old raw-text tail scan crossed element
        # boundaries, so any later backtick (a second legitimate escape, a
        # comment) could close the pair - `tColumn` Name was flagged.
        # Interior-backtick extents match neither shape, so the frozen
        # legitimate-backtick set (lone escape `n, adjacent escapes `n `t,
        # line continuation) never trips.
        $openerSeen = $false
        foreach ($element in $elements) {
            $extentText = $element.Extent.Text
            if ($extentText -match '^`[A-Za-z][^`]*`$') {
                return 'bash command substitution in backticks (`cmd`)'
            }
            if ($openerSeen -and $extentText -match '^[^`]*`$') {
                return 'bash command substitution in backticks (`cmd`)'
            }
            if (-not $openerSeen -and $extentText -match '^`[A-Za-z][^`]*$') {
                $openerSeen = $true
            }
        }
    }

    return $null
}

# Bounds the text legs of Compress-PtcOutput (greenfield-design D2, adopted
# 2026-07-08, amending the Phase 2 never-truncate contract): a generous
# head+tail window sized so real command output virtually never hits it —
# the cap exists for the pathological case, a whole-file Get-Content landing
# in context through a compression tool. Elision is always labeled and names
# raw=true as what it is (shell-dialect plan D2): the recovery hatch for the
# rare case the elided middle matters, never a standing invitation - the
# marker lands mid-output at the exact moment raw is tempting.
function Limit-PtcPassthrough {
    param(
        [AllowNull()][string]$Text,
        [int]$MaxLines = $script:PtcPassthroughMaxLines,
        [int]$MaxChars = $script:PtcPassthroughMaxChars,
        # The marker's recovery advice. The default speaks to the ptk_invoke
        # context; callers shaping output whose recovery differs (ptk_job
        # polls have no raw parameter - the recovery is the job's log file)
        # pass their own hint, so the advice is composed BY the elision
        # itself and can never be false or missing (sd3-2..sd3-4: two
        # downstream inference heuristics both failed - ANSI stripping
        # shortens without eliding, near-boundary elision lengthens).
        [string]$ElisionHint = 'rerun with raw=true only if the elided middle matters'
    )

    if ($null -eq $Text) { return '' }

    $lines = @($Text -split "`r?`n")
    $elidedLineCount = 0
    if ($lines.Count -gt $MaxLines) {
        $headCount = [int][Math]::Ceiling($MaxLines * 0.75)
        $tailCount = $MaxLines - $headCount
        $elidedLineCount = $lines.Count - $MaxLines
        $marker = '[{0} lines elided - {1}]' -f $elidedLineCount, $ElisionHint
        $Text = (@($lines | Select-Object -First $headCount) + $marker +
            @($lines | Select-Object -Last $tailCount)) -join [Environment]::NewLine
    }

    if ($Text.Length -gt $MaxChars) {
        $head = [int][Math]::Ceiling($MaxChars * 0.75)
        $tail = $MaxChars - $head
        # The char window can cut the line marker out of the elided middle, so
        # when both bounds fired this marker must carry both facts - every
        # elision stays explicit.
        $elided = if ($elidedLineCount -gt 0) {
            '[{0} lines and {1} chars elided - {2}]' -f
                $elidedLineCount, ($Text.Length - $MaxChars), $ElisionHint
        } else {
            '[{0} chars elided - {1}]' -f ($Text.Length - $MaxChars), $ElisionHint
        }
        $marker = '{0}{1}{0}' -f [Environment]::NewLine, $elided
        $Text = $Text.Substring(0, $head) + $marker + $Text.Substring($Text.Length - $tail)
    }

    $Text
}

# Shapes ptk_invoke output for the MCP server (Phase 2 plan): objects compress via
# Compress-PtcObject; log-shaped text goes to rtk when available; all other text
# (strings and primitive scalars) passes through verbatim and is NEVER truncated.
# Contract: never throws - any internal failure returns labeled unshaped output,
# because shaping must not be able to fail a ptk_invoke call.
function Compress-PtcOutput {
    [CmdletBinding()]
    param(
        [Parameter(ValueFromPipeline)]
        [AllowNull()]
        [object]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems,
        # Overrides the elision markers' recovery advice for callers whose
        # recovery path is not raw=true (see Limit-PtcPassthrough).
        [string]$ElisionHint
    )

    begin {
        $items = [System.Collections.Generic.List[object]]::new()
        $limitArgs = @{}
        if ($ElisionHint) { $limitArgs['ElisionHint'] = $ElisionHint }
    }
    process {
        if ($null -ne $InputObject) { $items.Add($InputObject) }
    }
    end {
        $array = @($items)
        if ($array.Count -eq 0) { return }

        try {
            $textual = $true
            foreach ($item in $array) {
                if ($item -is [string]) { continue }
                if ($item.GetType().IsPrimitive -or $item -is [decimal]) { continue }
                $textual = $false
                break
            }

            if ($textual) {
                # Strip ANSI/control sequences at ingest, BEFORE classification:
                # they are pure token waste to a model, and a color prefix
                # defeats Test-PtcLogShaped's line-start timestamp anchor, so a
                # colored log would dodge the rtk dedup leg. raw=true calls
                # never reach shaping (they return complete Out-String text).
                $text = Remove-PtcAnsi (@($array | ForEach-Object { "$_" }) -join [Environment]::NewLine)
                if (Test-PtcLogShaped -Text $text) { return (Limit-PtcPassthrough (Invoke-PtcRtkLog -Text $text) @limitArgs) }
                return (Limit-PtcPassthrough $text @limitArgs)
            }

            return ($array | Compress-PtcObject -MaxItems $MaxItems)
        }
        catch {
            $raw = ($array | Out-String).TrimEnd()
            # Bound the fallback too (P3: no unbounded path), but never let the
            # bounder violate the never-throw contract of this catch.
            try { $raw = Limit-PtcPassthrough $raw @limitArgs } catch { }
            return "[ptk:shape ERROR - $($_.Exception.Message). Returning unshaped output.]`n$raw"
        }
    }
}

Export-ModuleMember -Function @(
    'Compress-PtcObject',
    'Compress-PtcOutput',
    'Get-PtcShellDialectFinding',
    'Resolve-PtcInvokeScript'
)