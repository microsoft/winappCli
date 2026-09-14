param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)
}

Describe 'performance-diagnostics-lab sample' {
    BeforeAll {
        $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)
        $script:sampleDir = $PSScriptRoot
        $script:originalLocation = Get-Location
    }

    AfterAll {
        Set-Location $script:originalLocation

        if (-not $SkipCleanup) {
            Remove-Item -Path (Join-Path $script:sampleDir 'bin') -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path (Join-Path $script:sampleDir 'obj') -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Context 'Sample build check' {
        BeforeAll {
            if (-not $script:skip) {
                Push-Location $script:sampleDir
            }
        }

        AfterAll {
            if (-not $script:skip) {
                Pop-Location
            }
        }

        It 'Restores NuGet packages' -Skip:$script:skip {
            dotnet restore
            $LASTEXITCODE | Should -Be 0
        }

        It 'Builds the packaged WinUI target for the host architecture' -Skip:$script:skip {
            $rid = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq 'Arm64') {
                'win-arm64'
            } else {
                'win-x64'
            }

            dotnet build -c Debug -r $rid --nologo
            $LASTEXITCODE | Should -Be 0

            dotnet build -c Release -r $rid --nologo
            $LASTEXITCODE | Should -Be 0
        }
    }
}
