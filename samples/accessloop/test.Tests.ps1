param(
    [string]$WinappPath,
    [switch]$SkipCleanup
)

BeforeDiscovery {
    $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)
    if (-not $script:skip) {
        $script:skip = @(& dotnet --list-sdks | Where-Object { $_ -match '^10\.' }).Count -eq 0
    }
    $script:skipDemo = $script:skip -or -not [System.Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
        [System.Runtime.InteropServices.OSPlatform]::Windows)
}

Describe 'AccessLoop sample' {
    BeforeAll {
        Import-Module "$PSScriptRoot\..\SampleTestHelpers.psm1" -Force
        $script:sampleDir = $PSScriptRoot
        $script:copyDir = $null
        $script:skip = $null -eq (Get-Command dotnet -ErrorAction SilentlyContinue)
        if (-not $script:skip) {
            $script:skip = @(& dotnet --list-sdks | Where-Object { $_ -match '^10\.' }).Count -eq 0
        }
        if ($script:skip) { return }

        # WinappPath is accepted for the shared harness; these tests never install or invoke winapp.
        $script:rid = "win-$([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant())"

        function Copy-SampleSource {
            param([string]$Source, [string]$Destination)
            $null = New-Item -ItemType Directory -Path $Destination
            foreach ($item in Get-ChildItem -LiteralPath $Source -Force) {
                if ($item.Name -in @('bin', 'obj', '.accessloop', 'TestResults')) { continue }
                $target = Join-Path $Destination $item.Name
                if ($item.PSIsContainer) {
                    Copy-SampleSource -Source $item.FullName -Destination $target
                } else {
                    Copy-Item -LiteralPath $item.FullName -Destination $target
                }
            }
        }
    }

    AfterAll {
        if ($script:copyDir -and -not $SkipCleanup) {
            Remove-TempTestDirectory -Path $script:copyDir
        }
    }

    Context 'Phase 1: Fresh copy and deterministic runner tests' -Skip:$script:skip {
        BeforeAll {
            if ($script:skip) { return }
            $script:copyDir = Join-Path $script:sampleDir ".accessloop\sample-test-$([Guid]::NewGuid().ToString('N'))"
            Copy-SampleSource -Source $script:sampleDir -Destination $script:copyDir
        }

        It 'Copies the entire sample without stale build or evidence output' {
            Join-Path $script:copyDir 'suite.json' | Should -Exist
            Join-Path $script:copyDir 'scenario.json' | Should -Exist
            Join-Path $script:copyDir 'export.scenario.json' | Should -Exist
            Join-Path $script:copyDir 'Demo\MainWindow.xaml' | Should -Exist
            Join-Path $script:copyDir 'journey.json' | Should -Exist
            Join-Path $script:copyDir 'profiles\powertoys-settings.json' | Should -Exist
            Join-Path $script:copyDir 'survey-guide.md' | Should -Exist
            Join-Path $script:copyDir 'state-demo.json' | Should -Exist
            Join-Path $script:copyDir 'loop-demo.json' | Should -Exist
            Join-Path $script:copyDir 'state-demo-guide.md' | Should -Exist
            Join-Path $script:copyDir 'StateDemo\AccessLoop.StateDemo.csproj' | Should -Exist
            Join-Path $script:copyDir 'JourneyDemo\Pages\DocumentsPage.xaml' | Should -Exist
            Join-Path $script:copyDir 'JourneyDemo\Pages\ExportPage.xaml' | Should -Exist
            Join-Path $script:copyDir 'AccessLoop\obj' | Should -Not -Exist
            Join-Path $script:copyDir '.accessloop' | Should -Not -Exist
        }

        It 'Builds the runner with its pinned Axe.Windows SDK from the fresh copy' {
            dotnet build (Join-Path $script:copyDir 'AccessLoop\AccessLoop.csproj') -c Debug --verbosity minimal
            $LASTEXITCODE | Should -Be 0
        }

        It 'Runs deterministic .NET tests without a desktop or an AI service' {
            dotnet test --project (Join-Path $script:copyDir 'AccessLoop.Tests\AccessLoop.Tests.csproj') -c Debug --verbosity minimal
            $LASTEXITCODE | Should -Be 0
        }
    }

    Context 'Phase 2: Existing demo source and build' {
        BeforeAll {
            $script:suite = Get-Content -LiteralPath (Join-Path $script:sampleDir 'suite.json') -Raw | ConvertFrom-Json
            $script:scenario = Get-Content -LiteralPath (Join-Path $script:sampleDir 'scenario.json') -Raw | ConvertFrom-Json
            $script:exportScenario = Get-Content -LiteralPath (Join-Path $script:sampleDir 'export.scenario.json') -Raw | ConvertFrom-Json
            $script:xaml = [System.Xml.Linq.XDocument]::Load((Join-Path $script:sampleDir 'Demo\MainWindow.xaml'))
            $script:elements = @($script:xaml.Descendants())
        }

        It 'Includes both independent workflows in the suite in a fixed order' {
            $script:suite.schemaVersion | Should -Be 1
            $script:suite.name | Should -Be 'document-workflows'
            $script:suite.scenarios.Count | Should -Be 2
            $script:suite.scenarios[0] | Should -Be 'scenario.json'
            $script:suite.scenarios[1] | Should -Be 'export.scenario.json'
            $script:scenario.name | Should -Be 'save-document'
            $script:exportScenario.name | Should -Be 'export-document'
        }

        It 'Pins the distinct names and keyboard outcomes while sharing the same project and window' {
            $cases = @(
                @{ Scenario = $script:scenario; Target = 'SaveDocumentButton'; Name = 'Save document'; Result = 'Saved: AccessLoop' }
                @{ Scenario = $script:exportScenario; Target = 'ExportDocumentButton'; Name = 'Export document'; Result = 'Exported: AccessLoop' }
            )
            foreach ($case in $cases) {
                $case.Scenario.schemaVersion | Should -Be 1
                $case.Scenario.project | Should -Be 'Demo\AccessLoop.Demo.csproj'
                $case.Scenario.targetId | Should -Be $case.Target
                $case.Scenario.expectedName | Should -Be $case.Name
                $case.Scenario.anchorId | Should -Be 'DocumentTitleInput'
                $case.Scenario.resultId | Should -Be 'SaveStatus'
                $case.Scenario.inputText | Should -Be 'AccessLoop'
                $case.Scenario.expectedResult | Should -Be $case.Result
                $case.Scenario.maxTabs | Should -Be 5
                $case.Scenario.timeoutSeconds | Should -Be 15
            }
        }

        It 'Keeps two static icon-only buttons visible together with intentional missing names' {
            @($script:elements | Where-Object { $_.Name.LocalName -eq 'Button' }).Count | Should -Be 2
            $targets = @(
                @{ Id = 'SaveDocumentButton'; Label = 'Save document'; Handler = 'SaveDocument_Click'; Tab = '1' }
                @{ Id = 'ExportDocumentButton'; Label = 'Export document'; Handler = 'ExportDocument_Click'; Tab = '2' }
            )
            foreach ($target in $targets) {
                $buttons = @($script:elements | Where-Object {
                    $_.Name.LocalName -eq 'Button' -and
                    $_.Attribute('AutomationProperties.AutomationId').Value -eq $target.Id
                })
                $buttons.Count | Should -Be 1
                $button = $buttons[0]
                $button.Attribute('AutomationProperties.Name') | Should -BeNullOrEmpty
                $button.Attribute('AutomationProperties.LabeledBy') | Should -BeNullOrEmpty
                $button.Attribute('Content') | Should -BeNullOrEmpty
                $button.Attribute('Click').Value | Should -Be $target.Handler
                $button.Attribute('TabIndex').Value | Should -Be $target.Tab
                @($button.Elements()).Count | Should -Be 1
                @($button.Elements() | Where-Object { $_.Name.LocalName -eq 'Path' }).Count | Should -Be 1
                @($button.Descendants() | Where-Object { $_.Name.LocalName -in @('TextBlock', 'SymbolIcon') }).Count | Should -Be 0
                @($button.Parent.Elements() | Where-Object {
                    $_.Name.LocalName -eq 'TextBlock' -and $_.Attribute('Text').Value -eq $target.Label
                }).Count | Should -Be 1
                @($button.Ancestors() | Where-Object {
                    $_.Name.LocalName -in @('DataTemplate', 'ControlTemplate', 'Style')
                }).Count | Should -Be 0
                @($button.AncestorsAndSelf().Attributes() | Where-Object {
                    $_.Name.LocalName -in @('Load', 'DeferLoadStrategy', 'Visibility')
                }).Count | Should -Be 0
            }
        }

        It 'Keeps initial focus and independent nonpersistent save and export handlers' {
            $inputs = @($script:elements | Where-Object {
                $_.Name.LocalName -eq 'TextBox' -and
                $_.Attribute('AutomationProperties.AutomationId').Value -eq $script:scenario.anchorId
            })
            $inputs.Count | Should -Be 1
            $inputs[0].Attribute('AutomationProperties.Name').Value | Should -Be 'Document title'
            $inputs[0].Attribute('TabIndex').Value | Should -Be '0'
            $inputs[0].Attribute('Loaded').Value | Should -Be 'DocumentTitleInput_Loaded'
            $results = @($script:elements | Where-Object {
                $_.Name.LocalName -eq 'TextBlock' -and
                $_.Attribute('AutomationProperties.AutomationId').Value -eq $script:scenario.resultId
            })
            $results.Count | Should -Be 1
            $results[0].Attribute('Text').Value | Should -Be 'Not saved yet'
            $source = Get-Content -LiteralPath (Join-Path $script:sampleDir 'Demo\MainWindow.xaml.cs') -Raw
            $source.Contains('DocumentTitleInput.Focus(FocusState.Programmatic);') | Should -BeTrue
            $source.Contains('SaveStatus.Text = "Saved: " + DocumentTitleInput.Text;') | Should -BeTrue
            $source.Contains('SaveStatus.Text = "Exported: " + DocumentTitleInput.Text;') | Should -BeTrue
            $source.Contains('(720 * scale), (int)(460 * scale)') | Should -BeTrue
        }

        It 'Keeps the demo unpackaged without Windows App Runtime installation' {
            $project = [System.Xml.Linq.XDocument]::Load((Join-Path $script:sampleDir 'Demo\AccessLoop.Demo.csproj'))
            ($project.Descendants() | Where-Object { $_.Name.LocalName -eq 'WindowsPackageType' }).Value | Should -Be 'None'
            ($project.Descendants() | Where-Object { $_.Name.LocalName -eq 'WindowsAppSDKSelfContained' }).Value | Should -Be 'true'
            ($project.Descendants() | Where-Object { $_.Name.LocalName -eq 'WindowsAppSdkDeploymentManagerInitialize' }).Value | Should -Be 'false'
        }

        It 'Builds the unpackaged, self-contained Windows App SDK demo without launching it' -Skip:$script:skipDemo {
            dotnet build (Join-Path $script:sampleDir 'Demo\AccessLoop.Demo.csproj') -c Debug -r $script:rid --verbosity minimal
            $LASTEXITCODE | Should -Be 0
        }

        It 'Builds the owned audit fixture without launching or interacting with it' -Skip:$script:skipDemo {
            dotnet build (Join-Path $script:sampleDir 'AuditFixture\AccessLoop.AuditFixture.csproj') -c Debug --verbosity minimal
            $LASTEXITCODE | Should -Be 0
        }

        It 'Builds the three-page journey demo without launching it or changing its intentional defects' -Skip:$script:skipDemo {
            dotnet build (Join-Path $script:sampleDir 'JourneyDemo\AccessLoop.JourneyDemo.csproj') -c Debug -r $script:rid --verbosity minimal
            $LASTEXITCODE | Should -Be 0
        }

        It 'Builds the three-page UI-state demo without operating the desktop' -Skip:$script:skipDemo {
            dotnet build (Join-Path $script:sampleDir 'StateDemo\AccessLoop.StateDemo.csproj') -c Debug -r $script:rid --verbosity minimal
            $LASTEXITCODE | Should -Be 0
        }
    }
}
