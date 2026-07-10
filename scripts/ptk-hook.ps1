#Requires -Version 7
<#
.SYNOPSIS
Claude Code PreToolUse hook: redirects Bash/PowerShell tool calls to the
ptk_invoke MCP tool, where output is token-compressed and the warm runspace
persists state across calls (unified-shell-routing plan). The guidance names
the tool WITHOUT a harness prefix: the same tool carries a different id per
harness (docs/harness-support.md).

Cross-tool rewrite is impossible in the hook protocol (updatedInput is
same-tool only - slice-0 probe), so the redirect is deny-with-guidance: the
permissionDecisionReason is shown to the model verbatim.

Escape hatch: a command containing PTK_DIRECT (e.g. in a comment) is allowed
through for work that genuinely needs the harness shell - interactive
prompts, TTY-dependent tools, or commands that must run even when the ptk
server is down. When no ptk server process is running on the machine, the
deny guidance says so and points at PTK_DIRECT up front (the deny itself
always stands; liveness shapes wording only).

Installed by scripts/ptk_init.ps1; exits 0 with no output to allow a call.
#>
$ErrorActionPreference = 'Stop'

try {
    $payload = [Console]::In.ReadToEnd() | ConvertFrom-Json
    $command = [string]$payload.tool_input.command
    $cwd = if ($payload.PSObject.Properties['cwd']) { [string]$payload.cwd } else { '' }
} catch {
    # Unparseable input: never block the harness on our own failure.
    exit 0
}

if ([string]::IsNullOrWhiteSpace($command) -or $command -match 'PTK_DIRECT') {
    exit 0
}

# The warm runspace keeps its own current directory across calls, so a
# replayed command with relative paths must re-anchor to this call's cwd.
# Single-quote escaping: an apostrophe in the path must not break (or, if
# crafted, inject into) the suggested prefix the model will run verbatim.
$cwdAdvice = if ($cwd) {
    " The warm runspace keeps its own current directory, so anchor the command: prefix it with: Set-Location '{0}'; " -f $cwd.Replace("'", "''")
} else {
    ' '
}
# Liveness shapes the WORDING only - the deny always stands. With no ptk
# server process on the machine, the model is told up front to take the
# escape hatch instead of discovering an unanswerable tool.
# PTK_HOOK_LIVENESS ('up'/'down') overrides detection - test seam.
$serverUp = switch ($env:PTK_HOOK_LIVENESS) {
    'up' { $true }
    'down' { $false }
    default { [bool](Get-Process -Name PtkMcpServer -ErrorAction SilentlyContinue) }
}

# The dialect line is platform-neutral by design (shell-dialect plan D3):
# the deny is a static string rendered per-box, so "where bash exists"
# carries the condition instead of an install-time branch - bash -lc is
# never advised unconditionally on a box that cannot run it.
$reason =
    'Shell commands run through ptk: call the ptk_invoke MCP tool with "script" set to this same command.' +
    $cwdAdvice +
    'It runs in a persistent warm PowerShell runspace (state and imported modules survive across calls) ' +
    'and output comes back token-compressed. The dialect is PowerShell 7, not bash: translate bash-only ' +
    "syntax, or wrap a bash script whole as bash -lc '...' where bash exists. " +
    'Only if the command genuinely needs this harness shell ' +
    '(interactive/TTY, or ptk is unavailable), re-run it here with PTK_DIRECT in a comment.' +
    ($serverUp ? '' : (' NOTE: no ptk server process is running on this machine right now - if the ' +
        'ptk tools are not available in this session, re-run this command here with PTK_DIRECT now.'))

@{
    hookSpecificOutput = @{
        hookEventName            = 'PreToolUse'
        permissionDecision       = 'deny'
        permissionDecisionReason = $reason
    }
} | ConvertTo-Json -Depth 4 -Compress
