<#
.SYNOPSIS
Unit tests for samples/SampleTestHelpers.psm1.

.DESCRIPTION
Covers ConvertTo-ArgumentList, which splits the single -Arguments string that the
sample tests pass into the discrete arguments splatted onto the resolved executable.
A tokenizer defect here silently changes what every sample test executes, so it is
worth pinning down directly rather than only through the sample matrix.
#>

BeforeAll {
    $script:HelpersModule = Join-Path $PSScriptRoot "..\..\samples\SampleTestHelpers.psm1"
    Import-Module $script:HelpersModule -Force
}

AfterAll {
    Remove-Module SampleTestHelpers -Force -ErrorAction SilentlyContinue
}

Describe 'ConvertTo-ArgumentList' {

    Context 'Return shape' {
        It 'returns an array for a single token' {
            # Regression: PowerShell unrolls a one-element array on output, and splatting
            # the resulting scalar passes one character per argument.
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'restore'
                ($result -is [array]) | Should -BeTrue
                $result.Count | Should -Be 1
                $result[0] | Should -Be 'restore'
            }
        }

        It 'returns an empty array for empty or whitespace input' {
            InModuleScope SampleTestHelpers {
                foreach ($input in @('', '   ')) {
                    $result = ConvertTo-ArgumentList -Arguments $input
                    ($result -is [array]) | Should -BeTrue
                    $result.Count | Should -Be 0
                }
            }
        }

        It 'does not nest the array' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'cert generate'
                $result[0] | Should -BeOfType [string]
            }
        }
    }

    Context 'Splatting behavior' {
        It 'passes a single token as exactly one argument' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'restore'
                $probe = { $args.Count }
                (& $probe @result) | Should -Be 1
            }
        }

        It 'passes each token as its own argument' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'cert generate --if-exists skip'
                $probe = { $args -join '|' }
                (& $probe @result) | Should -Be 'cert|generate|--if-exists|skip'
            }
        }
    }

    Context 'Tokenizing' {
        It 'splits on whitespace' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'cert generate --if-exists skip'
                $result.Count | Should -Be 4
            }
        }

        It 'keeps a double-quoted value containing spaces as one argument' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'pack "C:\my path\out" --cert devcert.pfx'
                $result.Count | Should -Be 4
                $result[1] | Should -Be 'C:\my path\out'
            }
        }

        It 'keeps a single-quoted value containing spaces as one argument' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments "cert generate --publisher 'CN=Sparse Guide'"
                $result[-1] | Should -Be 'CN=Sparse Guide'
            }
        }

        It 'preserves an embedded equals sign' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'init . --use-defaults --setup-sdks=stable'
                $result[-1] | Should -Be '--setup-sdks=stable'
            }
        }

        It 'preserves backslashes in relative paths' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'run .\target\debug --with-alias'
                $result[1] | Should -Be '.\target\debug'
            }
        }

        It 'collapses runs of whitespace between tokens' {
            InModuleScope SampleTestHelpers {
                $result = ConvertTo-ArgumentList -Arguments 'cert    info   devcert.pfx'
                $result.Count | Should -Be 3
            }
        }
    }
}

Describe 'Invoke-WithRetry' {

    Context 'Return shape' {
        It 'returns a strict boolean when the command writes to stdout' {
            # Regression: a PowerShell function returns everything written to the output
            # stream, so invoking a native command bare inside the block made this return
            # @('...command output...', $true). The caller's 'Should -Be $true' then failed
            # on a command that had actually succeeded -- which is how a green
            # 'create-electron-app' run reported the whole electron sample as broken.
            $result = Invoke-WithRetry -OperationName 'emits-stdout' -MaxAttempts 1 -ScriptBlock {
                cmd /c "echo Resolving package manager& exit 0"
            }
            ($result -is [bool]) | Should -BeTrue
            $result | Should -Be $true
        }

        It 'returns a strict boolean when a failing command writes to stdout' {
            $result = Invoke-WithRetry -OperationName 'fails-with-stdout' -MaxAttempts 1 -ScriptBlock {
                cmd /c "echo partial output& exit 1"
            }
            ($result -is [bool]) | Should -BeTrue
            $result | Should -Be $false
        }

        It 'returns a strict boolean when the retry cleanup writes to stdout' {
            $script:cleanupAttempts = 0
            $result = Invoke-WithRetry -OperationName 'onretry-stdout' -MaxAttempts 2 -OnRetry {
                cmd /c "echo cleaning cache& exit 0"
            } -ScriptBlock {
                $script:cleanupAttempts++
                if ($script:cleanupAttempts -lt 2) { cmd /c "exit 1" } else { cmd /c "exit 0" }
            }
            ($result -is [bool]) | Should -BeTrue
            $result | Should -Be $true
        }
    }

    Context 'Retry behavior' {
        It 'stops as soon as the command succeeds' {
            $script:calls = 0
            $result = Invoke-WithRetry -OperationName 'succeeds-first' -MaxAttempts 3 -ScriptBlock {
                $script:calls++
                cmd /c "exit 0"
            }
            $result | Should -Be $true
            $script:calls | Should -Be 1
        }

        It 'retries a transient failure and reports success' {
            $script:calls = 0
            $result = Invoke-WithRetry -OperationName 'transient' -MaxAttempts 3 -ScriptBlock {
                $script:calls++
                if ($script:calls -lt 2) { cmd /c "exit 1" } else { cmd /c "exit 0" }
            }
            $result | Should -Be $true
            $script:calls | Should -Be 2
        }

        It 'gives up after MaxAttempts and reports failure' {
            $script:calls = 0
            $result = Invoke-WithRetry -OperationName 'always-fails' -MaxAttempts 2 -ScriptBlock {
                $script:calls++
                cmd /c "exit 1"
            }
            $result | Should -Be $false
            $script:calls | Should -Be 2
        }

        It 'reads the exit code through a Write-Host pipeline' {
            # The install-electron call sites pipe through ForEach-Object { Write-Host $_ },
            # which must not hide the native command's exit code.
            $result = Invoke-WithRetry -OperationName 'piped' -MaxAttempts 1 -ScriptBlock {
                & cmd /c "echo output& exit 1" 2>&1 | ForEach-Object { Write-Host $_ }
            }
            $result | Should -Be $false
        }
    }
}
