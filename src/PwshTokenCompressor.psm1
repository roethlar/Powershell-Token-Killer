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

function Format-PtcTable {
    param(
        [object[]]$Rows,
        [string[]]$Properties,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    if ($Rows.Count -eq 0) { return @() }
    if ($Properties.Count -eq 0) {
        $lines = @($Rows | Select-Object -First $MaxItems | ForEach-Object { [string]$_ })
        if ($Rows.Count -gt $MaxItems) { $lines += '+{0} more' -f ($Rows.Count - $MaxItems) }
        return $lines
    }

    $visible = @($Rows | Select-Object -First $MaxItems)
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

    $dirRows = @($dirs | Sort-Object Name | Select-Object -First $MaxItems | ForEach-Object {
        [pscustomobject]@{
            Type = 'dir'
            Name = $_.Name + '\'
            Modified = $_.LastWriteTime
        }
    })

    $fileRows = @($files | Sort-Object Name | Select-Object -First $MaxItems | ForEach-Object {
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

    foreach ($group in $groups | Select-Object -First $MaxItems) {
        $lines += ''
        $lines += '[file] {0} ({1})' -f $group.Name, $group.Count
        foreach ($match in $group.Group | Select-Object -First 8) {
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
    $rows = @($items | Sort-Object CPU -Descending | Select-Object -First $MaxItems | ForEach-Object {
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
    $rows = @($items | Sort-Object Status, Name | Select-Object -First $MaxItems | ForEach-Object {
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

    $first = $items | Select-Object -First 1
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
        foreach ($item in $items | Select-Object -First 3) {
            $lines += '  ' + (Limit-PtcText -Text ([string]$item) -MaxLines 1 -Width 110)
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

# Bounds the text legs of Compress-PtcOutput (greenfield-design D2, adopted
# 2026-07-08, amending the Phase 2 never-truncate contract): a generous
# head+tail window sized so real command output virtually never hits it —
# the cap exists for the pathological case, a whole-file Get-Content landing
# in context through a compression tool. Elision is always labeled and names
# raw=true, the deliberate unbounded escape hatch.
function Limit-PtcPassthrough {
    param(
        [AllowNull()][string]$Text,
        [int]$MaxLines = $script:PtcPassthroughMaxLines,
        [int]$MaxChars = $script:PtcPassthroughMaxChars
    )

    if ($null -eq $Text) { return '' }

    $lines = @($Text -split "`r?`n")
    $elidedLineCount = 0
    if ($lines.Count -gt $MaxLines) {
        $headCount = [int][Math]::Ceiling($MaxLines * 0.75)
        $tailCount = $MaxLines - $headCount
        $elidedLineCount = $lines.Count - $MaxLines
        $marker = '[{0} lines elided - use raw=true for everything]' -f $elidedLineCount
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
            '[{0} lines and {1} chars elided - use raw=true for everything]' -f
                $elidedLineCount, ($Text.Length - $MaxChars)
        } else {
            '[{0} chars elided - use raw=true for everything]' -f ($Text.Length - $MaxChars)
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
                if (Test-PtcLogShaped -Text $text) { return (Limit-PtcPassthrough (Invoke-PtcRtkLog -Text $text)) }
                return (Limit-PtcPassthrough $text)
            }

            return ($array | Compress-PtcObject -MaxItems $MaxItems)
        }
        catch {
            $raw = ($array | Out-String).TrimEnd()
            # Bound the fallback too (P3: no unbounded path), but never let the
            # bounder violate the never-throw contract of this catch.
            try { $raw = Limit-PtcPassthrough $raw } catch { }
            return "[ptk:shape ERROR - $($_.Exception.Message). Returning unshaped output.]`n$raw"
        }
    }
}

Export-ModuleMember -Function @(
    'Compress-PtcObject',
    'Compress-PtcOutput',
    'Resolve-PtcInvokeScript'
)