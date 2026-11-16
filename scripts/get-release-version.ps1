param(
    [Parameter(Mandatory)][ValidateSet('Desktop', 'Sdk', 'Minecraft')][string]$Product,
    [string]$Tag,
    [string]$OutputFile
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$prefix = switch ($Product) {
    'Desktop' { 'v' }
    'Sdk' { 'sdk-v' }
    'Minecraft' { 'minecraft-v' }
}
$version = switch ($Product) {
    'Desktop' {
        [xml]$props = Get-Content -LiteralPath (Join-Path $projectRoot 'Directory.Build.props') -Raw
        $versions = $props.SelectNodes('/Project/PropertyGroup/Version')
        if ($versions.Count -ne 1) { throw 'Expected one desktop project version.' }
        $versions[0].InnerText.Trim()
    }
    'Sdk' {
        [IO.File]::ReadAllText((Join-Path $projectRoot 'modules/sdk-version.txt')).Trim()
    }
    'Minecraft' {
        $properties = Get-Content -LiteralPath (Join-Path $projectRoot 'modules/minecraft/gradle.properties') -Raw
        $versions = [regex]::Matches($properties, '(?m)^mod_version=([^\r\n]*)\r?$')
        if ($versions.Count -ne 1) { throw 'Expected one Minecraft mod_version.' }
        $versions[0].Groups[1].Value.Trim()
    }
}
if ($version -cnotmatch '\A(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\z') {
    throw "Expected a stable MAJOR.MINOR.PATCH version for $Product."
}
if ($Product -eq 'Desktop') {
    if ([string]::IsNullOrEmpty($Tag)) { throw 'A desktop release requires an existing version tag.' }
    $notesPath = Join-Path $projectRoot "docs/releases/$version.md"
    if (!(Test-Path -LiteralPath $notesPath -PathType Leaf)) { throw 'Release notes are missing.' }
    if ((Get-Content -LiteralPath $notesPath -Raw) -match '(?m)^ {0,3}#(?:\s|$)') {
        throw 'Release notes must not repeat the release title as a level-one heading.'
    }
}

$commit = ''
if (![string]::IsNullOrEmpty($Tag)) {
    if ($Tag -cne "$prefix$version") { throw "Expected the tag $prefix$version for $Product." }
    $commit = & git -C $projectRoot rev-parse --verify "refs/tags/$Tag^{commit}" 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'The tag must already exist.' }
    $head = & git -C $projectRoot rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0) { throw 'Could not resolve the current checkout.' }
    if ($head -cne $commit) { throw 'Checkout does not match the tag.' }
}

$result = [pscustomobject]@{ version = $version; tag = $Tag; commit = $commit }
if (![string]::IsNullOrEmpty($OutputFile)) {
    $result.PSObject.Properties | ForEach-Object { "$($_.Name)=$($_.Value)" } |
        Add-Content -LiteralPath $OutputFile -Encoding utf8NoBOM
}
$result
