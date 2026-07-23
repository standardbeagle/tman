@{
    RootModule        = 'tman.psm1'
    ModuleVersion     = '0.1.1'
    GUID              = '7f3a2c1e-9b4d-4e5f-a6c8-1d2e3f4a5b6c'
    Author            = 'standardbeagle'
    CompanyName       = 'standardbeagle'
    Copyright         = '(c) 2026 standardbeagle. MIT licensed.'
    Description       = 'Supervised process/test runner - caps, culls, and reaps runaway processes. Native binary downloaded from GitHub Releases.'
    PowerShellVersion = '5.1'
    FunctionsToExport = @('Install-Tman', 'Invoke-Tman')
    AliasesToExport   = @('tman')
    CmdletsToExport   = @()
    VariablesToExport = @()
    PrivateData       = @{
        PSData = @{
            Tags       = @('process', 'test', 'runner', 'supervisor', 'cli')
            LicenseUri = 'https://github.com/standardbeagle/tman/blob/main/LICENSE'
            ProjectUri = 'https://github.com/standardbeagle/tman'
        }
    }
}
