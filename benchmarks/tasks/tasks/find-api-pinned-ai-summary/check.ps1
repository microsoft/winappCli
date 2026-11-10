param([string]$Workspace, $Baseline, [string]$OutFile)
$r = New-CheckResult -Task 'find-api-pinned-ai-summary'
$pinned = '1.7.260224002'

$sources = @(Get-ChildItem -LiteralPath $Workspace -Recurse -File -Include '*.cs', '*.csproj', '*.props', '*.targets' -ErrorAction SilentlyContinue | Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' })
$project = @($sources | Where-Object Extension -eq '.csproj' | Sort-Object { $_.Name -ne 'NoteKeeper.Core.csproj' }) | Select-Object -First 1
if (-not $project) {
    [void](Add-Check $r 'builds' $false 'no .csproj' -Required)
    Complete-CheckResult -Result $r -OutFile $OutFile
    return
}

$build = Invoke-DotnetBuild $project.FullName
# The fixture already builds, so building is a guard: it must still hold but isn't progress.
[void](Add-Check $r 'builds' $build.Ok $build.Detail -Guard)

# The method must exist with the asked-for shape: NoteSummary.SummarizeAsync(Note) returning Task<string>.
$probe = Join-Path $project.DirectoryName 'BenchProbe.g.cs'
Set-Content -LiteralPath $probe -Encoding utf8NoBOM -Value 'internal static class BenchProbe { internal static global::System.Threading.Tasks.Task<string> Probe() => global::NoteKeeper.NoteSummary.SummarizeAsync(new global::NoteKeeper.Note("t", "b")); }'
try { $probeBuild = Invoke-DotnetBuild $project.FullName } finally { Remove-Item -LiteralPath $probe -ErrorAction SilentlyContinue }
[void](Add-Check $r 'method-callable' ($build.Ok -and $probeBuild.Ok) "NoteKeeper.NoteSummary.SummarizeAsync(Note) returning Task<string>, called from a probe file: $($probeBuild.Detail)" -Required)

# The summary must come from the Windows App SDK's on-device model. In the pinned version that is
# TextSummarizer over a LanguageModel; LanguageModel.GenerateResponseAsync doesn't exist there.
$all = (@($sources | Where-Object Extension -eq '.cs' | ForEach-Object { Get-CSharpCode $_.FullName }) -join "`n")
$model = $all -cmatch '\bLanguageModel\s*\.\s*CreateAsync\s*\('
$summarizer = $all -cmatch '\bnew\s+(Microsoft\.Windows\.AI\.Text\.)?TextSummarizer\s*\(' -and $all -cmatch '\.Summarize(Paragraph)?Async\s*\('
[void](Add-Check $r 'on-device-summary' ($model -and $summarizer) "LanguageModel.CreateAsync(): $model; TextSummarizer with SummarizeAsync or SummarizeParagraphAsync: $summarizer" -Required)

# Guard: the Windows App SDK stays on the pinned version, with no newer Windows App SDK package beside it.
$items = $null
try { $items = (& dotnet msbuild $project.FullName -nologo -getItem:PackageReference 2>$null | Out-String | ConvertFrom-Json).Items.PackageReference } catch { }
$wasdk = @($items | Where-Object { $_.Identity -like 'Microsoft.WindowsAppSDK*' } | ForEach-Object { "$($_.Identity) $($_.Version)" })
$pinnedOk = ($wasdk.Count -eq 1) -and ($wasdk[0] -eq "Microsoft.WindowsAppSDK $pinned")
[void](Add-Check $r 'sdk-pinned' $pinnedOk "Windows App SDK references: $(if ($wasdk) { $wasdk -join ', ' } else { 'none' }) (expected only Microsoft.WindowsAppSDK $pinned)" -Guard)

# Guard: no hand-written stand-in for the AI types, and no reflection or dynamic to hide a missing member.
$hacks = [System.Collections.Generic.List[string]]::new()
foreach ($m in [regex]::Matches($all, '\b(class|struct|record|interface)\s+(LanguageModel|TextSummarizer|LanguageModelResponseResult)\b|\bnamespace\s+Microsoft\.Windows\.AI\b')) { $hacks.Add("declares '$($m.Value)'") }
if ($all -cmatch '\bdynamic\b') { $hacks.Add('dynamic') }
if ($all -cmatch '\.(GetMethod|InvokeMember)\s*\(') { $hacks.Add('reflection') }
[void](Add-Check $r 'no-stand-ins' (-not $hacks.Count) ($(if ($hacks.Count) { $hacks -join '; ' } else { 'no stand-in AI types, dynamic or reflection' })) -Guard)

# Improvement: the model may not be installed yet, so the code checks or ensures readiness first.
$ready = $all -cmatch '\bLanguageModel\s*\.\s*(GetReadyState|EnsureReadyAsync)\s*\('
[void](Add-Check $r 'checks-model-ready' $ready "calls LanguageModel.GetReadyState() or EnsureReadyAsync(): $ready")

$r.facts.project = $project.FullName
Complete-CheckResult -Result $r -OutFile $OutFile
