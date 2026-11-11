[CmdletBinding()]
param(
    [string]$RepoRoot = (Split-Path -Parent $PSScriptRoot),
    [switch]$IncludeArchive
)

$ErrorActionPreference = 'Stop'

$repositoryPath = [System.IO.Path]::GetFullPath($RepoRoot)
$docsPath = Join-Path $repositoryPath 'docs'
if (-not (Test-Path -LiteralPath $docsPath -PathType Container)) {
    throw "Documentation directory was not found: $docsPath"
}

$files = @(
    Get-ChildItem -LiteralPath $docsPath -Recurse -File -Filter '*.md' |
        Where-Object { $IncludeArchive -or $_.FullName -notmatch '[\\/]docs[\\/]archive[\\/]' }
)

foreach ($rootDocument in @('README.md', 'AGENTS.md')) {
    $path = Join-Path $repositoryPath $rootDocument
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        $files += Get-Item -LiteralPath $path
    }
}

$utf8 = [System.Text.UTF8Encoding]::new($false, $true)
# Characters U+00C3 (Ã) and U+00C2 (Â) are valid in Portuguese words.
# Check characteristic two-character mojibake sequences instead.
$mojibakePattern = '[\u00C3\u00C2][\u0080-\u00BF]|\uFFFD'
# Regression guard: do not reject valid Portuguese uppercase accented words.
if ('NÃO' -match $mojibakePattern -or 'VALIDAÇÃO' -match $mojibakePattern -or
    'Ã£' -notmatch $mojibakePattern -or 'Â©' -notmatch $mojibakePattern -or
    ([string][char]0xFFFD) -notmatch $mojibakePattern) {
    throw 'Mojibake detector self-check failed.'
}
$failures = [System.Collections.Generic.List[string]]::new()
$checkedLinks = 0
$removedDocumentationPaths = @(
    'DEPLOY.md',
    '.github/agent_rules.md',
    '.github/architecture.md',
    '.github/skills.md',
    'docs/documentation-audit.md',
    'docs/roadmap.md'
)
$historicalPathReferences = @{
    'docs/specs/spec-0030-canonical-application-reference.md' = @(
        'DEPLOY.md',
        '.github/agent_rules.md',
        '.github/architecture.md',
        '.github/skills.md',
        'docs/documentation-audit.md'
    )
}

foreach ($file in $files | Sort-Object FullName -Unique) {
    try {
        $content = $utf8.GetString([System.IO.File]::ReadAllBytes($file.FullName))
    }
    catch {
        $failures.Add("$($file.FullName): conteúdo não é UTF-8 válido ($($_.Exception.Message)).")
        continue
    }

    if ($content -match $mojibakePattern) {
        $failures.Add("$($file.FullName): possível sequência de mojibake ou caractere de substituição: '$($Matches[0])'.")
    }

    if ($content -match '(?i)(?<![\w/])TODO\.md(?![\w])') {
        $failures.Add("$($file.FullName): referência a TODO.md, que não existe no repositório.")
    }

    $relativeFilePath = [System.IO.Path]::GetRelativePath($repositoryPath, $file.FullName).Replace('\', '/')
    foreach ($removedPath in $removedDocumentationPaths) {
        $escapedPath = [regex]::Escape($removedPath)
        $pathPattern = "(?i)(?<![\w/])(?:\.\.?/)?$escapedPath(?![\w.-])"
        if ($content -match $pathPattern) {
            $historicalPaths = $historicalPathReferences[$relativeFilePath]
            if ($historicalPaths -notcontains $removedPath) {
                $failures.Add("$($file.FullName): referência em texto simples ao caminho removido '$removedPath'.")
            }
        }
    }

    $markdownLinks = [regex]::Matches($content, '\[[^\]]*\]\((?<target><[^>]+>|[^)\s]+)(?:\s+[^)]*)?\)')
    foreach ($match in $markdownLinks) {
        $target = $match.Groups['target'].Value.Trim('<', '>')
        if ([string]::IsNullOrWhiteSpace($target) -or $target.StartsWith('#') -or $target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') {
            continue
        }

        $pathPart = ([System.Uri]::UnescapeDataString($target) -split '#', 2)[0]
        if ([string]::IsNullOrWhiteSpace($pathPart)) {
            continue
        }

        $candidate = if ($pathPart.StartsWith('/')) {
            Join-Path $repositoryPath $pathPart.TrimStart('/')
        }
        else {
            Join-Path $file.DirectoryName $pathPart
        }

        try {
            $fullCandidate = [System.IO.Path]::GetFullPath($candidate)
        }
        catch {
            $failures.Add("$($file.FullName): link local inválido '$target'.")
            continue
        }

        if (-not $fullCandidate.StartsWith($repositoryPath, [System.StringComparison]::OrdinalIgnoreCase)) {
            $failures.Add("$($file.FullName): link local fora do repositório '$target'.")
            continue
        }

        $checkedLinks++
        if (-not (Test-Path -LiteralPath $fullCandidate)) {
            $failures.Add("$($file.FullName): destino de link não encontrado '$target'.")
        }
    }
}

if ($failures.Count -gt 0) {
    $failures | ForEach-Object { Write-Error $_ }
    throw "Documentation check failed with $($failures.Count) issue(s)."
}

Write-Host "Documentation check passed: $($files.Count) file(s) and $checkedLinks local link(s)."
