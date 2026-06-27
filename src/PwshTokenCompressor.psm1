Set-StrictMode -Version Latest

$script:DefaultMaxItems = 40
$script:DefaultWidth = 140

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

function Get-PtcLastExitCode {
    $variable = Get-Variable -Name LASTEXITCODE -Scope Global -ErrorAction SilentlyContinue
    if ($null -ne $variable -and $variable.Value -is [int]) { return $variable.Value }
    0
}

function Invoke-PtcBoundCommand {
    param(
        [Parameter(Mandatory)]
        [object]$Name,

        [AllowNull()]
        [object[]]$Arguments
    )

    if ($null -eq $Arguments) { $Arguments = @() }
    $positionals = [System.Collections.Generic.List[object]]::new()
    $named = @{}

    $endOfOptions = $false
    for ($i = 0; $i -lt $Arguments.Count; $i++) {
        $item = $Arguments[$i]
        if (-not $endOfOptions -and $item -is [string] -and $item -eq '--') {
            $endOfOptions = $true
            continue
        }
        $isFlag = (-not $endOfOptions) -and ($item -is [string]) -and ($item -match '^-[A-Za-z]')
        if ($isFlag) {
            $key = $item.TrimStart('-')
            $nextIsValue = ($i + 1 -lt $Arguments.Count) -and -not (
                $Arguments[$i + 1] -is [string] -and $Arguments[$i + 1] -match '^-[A-Za-z]'
            )
            if ($nextIsValue) {
                $named[$key] = $Arguments[$i + 1]
                $i++
            } else {
                $named[$key] = $true
            }
        } else {
            $positionals.Add($item)
        }
    }

    & $Name @positionals @named
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

function Format-PtcTable {
    param(
        [object[]]$Rows,
        [string[]]$Properties,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    if ($Rows.Count -eq 0) { return @() }
    if ($Properties.Count -eq 0) { return @($Rows | ForEach-Object { [string]$_ }) }

    $visible = @($Rows | Select-Object -First $MaxItems)
    $widths = @{}
    foreach ($prop in $Properties) {
        $max = $prop.Length
        foreach ($row in $visible) {
            $value = ConvertTo-PtcScalar (Get-PtcPropertyValue -Object $row -Name $prop)
            if ($value.Length -gt $max) { $max = [Math]::Min($value.Length, 50) }
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
            if ($value.Length -gt 50) { $value = $value.Substring(0, 49) + '...' }
            $value.PadRight($widths[$prop])
        }
        $lines += (($cells -join '  ').TrimEnd())
    }

    if ($Rows.Count -gt $MaxItems) {
        $lines += '+{0} more' -f ($Rows.Count - $MaxItems)
    }

    $lines
}

function Get-PtcLanguage {
    param([string]$Path)

    $ext = [System.IO.Path]::GetExtension($Path).TrimStart('.').ToLowerInvariant()
    switch ($ext) {
        'ps1' { 'PowerShell' }
        'psm1' { 'PowerShell' }
        'psd1' { 'PowerShellData' }
        'cs' { 'CSharp' }
        'rs' { 'Rust' }
        'py' { 'Python' }
        'js' { 'JavaScript' }
        'mjs' { 'JavaScript' }
        'cjs' { 'JavaScript' }
        'ts' { 'TypeScript' }
        'tsx' { 'TypeScript' }
        'go' { 'Go' }
        'java' { 'Java' }
        'rb' { 'Ruby' }
        'sh' { 'Shell' }
        'json' { 'Data' }
        'yaml' { 'Data' }
        'yml' { 'Data' }
        'toml' { 'Data' }
        'xml' { 'Data' }
        'csv' { 'Data' }
        'md' { 'Markdown' }
        'markdown' { 'Markdown' }
        'txt' { 'Text' }
        default { 'Unknown' }
    }
}

function Use-PtcNeverWorse {
    param(
        [string]$Raw,
        [string]$Compressed
    )

    if ([string]::IsNullOrWhiteSpace($Compressed) -and -not [string]::IsNullOrWhiteSpace($Raw)) {
        return $Raw
    }
    if ($Compressed.Length -gt $Raw.Length) { return $Raw }
    $Compressed
}

function Normalize-PtcBlankLines {
    param([string]$Text)
    ($Text -replace "(\r?\n){3,}", "`n`n").TrimEnd()
}

function Remove-PtcPowerShellComments {
    param([string]$Text)

    $lines = @($Text -split "`r?`n")
    $out = [System.Collections.Generic.List[string]]::new()
    $inHelp = $false

    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^<#') {
            $inHelp = $true
            if ($trimmed -match '#>$') { $inHelp = $false }
            continue
        }
        if ($inHelp) {
            if ($trimmed -match '#>$') { $inHelp = $false }
            continue
        }
        if ($trimmed.StartsWith('#')) { continue }
        $out.Add($line)
    }

    Normalize-PtcBlankLines ($out -join [Environment]::NewLine)
}

function Remove-PtcGenericComments {
    param(
        [string]$Text,
        [string]$Language
    )

    if ($Language -in 'Data', 'Markdown', 'Text', 'PowerShellData') {
        return Normalize-PtcBlankLines $Text
    }

    $lines = @($Text -split "`r?`n")
    $out = [System.Collections.Generic.List[string]]::new()
    $inBlock = $false
    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($inBlock) {
            if ($trimmed -match '\*/|"""|=end') { $inBlock = $false }
            continue
        }
        if ($trimmed -match '^/\*|^"""|^=begin') {
            if ($trimmed -notmatch '\*/$|"""$') { $inBlock = $true }
            continue
        }
        if ($trimmed -match '^(//|#)') { continue }
        $out.Add($line)
    }

    Normalize-PtcBlankLines ($out -join [Environment]::NewLine)
}

function Compress-PtcMinimalText {
    param(
        [string]$Text,
        [string]$Language
    )

    if ($Language -eq 'PowerShell') {
        return Remove-PtcPowerShellComments $Text
    }
    Remove-PtcGenericComments -Text $Text -Language $Language
}

function Get-PtcPowerShellAst {
    param([string]$Text)

    $tokens = $null
    $errors = $null
    [System.Management.Automation.Language.Parser]::ParseInput($Text, [ref]$tokens, [ref]$errors)
}

function Get-PtcPowerShellSummary {
    param(
        [string]$Text,
        [string]$Path
    )

    $ast = Get-PtcPowerShellAst $Text
    $lines = @($Text -split "`r?`n")
    $functions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true))
    $classes = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.TypeDefinitionAst] }, $true))
    $commands = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))
    $imports = @($commands | Where-Object {
        $name = $_.GetCommandName()
        $name -in 'Import-Module', 'using'
    } | ForEach-Object { Limit-PtcText -Text $_.Extent.Text -MaxLines 1 -Width 90 } | Select-Object -Unique -First 5)
    $exports = @($commands | Where-Object { $_.GetCommandName() -eq 'Export-ModuleMember' } |
        ForEach-Object { Limit-PtcText -Text $_.Extent.Text -MaxLines 1 -Width 120 } | Select-Object -First 5)
    $aliases = @($commands | Where-Object { $_.GetCommandName() -in 'Set-Alias', 'New-Alias' } |
        ForEach-Object { Limit-PtcText -Text $_.Extent.Text -MaxLines 1 -Width 90 } | Select-Object -First 5)
    $fnNames = @($functions | ForEach-Object { $_.Name } | Select-Object -First 12)
    $classNames = @($classes | ForEach-Object { $_.Name } | Select-Object -First 8)

    $out = [System.Collections.Generic.List[string]]::new()
    $out.Add(('PowerShell file: {0} lines, {1} functions, {2} types' -f $lines.Count, $functions.Count, $classes.Count))
    if ($fnNames.Count -gt 0) { $out.Add(('functions: {0}' -f ($fnNames -join ', '))) }
    if ($classNames.Count -gt 0) { $out.Add(('types: {0}' -f ($classNames -join ', '))) }
    if ($imports.Count -gt 0) { $out.Add(('imports: {0}' -f ($imports -join ' | '))) }
    if ($exports.Count -gt 0) { $out.Add(('exports: {0}' -f ($exports -join ' | '))) }
    if ($aliases.Count -gt 0) { $out.Add(('aliases: {0}' -f ($aliases -join ' | '))) }
    if ($out.Count -eq 1) { $out.Add('purpose: script/module content; no functions detected') }

    Join-PtcLines $out
}

function Compress-PtcPowerShellAggressive {
    param([string]$Text)

    $ast = Get-PtcPowerShellAst $Text
    $out = [System.Collections.Generic.List[string]]::new()
    $commands = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true))
    foreach ($cmd in $commands) {
        $name = $cmd.GetCommandName()
        if ($name -in 'using', 'Import-Module', 'Export-ModuleMember', 'Set-Alias', 'New-Alias') {
            $out.Add((Limit-PtcText -Text $cmd.Extent.Text -MaxLines 1 -Width 120))
        }
    }

    $classes = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.TypeDefinitionAst] }, $true))
    foreach ($class in $classes) {
        $out.Add(('class {0} {{ ... }}' -f $class.Name))
    }

    $functions = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true))
    foreach ($function in $functions) {
        $params = @()
        if ($function.Body.ParamBlock) {
            $params = @($function.Body.ParamBlock.Parameters | ForEach-Object {
                '$' + $_.Name.VariablePath.UserPath
            })
        }
        $out.Add(('function {0}({1}) {{ ... }}' -f $function.Name, ($params -join ', ')))
    }

    if ($out.Count -eq 0) {
        return Compress-PtcMinimalText -Text $Text -Language 'PowerShell'
    }
    Join-PtcLines ($out | Select-Object -Unique)
}

function Compress-PtcCodeAggressive {
    param(
        [string]$Text,
        [string]$Language
    )

    if ($Language -eq 'PowerShell') {
        return Compress-PtcPowerShellAggressive $Text
    }
    if ($Language -in 'Data', 'Markdown', 'Text', 'PowerShellData') {
        return Compress-PtcSummaryText -Text $Text -Language $Language -Path ''
    }

    $minimal = Compress-PtcMinimalText -Text $Text -Language $Language
    $out = [System.Collections.Generic.List[string]]::new()
    foreach ($line in @($minimal -split "`r?`n")) {
        $trimmed = $line.Trim()
        $important = $trimmed -match '^(using |use |import |from |require\(|#include|namespace |package |module )' -or
            $trimmed -match '^(public |private |protected |internal |export |pub )?(async )?(function|func|fn|def|class|struct|enum|trait|interface|type)\s+[A-Za-z_][A-Za-z0-9_]*' -or
            $trimmed -match '^(const |let |var |static |public static |pub const |pub static )'
        if ($important) { $out.Add($line) }
    }

    if ($out.Count -eq 0) { return $minimal }
    Join-PtcLines $out
}

function Compress-PtcMarkdownSummary {
    param([string]$Text)

    $lines = @($Text -split "`r?`n")
    $headings = @($lines | Where-Object { $_ -match '^\s{0,3}#{1,6}\s+\S' } | Select-Object -First 12)
    $bullets = @($lines | Where-Object { $_ -match '^\s{0,4}[-*+]\s+\S' } | Select-Object -First 8)
    $codeLangs = @([regex]::Matches($Text, '```([A-Za-z0-9_+-]*)') | ForEach-Object {
        if ($_.Groups[1].Value) { $_.Groups[1].Value } else { 'plain' }
    })
    $title = @($headings | Select-Object -First 1)

    $out = [System.Collections.Generic.List[string]]::new()
    $out.Add(('Markdown: {0} lines, {1} headings, {2} code blocks' -f $lines.Count, $headings.Count, $codeLangs.Count))
    if ($title.Count -gt 0) { $out.Add(('title: {0}' -f ($title[0] -replace '^\s*#+\s*', ''))) }
    if ($headings.Count -gt 0) { $out.Add(('outline: {0}' -f (($headings | ForEach-Object { $_.Trim() }) -join ' | '))) }
    if ($bullets.Count -gt 0) { $out.Add(('bullets: {0}' -f (($bullets | ForEach-Object { (Limit-PtcText -Text $_.Trim() -MaxLines 1 -Width 90) }) -join ' | '))) }
    if ($codeLangs.Count -gt 0) { $out.Add(('code: {0}' -f ((@($codeLangs | Group-Object | ForEach-Object { '{0}={1}' -f $_.Name, $_.Count })) -join ', '))) }
    Join-PtcLines $out
}

function Compress-PtcGenericTextSummary {
    param([string]$Text)

    $lines = @($Text -split "`r?`n")
    $nonEmpty = @($lines | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    $words = [regex]::Matches($Text, '\S+').Count
    $title = @($nonEmpty | Where-Object { $_.Length -lt 100 } | Select-Object -First 1)
    $bullets = @($nonEmpty | Where-Object { $_ -match '^\s*[-*+]\s+\S' } | Select-Object -First 8)
    $signals = @($nonEmpty | Where-Object { $_ -match '(?i)\b(error|warning|failed|todo|fixme|important|note)\b' } | Select-Object -First 8)

    $out = [System.Collections.Generic.List[string]]::new()
    $out.Add(('Text: {0} lines, {1} words' -f $lines.Count, $words))
    if ($title.Count -gt 0) { $out.Add(('title: {0}' -f (Limit-PtcText -Text $title[0].Trim() -MaxLines 1 -Width 100))) }
    if ($bullets.Count -gt 0) { $out.Add(('bullets: {0}' -f (($bullets | ForEach-Object { Limit-PtcText -Text $_.Trim() -MaxLines 1 -Width 90 }) -join ' | '))) }
    if ($signals.Count -gt 0) { $out.Add(('signals: {0}' -f (($signals | ForEach-Object { Limit-PtcText -Text $_.Trim() -MaxLines 1 -Width 90 }) -join ' | '))) }
    if ($out.Count -eq 1 -and $nonEmpty.Count -gt 0) {
        $out.Add(('start: {0}' -f (Limit-PtcText -Text $nonEmpty[0].Trim() -MaxLines 1 -Width 110)))
    }
    Join-PtcLines $out
}

function Compress-PtcCodeSummary {
    param(
        [string]$Text,
        [string]$Language,
        [string]$Path
    )

    if ($Language -eq 'PowerShell') {
        return Get-PtcPowerShellSummary -Text $Text -Path $Path
    }

    $lines = @($Text -split "`r?`n")
    $imports = [System.Collections.Generic.List[string]]::new()
    $functions = [System.Collections.Generic.List[string]]::new()
    $types = [System.Collections.Generic.List[string]]::new()

    foreach ($line in $lines) {
        $trimmed = $line.Trim()
        if ($trimmed -match '^(using |use |import |from |require\(|#include|package )') {
            $imports.Add((Limit-PtcText -Text $trimmed -MaxLines 1 -Width 70))
        }
        if ($trimmed -match '^(?:public |private |protected |internal |export |pub )?(?:async )?(?:function|func|fn|def)\s+([A-Za-z_][A-Za-z0-9_]*)') {
            $functions.Add($Matches[1])
        }
        if ($trimmed -match '^(?:public |private |protected |internal |export |pub )?(?:class|struct|enum|trait|interface|type)\s+([A-Za-z_][A-Za-z0-9_]*)') {
            $types.Add($Matches[1])
        }
    }

    $out = [System.Collections.Generic.List[string]]::new()
    $out.Add(('{0} code: {1} lines, {2} functions, {3} types' -f $Language, $lines.Count, $functions.Count, $types.Count))
    if ($functions.Count -gt 0) { $out.Add(('functions: {0}' -f ((@($functions | Select-Object -First 12)) -join ', '))) }
    if ($types.Count -gt 0) { $out.Add(('types: {0}' -f ((@($types | Select-Object -First 8)) -join ', '))) }
    if ($imports.Count -gt 0) { $out.Add(('imports: {0}' -f ((@($imports | Select-Object -Unique -First 5)) -join ' | '))) }
    if ($out.Count -eq 1) { $out.Add('purpose: source-like text; no common declarations detected') }
    Join-PtcLines $out
}

function Compress-PtcSummaryText {
    param(
        [string]$Text,
        [string]$Language,
        [string]$Path
    )

    switch ($Language) {
        'Markdown' { Compress-PtcMarkdownSummary $Text }
        'Text' { Compress-PtcGenericTextSummary $Text }
        'Data' { Compress-PtcGenericTextSummary $Text }
        'PowerShellData' { Compress-PtcGenericTextSummary $Text }
        default { Compress-PtcCodeSummary -Text $Text -Language $Language -Path $Path }
    }
}

function Invoke-PtcTextFilter {
    param(
        [string]$Text,
        [string]$Path,
        [ValidateSet('none', 'minimal', 'aggressive', 'summary')]
        [string]$Level = 'none',
        [int]$MaxLines = 120,
        [switch]$Tail,
        [int]$Width = $script:DefaultWidth
    )

    $language = Get-PtcLanguage $Path
    $rawLines = @($Text -split "`r?`n")
    $rawWindowText = if ($Tail) {
        (@($rawLines | Select-Object -Last $MaxLines) -join [Environment]::NewLine)
    } else {
        (@($rawLines | Select-Object -First $MaxLines) -join [Environment]::NewLine)
    }
    $rawWindow = Limit-PtcText -Text $rawWindowText -MaxLines $MaxLines -Width $Width

    if ($Level -eq 'none') { return $rawWindow }

    $filtered = switch ($Level) {
        'minimal' { Compress-PtcMinimalText -Text $Text -Language $language }
        'aggressive' { Compress-PtcCodeAggressive -Text $Text -Language $language }
        'summary' { Compress-PtcSummaryText -Text $Text -Language $language -Path $Path }
    }

    if ($Level -ne 'summary') {
        $filteredLines = @($filtered -split "`r?`n")
        $filteredText = if ($Tail) {
            (@($filteredLines | Select-Object -Last $MaxLines) -join [Environment]::NewLine)
        } else {
            (@($filteredLines | Select-Object -First $MaxLines) -join [Environment]::NewLine)
        }
        $filtered = Limit-PtcText -Text $filteredText -MaxLines $MaxLines -Width $Width
    } else {
        $filtered = Limit-PtcText -Text $filtered -MaxLines $MaxLines -Width $Width
    }

    Use-PtcNeverWorse -Raw $rawWindow -Compressed $filtered
}

function Compress-PtcFileSystem {
    param(
        [object[]]$InputObject,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $items = @($InputObject)
    $dirs = @($items | Where-Object { $_.PSIsContainer })
    $files = @($items | Where-Object { -not $_.PSIsContainer })
    $totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
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
            Size = Format-PtcSize $_.Length
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

    if (@($items | Where-Object { $_ -is [string] }).Count -eq $items.Count) {
        return Limit-PtcText -Text ($items -join [Environment]::NewLine) -MaxLines $MaxItems
    }

    $first = $items | Select-Object -First 1
    $props = @(Get-PtcDisplayProperties -Object $first)
    $typeName = $first.PSObject.TypeNames[0]
    Join-PtcLines (@("objects: {0} ({1})" -f $items.Count, $typeName) + (Format-PtcTable -Rows $items -Properties $props -MaxItems $MaxItems))
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

        $typeNames = @($array | ForEach-Object { $_.PSObject.TypeNames[0] } | Select-Object -Unique)
        if (@($typeNames | Where-Object { $_ -like '*System.IO.DirectoryInfo*' -or $_ -like '*System.IO.FileInfo*' }).Count -gt 0) {
            Compress-PtcFileSystem -InputObject $array -MaxItems $MaxItems
            return
        }
        if (@($typeNames | Where-Object { $_ -like '*Microsoft.PowerShell.Commands.MatchInfo*' }).Count -gt 0) {
            Compress-PtcMatchInfo -InputObject $array -MaxItems $MaxItems
            return
        }
        if (@($typeNames | Where-Object { $_ -like '*System.Diagnostics.Process*' }).Count -gt 0) {
            Compress-PtcProcess -InputObject $array -MaxItems $MaxItems
            return
        }
        if (@($typeNames | Where-Object { $_ -like '*ServiceController*' }).Count -gt 0) {
            Compress-PtcService -InputObject $array -MaxItems $MaxItems
            return
        }

        Compress-PtcGenericObject -InputObject $array -MaxItems $MaxItems
    }
}

function Invoke-PtcList {
    [CmdletBinding()]
    param(
        [string]$Path = '.',
        [switch]$Recurse,
        [switch]$Force,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $args = @{
        LiteralPath = $Path
        Force = $Force
        ErrorAction = 'Stop'
    }
    if ($Recurse) { $args.Recurse = $true }

    Get-ChildItem @args | Compress-PtcObject -MaxItems $MaxItems
}

function Invoke-PtcRead {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Path,
        [int]$MaxLines = 120,
        [switch]$Tail,
        [int]$Width = $script:DefaultWidth,
        [ValidateSet('none', 'minimal', 'aggressive', 'summary')]
        [string]$Level = 'none',
        [switch]$NoHeader
    )

    $all = @()
    $singleFile = $Path.Count -eq 1
    foreach ($item in $Path) {
        if (-not (Test-Path -LiteralPath $item -PathType Leaf)) {
            $all += '[missing] {0}' -f $item
            continue
        }

        $text = Get-Content -LiteralPath $item -Raw -ErrorAction Stop
        $total = @($text -split "`r?`n").Count
        $filtered = Invoke-PtcTextFilter -Text $text -Path $item -Level $Level -MaxLines $MaxLines -Tail:$Tail -Width $Width
        $isTruncated = $Level -eq 'none' -and $total -gt $MaxLines
        $header = if ($Level -eq 'none') {
            '[file] {0}' -f $item
        } else {
            '[file] {0} ({1})' -f $item, $Level
        }
        $includeHeader = -not $NoHeader
        if ($includeHeader -and $singleFile -and $Level -eq 'none' -and -not $isTruncated) {
            $withHeader = Join-PtcLines @($header, $filtered)
            if ($withHeader.Length -gt $filtered.Length) {
                $includeHeader = $false
            }
        }

        if ($includeHeader) { $all += $header }
        $all += $filtered
        if ($isTruncated) { $all += '[{0} more lines]' -f ($total - $MaxLines) }
    }

    Join-PtcLines $all
}

function Invoke-PtcSmart {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string[]]$Path,
        [int]$MaxLines = 80,
        [int]$Width = $script:DefaultWidth,
        [switch]$NoHeader
    )

    Invoke-PtcRead -Path $Path -MaxLines $MaxLines -Width $Width -Level summary -NoHeader:$NoHeader
}

function Measure-PtcSavings {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path,
        [ValidateSet('minimal', 'aggressive', 'summary')]
        [string[]]$Level = @('minimal', 'aggressive', 'summary'),
        [int]$MaxLines = 120
    )

    $raw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
    foreach ($levelName in $Level) {
        $compressed = Invoke-PtcRead -Path $Path -Level $levelName -MaxLines $MaxLines -NoHeader
        $rawChars = $raw.Length
        $compressedChars = $compressed.Length
        [pscustomobject]@{
            Path = $Path
            Level = $levelName
            RawChars = $rawChars
            CompressedChars = $compressedChars
            RawTokensApprox = [math]::Ceiling($rawChars / 4)
            CompressedTokensApprox = [math]::Ceiling($compressedChars / 4)
            SavingsPct = if ($rawChars -gt 0) { [math]::Round((1 - ($compressedChars / $rawChars)) * 100, 1) } else { 0 }
        }
    }
}

function Invoke-PtcSearch {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Pattern,
        [string]$Path = '.',
        [string[]]$Include,
        [switch]$SimpleMatch,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    $searchArgs = @{
        Pattern = $Pattern
        ErrorAction = 'SilentlyContinue'
    }
    if ($SimpleMatch) { $searchArgs.SimpleMatch = $true }

    if (Test-Path -LiteralPath $Path -PathType Container) {
        $childArgs = @{
            LiteralPath = $Path
            File = $true
            Recurse = $true
            ErrorAction = 'SilentlyContinue'
        }
        if ($Include) { $childArgs.Include = $Include }
        Get-ChildItem @childArgs | Select-String @searchArgs | Compress-PtcObject -MaxItems $MaxItems
    } else {
        Select-String -LiteralPath $Path @searchArgs | Compress-PtcObject -MaxItems $MaxItems
    }
}

function Invoke-PtcProcess {
    [CmdletBinding()]
    param(
        [string]$Name,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    if ($Name) {
        Get-Process -Name $Name -ErrorAction SilentlyContinue | Compress-PtcObject -MaxItems $MaxItems
    } else {
        Get-Process | Compress-PtcObject -MaxItems $MaxItems
    }
}

function Invoke-PtcService {
    [CmdletBinding()]
    param(
        [string]$Name,
        [int]$MaxItems = $script:DefaultMaxItems
    )

    if ($Name) {
        Get-Service -Name $Name -ErrorAction SilentlyContinue | Compress-PtcObject -MaxItems $MaxItems
    } else {
        Get-Service | Compress-PtcObject -MaxItems $MaxItems
    }
}

function Invoke-PtcRun {
    [CmdletBinding(DefaultParameterSetName = 'String')]
    param(
        [Parameter(Mandatory, Position = 0, ParameterSetName = 'ScriptBlock')]
        [scriptblock]$ScriptBlock,

        [Parameter(Mandatory, Position = 0, ParameterSetName = 'String')]
        [string]$Command,

        [int]$MaxItems = $script:DefaultMaxItems,
        [int]$Depth = 5
    )

    if ($PSCmdlet.ParameterSetName -eq 'ScriptBlock') {
        $global:LASTEXITCODE = 0
        $output = & $ScriptBlock 2>&1 3>&1 4>&1 5>&1 6>&1
        $exitCode = Get-PtcLastExitCode
        $compressed = $output | Compress-PtcObject -MaxItems $MaxItems
        if ($exitCode -ne 0) {
            Join-PtcLines @($compressed, '[exit] {0}' -f $exitCode)
        } else {
            $compressed
        }
        return
    }

    $temp = Join-Path ([System.IO.Path]::GetTempPath()) ('ptc-{0}.clixml' -f ([guid]::NewGuid()))
    $encoded = [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes(@"
`$ErrorActionPreference = 'Continue'
try {
    `$result = & {
$Command
    } 2>&1 3>&1 4>&1 5>&1 6>&1
    `$result | Export-Clixml -LiteralPath '$temp' -Depth $Depth
    `$code = if (`$global:LASTEXITCODE -is [int]) { `$global:LASTEXITCODE } else { 0 }
    exit `$code
} catch {
    `$_ | Export-Clixml -LiteralPath '$temp' -Depth $Depth
    exit 1
}
"@))

    $process = Start-Process -FilePath 'pwsh' -ArgumentList @('-NoProfile', '-NonInteractive', '-EncodedCommand', $encoded) -NoNewWindow -Wait -PassThru
    try {
        if (Test-Path -LiteralPath $temp) {
            $output = Import-Clixml -LiteralPath $temp
        } else {
            $output = '[no output]'
        }
    } finally {
        Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
    }

    $compressed = $output | Compress-PtcObject -MaxItems $MaxItems
    if ($process.ExitCode -ne 0) {
        Join-PtcLines @($compressed, '[exit] {0}' -f $process.ExitCode)
    } else {
        $compressed
    }
}

function Invoke-Ptc {
    [CmdletBinding()]
    param(
        [Parameter(Position = 0)]
        [string]$Command = 'help',

        [Parameter(ValueFromRemainingArguments)]
        [object[]]$Arguments,

        [Parameter(ValueFromPipeline)]
        [AllowNull()]
        [object]$InputObject
    )

    begin {
        $pipelineItems = [System.Collections.Generic.List[object]]::new()
    }
    process {
        if ($null -ne $InputObject) { $pipelineItems.Add($InputObject) }
    }
    end {
        switch -Regex ($Command) {
            '^(ls|dir|gci|list)$' {
                Invoke-PtcBoundCommand -Name Invoke-PtcList -Arguments $Arguments
                break
            }
            '^(read|cat|gc|content)$' {
                Invoke-PtcBoundCommand -Name Invoke-PtcRead -Arguments $Arguments
                break
            }
            '^(smart|summarize|summary)$' {
                Invoke-PtcBoundCommand -Name Invoke-PtcSmart -Arguments $Arguments
                break
            }
            '^(measure|savings)$' {
                Invoke-PtcBoundCommand -Name Measure-PtcSavings -Arguments $Arguments
                break
            }
            '^(grep|search|sls)$' {
                Invoke-PtcBoundCommand -Name Invoke-PtcSearch -Arguments $Arguments
                break
            }
            '^(ps|process)$' {
                Invoke-PtcBoundCommand -Name Invoke-PtcProcess -Arguments $Arguments
                break
            }
            '^(service|services|svc)$' {
                Invoke-PtcBoundCommand -Name Invoke-PtcService -Arguments $Arguments
                break
            }
            '^(run|exec)$' {
                Invoke-PtcBoundCommand -Name Invoke-PtcRun -Arguments $Arguments
                break
            }
            '^(compress|object)$' {
                $maxItems = $script:DefaultMaxItems
                if ($Arguments) {
                    for ($i = 0; $i -lt $Arguments.Count; $i++) {
                        if ($Arguments[$i] -in '-MaxItems', '--MaxItems' -and $i + 1 -lt $Arguments.Count) {
                            $maxItems = [int]$Arguments[$i + 1]
                        }
                    }
                }
                $pipelineItems | Compress-PtcObject -MaxItems $maxItems
                break
            }
            default {
                Join-PtcLines @(
                    'ptk commands:',
                    '  ptk ls [path] [-Recurse] [-Force]',
                    '  ptk read <path> [-Level none|minimal|aggressive|summary] [-MaxLines n] [-Tail]',
                    '  ptk smart <path>',
                    '  ptk savings <path>',
                    '  ptk grep <pattern> [path] [-Include *.ps1]',
                    '  ptk ps [name]',
                    '  ptk service [name]',
                    '  ptk run { <scriptblock> }',
                    '  ptk run "<PowerShell command string>"',
                    '  <objects> | ptk compress [-MaxItems n]'
                )
            }
        }
    }
}

Set-Alias -Name ptk -Value Invoke-Ptc

Export-ModuleMember -Function @(
    'Compress-PtcObject',
    'Invoke-PtcBoundCommand',
    'Invoke-Ptc',
    'Invoke-PtcRun',
    'Invoke-PtcList',
    'Invoke-PtcRead',
    'Invoke-PtcSmart',
    'Measure-PtcSavings',
    'Invoke-PtcSearch',
    'Invoke-PtcProcess',
    'Invoke-PtcService'
) -Alias 'ptk'
