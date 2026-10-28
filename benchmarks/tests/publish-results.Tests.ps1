Describe 'publish-results.ps1' {
    BeforeAll {
        $script:publisher = Join-Path $PSScriptRoot '..\publish-results.ps1'
        $script:example = Join-Path $PSScriptRoot '..\examples\v0.7.1\winforms-msix__cli-only__sonnet-5.5__1.json'
        function script:New-Run($Release = 'v0.7.1') {
            # Harness-style layout: a trial-result.json inside a per-trial folder, not the final name.
            $run = Join-Path $TestDrive "run-$([guid]::NewGuid().ToString('N'))"
            $trial = Join-Path $run 'trial-1'
            New-Item -ItemType Directory $trial -Force | Out-Null
            (Get-Content $script:example -Raw) -replace '"release": "v0.7.1"', "`"release`": `"$Release`"" | Set-Content (Join-Path $trial 'trial-result.json')
            Set-Content (Join-Path $trial 'results.json') '{ "not": "a trial" }'
            $run
        }
    }

    It 'stages renamed files and an index, without touching git' {
        $run = New-Run; $out = Join-Path $TestDrive 'out'
        & pwsh -NoProfile -File $script:publisher -Path $run -OutDir $out 2>&1 | Out-Null
        $LASTEXITCODE | Should -Be 0
        Join-Path $out 'v0.7.1\winforms-msix__cli-only__sonnet-5.5__1.json' | Should -Exist
        $index = Get-Content (Join-Path $out 'v0.7.1\index.json') -Raw | ConvertFrom-Json
        $index.trialCount | Should -Be 1
        $index.groups[0].trials | Should -Be 1
        $index.trials[0].file | Should -Be 'winforms-msix__cli-only__sonnet-5.5__1.json'
    }
    It 'refuses to open a PR for a dev run' {
        $run = New-Run 'dev'
        $out = & pwsh -NoProfile -File $script:publisher -Path $run -OutDir (Join-Path $TestDrive 'o2') -OpenPr 2>&1 | Out-String
        $LASTEXITCODE | Should -Not -Be 0
        $out | Should -Match 'dev runs stay local|Validation failed|folder should be named'
    }
    It 'refuses when results come from a different commit than the checkout' {
        $run = New-Run
        & pwsh -NoProfile -File $script:publisher -Path $run -OutDir (Join-Path $TestDrive 'o3') -OpenPr 2>&1 | Out-Null
        $LASTEXITCODE | Should -Not -Be 0
    }
}
