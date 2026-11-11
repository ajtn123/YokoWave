param([Parameter(Mandatory)][ValidatePattern('^v\d+\.\d+\.\d+$')][string]$Tag)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = $Tag.Substring(1)
$outputPath = Join-Path $projectRoot 'artifacts/release'
$notes = Join-Path $projectRoot "docs/releases/$version.md"
if (!(Test-Path -LiteralPath $notes)) { throw 'Release notes are missing.' }
if ((Get-Content -LiteralPath $notes -Raw) -match '(?m)^#\s') { throw 'Release notes must not repeat the release title as a level-one heading.' }
$expected = foreach ($runtime in @('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')) {
    $extension = if ($runtime.StartsWith('win-')) { '.zip' } else { '.tar.gz' }
    "YokoWave-$Tag-$runtime$extension"
}
foreach ($file in $expected) {
    if (!(Test-Path -LiteralPath (Join-Path $outputPath $file))) { throw "Missing release package: $file" }
}

$files = $expected | ForEach-Object { Join-Path $outputPath $_ }

Push-Location $projectRoot
try {
    $state = & gh release view $Tag --json isDraft,assets 2>$null
    if ($LASTEXITCODE -eq 0) {
        $release = $state | ConvertFrom-Json
        if (!$release.isDraft) { throw 'This version is already published; published assets are not overwritten.' }
        if ($release.assets | Where-Object { $_.name -notin $expected }) { throw 'The draft contains unexpected assets; review it before publishing.' }
    }
    else {
        & gh release create $Tag --verify-tag --draft --title "YokoWave $Tag" --notes-file $notes
        if ($LASTEXITCODE -ne 0) { throw 'Could not create the draft release.' }
    }
    & gh release upload $Tag @files --clobber
    if ($LASTEXITCODE -ne 0) { throw 'Asset upload failed; the release remains a draft.' }
    & gh release edit $Tag --draft=false --latest --notes-file $notes
    if ($LASTEXITCODE -ne 0) { throw 'Could not publish the release.' }
}
finally {
    Pop-Location
}
