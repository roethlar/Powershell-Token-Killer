@{
    RootModule        = 'PwshTokenCompressor.psm1'
    ModuleVersion     = '0.1.0'
    GUID              = 'f1110f9c-3d25-4e74-8f46-f7f6a13d2b23'
    Author            = 'pwsh_token_compressor'
    CompanyName       = 'Unknown'
    Copyright         = '(c) 2026. All rights reserved.'
    Description       = 'PowerShell-first token compression for agent workflows.'
    PowerShellVersion = '7.2'
    FunctionsToExport = @(
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
    )
    AliasesToExport   = @('ptk')
    CmdletsToExport   = @()
    VariablesToExport = @()
}
