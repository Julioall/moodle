[CmdletBinding()]
param(
    [Parameter()]
    [string]$PluginPath = "plugins/moodle-connector"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Fail([string]$Message) { throw "Plugin validation failed: $Message" }
function Require-NonEmptyString($Value, [string]$Field) {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value)) { Fail "$Field must be a non-empty string." }
}
function Read-JsonFile([string]$Path, [string]$Label) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { Fail "missing $Label." }
    try { return Get-Content -LiteralPath $Path -Raw -Encoding utf8 | ConvertFrom-Json -AsHashtable }
    catch { Fail "$Label must contain valid JSON: $($_.Exception.Message)" }
}

$pluginRoot = (Resolve-Path -LiteralPath $PluginPath).Path
$portablePath = Join-Path $pluginRoot "plugin.json"
$appManifestPath = Join-Path $pluginRoot ".app.json"
$mcpManifestPath = Join-Path $pluginRoot "mcp.json"
$skillsRoot = Join-Path $pluginRoot "skills"

$portable = Read-JsonFile $portablePath "plugin.json"
Require-NonEmptyString $portable.name "plugin.json name"
Require-NonEmptyString $portable.version "plugin.json version"
Require-NonEmptyString $portable.description "plugin.json description"
if ($portable.version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?$') { Fail "plugin.json version must use semantic versioning." }
if ($portable.extensions -isnot [hashtable] -or $portable.extensions["com.openai"] -isnot [hashtable]) { Fail "plugin.json must declare extensions.com.openai." }
$openAi = $portable.extensions["com.openai"]
if ($openAi.ContainsKey("apps")) { Fail "portable plugin.json must not bind a pre-registered MCP app through extensions.com.openai.apps." }
if ($openAi.ContainsKey("mcpServers")) { Fail "portable MCP servers must be declared in root mcp.json, not extensions.com.openai.mcpServers." }
if ($openAi.interface -isnot [hashtable]) { Fail "plugin.json extensions.com.openai.interface must be an object." }

if (Test-Path -LiteralPath $appManifestPath) { Fail "obsolete .app.json must be removed; this portable package connects through mcp.json." }

$mcp = Read-JsonFile $mcpManifestPath "mcp.json"
if ($mcp['$schema'] -ne "https://agent-plugins.org/schemas/1.0.0/mcp.schema.json") { Fail "mcp.json must declare the Agent Plugins MCP schema." }
if ($mcp.Keys.Count -ne 2 -or $mcp.mcpServers -isnot [hashtable]) { Fail 'mcp.json must contain only $schema and mcpServers.' }
if ($mcp.mcpServers.Count -ne 1 -or -not $mcp.mcpServers.ContainsKey("moodle")) { Fail "mcp.json must declare exactly one 'moodle' server." }
$moodleServer = $mcp.mcpServers.moodle
if ($moodleServer -isnot [hashtable]) { Fail "mcp.json server 'moodle' must be an object." }
if ($moodleServer.type -ne "streamable-http") { Fail "mcp.json server 'moodle' must use the streamable-http transport." }
if ($moodleServer.url -ne "https://novascript.com.br/mcp") { Fail "mcp.json server 'moodle' must point to https://novascript.com.br/mcp." }

if (Test-Path -LiteralPath (Join-Path $pluginRoot ".codex-plugin/plugin.json")) {
    Fail "remove the legacy .codex-plugin manifest so it cannot restore an account-specific app mapping."
}

if (-not (Test-Path -LiteralPath $skillsRoot -PathType Container)) { Fail "skills directory is missing." }
$skillDirectories = @(Get-ChildItem -LiteralPath $skillsRoot -Directory | Sort-Object Name)
if ($skillDirectories.Count -eq 0) { Fail "skills directory is empty." }

foreach ($skillDirectory in $skillDirectories) {
    if ($skillDirectory.Name -notmatch '^moodle-[a-z0-9-]+$') { Fail "skill directory '$($skillDirectory.Name)' must start with moodle-." }
    $skillPath = Join-Path $skillDirectory.FullName "SKILL.md"
    if (-not (Test-Path -LiteralPath $skillPath -PathType Leaf)) { Fail "skill '$($skillDirectory.Name)' is missing SKILL.md." }
    $contents = Get-Content -LiteralPath $skillPath -Raw -Encoding utf8
    if ($contents -match '\[TODO:') { Fail "skill '$($skillDirectory.Name)' contains a TODO placeholder." }
    if ($contents -notmatch '(?s)\A---\r?\n(?<frontmatter>.*?)\r?\n---') { Fail "skill '$($skillDirectory.Name)' must begin with closed YAML frontmatter." }
    $frontmatter = $Matches.frontmatter
    if ($frontmatter -notmatch '(?m)^name:\s*(?<name>\S+)\s*$') { Fail "skill '$($skillDirectory.Name)' frontmatter must declare name." }
    if ($Matches.name -ne $skillDirectory.Name) { Fail "skill '$($skillDirectory.Name)' frontmatter name '$($Matches.name)' must match the directory." }
    if ($frontmatter -notmatch '(?m)^description:\s*.+$') { Fail "skill '$($skillDirectory.Name)' frontmatter must declare description." }
    $metadataPath = Join-Path $skillDirectory.FullName "agents/openai.yaml"
    if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) { Fail "skill '$($skillDirectory.Name)' is missing agents/openai.yaml." }
}

$forbiddenGenerated = @(Get-ChildItem -LiteralPath $pluginRoot -Recurse -Force -File | Where-Object { $_.Extension -eq '.pyc' -or $_.FullName -match '[/\\]__pycache__[/\\]' })
if ($forbiddenGenerated.Count -gt 0) { Fail "generated Python bytecode must not be packaged: $($forbiddenGenerated[0].FullName)" }

$forbiddenConnectionAliases = @("goias", "ctm")
$publishedPluginFiles = Get-ChildItem -LiteralPath $pluginRoot -Recurse -File | Where-Object { $_.Extension -in @(".json", ".md", ".yaml", ".yml") }
foreach ($publishedPluginFile in $publishedPluginFiles) {
    $publishedContents = Get-Content -LiteralPath $publishedPluginFile.FullName -Raw -Encoding utf8
    foreach ($forbiddenConnectionAlias in $forbiddenConnectionAliases) {
        if ($publishedContents -match "(?i)\b$([regex]::Escape($forbiddenConnectionAlias))\b") { Fail "published plugin file '$($publishedPluginFile.FullName)' suggests the unregistered connection alias '$forbiddenConnectionAlias'." }
    }
}

Write-Host "Plugin validation passed: $pluginRoot ($($skillDirectories.Count) skills)"
