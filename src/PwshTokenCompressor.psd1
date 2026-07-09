@{
    RootModule        = 'PwshTokenCompressor.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = 'f1110f9c-3d25-4e74-8f46-f7f6a13d2b23'
    Author            = 'pwsh_token_compressor'
    CompanyName       = 'Unknown'
    Copyright         = '(c) 2026. All rights reserved.'
    Description       = 'Output shaping library for the ptk warm-runspace MCP server.'
    PowerShellVersion = '7.2'
    FunctionsToExport = @(
        'Compress-PtcObject',
        'Compress-PtcOutput',
        'Get-PtcShellDialectFinding',
        'Resolve-PtcInvokeScript'
    )
    AliasesToExport   = @()
    CmdletsToExport   = @()
    VariablesToExport = @()
}
