# Plugin manifests whose "version" fields must all equal version.json. generate-llm-docs.ps1 stamps
# them (build-cli.ps1 runs it, including on both release branches start-release.ps1 creates), and
# validate-llm-docs.ps1 fails CI when any of them drifts. Every plugin this repo ships follows the
# repo version, so a release updates all of them in the same change.
function Get-VersionedPluginManifests {
    param([Parameter(Mandatory)][string]$ProjectRoot)
    @(
        'plugin.json',
        'plugins\winapp\plugin.json',
        'plugins\winapp\.claude-plugin\plugin.json',
        'plugins\winui\agent-plugin\plugin.json',
        'plugins\winui\.claude-plugin\plugin.json',
        'plugins\winui\.codex-plugin\plugin.json',
        '.github\plugin\marketplace.json',
        '.claude-plugin\marketplace.json'
    ) | ForEach-Object { Join-Path $ProjectRoot $_ }
}
