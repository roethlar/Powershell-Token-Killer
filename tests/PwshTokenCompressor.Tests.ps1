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

    # Contract reconciled 2026-07-09 under the issue-1 plan
    # (.agents/plans/issue-1-mixed-stream-shaping.md): string-bearing mixed
    # streams render as text (the string form of every item) instead of a
    # first-item property table that loses the payload. The original purpose
    # of these tests — never throw, never hit the shape-error fallback —
    # stands unchanged.
    It 'renders a mixed FileInfo + string stream as text without throwing' {
        $mixed = @((Get-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'README.md')), 'done')

        $result = $mixed | Compress-PtcObject

        $result | Should -Match 'README\.md'
        $result | Should -Match 'done'
        $result | Should -Not -Match '^objects:'
        $result | Should -Not -Match 'cannot be found'
    }

    It 'keeps a mixed stream''s payload through Compress-PtcOutput (no shape-error fallback)' {
        $mixed = @((Get-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'README.md')), 'done')

        $result = $mixed | Compress-PtcOutput

        $result | Should -Not -Match '\[ptk:shape ERROR'
        $result | Should -Match 'done'
    }

    It 'labels heterogeneous object streams and appends payload samples' {
        # No strings in the stream, so the generic table still renders - but
        # its columns come from the first item only, so the header must say
        # mixed and samples must carry some per-item payload (issue #1
        # guardrail).
        $mixed = @((Get-Item -LiteralPath (Join-Path $PSScriptRoot '..' 'README.md')), [pscustomobject]@{ A = 1 })

        $result = $mixed | Compress-PtcObject

        $result | Should -Match '^objects: 2 \(mixed:'
        $result | Should -Match 'samples:'
        $result | Should -Match 'README\.md'
        $result | Should -Match 'A=1'
    }

    It 'does not mutate deserialized PSObjects routed to specialized compressors' {
        # i1-2: deserialized objects (remoting, Import-Clixml) are
        # persistently PSObject-wrapped, so a Select-Object -First anywhere
        # in the specialized routes stamps Selected.* into the caller's
        # live TypeNames. One probe per route.
        $probes = @(
            [pscustomobject]@{
                PSTypeName = 'Deserialized.System.IO.FileInfo'
                PSIsContainer = $false; Name = 'a.txt'; Length = 5
                LastWriteTime = Get-Date; FullName = 'C:\a.txt'
            }
            [pscustomobject]@{
                PSTypeName = 'Deserialized.Microsoft.PowerShell.Commands.MatchInfo'
                LineNumber = 3; Path = 'C:\x.txt'; Line = 'hit'
            }
            [pscustomobject]@{
                PSTypeName = 'Deserialized.System.Diagnostics.Process'
                Id = 1; ProcessName = 'x'; CPU = 1.0; WorkingSet64 = 1024
            }
            [pscustomobject]@{
                PSTypeName = 'Deserialized.System.ServiceProcess.ServiceController'
                Status = 'Running'; Name = 'svc'; DisplayName = 'A Service'
            }
        )

        foreach ($probe in $probes) {
            $expected = $probe.PSObject.TypeNames[0]
            $null = @($probe) | Compress-PtcObject
            $probe.PSObject.TypeNames[0] | Should -BeExactly $expected
        }
    }

    It 'emits no rows when MaxItems is zero (bound, not wraparound)' {
        # i1-3: an index slice of [0..-1] wraps to first+last in PowerShell;
        # -MaxItems 0 must keep Select-Object -First 0 semantics.
        $result = 1..5 | Compress-PtcObject -MaxItems 0

        $result | Should -Match '\+5 more'
        # \r? because the joined lines are CRLF on Windows and (?m)$ does
        # not match before \r - without it these asserts are vacuous.
        $result | Should -Not -Match '(?m)^1\r?$'
        $result | Should -Not -Match '(?m)^5\r?$'
    }

    It 'bounds the mixed-type header to a few names' {
        # i1-1: unique type names are unbounded input; the header must not
        # grow with them.
        $items = 1..5 | ForEach-Object { [pscustomobject]@{ PSTypeName = "PtcTest.Type$_"; V = $_ } }

        $result = $items | Compress-PtcObject

        $result | Should -Match 'mixed: PtcTest\.Type1, PtcTest\.Type2, PtcTest\.Type3, \+2 more\)'
        $result | Should -Not -Match 'PtcTest\.Type4'
        # Piping objects through Select-Object stamps 'Selected.*' into their
        # LIVE TypeNames - the header must name real types, and the caller's
        # objects must come back unmutated.
        $result | Should -Not -Match 'Selected\.'
        $items[0].PSObject.TypeNames[0] | Should -BeExactly 'PtcTest.Type1'
    }

    It 'keeps the payload of a mixed string/MatchInfo stream (issue #1 repro shape)' {
        # The live repro: separator strings mixed with Select-String output
        # rendered a Length-only table; the string form of a MatchInfo is
        # path:line:content, so text rendering keeps the answer.
        $match = 'needle in line two' | Select-String -Pattern 'needle'
        $result = @('---marker---', $match) | Compress-PtcOutput

        $result | Should -Match '---marker---'
        $result | Should -Match 'needle in line two'
        $result | Should -Not -Match '^objects:'
        $result | Should -Not -Match 'Length'
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
        # gci is an alias on every platform; resolution happens in the
        # runspace, not by name shape.
        Resolve-PtcInvokeScript -Script 'gci' | Should -BeExactly 'gci'
        Resolve-PtcInvokeScript -Script 'Get-ChildItem -Force' | Should -BeExactly 'Get-ChildItem -Force'
    }

    It 'routes ls per platform: alias on Windows, native command elsewhere' {
        # Owner-ratified 2026-07-04: ls IS the shell command where a native
        # one exists (Get-ChildItem is the PowerShell way). On Unix, ls
        # resolves to the native Application and routes to rtk; on Windows
        # there is no native ls - it is the Get-ChildItem alias and stays on
        # the PowerShell path (rtk ls fails there - slice-0 probe).
        if ($IsWindows) {
            Resolve-PtcInvokeScript -Script 'ls' | Should -BeExactly 'ls'
        }
        else {
            Resolve-PtcInvokeScript -Script 'ls' |
                Should -BeExactly ("& '{0}' ls" -f $script:fakeRtk)
        }
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

    It 'denies a shell tool call with harness-neutral guidance naming ptk_invoke' {
        $out = '{"tool_name":"Bash","tool_input":{"command":"git status"}}' |
            pwsh -NoProfile -File $script:hookScript
        $LASTEXITCODE | Should -Be 0

        $decision = ($out | ConvertFrom-Json).hookSpecificOutput
        $decision.permissionDecision | Should -BeExactly 'deny'
        $decision.permissionDecisionReason | Should -Match 'ptk_invoke MCP tool'
        # Three prefix schemes exist for the same tool across harnesses
        # (docs/harness-support.md); the guidance must not pin Claude's.
        $decision.permissionDecisionReason | Should -Not -Match 'mcp__ptk'
        $decision.permissionDecisionReason | Should -Match 'PTK_DIRECT'
    }

    It 'appends down-server guidance when no ptk server process is running' {
        $env:PTK_HOOK_LIVENESS = 'down'
        try {
            $out = '{"tool_name":"Bash","tool_input":{"command":"git status"}}' |
                pwsh -NoProfile -File $script:hookScript
        }
        finally { Remove-Item env:PTK_HOOK_LIVENESS -ErrorAction SilentlyContinue }

        $decision = ($out | ConvertFrom-Json).hookSpecificOutput
        $decision.permissionDecision | Should -BeExactly 'deny'
        $decision.permissionDecisionReason | Should -Match 'no ptk server process'
    }

    It 'omits the down-server note when a server process is running' {
        $env:PTK_HOOK_LIVENESS = 'up'
        try {
            $out = '{"tool_name":"Bash","tool_input":{"command":"git status"}}' |
                pwsh -NoProfile -File $script:hookScript
        }
        finally { Remove-Item env:PTK_HOOK_LIVENESS -ErrorAction SilentlyContinue }

        $reason = ($out | ConvertFrom-Json).hookSpecificOutput.permissionDecisionReason
        $reason | Should -Not -Match 'no ptk server process'
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
        BeforeAll {
            # Payload-gate seam: a home dir containing bin/ counts as an
            # installed payload regardless of this machine's real ~/.ptk.
            $script:fakeHome = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-home-{0}" -f ([guid]::NewGuid()))
            New-Item -ItemType Directory -Path (Join-Path $script:fakeHome 'bin') -Force | Out-Null
            # CLI-gate seam (mhi-9): the claude leg now installs its hook
            # only when a `claude` command resolves; a shim on PATH keeps
            # these tests hermetic on machines without the real CLI.
            $script:claudeShimDir = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-fakeclaude-{0}" -f ([guid]::NewGuid()))
            New-Item -ItemType Directory -Path $script:claudeShimDir -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $script:claudeShimDir 'claude.ps1') -Value 'exit 0'
            $script:savedInitPath = $env:PATH
            $env:PATH = $script:claudeShimDir + [System.IO.Path]::PathSeparator + $env:PATH
        }
        AfterAll {
            $env:PATH = $script:savedInitPath
            Remove-Item -LiteralPath $script:fakeHome -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $script:claudeShimDir -Recurse -Force -ErrorAction SilentlyContinue
        }
        BeforeEach {
            $script:settings = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-init-{0}.json" -f ([guid]::NewGuid()))
            $script:nudgeFile = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-nudge-{0}.md" -f ([guid]::NewGuid()))
        }
        AfterEach {
            Remove-Item -LiteralPath $script:settings -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $script:nudgeFile -Force -ErrorAction SilentlyContinue
        }

        It 'installs one Bash|PowerShell entry into fresh settings, idempotently' {
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-Null
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-Null

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

            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-Null
            $config = Get-Content -LiteralPath $script:settings -Raw | ConvertFrom-Json
            $config.model | Should -BeExactly 'sonnet'
            @($config.hooks.PreToolUse).Count | Should -Be 2
            @($config.hooks.PreToolUse | Where-Object { $_.hooks[0].command -eq 'rtk hook claude' }).Count | Should -Be 1

            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome -Uninstall | Out-Null
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

            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome -Uninstall | Out-Null

            $config = Get-Content -LiteralPath $script:settings -Raw | ConvertFrom-Json
            @($config.hooks.PreToolUse).Count | Should -Be 1
            @($config.hooks.PreToolUse[0].hooks).Count | Should -Be 1
            $config.hooks.PreToolUse[0].hooks[0].command | Should -BeExactly 'rtk hook claude'
        }

        It 'writes nothing under -DryRun' {
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome -DryRun | Out-Null
            Test-Path -LiteralPath $script:settings | Should -BeFalse
        }

        It 'claude leg skips the hook but keeps the nudge when the claude CLI is absent' {
            # mhi-9 guard: the hook steers shell calls at MCP ptk, which only
            # answers where Claude Code can see the server; without the
            # claude CLI that user-scope registration cannot exist, so a hook
            # would deny every call toward an invisible tool (mhi-6). The leg
            # must skip ONLY the hook and still write the conditionally
            # worded guidance block (grok's single layer).
            $pwshExe = (Get-Command pwsh).Source
            $oldPath = $env:PATH
            try {
                $env:PATH = [System.IO.Path]::GetTempPath() # no claude, no shim
                $out = & $pwshExe -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome 2>&1 | Out-String
            }
            finally {
                $env:PATH = $oldPath
            }

            $LASTEXITCODE | Should -Not -Be 0
            $out | Should -Match 'claude CLI not found'
            # No hook write at all - not even an empty settings file.
            Test-Path -LiteralPath $script:settings | Should -BeFalse
            # The nudge stays.
            Get-Content -LiteralPath $script:nudgeFile -Raw | Should -Match 'ptk-guidance'
        }

        It 'registers the installed hook copy when the payload carries it' {
            # issue #2: checkouts move and strand registrations; the
            # installed payload is the stable target.
            $homeWithScripts = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-home2-{0}" -f ([guid]::NewGuid()))
            New-Item -ItemType Directory -Path (Join-Path $homeWithScripts 'bin') -Force | Out-Null
            New-Item -ItemType Directory -Path (Join-Path $homeWithScripts 'scripts') -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $homeWithScripts 'scripts' 'ptk-hook.ps1') -Value '# installed copy'
            try {
                pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $homeWithScripts | Out-Null

                $config = Get-Content -LiteralPath $script:settings -Raw | ConvertFrom-Json
                $config.hooks.PreToolUse[0].hooks[0].command | Should -Match ([regex]::Escape($homeWithScripts))
            }
            finally {
                Remove-Item -LiteralPath $homeWithScripts -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'heals a stale registration and names the missing target' {
            # issue #2: a ptk entry whose -File target no longer exists fails
            # open silently; install must replace it and say what was stale.
            Set-Content -LiteralPath $script:settings -Value (@{
                hooks = @{
                    PreToolUse = @(
                        @{ matcher = 'Bash|PowerShell'; hooks = @(@{ type = 'command'; command = 'pwsh -NoProfile -File "C:\gone\src\ptk-hook.ps1"' }) }
                    )
                }
            } | ConvertTo-Json -Depth 8)

            $out = pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-String
            $out | Should -Match 'STALE'
            $out | Should -Match ([regex]::Escape('C:\gone\src\ptk-hook.ps1'))

            $config = Get-Content -LiteralPath $script:settings -Raw | ConvertFrom-Json
            @($config.hooks.PreToolUse).Count | Should -Be 1
            $config.hooks.PreToolUse[0].hooks[0].command | Should -Not -Match 'gone'
        }

        It 'treats a directory at the registered target as stale' {
            # i2-3: pwsh -File <directory> fails open exactly like a missing
            # file; a bare Test-Path would bless it.
            $dirTarget = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-dir-{0}" -f ([guid]::NewGuid())) 'ptk-hook.ps1'
            New-Item -ItemType Directory -Path $dirTarget -Force | Out-Null
            try {
                Set-Content -LiteralPath $script:settings -Value (@{
                    hooks = @{
                        PreToolUse = @(
                            @{ matcher = 'Bash|PowerShell'; hooks = @(@{ type = 'command'; command = ('pwsh -NoProfile -File "{0}"' -f $dirTarget) }) }
                        )
                    }
                } | ConvertTo-Json -Depth 8)

                $out = pwsh -NoProfile -File $script:initScript -Show -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-String
                $out | Should -Match 'STALE'
            }
            finally {
                Remove-Item -LiteralPath (Split-Path -Parent $dirTarget) -Recurse -Force -ErrorAction SilentlyContinue
            }
        }

        It 'flags a stale registered target under -Show' {
            Set-Content -LiteralPath $script:settings -Value (@{
                hooks = @{
                    PreToolUse = @(
                        @{ matcher = 'Bash|PowerShell'; hooks = @(@{ type = 'command'; command = 'pwsh -NoProfile -File "C:\gone\src\ptk-hook.ps1"' }) }
                    )
                }
            } | ConvertTo-Json -Depth 8)

            $out = pwsh -NoProfile -File $script:initScript -Show -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-String
            $out | Should -Match 'STALE'
        }

        It 'refuses the hook install when no installed payload exists' {
            # Enforce only where the steered-to tool can answer: without
            # ~/.ptk the hook would deny every shell call toward nothing.
            $emptyHome = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-nohome-{0}" -f ([guid]::NewGuid()))
            $out = pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $emptyHome 2>&1 | Out-String

            $LASTEXITCODE | Should -Be 1
            $out | Should -Match 'dev-install'
            Test-Path -LiteralPath $script:settings | Should -BeFalse
        }

        It 'installs and removes the nudge block, preserving user content' {
            Set-Content -LiteralPath $script:nudgeFile -Value "# my file`n`nkeep me"
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-Null
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-Null

            $text = Get-Content -LiteralPath $script:nudgeFile -Raw
            ([regex]::Matches($text, [regex]::Escape('<!-- ptk-guidance -->'))).Count | Should -Be 1
            $text | Should -Match 'keep me'
            $text | Should -Match 'ptk_invoke'

            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome -Uninstall | Out-Null
            $text = Get-Content -LiteralPath $script:nudgeFile -Raw
            $text | Should -Not -Match 'ptk-guidance'
            $text | Should -Match 'keep me'
        }

        It 'round-trips the nudge file byte-exactly, preserving user whitespace' {
            # mhi-7: leading whitespace is user content (e.g. an indented
            # Markdown code block at the top of the file) and the file may
            # lack a trailing newline; install/uninstall must not eat either.
            $original = "    indented code`r`n`r`nkeep me"
            Set-Content -LiteralPath $script:nudgeFile -Value $original -NoNewline
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-Null
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome -Uninstall | Out-Null

            Get-Content -LiteralPath $script:nudgeFile -Raw | Should -BeExactly $original
        }

        It 'installs the nudge block by default - no opt-in flag' {
            # Owner amendment 2026-07-09: a bare run produces the full
            # correct state; flags nobody remembers do not gate layers.
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-Null

            Get-Content -LiteralPath $script:nudgeFile -Raw | Should -Match 'ptk-guidance'
        }

        It 'does not create the settings file when uninstalling with nothing installed' {
            pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome -Uninstall | Out-Null
            $LASTEXITCODE | Should -Be 0
            Test-Path -LiteralPath $script:settings | Should -BeFalse
        }

        It 'reports per-leg status under -Show without writing' {
            $out = pwsh -NoProfile -File $script:initScript -Show -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-String
            $out | Should -Match '\[claude\] ptk hook: not installed'
            $out | Should -Match 'nudge block: not installed'
            Test-Path -LiteralPath $script:settings | Should -BeFalse
        }

        It 'announces the user-level default flip on a global install' {
            # -DryRun writes nothing, so exercising the default (no
            # -SettingsPath) target is safe on any machine.
            $out = pwsh -NoProfile -File $script:initScript -Agent claude -DryRun -PtkHome $script:fakeHome | Out-String
            $out | Should -Match 'USER-LEVEL by default'
        }

        It 'warns about content tracking under -Local' {
            $out = pwsh -NoProfile -File $script:initScript -Local -DryRun -PtkHome $script:fakeHome 2>&1 | Out-String
            $out | Should -Match 'by content'
        }

        It 'agy leg writes a hook-less plugin carrying the registration' {
            $root = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-agy-{0}" -f ([guid]::NewGuid()))
            $cfg = Join-Path $root 'mcp_config.json'   # absent: no global registration
            $homeWithBin = Join-Path $root 'home'
            New-Item -ItemType Directory -Path (Join-Path $homeWithBin 'bin') -Force | Out-Null
            $binName = $IsWindows ? 'PtkMcpServer.exe' : 'PtkMcpServer'
            Set-Content -LiteralPath (Join-Path $homeWithBin 'bin' $binName) -Value 'stub'
            try {
                pwsh -NoProfile -File $script:initScript -Agent agy -AgyPluginRoot $root -AgyConfigPath $cfg -PtkHome $homeWithBin | Out-Null
                $LASTEXITCODE | Should -Be 0

                $plugin = Join-Path $root 'ptk'
                Test-Path (Join-Path $plugin 'plugin.json') | Should -BeTrue
                Test-Path (Join-Path $plugin 'rules' 'ptk.md') | Should -BeTrue
                Get-Content (Join-Path $plugin 'rules' 'ptk.md') -Raw | Should -Match 'ptk_invoke'
                Get-Content (Join-Path $plugin 'mcp_config.json') -Raw | Should -Match ([regex]::Escape($binName))
                # Enforcement is deferred: no hooks.json may ship (plan
                # amendment - the verify-once bar is unmet for agy).
                Test-Path (Join-Path $plugin 'hooks.json') | Should -BeFalse

                pwsh -NoProfile -File $script:initScript -Agent agy -AgyPluginRoot $root -AgyConfigPath $cfg -PtkHome $homeWithBin -Uninstall | Out-Null
                Test-Path $plugin | Should -BeFalse
            }
            finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'agy leg leaves an existing global registration as-is (plugin carries rules only)' {
            $root = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-agy-{0}" -f ([guid]::NewGuid()))
            New-Item -ItemType Directory -Path $root -Force | Out-Null
            $cfg = Join-Path $root 'mcp_config.json'
            Set-Content -LiteralPath $cfg -Value '{"mcpServers":{"ptk":{"command":"x","args":[]}}}'
            try {
                $out = pwsh -NoProfile -File $script:initScript -Agent agy -AgyPluginRoot $root -AgyConfigPath $cfg -PtkHome $script:fakeHome | Out-String
                $LASTEXITCODE | Should -Be 0
                $out | Should -Match 'left as-is'
                Test-Path (Join-Path $root 'ptk' 'rules' 'ptk.md') | Should -BeTrue
                Test-Path (Join-Path $root 'ptk' 'mcp_config.json') | Should -BeFalse
                # The global config is untouched.
                Get-Content -LiteralPath $cfg -Raw | Should -Match '"command":\s*"x"'
            }
            finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'agy leg removes a pre-existing hooks.json (no hook is shipped)' {
            # mhi-11 guard: a stale hooks.json from an earlier attempt would
            # stay active - an unverified deny hook blocking agy commands -
            # while install reports that no hook ships. Install must enforce
            # the documented no-hook end-state; -DryRun must disclose the
            # pending removal without performing it.
            $root = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-agy-{0}" -f ([guid]::NewGuid()))
            New-Item -ItemType Directory -Path (Join-Path $root 'ptk') -Force | Out-Null
            $cfg = Join-Path $root 'mcp_config.json'
            Set-Content -LiteralPath $cfg -Value '{"mcpServers":{"ptk":{"command":"x","args":[]}}}'
            $hooks = Join-Path $root 'ptk' 'hooks.json'
            Set-Content -LiteralPath $hooks -Value '{"hooks":{"BeforeTool":[{"deny":".*"}]}}'
            try {
                $dry = pwsh -NoProfile -File $script:initScript -Agent agy -DryRun -AgyPluginRoot $root -AgyConfigPath $cfg -PtkHome $script:fakeHome | Out-String
                $LASTEXITCODE | Should -Be 0
                $dry | Should -Match 'would remove the pre-existing hooks.json'
                Test-Path -LiteralPath $hooks | Should -BeTrue

                $out = pwsh -NoProfile -File $script:initScript -Agent agy -AgyPluginRoot $root -AgyConfigPath $cfg -PtkHome $script:fakeHome | Out-String
                $LASTEXITCODE | Should -Be 0
                $out | Should -Match 'pre-existing hooks\.json removed'
                Test-Path -LiteralPath $hooks | Should -BeFalse
            }
            finally { Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue }
        }

        It 'grok leg -DryRun snapshots the registration command, writing nothing' {
            $toml = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-grok-{0}.toml" -f ([guid]::NewGuid()))
            $out = pwsh -NoProfile -File $script:initScript -Agent grok -DryRun -NudgePath $script:nudgeFile -PtkHome $script:fakeHome -GrokConfigPath $toml | Out-String
            $LASTEXITCODE | Should -Be 0
            $out | Should -Match 'grok mcp add -s user ptk '
            Test-Path -LiteralPath $script:nudgeFile | Should -BeFalse
        }

        It 'grok leg leaves an existing registration as-is and still writes the nudge' {
            # The toml presence check short-circuits before any CLI call, so
            # this runs identically on machines with or without a grok CLI.
            $toml = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-grok-{0}.toml" -f ([guid]::NewGuid()))
            Set-Content -LiteralPath $toml -Value "[mcp_servers.ptk]`ncommand = 'x'`n"
            try {
                $out = pwsh -NoProfile -File $script:initScript -Agent grok -NudgePath $script:nudgeFile -PtkHome $script:fakeHome -GrokConfigPath $toml | Out-String
                $LASTEXITCODE | Should -Be 0
                $out | Should -Match 'already registered - left as is'
                Get-Content -LiteralPath $script:nudgeFile -Raw | Should -Match 'ptk-guidance'
            }
            finally { Remove-Item -LiteralPath $toml -Force -ErrorAction SilentlyContinue }
        }

        It 'grok leg -Uninstall -DryRun names the removal and leaves the shared nudge alone' {
            Set-Content -LiteralPath $script:nudgeFile -Value "x`n<!-- ptk-guidance -->b<!-- /ptk-guidance -->`n"
            $out = pwsh -NoProfile -File $script:initScript -Agent grok -Uninstall -DryRun -NudgePath $script:nudgeFile -PtkHome $script:fakeHome -GrokConfigPath 'C:\nonexistent.toml' | Out-String
            $LASTEXITCODE | Should -Be 0
            $out | Should -Match 'grok mcp remove -s user ptk'
            Get-Content -LiteralPath $script:nudgeFile -Raw | Should -Match 'ptk-guidance'
        }

        It 'codex leg -DryRun snapshots the registration command and nudge, writing nothing' {
            # The codex leg is a thin CLI wrapper; live registration paths are
            # deliberately untested (they would mutate the real ~/.codex
            # config on a dev box). -DryRun is fully offline.
            $out = pwsh -NoProfile -File $script:initScript -Agent codex -DryRun -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-String
            $LASTEXITCODE | Should -Be 0
            $out | Should -Match 'codex mcp add ptk -- '
            $out | Should -Match ([regex]::Escape($script:fakeHome))
            $out | Should -Match 'ptk-guidance'
            Test-Path -LiteralPath $script:nudgeFile | Should -BeFalse
        }

        It 'codex leg -Uninstall -DryRun names the removal without running it' {
            $out = pwsh -NoProfile -File $script:initScript -Agent codex -Uninstall -DryRun -NudgePath $script:nudgeFile -PtkHome $script:fakeHome | Out-String
            $LASTEXITCODE | Should -Be 0
            $out | Should -Match 'codex mcp remove ptk'
        }

        It 'bare -Uninstall reverses every leg, not just detected ones' {
            # mhi-10 guard: a harness CLI that left PATH after install must
            # not strand its ptk state - uninstall scope is the supported
            # set, independent of live CLI detection. PATH is gutted so
            # nothing is detected; -DryRun keeps the run read-only (every
            # leg's dry uninstall names its action without performing it,
            # and both nudge helpers early-return under -DryRun).
            $pwshExe = (Get-Command pwsh).Source
            $oldPath = $env:PATH
            try {
                $env:PATH = [System.IO.Path]::GetTempPath()
                $out = & $pwshExe -NoProfile -File $script:initScript -Uninstall -DryRun 2>&1 | Out-String
            }
            finally {
                $env:PATH = $oldPath
            }

            $LASTEXITCODE | Should -Be 0
            $out | Should -Match 'codex mcp remove ptk'
            $out | Should -Match 'grok mcp remove -s user ptk'
            $out | Should -Match ([regex]::Escape((Join-Path 'plugins' 'ptk')))
        }

        It 'leaves an existing codex registration as-is even without an installed payload' {
            # mhi-8: the payload gate guards only the add the leg would
            # perform; a machine whose codex already has a (possibly custom)
            # ptk entry must take the leave-as-is path, not fail. Fake codex
            # shim: answers `mcp get ptk` with exit 0.
            $fakeBin = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-fakecodex-{0}" -f ([guid]::NewGuid()))
            New-Item -ItemType Directory -Path $fakeBin -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $fakeBin 'codex.ps1') -Value @'
if (($args -join ' ') -eq 'mcp get ptk') { exit 0 }
exit 1
'@
            $emptyHome = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-nohome-{0}" -f ([guid]::NewGuid()))
            $oldPath = $env:PATH
            try {
                $env:PATH = $fakeBin + [System.IO.Path]::PathSeparator + $env:PATH
                $out = pwsh -NoProfile -File $script:initScript -Agent codex -NudgePath $script:nudgeFile -PtkHome $emptyHome 2>&1 | Out-String
            }
            finally {
                $env:PATH = $oldPath
                Remove-Item -LiteralPath $fakeBin -Recurse -Force -ErrorAction SilentlyContinue
            }

            $LASTEXITCODE | Should -Be 0
            $out | Should -Match 'already registered - left as is'
        }

        It 'codex leg uninstall sweeps orphaned tool-approval subtables (mhi-12)' {
            # mhi-12 guard: codex writes [mcp_servers.ptk.tools.*] approval
            # subtables when the user approves ptk tools; `codex mcp remove
            # ptk` strips only the base table. Orphaned, those subtables make
            # the whole config unloadable ("invalid transport") - the codex
            # CLI bricks itself and cannot self-repair, since every command
            # starts by loading the config. The leg must sweep them, and
            # only them. Fake codex shim keeps the real ~/.codex out of the
            # run (precedent: the mhi-8 leave-as-is test).
            $fakeBin = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-fakecodex-{0}" -f ([guid]::NewGuid()))
            New-Item -ItemType Directory -Path $fakeBin -Force | Out-Null
            Set-Content -LiteralPath (Join-Path $fakeBin 'codex.ps1') -Value @'
if (($args -join ' ') -eq 'mcp remove ptk') { exit 0 }
exit 1
'@
            $toml = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-codexcfg-{0}.toml" -f ([guid]::NewGuid()))
            Set-Content -LiteralPath $toml -Value @'
model = "keep-me"

[mcp_servers.other]
command = "/bin/echo"

[mcp_servers.ptk.tools.ptk_ping]
approval_mode = "approve"

[mcp_servers.ptk.tools.ptk_invoke]
approval_mode = "approve"

[hooks.state]
'@
            $oldPath = $env:PATH
            try {
                # Dry run: discloses the pending sweep, writes nothing.
                $out = pwsh -NoProfile -File $script:initScript -Agent codex -Uninstall -DryRun -CodexConfigPath $toml -NudgePath $script:nudgeFile -PtkHome $script:fakeHome 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0
                $out | Should -Match ([regex]::Escape('would sweep orphaned [mcp_servers.ptk.*] tables'))
                Get-Content -LiteralPath $toml -Raw | Should -Match 'mcp_servers\.ptk\.tools'

                # Real uninstall: CLI remove runs (shim), then the sweep
                # removes exactly the ptk-scoped subtables.
                $env:PATH = $fakeBin + [System.IO.Path]::PathSeparator + $env:PATH
                $out = pwsh -NoProfile -File $script:initScript -Agent codex -Uninstall -CodexConfigPath $toml -NudgePath $script:nudgeFile -PtkHome $script:fakeHome 2>&1 | Out-String
                $LASTEXITCODE | Should -Be 0
                $out | Should -Match 'registration removed'
                $out | Should -Match ([regex]::Escape('swept orphaned [mcp_servers.ptk.*] tables'))
                $raw = Get-Content -LiteralPath $toml -Raw
                $raw | Should -Not -Match 'mcp_servers\.ptk'
                $raw | Should -Match '\[mcp_servers\.other\]'
                $raw | Should -Match 'model = "keep-me"'
                $raw | Should -Match '\[hooks\.state\]'
            }
            finally {
                $env:PATH = $oldPath
                Remove-Item -LiteralPath $fakeBin -Recurse -Force -ErrorAction SilentlyContinue
                Remove-Item -LiteralPath $toml -Force -ErrorAction SilentlyContinue
            }
        }

        It 'codex leg uninstall warns about a stale registration when the CLI left PATH (mhi-10)' {
            # mhi-10 re-grade completion: with the codex CLI gone, the leg
            # printed "no registration to remove" without reading the config
            # - a stale [mcp_servers.ptk] entry survived behind a false
            # report. Grok-leg parity: detect it and name the manual removal.
            # Warn, not edit: the base entry is valid config a reinstalled
            # CLI can manage; only orphaned subtables get swept (mhi-12).
            $toml = Join-Path ([System.IO.Path]::GetTempPath()) ("ptk-codexcfg-{0}.toml" -f ([guid]::NewGuid()))
            Set-Content -LiteralPath $toml -Value @'
[mcp_servers.ptk]
command = "/Users/nobody/.ptk/bin/PtkMcpServer"
'@
            $pwshExe = (Get-Command pwsh).Source
            $oldPath = $env:PATH
            try {
                $env:PATH = [System.IO.Path]::GetTempPath()
                $out = & $pwshExe -NoProfile -File $script:initScript -Agent codex -Uninstall -CodexConfigPath $toml -NudgePath $script:nudgeFile -PtkHome $script:fakeHome 2>&1 | Out-String
            }
            finally {
                $env:PATH = $oldPath
            }

            $LASTEXITCODE | Should -Be 0
            $out | Should -Match 'remove the \[mcp_servers\.ptk\] entry'
            $out | Should -Match 'manually'
            Get-Content -LiteralPath $toml -Raw | Should -Match '\[mcp_servers\.ptk\]'
            Remove-Item -LiteralPath $toml -Force -ErrorAction SilentlyContinue
        }

        It 'rejects -SettingsPath without -NudgePath (seam runs stay sandboxed)' {
            # The nudge is a standard layer; a redirected settings target
            # with a defaulted nudge target would leak writes onto the real
            # user guidance file (this bit the suite once).
            $out = pwsh -NoProfile -File $script:initScript -SettingsPath $script:settings -PtkHome $script:fakeHome 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            $out | Should -Match 'must pass both'
        }

        It 'rejects -SettingsPath with a non-claude leg' {
            $out = pwsh -NoProfile -File $script:initScript -Agent codex -SettingsPath $script:settings -NudgePath $script:nudgeFile -PtkHome $script:fakeHome 2>&1 | Out-String
            $LASTEXITCODE | Should -Not -Be 0
            # Specifically the resolution-rule rejection, not a downstream
            # leg failure that happens to exit nonzero.
            $out | Should -Match 'claude-leg targets'
        }
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

    # Reconciled 2026-07-08 under the greenfield-design adoption decision
    # (.agents/decisions.md): the Phase 2 never-truncate contract is amended
    # to bounded-with-labeled-elision. Under the bounds, passthrough stays
    # complete and unaltered; over them, a labeled head+tail window applies.
    It 'passes plain text through complete and unaltered when under the bounds' {
        $lines = 1..40 | ForEach-Object { "line $_" }
        $result = $lines | Compress-PtcOutput

        $result | Should -BeExactly ($lines -join [Environment]::NewLine)
        $result | Should -Match 'line 40'
        $result | Should -Not -Match 'elided'
    }

    It 'bounds pathological line counts with a labeled head+tail window' {
        $lines = 1..1000 | ForEach-Object { "line $_" }
        $result = $lines | Compress-PtcOutput

        $result | Should -Match '\[600 lines elided - use raw=true for everything\]'
        $result | Should -Match 'line 1\b'
        $result | Should -Match 'line 1000'
        $result | Should -Not -Match 'line 500\b'
        @($result -split "`r?`n").Count | Should -Be 401
    }

    It 'bounds pathological character counts even at few lines' {
        $big = ('x' * 20000)
        $result = "$big-A", "$big-B", "$big-C" | Compress-PtcOutput

        $result | Should -Match '\[\d+ chars elided - use raw=true for everything\]'
        $result.Length | Should -BeLessThan 42000
        $result | Should -Match '^x'
        $result | Should -Match '-C$'
    }

    It 'keeps line elision explicit when the char bound cuts the line marker' {
        $lines = 1..1000 | ForEach-Object { "line $_ " + ('y' * 400) }
        $result = $lines | Compress-PtcOutput

        $result | Should -Match '\[\d+ lines and \d+ chars elided - use raw=true for everything\]'
    }

    It 'bounds the labeled log-leg fallback too' {
        $env:PTK_RTK_PATH = Join-Path ([System.IO.Path]::GetTempPath()) 'no-such-rtk-binary.exe'
        $lines = 1..500 | ForEach-Object { "2026-07-08 10:00:0$($_ % 10) INFO worker: step $_" }
        $result = $lines | Compress-PtcOutput

        $result | Should -Match '\[ptk:log rtk not found'
        $result | Should -Match 'lines elided - use raw=true for everything'
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

    It 'routes mixed string/object output down the object path, keeping the string payload' {
        # Reconciled 2026-07-09 (issue-1 plan): string-bearing mixes render
        # as text - the string line survives, the object contributes its
        # string form.
        $result = 'text', [pscustomobject]@{ A = 1 } | Compress-PtcOutput

        $result | Should -Match 'text'
        $result | Should -Match 'A=1'
        $result | Should -Not -Match '^objects:'
    }

    It 'strips ANSI sequences from plain text output' {
        $esc = [char]27
        $lines = @(
            "$esc[32mVITE v5.0.0$esc[0m  ready in 300 ms",
            "$esc[36m  -> Local: http://localhost:5173/$esc[0m"
        )
        $result = $lines | Compress-PtcOutput

        $result | Should -Not -Match ([regex]::Escape([string]$esc))
        $result | Should -Match 'VITE v5\.0\.0  ready in 300 ms'
    }

    It 'classifies ANSI-colored timestamped logs as log-shaped after stripping' {
        # Without the ingest strip, the ANSI prefix defeats the line-start
        # timestamp anchor and no other heuristic applies to these lines (no
        # [LEVEL] brackets, no colon after the level word), so the text would
        # pass through unshaped instead of reaching the labeled log leg.
        $esc = [char]27
        $logLines = @(1..8 | ForEach-Object {
            "$esc[32m2026-07-08 12:00:0$($_ % 10) INFO worker started step $_$esc[0m"
        })
        $env:PTK_RTK_PATH = Join-Path ([System.IO.Path]::GetTempPath()) 'no-such-rtk-binary.exe'
        $result = $logLines | Compress-PtcOutput

        $result | Should -Match '^\[ptk:log'
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

Describe 'Get-PtcShellDialectFinding' {
    # Detection list and false-positive set are frozen in
    # .agents/plans/shell-dialect.md "Slice 0 results" (probed 2026-07-09).
    Context 'names every construct on the frozen detection list' {
        It 'flags <expected> for: <script>' -TestCases @(
            @{ script = "cat <<EOF`nhello`nEOF"; expected = 'heredoc' }
            @{ script = "cat <<'EOF'`nhello`nEOF"; expected = 'heredoc' }
            @{ script = 'if [ -f x.txt ]; then echo hi; fi'; expected = 'if/then' }
            @{ script = '[ -f x.txt ]'; expected = 'test expression' }
            @{ script = '[[ -f x.txt ]]'; expected = 'test expression' }
            @{ script = 'for i in 1 2 3; do echo $i; done'; expected = 'do/done' }
            @{ script = 'greet() { echo hi; }'; expected = 'function definition' }
            @{ script = 'diff <(sort a.txt) <(sort b.txt)'; expected = 'process substitution' }
            @{ script = 'export FOO=1'; expected = 'export' }
            @{ script = 'FOO=bar echo hi'; expected = 'environment-variable prefix' }
            @{ script = 'local x=1'; expected = 'local' }
            @{ script = 'source ./env.sh'; expected = 'source' }
            @{ script = 'set -e'; expected = 'shell options' }
            @{ script = 'set -euo pipefail'; expected = 'shell options' }
            @{ script = 'echo `date`'; expected = 'backticks' }
            @{ script = 'echo `date +%s`'; expected = 'backticks' }
        ) {
            Get-PtcShellDialectFinding -Script $script | Should -Match $expected
        }
    }

    Context 'the frozen false-positive set never trips' {
        It 'stays silent for: <script>' -TestCases @(
            @{ script = 'echo hi && echo there' }
            @{ script = 'Get-Date | Out-String' }
            @{ script = 'node --version 2>/dev/null' }
            @{ script = 'echo $(1+1)' }
            @{ script = 'echo ''literal $x''' }
            @{ script = 'bash -lc ''echo hi''' }
            @{ script = 'bash -lc ''local x=1; export FOO=1''' }
            @{ script = 'git commit -m ''set -e belongs in the message''' }
            @{ script = 'Set-Variable -Name x -Value 1' }
            @{ script = 'set' }
            @{ script = 'dotnet test --filter Name=Foo' }
        ) {
            Get-PtcShellDialectFinding -Script $script | Should -BeNullOrEmpty
        }
    }

    Context 'session-shadowed names never trip (sd1-1)' {
        It 'stays silent when the bash name is shadowed by a real session command' {
            function global:export { param($Assignment) "ran:$Assignment" }
            try {
                Get-PtcShellDialectFinding -Script 'export X=1' | Should -BeNullOrEmpty
            } finally {
                # NOT function:global:export - the scope-qualified provider
                # path does not remove the item (probed); the unqualified
                # path resolves to the global function and does.
                Remove-Item function:export -Force
            }
            # Guard sanity: with the shadow gone the finding returns.
            Get-PtcShellDialectFinding -Script 'export X=1' | Should -Match 'export'
        }
    }

    Context 'token-aware backtick handling (legitimate escapes never trip)' {
        It 'ignores a lone escape backtick' {
            Get-PtcShellDialectFinding -Script 'Write-Host `n' | Should -BeNullOrEmpty
        }

        It 'ignores adjacent lone escapes (`n `t) - the boundary check holds' {
            Get-PtcShellDialectFinding -Script 'Write-Host `n `t' | Should -BeNullOrEmpty
        }

        It 'ignores backticks inside quoted strings' {
            Get-PtcShellDialectFinding -Script 'echo ''a `date` b''' | Should -BeNullOrEmpty
        }

        It 'ignores escape sequences joined by an escaped space (sd1-2)' {
            # `t escape + escaped space: one legitimate argument whose extent
            # holds interior backticks - the old raw-text tail scan paired
            # them across the element and flagged valid PowerShell.
            Get-PtcShellDialectFinding -Script 'Write-Output `tColumn` Name' | Should -BeNullOrEmpty
        }
    }

    Context 'accepted misses stay misses (frozen OUT decisions)' {
        It 'does not flag a trailing backslash - legal Windows path ending' {
            Get-PtcShellDialectFinding -Script 'Get-ChildItem C:\Temp\' | Should -BeNullOrEmpty
        }

        It 'does not flag bash-style line continuation (accepted miss)' {
            Get-PtcShellDialectFinding -Script "echo a \`necho b" | Should -BeNullOrEmpty
        }
    }
}
