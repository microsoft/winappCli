#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0' }

BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\lib\KeepAwake.psm1') -Force
}

Describe 'Enable-KeepAwake' {
    It 'holds the keep-awake request on Windows and releases it' -Skip:(-not $IsWindows) {
        try { Enable-KeepAwake | Should -BeTrue }
        finally { Disable-KeepAwake }
    }
    It 'asks for system and display with ES_CONTINUOUS so the request lasts until released' {
        InModuleScope KeepAwake {
            $script:KeepAwakeFlags | Should -Be ([uint32](0x80000000L -bor 0x1 -bor 0x2))
            $script:ReleaseFlags | Should -Be ([uint32]0x80000000L)
        }
    }
}
