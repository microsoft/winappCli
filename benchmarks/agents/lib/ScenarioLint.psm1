Set-StrictMode -Version Latest

# Words that carry no routing signal. Content words are the remaining tokens of 3+ characters.
$script:StopWords = [System.Collections.Generic.HashSet[string]]::new([string[]]@(
        'a', 'about', 'above', 'after', 'again', 'all', 'also', 'am', 'an', 'and', 'any', 'are', 'as', 'at', 'be', 'been',
        'before', 'being', 'both', 'but', 'by', 'can', 'could', 'did', 'do', 'does', 'doing', 'done', 'don', 'down', 'each',
        'either', 'else', 'even', 'every', 'for', 'from', 'get', 'gets', 'getting', 'give', 'go', 'going', 'got', 'had', 'has',
        'have', 'having', 'he', 'her', 'here', 'him', 'his', 'how', 'i', 'if', 'in', 'into', 'is', 'isn', 'it', 'its', 'just',
        'let', 'like', 'll', 'make', 'me', 'more', 'most', 'much', 'must', 'my', 'need', 'needs', 'no', 'not', 'now', 'of',
        'off', 'on', 'once', 'one', 'only', 'or', 'other', 'our', 'out', 'over', 'own', 're', 'same', 'see', 'she', 'should',
        'so', 'some', 'still', 'such', 'than', 'that', 'the', 'their', 'them', 'then', 'there', 'these', 'they', 'this',
        'those', 'through', 'to', 'too', 'under', 'until', 'up', 'us', 'use', 'used', 'using', 'very', 've', 'want', 'was',
        'we', 'were', 'what', 'when', 'where', 'whether', 'which', 'while', 'who', 'why', 'will', 'with', 'without', 'won',
        'would', 'yet', 'you', 'your', 'e.g', 'i.e', 'etc', 'via', 'per', 'instead', 'rather', 'already', 'every', 'way',
        'thing', 'things', 'something', 'anything', 'work', 'works', 'working', 'new', 'one', 'two', 'three', 'first'
    ), [System.StringComparer]::OrdinalIgnoreCase)

# Framework, product, and generic project words a developer uses to describe their own context. A pair
# made only of these ("WinUI app", "Windows desktop", "Microsoft Store") is not distinctive phrasing, and they are
# left out of the Jaccard overlap so naming your own framework does not count as echoing a description.
$script:NeutralWords = [System.Collections.Generic.HashSet[string]]::new([string[]]@(
        'window', 'desktop', 'app', 'application', 'winui', 'wpf', 'winform', 'electron', 'tauri', 'flutter', 'maui',
        'net', 'rust', 'c++', 'c#', 'microsoft', 'store', 'build', 'error', 'project', 'code', 'file', 'line', 'command', 'api',
        'msix', 'xaml', 'sdk', 'csproj'
    ), [System.StringComparer]::OrdinalIgnoreCase)

function Get-LintTokens {
    # Lowercase word tokens, keeping file-name and version shapes (main.js, net8.0, c++) intact, with
    # a light plural strip so "packages" and "package" match.
    param([AllowEmptyString()][string]$Text)
    if (-not $Text) { return @() }
    $tokens = foreach ($m in [regex]::Matches($Text.ToLowerInvariant(), "[a-z0-9][a-z0-9+#._-]*[a-z0-9+#]|[a-z0-9]")) {
        $t = $m.Value.Trim('.', '-', '_')
        if ($t.Length -gt 3 -and -not $script:StopWords.Contains($t) -and $t -notmatch '\.' -and $t -match '[a-z]s$' -and $t -notmatch '(ss|us|is|os)$') { $t = $t.Substring(0, $t.Length - 1) }
        if ($t) { $t }
    }
    return @($tokens)
}

function Test-ContentWord {
    param([string]$Token)
    return $Token.Length -ge 3 -and -not $script:StopWords.Contains($Token) -and $Token -notmatch '^\d+$'
}

function Get-ContentWords {
    param([AllowEmptyString()][string]$Text, [switch]$ExcludeNeutral)
    return @(Get-LintTokens $Text | Where-Object { (Test-ContentWord $_) -and -not ($ExcludeNeutral -and $script:NeutralWords.Contains($_)) } | Select-Object -Unique)
}

function Get-ContentBigrams {
    # Adjacent token pairs where both tokens are content words and at least one is not neutral
    # platform vocabulary; stop words and punctuation break adjacency.
    param([AllowEmptyString()][string]$Text)
    $pairs = foreach ($segment in [regex]::Split($Text, '[,;:!?()\[\]{}"`“”‘’/\\|]|\.(?=\s|$)|\s[-–—]+\s')) {
        $t = @(Get-LintTokens $segment)
        for ($i = 0; $i -lt $t.Count - 1; $i++) {
            $a = $t[$i]; $b = $t[$i + 1]
            if (-not (Test-ContentWord $a) -or -not (Test-ContentWord $b)) { continue }
            if ($script:NeutralWords.Contains($a) -and $script:NeutralWords.Contains($b)) { continue }
            "$a $b"
        }
    }
    return @($pairs | Select-Object -Unique)
}

function Get-Jaccard {
    param([AllowEmptyCollection()][string[]]$A = @(), [AllowEmptyCollection()][string[]]$B = @())
    $setA = [System.Collections.Generic.HashSet[string]]::new([string[]]@($A))
    $union = [System.Collections.Generic.HashSet[string]]::new([string[]]@($A))
    $union.UnionWith([string[]]@($B))
    if ($union.Count -eq 0) { return 0.0 }
    $setA.IntersectWith([string[]]@($B))
    return [double]$setA.Count / $union.Count
}

function Get-FrontmatterDescription {
    param([Parameter(Mandatory)][string]$Path)
    $lines = @([System.IO.File]::ReadAllLines($Path))
    if (-not $lines -or $lines[0].Trim() -ne '---') { return $null }
    for ($i = 1; $i -lt $lines.Count -and $lines[$i].Trim() -ne '---'; $i++) {
        if ($lines[$i] -notmatch '^description:\s*(.*)$') { continue }
        $value = $Matches[1].Trim()
        if ($value -in '', '>', '|', '>-', '|-') {
            $block = for ($j = $i + 1; $j -lt $lines.Count -and $lines[$j] -match '^\s+\S'; $j++) { $lines[$j].Trim() }
            return ($block -join ' ')
        }
        return $value.Trim('"', "'")
    }
    return $null
}

function Get-LintCorpus {
    # Skill and agent descriptions of the given plugin folders: what a prompt must not echo.
    param([Parameter(Mandatory)][string[]]$PluginPaths)
    $docs = foreach ($root in $PluginPaths) {
        $files = @(Get-ChildItem -LiteralPath (Join-Path $root 'skills') -Filter SKILL.md -Recurse -File -ErrorAction SilentlyContinue) +
        @(Get-ChildItem -LiteralPath $root -Filter *.agent.md -Recurse -File -ErrorAction SilentlyContinue)
        foreach ($f in $files) {
            $d = Get-FrontmatterDescription -Path $f.FullName
            if (-not $d) { continue }
            $kind = if ($f.Name -eq 'SKILL.md') { "skill $($f.Directory.Name)" } else { "agent $($f.BaseName -replace '\.agent$', '')" }
            [pscustomobject]@{ Name = $kind; Words = @(Get-ContentWords $d -ExcludeNeutral); Bigrams = @(Get-ContentBigrams $d) }
        }
    }
    # Identical descriptions (the same agent shipped twice) count once.
    $docs = @($docs | Group-Object { $_.Name + '|' + ($_.Words -join ' ') } | ForEach-Object { $_.Group[0] })
    $df = @{}
    foreach ($d in $docs) { foreach ($b in $d.Bigrams) { $df[$b] = 1 + ($df.ContainsKey($b) ? $df[$b] : 0) } }
    return [pscustomobject]@{ Documents = $docs; BigramDocFrequency = $df }
}

function Get-ScenarioLeakage {
    # Distinctive bigrams (in at most $MaxDocFrequency descriptions) a prompt shares with any
    # description, and its highest content-word Jaccard overlap with a single description.
    param(
        [Parameter(Mandatory)][AllowEmptyString()][string]$Text,
        [Parameter(Mandatory)]$Corpus,
        [int]$MaxDocFrequency = 3,
        [string[]]$Allow = @()
    )
    $allowed = @($Allow | ForEach-Object { (Get-LintTokens $_) -join ' ' })
    $bigrams = @(Get-ContentBigrams $Text)
    $words = @(Get-ContentWords $Text -ExcludeNeutral)
    $shared = foreach ($b in $bigrams) {
        if ($b -in $allowed -or -not $Corpus.BigramDocFrequency.ContainsKey($b)) { continue }
        if ($Corpus.BigramDocFrequency[$b] -gt $MaxDocFrequency) { continue }
        $in = @($Corpus.Documents | Where-Object { $b -in $_.Bigrams } | ForEach-Object Name)
        [pscustomobject]@{ Bigram = $b; Documents = $in }
    }
    $best = $null
    foreach ($d in $Corpus.Documents) {
        $j = Get-Jaccard $words $d.Words
        if (-not $best -or $j -gt $best.Jaccard) { $best = [pscustomobject]@{ Jaccard = $j; Document = $d.Name } }
    }
    [pscustomobject]@{ SharedBigrams = @($shared); MaxJaccard = if ($best) { $best.Jaccard } else { 0.0 }; MaxJaccardDocument = if ($best) { $best.Document } else { $null } }
}

function Invoke-ScenarioLint {
    # Leakage and fixture-realism findings. Held-out scenarios get errors where dev scenarios get warnings.
    param(
        [Parameter(Mandatory)][object[]]$Scenarios,
        [Parameter(Mandatory)]$Corpus,
        [double]$MaxJaccard = 0.10,
        [int]$MaxDocFrequency = 3
    )
    $pathPattern = '(?<![\w])[\w.\\/-]*[\w-]\.(?:msix|msixbundle|appx|appinstaller|pfx|cer|png|ico|json|xml|xaml|cs|cpp|h|hpp|yaml|yml|toml|ps1|wxs|csproj|vcxproj|appxmanifest|txt|log|js|ts|html|rs|dart|exe|dll|sln|config|cmake|gradle)\b'
    foreach ($s in $Scenarios) {
        $level = if ($s.Set -eq 'heldout') { 'error' } else { 'warning' }
        $files = @()
        if ($s.FixturePath) {
            $root = $s.FixturePath.TrimEnd('\') + '\'
            $files = @(Get-ChildItem -LiteralPath $s.FixturePath -Recurse -File -Force | ForEach-Object {
                    [pscustomobject]@{ Relative = $_.FullName.Substring($root.Length); Name = $_.Name; Length = $_.Length }
                })
        }
        # File names are visible to the agent, so they count as prompt text.
        $names = @($files | ForEach-Object { ($_.Relative -creplace '([a-z])([A-Z])', '$1 $2') -replace '[\\/._-]', ' ' })
        $text = (@($s.Prompt) + $names) -join "`n"
        $leak = Get-ScenarioLeakage -Text $text -Corpus $Corpus -MaxDocFrequency $MaxDocFrequency -Allow $s.LeakAllow
        foreach ($b in $leak.SharedBigrams) {
            [pscustomobject]@{ Scenario = $s.Id; Set = $s.Set; Level = $level; Rule = 'leak-bigram'; Message = "shares '$($b.Bigram)' with $($b.Documents -join ', ')" }
        }
        if ($leak.MaxJaccard -gt $MaxJaccard) {
            [pscustomobject]@{ Scenario = $s.Id; Set = $s.Set; Level = $level; Rule = 'leak-jaccard'; Message = ('content-word overlap {0:N3} with {1} (max {2})' -f $leak.MaxJaccard, $leak.MaxJaccardDocument, $MaxJaccard) }
        }
        if ($s.Set -eq 'heldout') {
            if ($text -match '(?i)\bcontoso\b') { [pscustomobject]@{ Scenario = $s.Id; Set = $s.Set; Level = 'error'; Rule = 'fixed-name'; Message = 'uses the name Contoso; held-out scenarios use randomized names' } }
            if ($s.Prompt -match '(?i)\bwinapp\b|\bskill\b|\bplugin\b') { [pscustomobject]@{ Scenario = $s.Id; Set = $s.Set; Level = 'error'; Rule = 'names-tooling'; Message = 'prompt names the tooling under test' } }
        }
        if (-not $s.RoutingSnapshot) {
            foreach ($f in $files | Where-Object { $_.Length -eq 0 }) {
                if ($s.Prompt -match [regex]::Escape($f.Name)) {
                    [pscustomobject]@{ Scenario = $s.Id; Set = $s.Set; Level = 'error'; Rule = 'fixture-empty'; Message = "prompt references '$($f.Relative)', which is an empty file (mark the scenario routingSnapshot or give it content)" }
                }
            }
            foreach ($m in [regex]::Matches($s.Prompt, $pathPattern)) {
                $leaf = Split-Path ($m.Value -replace '/', '\') -Leaf
                if (-not @($files | Where-Object Name -eq $leaf)) {
                    [pscustomobject]@{ Scenario = $s.Id; Set = $s.Set; Level = 'warning'; Rule = 'fixture-missing'; Message = "prompt mentions '$($m.Value)', which the fixture does not contain (add it or mark the scenario routingSnapshot)" }
                }
            }
        }
    }
}

Export-ModuleMember -Function Get-LintTokens, Get-ContentWords, Get-ContentBigrams, Get-Jaccard, Get-FrontmatterDescription,
Get-LintCorpus, Get-ScenarioLeakage, Invoke-ScenarioLint
