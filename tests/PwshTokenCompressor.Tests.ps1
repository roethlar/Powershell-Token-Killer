BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '../src/PwshTokenCompressor.psd1') -Force
}

Describe 'Compress-PtcObject' {
    It 'compresses filesystem objects before formatting' {
        # Deterministic fixture: the live repo root is environment-sensitive (a
        # checkout with >10 entries pushes README.md past the -MaxItems cap).
        $fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("ptc-fsfix-{0}" -f ([guid]::NewGuid()))
        New-Item -ItemType Directory -Path (Join-Path $fixture 'sub') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $fixture 'README.md') -Value 'fixture readme'
        Set-Content -LiteralPath (Join-Path $fixture 'notes.txt') -Value 'notes'
        try {
            $result = Get-ChildItem -LiteralPath $fixture | Compress-PtcObject -MaxItems 10

            $result | Should -Match '^fs:'
            $result | Should -Match 'README.md'
        } finally {
            Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
        }
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

    It 'applies MaxItems to rows with no display properties' {
        $result = 1..5 | Compress-PtcObject -MaxItems 2
        $lines = @($result -split "`r?`n")

        $lines | Should -Contain '1'
        $lines | Should -Contain '2'
        $lines | Should -Not -Contain '3'
        $result | Should -Match '\+3 more'
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
        # Deterministic temp fixture: asserting on live README content broke
        # when the README was rewritten (2026-07-04).
        $file = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-read-{0}.txt" -f ([guid]::NewGuid()))
        Set-Content -LiteralPath $file -Value @('ptk-read-fixture-marker', 'second line', 'third line')
        try {
            $result = ptk read $file -MaxLines 3

            $result | Should -Match 'ptk-read-fixture-marker'
        }
        finally {
            Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        }
    }

    It 'runs scriptblock commands with structured output' {
        $result = ptk run { [pscustomobject]@{ Name = 'alpha'; Status = 'ok' } }

        $result | Should -Match 'objects: 1'
        $result | Should -Match 'alpha'
    }

    It 'runs string Command branch and returns compressed output' {
        $result = Invoke-PtcRun -Command '[pscustomobject]@{ Name = "strpath"; Value = 42 }'

        $result | Should -Match 'objects: 1'
        $result | Should -Match 'strpath'
    }

    It 'does not embed the temp path as a literal in the generated script' {
        # Guard for the env-var injection fix: intercept Start-Process to capture the
        # -EncodedCommand argument, then base64-decode it and assert the literal temp
        # path does NOT appear in the generated script. If the fix were reverted (i.e.,
        # the path were interpolated into the script string), the decoded script would
        # contain the literal path and this test would fail.
        $script:capturedScript = $null
        Mock -ModuleName PwshTokenCompressor Start-Process {
            # Find the encoded command argument (the element after '-EncodedCommand')
            $argArr = @($ArgumentList)
            $idx = [Array]::IndexOf($argArr, '-EncodedCommand')
            if ($idx -ge 0 -and $idx + 1 -lt $argArr.Count) {
                $encoded = $argArr[$idx + 1]
                $script:capturedScript = [Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($encoded))
            }
            # Return a fake process object with ExitCode 0
            [pscustomobject]@{ ExitCode = 0 }
        } -Verifiable

        # Also mock Test-Path and Import-Clixml so we don't need a real temp file
        Mock -ModuleName PwshTokenCompressor Test-Path { $false } -Verifiable

        Invoke-PtcRun -Command 'Write-Output "injection-guard"'

        # The generated script must not contain the literal temp path; it should only
        # reference $env:PTC_TEMP (the safe env-var indirection).
        $tempBase = [System.IO.Path]::GetTempPath().TrimEnd([System.IO.Path]::DirectorySeparatorChar, '/')
        $script:capturedScript | Should -Not -Match ([regex]::Escape($tempBase))
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
        $result | Should -Match '\+\d+ more filesystem items'
    }
}

Describe 'ptk.ps1 launcher' {
    It 'loads the module and dispatches commands' {
        # Deterministic temp fixture: asserting on live README content broke
        # when the README was rewritten (2026-07-04).
        $root = Resolve-Path (Join-Path $PSScriptRoot '..')
        $launcher = Join-Path $root 'ptk.ps1'
        $file = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-launcher-{0}.txt" -f ([guid]::NewGuid()))
        Set-Content -LiteralPath $file -Value @('ptk-launcher-fixture-marker', 'second line')
        try {
            $output = & $launcher read $file -MaxLines 2

            ($output -join "`n") | Should -Match 'ptk-launcher-fixture-marker'
        }
        finally {
            Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        }
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

    It 'aggressively keeps C# class/namespace/using declarations but not method signatures' {
        # C# method signatures (e.g. "public void Run()") use no function keyword and
        # are not captured by aggressive mode; this is documented behaviour, not a gap.
        $file = Join-Path $PSScriptRoot 'fixtures/Sample.cs'
        $result = ptk read $file -Level aggressive

        $result | Should -Match 'using System'
        $result | Should -Match 'namespace Demo'
        $result | Should -Match 'public class WidgetService'
        $result | Should -Match 'public interface IWidget'
        # Method bodies and bare method signatures are not retained in aggressive mode for C#
        $result | Should -Not -Match 'Console\.WriteLine'
    }

    It 'summarizes Markdown outline and code blocks' {
        $file = Join-Path $PSScriptRoot 'fixtures/Sample.md'
        $result = ptk smart $file

        $result | Should -Match 'Markdown:'
        $result | Should -Match 'Sample Guide'
        $result | Should -Match 'powershell'
        # Guard against fence double-count: Sample.md has exactly one fenced block
        $result | Should -Match '1 code block'
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

    It 'preserves lines starting with # inside multi-line strings at minimal level' {
        # A Python triple-quoted string containing a # line — must not be stripped at minimal level
        $text = @"
x = """
# comment inside string
some value
"""
y = 1
"@
        $tmp = Join-Path ([System.IO.Path]::GetTempPath()) 'ptk-test-multiline.py'
        Set-Content -LiteralPath $tmp -Value $text -NoNewline
        try {
            $result = Invoke-PtcRead -Path $tmp -Level minimal
        } finally {
            Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        }
        $result | Should -Match '# comment inside string'
    }

    It 'reports savings for smart modes' {
        $file = Join-Path $PSScriptRoot 'fixtures/SampleModule.psm1'
        $result = Measure-PtcSavings -Path $file

        $result.Count | Should -Be 3
        @($result | Where-Object Level -eq 'summary')[0].SavingsPct | Should -BeGreaterThan 40
    }
}

Describe 'object routing robustness' {
    It 'does not crash on projected objects that carry a FileInfo type name' {
        # Select-Object / Clixml round-trips tag projections with the source type
        # name but drop properties like PSIsContainer. Routing must not assume them.
        $obj = [pscustomobject]@{ Name = 'alpha'; Length = 10 }
        $obj.PSObject.TypeNames.Insert(0, 'Deserialized.Selected.System.IO.FileInfo')

        $result = $obj | Compress-PtcObject

        $result | Should -Match 'alpha'
        $result | Should -Not -Match 'cannot be found'
    }

    It 'still routes real (deserialized) filesystem objects to the fs compressor' {
        # Deterministic fixture: same rationale as the fs test above.
        $fixture = Join-Path ([System.IO.Path]::GetTempPath()) ("ptc-fsfix-{0}" -f ([guid]::NewGuid()))
        New-Item -ItemType Directory -Path (Join-Path $fixture 'sub') -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $fixture 'README.md') -Value 'fixture readme'
        Set-Content -LiteralPath (Join-Path $fixture 'notes.txt') -Value 'notes'
        $temp = Join-Path ([System.IO.Path]::GetTempPath()) ("ptc-fs-{0}.clixml" -f ([guid]::NewGuid()))
        try {
            Get-ChildItem -LiteralPath $fixture | Export-Clixml -LiteralPath $temp
            $result = Import-Clixml -LiteralPath $temp | Compress-PtcObject -MaxItems 10
            $result | Should -Match '^fs:'
            $result | Should -Match 'README.md'
        } finally {
            Remove-Item -LiteralPath $temp -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $fixture -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'runs a command string that projects objects without crashing' {
        $result = Invoke-PtcRun -Command "Get-ChildItem -LiteralPath '$PSScriptRoot' | Select-Object Name,Length"

        $result | Should -Not -Match 'cannot be found'
        $result | Should -Match 'Name'
    }

    It 'routes an fs projection lacking display properties to the generic path' {
        # Round-2 review repro: Select-Object keeps the FileInfo type name and
        # PSIsContainer but drops Name/LastWriteTime, which the fs compressor
        # dereferences under strict mode.
        $projected = Get-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'README.md') |
            Select-Object PSIsContainer

        $result = $projected | Compress-PtcObject

        $result | Should -Not -Match '^fs:'
        $result | Should -Not -Match 'cannot be found'
    }

    It 'routes a file projection lacking Length to the generic path instead of reporting 0B' {
        # Codex review of the guard-completion commit: a file (not a directory)
        # without Length is a projection whose size is unknown, not zero — the
        # fs compressor would render a misleading "0B" total for it.
        $projected = Get-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'README.md') |
            Select-Object PSIsContainer, Name, LastWriteTime

        $result = $projected | Compress-PtcObject

        $result | Should -Not -Match '^fs:'
        $result | Should -Not -Match 'cannot be found'
    }

    It 'routes a file projection with a null Length value to the generic path' {
        # A Length property whose value is null is still an unknown size, not
        # zero — presence of the property alone must not satisfy the fs guard.
        $projected = Get-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'README.md') |
            Select-Object PSIsContainer, Name, LastWriteTime, @{ n = 'Length'; e = { $null } }

        $result = $projected | Compress-PtcObject

        $result | Should -Not -Match '^fs:'
        $result | Should -Not -Match 'cannot be found'
    }

    It 'does not let one genuine FileInfo drag fs-shaped foreign objects onto the fs route' {
        # Shape alone is not enough either: every item must carry a filesystem
        # type name, or a look-alike (here a process projection) would be
        # rendered as a file.
        $lookAlike = Get-Process -Id $PID | Select-Object `
            @{ n = 'PSIsContainer'; e = { $false } }, Name,
            @{ n = 'LastWriteTime'; e = { Get-Date } }, @{ n = 'Length'; e = { 5 } }
        $mixed = @((Get-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'README.md')), $lookAlike)

        $result = $mixed | Compress-PtcObject

        $result | Should -Not -Match '^fs:'
        $result | Should -Match '^objects: 2'
    }

    It 'routes a MatchInfo projection lacking Line to the generic path' {
        $projected = Select-String -Path (Join-Path $PSScriptRoot '..' 'README.md') -Pattern 'PowerShell' |
            Select-Object LineNumber, Path

        $result = $projected | Compress-PtcObject

        $result | Should -Not -Match 'matches in'
        $result | Should -Not -Match 'cannot be found'
    }

    It 'compresses a mixed FileInfo + string stream via the generic path without throwing' {
        $mixed = @((Get-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'README.md')), 'done')

        $result = $mixed | Compress-PtcObject

        $result | Should -Match '^objects: 2'
        $result | Should -Not -Match 'cannot be found'
    }

    It 'keeps a mixed stream compressed through Compress-PtcOutput (no shape-error fallback)' {
        $mixed = @((Get-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'README.md')), 'done')

        $result = $mixed | Compress-PtcOutput

        $result | Should -Not -Match '\[ptk:shape ERROR'
        $result | Should -Match '^objects: 2'
    }
}

Describe 'Resolve-PtcInvokeScript routing' {
    BeforeAll {
        # A real file so Get-PtcRtkCommand resolves; nothing executes in these
        # tests - routing only rewrites the script text.
        $script:fakeRtk = Join-Path ([System.IO.Path]::GetTempPath()) ("rtk-route-{0}.exe" -f ([guid]::NewGuid()))
        Set-Content -LiteralPath $script:fakeRtk -Value ''
    }
    AfterAll {
        Remove-Item -LiteralPath $script:fakeRtk -Force -ErrorAction SilentlyContinue
    }
    BeforeEach { $env:PTK_RTK_PATH = $script:fakeRtk }
    AfterEach { Remove-Item env:PTK_RTK_PATH -ErrorAction SilentlyContinue }

    It 'rewrites a single native command with constant args to rtk' {
        Resolve-PtcInvokeScript -Script 'git status -s' |
            Should -BeExactly ("& '{0}' git status -s" -f $script:fakeRtk)
    }

    It 'keeps quoted constant arguments intact in the rewrite' {
        Resolve-PtcInvokeScript -Script 'git commit -m "hello world"' |
            Should -BeExactly ("& '{0}' git commit -m `"hello world`"" -f $script:fakeRtk)
    }

    It 'leaves aliases and cmdlets on the PowerShell path' {
        # On Windows ls is an alias; rtk ls fails (slice-0 probe), so
        # resolution must happen in the runspace, not by name shape.
        Resolve-PtcInvokeScript -Script 'ls' | Should -BeExactly 'ls'
        Resolve-PtcInvokeScript -Script 'Get-ChildItem -Force' | Should -BeExactly 'Get-ChildItem -Force'
    }

    It 'leaves pipelines, chains, variables, expandable strings, and redirections unchanged' {
        foreach ($s in @(
            'git status | Select-String modified',
            'git status && git diff',
            '$x = git status',
            'git log -1 > out.txt',
            'git commit -m "$msg"',
            '& git status'
        )) {
            Resolve-PtcInvokeScript -Script $s | Should -BeExactly $s
        }
    }

    It 'leaves scripts with parse errors unchanged' {
        Resolve-PtcInvokeScript -Script 'git status ||| (' |
            Should -BeExactly 'git status ||| ('
    }

    It 'does not double-route rtk itself' {
        Resolve-PtcInvokeScript -Script 'rtk gain' | Should -BeExactly 'rtk gain'
    }

    It 'returns the script unchanged when rtk is absent' {
        $env:PTK_RTK_PATH = Join-Path ([System.IO.Path]::GetTempPath()) 'no-such-rtk-binary.exe'
        Resolve-PtcInvokeScript -Script 'git status' | Should -BeExactly 'git status'
    }

    It 'keeps batch-shim applications (.cmd/.bat) on the PowerShell path' -Skip:(-not $IsWindows) {
        # Windows-only: extensionless resolution of a .cmd needs PATHEXT, so
        # off-Windows this test would pass vacuously via the command-miss path.
        # Codex finding: PowerShell special-cases argument quoting for
        # .cmd/.bat shims (npm, npx); re-invoking them through rtk.exe can
        # change the argv the shim sees.
        $shimDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ptc-shim-{0}" -f ([guid]::NewGuid()))
        New-Item -ItemType Directory -Path $shimDir -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $shimDir 'ptcshimtest.cmd') -Value "@echo off`r`nexit /b 0"
        $savedPath = $env:PATH
        try {
            $env:PATH = "$shimDir$([System.IO.Path]::PathSeparator)$env:PATH"
            Resolve-PtcInvokeScript -Script 'ptcshimtest --flag' |
                Should -BeExactly 'ptcshimtest --flag'
        } finally {
            $env:PATH = $savedPath
            Remove-Item -LiteralPath $shimDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    It 'does not pollute $Error when rtk is absent from PATH' {
        Remove-Item env:PTK_RTK_PATH -ErrorAction SilentlyContinue
        $savedPath = $env:PATH
        try {
            $env:PATH = [System.IO.Path]::GetTempPath()
            $Error.Clear()
            Resolve-PtcInvokeScript -Script 'git status' | Should -BeExactly 'git status'

            $Error.Count | Should -Be 0
        } finally {
            $env:PATH = $savedPath
        }
    }

    It 'does not pollute $Error when the command name does not resolve' {
        # Codex finding: Get-Command -ErrorAction SilentlyContinue records a
        # hidden CommandNotFoundException on every typo, skewing $Error for
        # the user script that runs next.
        $Error.Clear()
        Resolve-PtcInvokeScript -Script 'gti status' | Should -BeExactly 'gti status'

        $Error.Count | Should -Be 0
    }

    It 'honors the route overrides' {
        Resolve-PtcInvokeScript -Script 'git status' -Route pwsh | Should -BeExactly 'git status'
        # force-rtk skips Application resolution but still needs the
        # single-command constant-args shape
        Resolve-PtcInvokeScript -Script 'Get-ChildItem' -Route rtk |
            Should -BeExactly ("& '{0}' Get-ChildItem" -f $script:fakeRtk)
        Resolve-PtcInvokeScript -Script 'git status | Out-Null' -Route rtk |
            Should -BeExactly 'git status | Out-Null'
    }
}

Describe 'redirect hook and installer' {
    BeforeAll {
        $script:hookScript = Join-Path $PSScriptRoot '..' 'scripts' 'ptk-hook.ps1'
        $script:initScript = Join-Path $PSScriptRoot '..' 'scripts' 'ptk_init.ps1'
    }

    It 'denies a shell tool call with guidance naming ptk_invoke' {
        $out = '{"tool_name":"Bash","tool_input":{"command":"git status"}}' |
            pwsh -NoProfile -File $script:hookScript
        $LASTEXITCODE | Should -Be 0

        $decision = ($out | ConvertFrom-Json).hookSpecificOutput
        $decision.permissionDecision | Should -BeExactly 'deny'
        $decision.permissionDecisionReason | Should -Match 'mcp__ptk__ptk_invoke'
        $decision.permissionDecisionReason | Should -Match 'PTK_DIRECT'
    }

    It 'carries the denied call''s cwd into the guidance' {
        # The warm runspace keeps its own current directory; without the cwd
        # a replayed relative-path command can run in the wrong place.
        $out = '{"tool_name":"Bash","tool_input":{"command":"dotnet test"},"cwd":"C:\\repo\\server"}' |
            pwsh -NoProfile -File $script:hookScript

        $reason = ($out | ConvertFrom-Json).hookSpecificOutput.permissionDecisionReason
        $reason | Should -Match ([regex]::Escape("Set-Location 'C:\repo\server'"))
    }

    It 'escapes apostrophes in the cwd so the suggested prefix stays valid PowerShell' {
        $out = '{"tool_name":"Bash","tool_input":{"command":"git status"},"cwd":"C:\\Users\\O''Brien\\repo"}' |
            pwsh -NoProfile -File $script:hookScript

        $reason = ($out | ConvertFrom-Json).hookSpecificOutput.permissionDecisionReason
        $reason | Should -Match ([regex]::Escape("Set-Location 'C:\Users\O''Brien\repo'"))
    }

    It 'allows a command carrying the PTK_DIRECT escape hatch' {
        $out = '{"tool_name":"PowerShell","tool_input":{"command":"gcloud auth login # PTK_DIRECT"}}' |
            pwsh -NoProfile -File $script:hookScript
        $LASTEXITCODE | Should -Be 0
        $out | Should -BeNullOrEmpty
    }

    It 'allows the call when its own input is unparseable (never blocks on self-failure)' {
        $out = 'not json at all' | pwsh -NoProfile -File $script:hookScript
        $LASTEXITCODE | Should -Be 0
        $out | Should -BeNullOrEmpty
    }

    Context 'ptk_init settings patching' {
        BeforeEach {
            $script:settings = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-init-{0}.json" -f ([guid]::NewGuid()))
        }
        AfterEach {
            Remove-Item -LiteralPath $script:settings -Force -ErrorAction SilentlyContinue
        }

        It 'installs one Bash|PowerShell entry into fresh settings, idempotently' {
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings | Out-Null
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings | Out-Null

            $config = Get-Content -LiteralPath $script:settings -Raw | ConvertFrom-Json
            $entries = @($config.hooks.PreToolUse)
            $entries.Count | Should -Be 1
            $entries[0].matcher | Should -BeExactly 'Bash|PowerShell'
            $entries[0].hooks[0].command | Should -Match 'ptk-hook\.ps1'
        }

        It 'preserves foreign hooks and settings on install and uninstall' {
            # The shape rtk init leaves behind, plus an unrelated setting.
            Set-Content -LiteralPath $script:settings -Value (@{
                model = 'sonnet'
                hooks = @{
                    PreToolUse = @(
                        @{ matcher = 'Bash'; hooks = @(@{ type = 'command'; command = 'rtk hook claude' }) }
                    )
                }
            } | ConvertTo-Json -Depth 8)

            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings | Out-Null
            $config = Get-Content -LiteralPath $script:settings -Raw | ConvertFrom-Json
            $config.model | Should -BeExactly 'sonnet'
            @($config.hooks.PreToolUse).Count | Should -Be 2
            @($config.hooks.PreToolUse | Where-Object { $_.hooks[0].command -eq 'rtk hook claude' }).Count | Should -Be 1

            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -Uninstall | Out-Null
            $config = Get-Content -LiteralPath $script:settings -Raw | ConvertFrom-Json
            $config.model | Should -BeExactly 'sonnet'
            @($config.hooks.PreToolUse).Count | Should -Be 1
            $config.hooks.PreToolUse[0].hooks[0].command | Should -BeExactly 'rtk hook claude'
        }

        It 'keeps a foreign hook that shares one matcher entry with ours' {
            # One entry, two hooks: uninstall must strip only ours, not drop
            # the whole entry.
            $ourCommand = 'pwsh -NoProfile -File "{0}"' -f (Join-Path $PSScriptRoot '..' 'scripts' 'ptk-hook.ps1')
            Set-Content -LiteralPath $script:settings -Value (@{
                hooks = @{
                    PreToolUse = @(
                        @{
                            matcher = 'Bash'
                            hooks   = @(
                                @{ type = 'command'; command = 'rtk hook claude' },
                                @{ type = 'command'; command = $ourCommand }
                            )
                        }
                    )
                }
            } | ConvertTo-Json -Depth 8)

            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -Uninstall | Out-Null

            $config = Get-Content -LiteralPath $script:settings -Raw | ConvertFrom-Json
            @($config.hooks.PreToolUse).Count | Should -Be 1
            @($config.hooks.PreToolUse[0].hooks).Count | Should -Be 1
            $config.hooks.PreToolUse[0].hooks[0].command | Should -BeExactly 'rtk hook claude'
        }

        It 'writes nothing under -DryRun' {
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -DryRun | Out-Null
            Test-Path -LiteralPath $script:settings | Should -BeFalse
        }
    }
}

Describe 'minimal mode comment stripping' {
    It 'preserves #requires directives and here-string content while stripping PowerShell comments' {
        $src = @'
#requires -Version 7
function Test-Thing {
    $banner = @"
#keepme inside herestring
"@
    $x = 1 # trailing comment
    return $banner
}
# whole-line comment
'@
        $file = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-directives-{0}.ps1" -f ([guid]::NewGuid()))
        Set-Content -LiteralPath $file -Value $src
        try {
            $result = Invoke-PtcRead -Path $file -Level minimal -MaxLines 80
        } finally {
            Remove-Item -LiteralPath $file -Force -ErrorAction SilentlyContinue
        }

        $result | Should -Match '#requires'
        $result | Should -Match 'keepme inside herestring'
        $result | Should -Not -Match 'whole-line comment'
        $result | Should -Not -Match 'trailing comment'
    }
}

Describe 'Compress-PtcOutput' {
    AfterEach {
        Remove-Item env:PTK_RTK_PATH -ErrorAction SilentlyContinue
    }

    It 'compresses object output via Compress-PtcObject' {
        $result = [pscustomobject]@{ Name = 'a'; Value = 1 },
                  [pscustomobject]@{ Name = 'b'; Value = 2 } | Compress-PtcOutput

        $result | Should -Match '^objects: 2'
        $result | Should -Match 'Name'
    }

    It 'passes string output through verbatim and never truncates it' {
        $lines = 1..40 | ForEach-Object { "line $_" }
        $result = $lines | Compress-PtcOutput

        $result | Should -BeExactly ($lines -join [Environment]::NewLine)
        $result | Should -Match 'line 40'
        $result | Should -Not -Match 'more'
    }

    It 'passes a single string through exactly' {
        'just one line, untouched' | Compress-PtcOutput |
            Should -BeExactly 'just one line, untouched'
    }

    It 'treats primitive scalars as text, not object tables' {
        42 | Compress-PtcOutput | Should -BeExactly '42'
        $result = 1, 2, 3 | Compress-PtcOutput
        $result | Should -BeExactly (@('1', '2', '3') -join [Environment]::NewLine)
    }

    It 'routes mixed string/object output down the object path' {
        $result = 'text', [pscustomobject]@{ A = 1 } | Compress-PtcOutput

        $result | Should -Match '^objects: 2'
    }

    It 'returns nothing for empty output' {
        @() | Compress-PtcOutput | Should -BeNullOrEmpty
    }

    Context 'log-shaped text' {
        BeforeAll {
            $script:logText = @(1..8 | ForEach-Object {
                "2026-07-03 10:00:0$($_ % 10) INFO worker: step $_ completed"
            })
        }

        It 'falls back to labeled raw text when rtk is absent' {
            $env:PTK_RTK_PATH = Join-Path ([System.IO.Path]::GetTempPath()) 'no-such-rtk-binary.exe'
            $result = $script:logText | Compress-PtcOutput

            $result | Should -Match '\[ptk:log rtk not found'
            $result | Should -Match 'step 8 completed'
        }

        It 'routes through rtk when a binary is configured' {
            $stub = Join-Path ([System.IO.Path]::GetTempPath()) ("rtk-stub-{0}.ps1" -f ([guid]::NewGuid()))
            Set-Content -LiteralPath $stub -Value 'param($verb, $path) "RTKSTUB verb=$verb exists=$(Test-Path -LiteralPath $path)"'
            try {
                $env:PTK_RTK_PATH = $stub
                $result = $script:logText | Compress-PtcOutput
            } finally {
                Remove-Item -LiteralPath $stub -Force -ErrorAction SilentlyContinue
            }

            $result | Should -Match '\[ptk:log via rtk\]'
            $result | Should -Match 'RTKSTUB verb=log exists=True'
        }

        It 'preserves the caller''s $LASTEXITCODE across the native rtk leg' {
            # The stub must be a native command (.cmd/.sh), not a .ps1: only a
            # native invocation overwrites $LASTEXITCODE, which is the bug under
            # test — a .ps1 stub would make this test pass without the fix.
            if ($IsWindows) {
                $stub = Join-Path ([System.IO.Path]::GetTempPath()) ("rtk-native-stub-{0}.cmd" -f ([guid]::NewGuid()))
                Set-Content -LiteralPath $stub -Value "@echo off`r`necho RTKSTUB native`r`nexit /b 0"
            } else {
                $stub = Join-Path ([System.IO.Path]::GetTempPath()) ("rtk-native-stub-{0}.sh" -f ([guid]::NewGuid()))
                Set-Content -LiteralPath $stub -Value "#!/bin/sh`necho 'RTKSTUB native'`nexit 0"
                chmod +x $stub
            }
            try {
                $env:PTK_RTK_PATH = $stub
                $global:LASTEXITCODE = 7
                $result = $script:logText | Compress-PtcOutput
            } finally {
                Remove-Item -LiteralPath $stub -Force -ErrorAction SilentlyContinue
            }

            $result | Should -Match '\[ptk:log via rtk\]'
            $result | Should -Match 'RTKSTUB native'
            $global:LASTEXITCODE | Should -Be 7
        }

        It 'leaves non-log multi-line text alone even when rtk is configured' {
            $env:PTK_RTK_PATH = 'anything'
            $prose = 1..6 | ForEach-Object { "paragraph $_ of plain prose without timestamps" }
            $result = $prose | Compress-PtcOutput

            $result | Should -BeExactly ($prose -join [Environment]::NewLine)
        }
    }
}
