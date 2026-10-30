Describe 'validate-results.ps1' {
    BeforeAll {
        $script:validator = Join-Path $PSScriptRoot '..\validate-results.ps1'
        $script:good = Get-Content (Join-Path $PSScriptRoot '..\examples\v0.7.1\winforms-msix__cli-only__sonnet-5.5__1.json') -Raw
        function script:Invoke-Validator($Json, $Folder = 'v0.7.1', $Name = 'winforms-msix__cli-only__sonnet-5.5__1.json') {
            $dir = Join-Path $TestDrive $Folder
            New-Item -ItemType Directory $dir -Force | Out-Null
            Set-Content (Join-Path $dir $Name) $Json
            & pwsh -NoProfile -File $script:validator -Path $TestDrive 2>&1 | Out-Null
            $LASTEXITCODE
        }
    }
    AfterEach { Remove-Item "$TestDrive\*" -Recurse -Force }

    It 'accepts the example file' { Invoke-Validator $good | Should -Be 0 }
    It 'rejects a passed flag that disagrees with the required checks' {
        Invoke-Validator ($good -replace '"passed": true,\s*"improvements"', '"passed": false, "improvements"') | Should -Be 1
    }
    It 'rejects improvement counts that disagree with the checks' {
        Invoke-Validator ($good -replace '"met": 1', '"met": 2') | Should -Be 1
    }
    It 'rejects a non-valid outcome that claims to have passed' {
        Invoke-Validator ($good -replace '"outcome": "valid"', '"outcome": "app-failed"') | Should -Be 1
    }
    It 'rejects an unknown outcome' { Invoke-Validator ($good -replace '"outcome": "valid"', '"outcome": "bogus"') | Should -Be 1 }
    It 'rejects a harness-error outcome without an error message' {
        Invoke-Validator ($good -replace '"outcome": "valid",\s*"passed": true', '"outcome": "harness-error", "passed": false') | Should -Be 1
    }
    It 'rejects an unknown arm' { Invoke-Validator ($good -replace '"arm": "cli-only"', '"arm": "bogus"') | Should -Be 1 }
    It 'rejects a file stored in the wrong release folder' { Invoke-Validator $good 'v0.8.0' | Should -Be 1 }
}
