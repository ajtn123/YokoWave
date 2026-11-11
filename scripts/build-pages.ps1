param([string]$OutputDirectory = 'artifacts/pages')

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputPath = [IO.Path]::GetFullPath($OutputDirectory, $projectRoot)
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
if (!$outputPath.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Pages output must be a subdirectory of artifacts/.'
}

$languages = @('en', 'zh-CN', 'ja', 'ko')
$modules = @(foreach ($directory in Get-ChildItem -LiteralPath (Join-Path $projectRoot 'modules') -Directory | Sort-Object Name) {
    if (!(Test-Path -LiteralPath (Join-Path $directory.FullName 'index.html'))) { continue }
    $manifest = Get-Content -LiteralPath (Join-Path $directory.FullName 'module.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($manifest.id -cne $directory.Name -or $manifest.id -cnotmatch '^[a-z][a-z0-9-]*$') {
        throw "Module ID must match its directory: $($directory.Name)"
    }
    foreach ($language in $languages) {
        $catalog = Get-Content -LiteralPath (Join-Path $projectRoot "locales/$($manifest.id)/$language.json") -Raw | ConvertFrom-Json -AsHashtable
        foreach ($key in @($manifest.titleKey, $manifest.descriptionKey)) {
            if ([string]::IsNullOrWhiteSpace($key) -or !($catalog[$key] -is [string]) -or [string]::IsNullOrWhiteSpace($catalog[$key])) {
                throw "Module metadata needs a translated title and summary: $($manifest.id)/$language"
            }
        }
    }
    [ordered]@{ id = $manifest.id; url = "modules/$($manifest.id)/"; titleKey = $manifest.titleKey; descriptionKey = $manifest.descriptionKey }
})

# The entire payload comes from these public static-source directories, never the repository root.
$publicGroups = @('site') + @($modules | ForEach-Object { $_.id })
foreach ($source in @('site', 'modules/shared') + @($modules | ForEach-Object { "modules/$($_.id)" }) + @($publicGroups | ForEach-Object { "locales/$_" })) {
    $links = Get-ChildItem -LiteralPath (Join-Path $projectRoot $source) -Recurse -Force -Attributes ReparsePoint
    if ($links) { throw "Public sources cannot contain symbolic links: $source" }
}
if (Test-Path -LiteralPath $outputPath) {
    if ((Get-Item -LiteralPath $outputPath).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Pages output cannot be a symbolic link.' }
    Remove-Item -LiteralPath $outputPath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $outputPath, (Join-Path $outputPath 'assets'), (Join-Path $outputPath 'modules'), (Join-Path $outputPath 'locales') | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot 'site') -Destination $outputPath -Recurse
Copy-Item -LiteralPath (Join-Path $projectRoot 'modules/shared') -Destination (Join-Path $outputPath 'modules') -Recurse
foreach ($module in $modules) {
    Copy-Item -LiteralPath (Join-Path $projectRoot "modules/$($module.id)") -Destination (Join-Path $outputPath 'modules') -Recurse
}
foreach ($group in $publicGroups) {
    New-Item -ItemType Directory -Force -Path (Join-Path $outputPath "locales/$group") | Out-Null
    foreach ($language in $languages) {
        Copy-Item -LiteralPath (Join-Path $projectRoot "locales/$group/$language.json") -Destination (Join-Path $outputPath "locales/$group")
    }
}
Copy-Item -LiteralPath (Join-Path $projectRoot 'src/YokoWave.App/Assets/YokoWave.svg') -Destination (Join-Path $outputPath 'assets')
Copy-Item -LiteralPath (Join-Path $projectRoot 'docs/assets/social-preview.png') -Destination (Join-Path $outputPath 'assets')
ConvertTo-Json -InputObject @($modules) -Depth 4 | Set-Content -LiteralPath (Join-Path $outputPath 'site/modules.json') -Encoding utf8NoBOM

# Pre-render the default language so the homepage remains readable without JavaScript.
$catalog = Get-Content -LiteralPath (Join-Path $projectRoot 'locales/site/zh-CN.json') -Raw | ConvertFrom-Json -AsHashtable
$html = Get-Content -LiteralPath (Join-Path $projectRoot 'site/index.html') -Raw
$html = [regex]::Replace($html, '(?<open><(?<tag>[a-z][a-z0-9]*)\b[^>]*\bdata-i18n="(?<key>[^"]+)"[^>]*>)[^<]*(?<close></\k<tag>>)', {
    param($match)
    $key = $match.Groups['key'].Value
    if (!$catalog.Contains($key)) { throw "Missing homepage translation: $key" }
    $match.Groups['open'].Value + [Net.WebUtility]::HtmlEncode($catalog[$key]) + $match.Groups['close'].Value
})
$template = [regex]::Match($html, '<template id="module-template">(?<card>[\s\S]*?)</template>').Groups['card'].Value
$cards = foreach ($module in $modules) {
    $moduleCatalog = Get-Content -LiteralPath (Join-Path $projectRoot "locales/$($module.id)/zh-CN.json") -Raw | ConvertFrom-Json -AsHashtable
    $card = $template.Replace('<a class="module-card">', "<a class=`"module-card`" href=`"$($module.url)?lang=zh-CN`">")
    foreach ($field in @(@('module-name', $moduleCatalog[$module.titleKey]), @('module-description', $moduleCatalog[$module.descriptionKey]), @('module-open', $catalog['Site.Open'] + ' ↗'))) {
        $class = $field[0]
        $text = [Net.WebUtility]::HtmlEncode($field[1])
        $card = [regex]::Replace($card, "(?<open><[^>]+class=`"$class`">)[^<]*(?<close></[^>]+>)", { param($match) $match.Groups['open'].Value + $text + $match.Groups['close'].Value })
    }
    $card
}
$html = $html.Replace('<div id="module-list" class="module-list" aria-live="polite"></div>', '<div id="module-list" class="module-list" aria-live="polite">' + ($cards -join '') + '</div>')
$html | Set-Content -LiteralPath (Join-Path $outputPath 'index.html') -Encoding utf8NoBOM
Remove-Item -LiteralPath (Join-Path $outputPath 'site/index.html')
'' | Set-Content -LiteralPath (Join-Path $outputPath '.nojekyll') -Encoding utf8NoBOM
Write-Output "Pages payload: $outputPath ($($modules.Count) modules)"
