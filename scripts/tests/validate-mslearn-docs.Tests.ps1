#Requires -Modules @{ ModuleName = 'Pester'; ModuleVersion = '5.0.0' }
<#
    Pester tests for the MS Learn docs gate (scripts/validate-mslearn-docs.ps1)
    and the shared front-matter rules (scripts/mslearn-doc-lib.ps1).

    The validator hard-gates the release porting job, so these tests pin its
    rules and exit codes: description bounds, banned words, YAML-unsafe values
    (including leading block indicators), the multi-paragraph alert callout
    behaviour, and the 0/1 exit contract.
#>

BeforeAll {
    $script:ScriptsDir = Split-Path $PSScriptRoot -Parent
    $script:Validator = Join-Path $ScriptsDir 'validate-mslearn-docs.ps1'
    $script:Lib = Join-Path $ScriptsDir 'mslearn-doc-lib.ps1'
    . $script:Lib

    function New-TempDocsRoot {
        $d = Join-Path ([System.IO.Path]::GetTempPath()) ("mslearn-val-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $d -Force | Out-Null
        return $d
    }

    # A safe description of exactly $Len chars: lowercase letters + spaces only,
    # never starting or ending with a space (which would itself be YAML-unsafe).
    function New-SafeDescription {
        param([int]$Len)
        $base = 'register temporary package identity so you can debug identity dependent windows features from build output '
        $s = ''
        while ($s.Length -lt $Len) { $s += $base }
        $s = $s.Substring(0, $Len)
        if ($s[0] -eq ' ') { $s = 'x' + $s.Substring(1) }
        if ($s[-1] -eq ' ') { $s = $s.Substring(0, $Len - 1) + 'x' }
        return $s
    }

    function New-DocContent {
        param(
            [string]$Title = 'Sample Page',
            [string]$Description = '',
            [string]$Body = 'Plain instructional body text.'
        )
        if ([string]::IsNullOrEmpty($Description)) { $Description = New-SafeDescription 130 }
        return @"
<!-- mslearn: true -->
<!-- description: $Description -->

# $Title

$Body
"@
    }

    function Invoke-Validator {
        param([string]$Root)
        $out = & pwsh -NoProfile -File $script:Validator -DocsRoot $Root *>&1 | Out-String
        return [pscustomobject]@{ ExitCode = $LASTEXITCODE; Output = $out }
    }

    function Set-Doc {
        param([string]$Root, [string]$Content, [string]$Name = 'doc.md')
        Set-Content -Path (Join-Path $Root $Name) -Value $Content -Encoding UTF8
    }
}

Describe 'mslearn-doc-lib: Test-MsLearnYamlUnsafe' {
    It 'treats a plain sentence as safe' {
        Test-MsLearnYamlUnsafe 'Register identity so you can debug features' | Should -BeFalse
    }
    It 'flags <_> as unsafe' -ForEach @('- leading dash', '? leading question', ': has colon', '#hash', '| pipe', '> gt', 'trailing space ', ' leading space', '~null', ', comma') {
        Test-MsLearnYamlUnsafe $_ | Should -BeTrue
    }
    It 'quotes only unsafe values' {
        Format-MsLearnYamlValue 'plain text' | Should -Be 'plain text'
        Format-MsLearnYamlValue '- dash' | Should -Be '"- dash"'
    }
    It 'escapes backslashes before quotes so quoted Windows paths stay valid YAML' {
        # ':' forces double-quoting; the backslash must be doubled, not left bare.
        Format-MsLearnYamlValue 'C:\Windows: notes' | Should -Be '"C:\\Windows: notes"'
    }
}

Describe 'mslearn-doc-lib: description + title resolution' {
    It 'extracts the first H1 as the title' {
        Get-MsLearnTitle "# Hello World`n`nbody" | Should -Be 'Hello World'
    }
    It 'prefers the description marker over the title' {
        $r = Resolve-MsLearnDescription -Content '<!-- description: My desc -->' -Title 'T'
        $r.Description | Should -Be 'My desc'
        $r.HasMarker | Should -BeTrue
    }
    It 'defaults the description to the title when no marker exists' {
        $r = Resolve-MsLearnDescription -Content 'no marker here' -Title 'T'
        $r.Description | Should -Be 'T'
        $r.HasMarker | Should -BeFalse
    }
}

Describe 'validate-mslearn-docs gate' {
    BeforeEach { $script:Root = New-TempDocsRoot }
    AfterEach { Remove-Item $script:Root -Recurse -Force -ErrorAction SilentlyContinue }

    It 'passes a well-formed doc (exit 0)' {
        Set-Doc $Root (New-DocContent)
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Not -Match '\[error\]'
    }

    It 'fails a too-short description (exit 1)' {
        Set-Doc $Root (New-DocContent -Description (New-SafeDescription 50))
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'description length'
    }

    It 'fails a too-long description (exit 1)' {
        Set-Doc $Root (New-DocContent -Description (New-SafeDescription 200))
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'description length'
    }

    It 'fails when the description marker is missing (exit 1)' {
        Set-Doc $Root "<!-- mslearn: true -->`n`n# Only A Title`n`nbody"
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'description is missing or duplicates'
    }

    It 'fails a YAML-unsafe description with a leading block indicator (exit 1)' {
        Set-Doc $Root (New-DocContent -Description ('- ' + (New-SafeDescription 128)))
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match 'quoted YAML value'
    }

    It 'fails on a banned marketing word and reports the line number (exit 1)' {
        Set-Doc $Root (New-DocContent -Body 'This is a powerful feature.')
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 1
        $r.Output | Should -Match "banned marketing word 'powerful' on line"
    }

    It 'warns (non-fatal) on a bold blockquote callout without alert syntax' {
        Set-Doc $Root (New-DocContent -Body "> **Note:** a bold blockquote used as a callout.")
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Match 'does not use MS Learn alert syntax'
    }

    It 'does NOT warn on a multi-paragraph alert whose block opened with a marker' {
        $body = @"
> [!NOTE]
> This is an important note about the feature.
>
> **Details:** additional explanation here.
"@
        Set-Doc $Root (New-DocContent -Body $body)
        $r = Invoke-Validator $Root
        $r.ExitCode | Should -Be 0
        $r.Output | Should -Not -Match 'does not use MS Learn alert syntax'
    }
}

Describe 'mslearn-doc-lib: inline code span rule' {
    BeforeAll {
        # Mirrors what port-mslearn-docs.ps1 does: replace every span with a
        # placeholder, so whatever survives is what the link rewriter can see.
        function Get-Unprotected {
            param([string]$Content)
            return [regex]::Replace($Content, (Get-MsLearnInlineCodePattern), '%%C%%')
        }
    }

    It 'protects an ordinary single-backtick span' {
        Get-Unprotected 'run `winapp init` first' | Should -Be 'run %%C%% first'
    }

    It 'protects a double-backtick span holding a literal backtick' {
        # `` IAsyncOperation`1 `` is the only correct way to write a code span
        # containing a backtick, and docs/usage.md uses it for generic arity.
        Get-Unprotected 'the (`` IAsyncOperation`1 ``) form' | Should -Be 'the (%%C%%) form'
    }

    It 'does not swallow a link that follows a double-backtick span' {
        # Regression: a single-backtick rule paired the opening `` with a later
        # backtick, absorbing everything between - including this link - into one
        # "span", so the link was never rewritten and 404'd on MS Learn.
        $doc = 'An arity suffix (`` IAsyncOperation`1 ``) is odd.' + "`n`n" +
               'See the [guide](guides/electron/js-file-picker.md) for the `winapp.jsBindings` options.'
        Get-Unprotected $doc | Should -Match '\[guide\]\(guides/electron/js-file-picker\.md\)'
    }

    It 'keeps adjacent spans separate' {
        Get-Unprotected 'both `a` and `b` here' | Should -Be 'both %%C%% and %%C%% here'
    }

    It 'protects a span that wraps across a line break' {
        # docs/usage.md wraps one long quoted error message across two lines.
        Get-Unprotected "a ``long`nmessage`` b" | Should -Be 'a %%C%% b'
    }
}

Describe 'port-mslearn-docs: generated toc.yml + guides index' {
    BeforeAll {
        $script:PortScript = Join-Path $script:ScriptsDir 'port-mslearn-docs.ps1'
        $script:PortOut = Join-Path ([System.IO.Path]::GetTempPath()) ("mslearn-port-" + [guid]::NewGuid().ToString('N'))
        & pwsh -NoProfile -File $script:PortScript -OutputPath $script:PortOut -Version '0.0.0-test' *>&1 | Out-Null
        $script:PortExit = $LASTEXITCODE
        $script:TocPath = Join-Path $script:PortOut 'toc.yml'
        $script:Toc = if (Test-Path $script:TocPath) { Get-Content $script:TocPath -Raw } else { '' }
    }
    AfterAll { Remove-Item $script:PortOut -Recurse -Force -ErrorAction SilentlyContinue }

    It 'runs to completion (exit 0) and writes toc.yml' {
        $script:PortExit | Should -Be 0
        Test-Path $script:TocPath | Should -BeTrue
    }

    It 'lists the overview as the first entry' {
        $script:Toc | Should -Match '(?m)^- name: winapp CLI overview'
    }

    It 'nests the Electron guides under the Framework guides subtree' {
        $script:Toc | Should -Match '(?m)^- name: Framework guides'
        $script:Toc | Should -Match '(?m)^  - name: Electron'
        $script:Toc | Should -Match 'href: guides/electron-setup.md'
    }

    It 'includes every ported markdown page as a toc href' {
        $hrefs = [regex]::Matches($script:Toc, '(?m)href:\s*(\S+)') | ForEach-Object { $_.Groups[1].Value }
        $ported = Get-ChildItem $script:PortOut -Recurse -File -Filter *.md | ForEach-Object {
            [System.IO.Path]::GetRelativePath($script:PortOut, $_.FullName) -replace '\\', '/'
        }
        foreach ($p in $ported) { $hrefs | Should -Contain $p }
    }

    It 'produces valid, unquoted toc names (no accidental YAML quoting)' {
        # Curated labels are all plain scalars; guard against a regression that
        # would force quoting (which would signal an unexpected special char).
        $script:Toc | Should -Not -Match '(?m)^\s*- name: "'
    }

    It 'generates a clean guides/index.md heading with no stray carriage return' {
        $gi = Get-Content (Join-Path $script:PortOut 'guides\index.md') -Raw
        $gi | Should -Match '# Framework guides'
        $gi | Should -Not -Match "`r`r"
    }

    It 'separates every generated table from the preceding paragraph with a blank line' {
        # A table header glued to the paragraph above it is absorbed into that
        # paragraph instead of rendering as a table. Here-strings drop the newline
        # before their closing terminator, which makes this easy to reintroduce.
        $offenders = @()
        foreach ($file in Get-ChildItem $script:PortOut -Recurse -File -Filter *.md) {
            $lines = Get-Content $file.FullName
            for ($i = 1; $i -lt $lines.Count; $i++) {
                # A delimiter row identifies a table; its header is the line above.
                if ($lines[$i] -notmatch '^\s*\|[\s\-:|]+\|\s*$') { continue }
                if ($lines[$i - 1] -notmatch '^\s*\|') { continue }
                $headerIndex = $i - 1
                if ($headerIndex -gt 0 -and $lines[$headerIndex - 1].Trim() -ne '') {
                    $rel = [System.IO.Path]::GetRelativePath($script:PortOut, $file.FullName)
                    $offenders += "${rel}:$($headerIndex + 1)"
                }
            }
        }
        $offenders -join ', ' | Should -BeNullOrEmpty
    }

    It 'resolves every relative link in the generated docs to a file that exists' {
        # guides/index.md is assembled from root-relative links lifted out of
        # index.md, so a target one directory up (security.md) silently 404s
        # unless it is rebased to ../. Site-absolute MS Learn paths (/windows,
        # /azure) are intentional and are not resolved against the output tree.
        $broken = @()
        foreach ($file in Get-ChildItem $script:PortOut -Recurse -File -Filter *.md) {
            $content = Get-Content $file.FullName -Raw
            foreach ($m in [regex]::Matches($content, '\]\(([^)\s]+)\)')) {
                $href = $m.Groups[1].Value
                if ($href -match '^(https?:|mailto:|#|/)') { continue }
                $path = ($href -split '#')[0]
                if (-not $path) { continue }
                $target = Join-Path $file.DirectoryName ($path -replace '/', '\')
                if (-not (Test-Path $target)) {
                    $rel = [System.IO.Path]::GetRelativePath($script:PortOut, $file.FullName)
                    $broken += "$rel -> $href"
                }
            }
        }
        $broken -join ', ' | Should -BeNullOrEmpty
    }
}
