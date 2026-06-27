BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../src/PwshTokenCompressor.psd1') -Force
}

Describe 'Compress-PtcObject' {
    It 'compresses filesystem objects before formatting' {
        $result = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..') | Compress-PtcObject -MaxItems 10

        $result | Should -Match '^fs:'
        $result | Should -Match 'README.md'
    }

    It 'compresses match info grouped by file' {
        $root = Join-Path $PSScriptRoot '..'
        $result = Select-String -Path (Join-Path $root 'README.md') -Pattern 'PowerShell' | Compress-PtcObject

        $result | Should -Match 'matches in'
        $result | Should -Match '\[file\]'
    }

    It 'compresses generic objects into a compact table' {
        $objects = 1..3 | ForEach-Object {
            [pscustomobject]@{ Name = "item$_"; Status = 'ok'; Value = $_ }
        }

        $result = $objects | Compress-PtcObject

        $result | Should -Match 'objects: 3'
        $result | Should -Match 'Name'
        $result | Should -Match 'item1'
    }

    It 'limits long strings' {
        $result = (1..100 | ForEach-Object { "line $_" }) | Compress-PtcObject -MaxItems 5

        $result | Should -Match '\[95 more lines\]'
    }
}

Describe 'Invoke-PtcBoundCommand arg parser' {
    It 'treats a dash-digit token as a value, not a flag' {
        # A negative integer should be bound as a value, not treated as a switch named "3"
        $sb = { param([int]$Count) "Count=$Count" }
        $result = Invoke-PtcBoundCommand -Name $sb -Arguments @('-Count', '-3')
        $result | Should -Be 'Count=-3'
    }

    It 'treats a dash-digit first argument as a positional value' {
        $sb = { param([string]$x) "x=$x" }
        $result = Invoke-PtcBoundCommand -Name $sb -Arguments @('-5')
        # -5 is not a flag (digit after dash), so it is a positional -> bound to $x
        $result | Should -Be 'x=-5'
    }

    It 'passes tokens after -- as positional values regardless of leading dash' {
        $sb = { param([string]$x) "x=$x" }
        $result = Invoke-PtcBoundCommand -Name $sb -Arguments @('--', '-not-a-flag')
        $result | Should -Be 'x=-not-a-flag'
    }
}

Describe 'ptk dispatcher' {
    It 'runs list wrapper' {
        $root = Join-Path $PSScriptRoot '..'
        $result = ptk ls $root -MaxItems 5

        $result | Should -Match '^fs:'
    }

    It 'runs read wrapper' {
        $file = Join-Path $PSScriptRoot '../README.md'
        $result = ptk read $file -MaxLines 3

        $result | Should -Match 'pwsh_token_compressor'
    }

    It 'runs scriptblock commands with structured output' {
        $result = ptk run { [pscustomobject]@{ Name = 'alpha'; Status = 'ok' } }

        $result | Should -Match 'objects: 1'
        $result | Should -Match 'alpha'
    }

    It 'does not emit [exit] for a pure-PowerShell ScriptBlock when stale LASTEXITCODE is set' {
        $global:LASTEXITCODE = 7
        $result = Invoke-PtcRun -ScriptBlock { [pscustomobject]@{ Name = 'ok' } }
        $global:LASTEXITCODE = 0

        $result | Should -Not -Match '\[exit\]'
    }

    It 'compresses pipeline input' {
        $result = Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot '..') | ptk compress -MaxItems 3

        $result | Should -Match '^fs:'
        $result | Should -Match '\+'
    }
}

Describe 'ptk.ps1 launcher' {
    It 'loads the module and dispatches commands' {
        $root = Resolve-Path (Join-Path $PSScriptRoot '..')
        $launcher = Join-Path $root 'ptk.ps1'
        $output = & $launcher read (Join-Path $root 'README.md') -MaxLines 2

        ($output -join "`n") | Should -Match 'pwsh_token_compressor'
    }
}

Describe 'smart read modes' {
    It 'keeps existing read behavior at level none' {
        $file = Join-Path $PSScriptRoot 'fixtures/SampleModule.psm1'
        $result = ptk read $file -MaxLines 6

        $result | Should -Match '\.SYNOPSIS'
    }

    It 'omits single-file raw header when it only adds noise' {
        $file = Join-Path $PSScriptRoot 'fixtures/SmallLog.errlog'
        $result = ptk cat $file

        $result | Should -Not -Match '^\[file\]'
        $result | Should -Match 'PyTorch was not found'
    }

    It 'keeps single-file raw header when output is truncated' {
        $file = Join-Path $PSScriptRoot 'fixtures/SampleModule.psm1'
        $result = ptk read $file -MaxLines 2

        $result | Should -Match '^\[file\]'
        $result | Should -Match 'more lines'
    }

    It 'strips PowerShell comment help in minimal mode' {
        $file = Join-Path $PSScriptRoot 'fixtures/SampleModule.psm1'
        $result = ptk read $file -Level minimal -MaxLines 80

        $result | Should -Not -Match '\.SYNOPSIS'
        $result | Should -Match 'function Get-SampleThing'
    }

    It 'summarizes PowerShell files using AST data' {
        $file = Join-Path $PSScriptRoot 'fixtures/SampleModule.psm1'
        $result = ptk smart $file

        $result | Should -Match 'PowerShell file:'
        $result | Should -Match 'Get-SampleThing'
        $result | Should -Match 'Export-ModuleMember'
    }

    It 'aggressively keeps PowerShell signatures and exports' {
        $file = Join-Path $PSScriptRoot 'fixtures/SampleModule.psm1'
        $result = ptk read $file -Level aggressive -MaxLines 80

        $result | Should -Match 'function Get-SampleThing'
        $result | Should -Match 'Export-ModuleMember'
        $result | Should -Not -Match 'This body should be dropped'
    }

    It 'summarizes Markdown outline and code blocks' {
        $file = Join-Path $PSScriptRoot 'fixtures/Sample.md'
        $result = ptk smart $file

        $result | Should -Match 'Markdown:'
        $result | Should -Match 'Sample Guide'
        $result | Should -Match 'powershell'
    }

    It 'summarizes generic text signals' {
        $file = Join-Path $PSScriptRoot 'fixtures/Sample.txt'
        $result = ptk smart $file

        $result | Should -Match 'Text:'
        $result | Should -Match 'Warning'
        $result | Should -Match 'TODO'
    }

    It 'summarizes non-PowerShell code with regex heuristics' {
        $file = Join-Path $PSScriptRoot 'fixtures/Sample.cs'
        $result = ptk smart $file

        $result | Should -Match 'CSharp code:'
        $result | Should -Match 'WidgetService'
        $result | Should -Match 'IWidget'
    }

    It 'reports savings for smart modes' {
        $file = Join-Path $PSScriptRoot 'fixtures/SampleModule.psm1'
        $result = Measure-PtcSavings -Path $file

        $result.Count | Should -Be 3
        @($result | Where-Object Level -eq 'summary')[0].SavingsPct | Should -BeGreaterThan 40
    }
}
