# Local Usage

This page covers the PowerShell module and launcher. For the warm-runspace MCP
server, see [../server/README.md](../server/README.md).

## Requirements

- PowerShell 7.2 or newer for the module manifest.
- Pester when running the PowerShell test suite.
- Optional: `rtk` on `PATH`, or `PTK_RTK_PATH` pointing at an `rtk` binary, for
  log-shaped text compression and MCP native-command routing.

## Loading

```powershell
Import-Module .\src\PwshTokenCompressor.psd1 -Force
```

The module exports the `ptk` alias plus these public functions:

- `Compress-PtcObject`
- `Compress-PtcOutput`
- `Resolve-PtcInvokeScript`
- `Invoke-PtcBoundCommand`
- `Invoke-Ptc`
- `Invoke-PtcRun`
- `Invoke-PtcList`
- `Invoke-PtcRead`
- `Invoke-PtcSmart`
- `Measure-PtcSavings`
- `Invoke-PtcSearch`
- `Invoke-PtcProcess`
- `Invoke-PtcService`

The repo launcher imports the module for a single call:

```powershell
.\ptk.ps1 read .\README.md -MaxLines 20
```

## Command Dispatcher

`ptk` dispatches a fixed set of local helper commands:

| Command | Aliases | Behavior |
| --- | --- | --- |
| `ptk ls [path]` | `dir`, `gci`, `list` | Runs `Get-ChildItem` and compresses filesystem objects. Supports `-Recurse`, `-Force`, and `-MaxItems`. |
| `ptk read <path>` | `cat`, `gc`, `content` | Reads one or more files with `-MaxLines`, `-Tail`, `-Width`, `-Level`, and `-NoHeader`. |
| `ptk smart <path>` | `summarize`, `summary` | Shortcut for summary-level file reads. |
| `ptk savings <path>` | `measure` | Reports approximate character/token savings for `minimal`, `aggressive`, and `summary` read levels. |
| `ptk grep <pattern> [path]` | `search`, `sls` | Runs `Select-String`; recursively searches files when `path` is a directory. Supports `-Include`, `-SimpleMatch`, and `-MaxItems`. |
| `ptk ps [name]` | `process` | Runs `Get-Process` and compresses process objects. |
| `ptk service [name]` | `services`, `svc` | Runs `Get-Service` and compresses service objects. |
| `ptk run { ... }` | `exec` | Runs a scriptblock in the current PowerShell process and compresses the result. |
| `ptk run "<command>"` | `exec` | Runs a command string in a fresh `pwsh -NoProfile -NonInteractive` child, imports CLIXML output, and compresses it. |
| `<objects> | ptk compress` | `object` | Compresses pipeline input directly. Supports `-MaxItems`. |

## Read Levels

`ptk read` supports four levels:

- `none`: preserve the requested text window.
- `minimal`: strip comments and repeated blank lines where safe.
- `aggressive`: keep imports, exports, and structural declarations while
  dropping most bodies. PowerShell uses the AST; other languages use
  conservative regex heuristics.
- `summary`: emit a compact technical summary.

PowerShell file summaries use the parser/AST for function, class, alias,
import, and export detection. Markdown summaries keep headings, bullets, and
code fence languages. Other text and code use local heuristics.

## Object Summaries

`Compress-PtcObject` chooses a specialized renderer only when every item has a
matching type name and the properties that renderer dereferences:

- Files and directories render as an `fs:` summary.
- `Select-String` matches group by file and line.
- Processes show process name, PID, CPU, and working set.
- Services show status, name, and display name.
- Other objects render as a compact table using display properties when
  available.

Mixed streams and projections fall back to the generic path. That is deliberate:
PowerShell projections and CLIXML round trips can keep source type names while
dropping properties.

## MCP Output Shaping Helper

`Compress-PtcOutput` is public because the MCP server calls it inside the warm
runspace. It is also useful for local experiments:

```powershell
[pscustomobject]@{ Name = 'a'; Value = 1 }, [pscustomobject]@{ Name = 'b'; Value = 2 } |
    Compress-PtcOutput

1..40 | ForEach-Object { "line $_" } | Compress-PtcOutput
```

Its contract is conservative: object output compresses, plain text and
primitive scalars pass through exactly, log-shaped text tries `rtk log`, and
internal shaping failures return labeled unshaped output instead of failing the
call.

## Routing Helper

`Resolve-PtcInvokeScript` is the MCP routing helper. With route `auto`, it
rewrites only a single bare native application command with constant arguments
to run through `rtk`. Everything else returns unchanged and runs as PowerShell.

Use `PTK_RTK_PATH` to force the exact `rtk` binary. If it points at a missing
path, `rtk` is treated as absent rather than falling back to another binary on
`PATH`.

## Verification

```powershell
pwsh -NoProfile -Command "Invoke-Pester -Path tests/PwshTokenCompressor.Tests.ps1 -Output Minimal"
```
