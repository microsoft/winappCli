#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }

# Only the parts of AppState.psm1 that don't touch the desktop; importing it just loads UIA types.
BeforeAll {
    Import-Module (Join-Path $PSScriptRoot '..\lib\AppState.psm1') -Force
}

Describe "Calculator's saved mode" {
    BeforeEach {
        $script:record = Join-Path $TestDrive 'cache\calculator-mode.json'
        Remove-Item -LiteralPath $script:record -ErrorAction SilentlyContinue
    }

    It 'records the first mode it sees and writes it to the record file' {
        Initialize-CalculatorModeRecord -Path $script:record | Should -BeNullOrEmpty
        Register-CalculatorMode -Mode 'programmer' | Should -Be 'programmer'
        Get-CalculatorRestoreMode | Should -Be 'programmer'
        (Get-Content -Raw -LiteralPath $script:record | ConvertFrom-Json).mode | Should -Be 'programmer'
    }

    It 'keeps the first recorded mode when a later Calculator shows another' {
        Initialize-CalculatorModeRecord -Path $script:record
        Register-CalculatorMode -Mode 'date' | Out-Null
        Register-CalculatorMode -Mode 'scientific' | Should -Be 'date'
        (Get-Content -Raw -LiteralPath $script:record | ConvertFrom-Json).mode | Should -Be 'date'
    }

    It 'restores the mode an interrupted benchmark recorded, not the mode it left behind' {
        New-Item -ItemType Directory -Force -Path (Split-Path $script:record) | Out-Null
        '{"mode":"graphing","recordedAt":"2026-01-01T00:00:00Z"}' | Set-Content -LiteralPath $script:record
        Initialize-CalculatorModeRecord -Path $script:record | Should -Be 'graphing'
        Register-CalculatorMode -Mode 'scientific' | Should -Be 'graphing'
        (Get-Content -Raw -LiteralPath $script:record | ConvertFrom-Json).mode | Should -Be 'graphing'
    }

    It 'treats an unreadable record as missing' {
        New-Item -ItemType Directory -Force -Path (Split-Path $script:record) | Out-Null
        '{ not json' | Set-Content -LiteralPath $script:record
        Initialize-CalculatorModeRecord -Path $script:record -WarningAction SilentlyContinue | Should -BeNullOrEmpty
        '{"recordedAt":"2026-01-01T00:00:00Z"}' | Set-Content -LiteralPath $script:record
        Initialize-CalculatorModeRecord -Path $script:record -WarningAction SilentlyContinue | Should -BeNullOrEmpty
        Register-CalculatorMode -Mode 'standard' | Should -Be 'standard'
        (Get-Content -Raw -LiteralPath $script:record | ConvertFrom-Json).mode | Should -Be 'standard'
    }

    It 'removes the record after a clean finish, so a later change by the user is picked up' {
        Initialize-CalculatorModeRecord -Path $script:record
        Register-CalculatorMode -Mode 'scientific' | Out-Null
        Complete-CalculatorModeRecord
        $script:record | Should -Not -Exist
        Initialize-CalculatorModeRecord -Path $script:record | Should -BeNullOrEmpty
    }

    It 'keeps the record while owned processes are still left to close' {
        Initialize-CalculatorModeRecord -Path $script:record
        Register-CalculatorMode -Mode 'scientific' | Out-Null
        Complete-CalculatorModeRecord -Unfinished
        $script:record | Should -Exist
    }

    It 'keeps the record and warns when a Calculator could not be switched back' {
        Initialize-CalculatorModeRecord -Path $script:record
        Register-CalculatorMode -Mode 'programmer' | Out-Null
        InModuleScope AppState { $script:CalculatorRestoreFailures = 1 }
        $warned = Complete-CalculatorModeRecord 3>&1
        $script:record | Should -Exist
        "$warned" | Should -Match 'programmer'
    }

    It 'starts each benchmark with no restore failures' {
        InModuleScope AppState { $script:CalculatorRestoreFailures = 3 }
        Initialize-CalculatorModeRecord -Path $script:record
        InModuleScope AppState { $script:CalculatorRestoreFailures } | Should -Be 0
    }

    It 'maps a mode to its nav item' -ForEach @(
        @{ Mode = 'standard'; Id = 'Standard' }
        @{ Mode = 'scientific'; Id = 'Scientific' }
        @{ Mode = 'date'; Id = 'Date' }
        @{ Mode = 'currency'; Id = 'Currency' }
    ) {
        ConvertTo-CalculatorNavId -Mode $Mode | Should -BeExactly $Id
    }
}
