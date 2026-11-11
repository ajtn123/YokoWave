param(
    [Parameter(Mandatory)][string]$BundleDirectory,
    [Parameter(Mandatory)][string]$WorkDirectory,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)

$ErrorActionPreference = 'Stop'
if (!$IsMacOS) { throw 'macOS bundle creation requires macOS.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$resources = Join-Path $BundleDirectory 'Contents/Resources'
$iconset = Join-Path $WorkDirectory 'YokoWave.iconset'
New-Item -ItemType Directory -Force -Path $resources, $iconset | Out-Null

# The existing ICO contains PNG entries; reuse its largest image for the macOS icon.
$icon = [IO.File]::ReadAllBytes((Join-Path $projectRoot 'src/YokoWave.App/Assets/YokoWave.ico'))
$entries = for ($index = 0; $index -lt [BitConverter]::ToUInt16($icon, 4); $index++) {
    $entry = 6 + 16 * $index
    [pscustomobject]@{ Length = [BitConverter]::ToUInt32($icon, $entry + 8); Offset = [BitConverter]::ToUInt32($icon, $entry + 12) }
}
$largest = $entries | Sort-Object Length -Descending | Select-Object -First 1
$png = Join-Path $WorkDirectory 'YokoWave.png'
[IO.File]::WriteAllBytes($png, $icon[$largest.Offset..($largest.Offset + $largest.Length - 1)])
foreach ($size in @(16, 32, 128, 256, 512)) {
    foreach ($scale in @(1, 2)) {
        $suffix = if ($scale -eq 2) { '@2x' } else { '' }
        $pixels = $size * $scale
        & sips -z $pixels $pixels $png --out (Join-Path $iconset "icon_$($size)x$size$suffix.png") | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Icon resizing failed.' }
    }
}
& iconutil --convert icns --output (Join-Path $resources 'YokoWave.icns') $iconset
if ($LASTEXITCODE -ne 0) { throw 'macOS icon creation failed.' }

$catalogs = @{}
foreach ($language in @('en', 'zh-CN', 'ja', 'ko')) {
    $catalog = Get-Content (Join-Path $projectRoot "locales/app/$language.json") -Raw | ConvertFrom-Json -AsHashtable
    $catalogs[$language] = $catalog
    $directory = Join-Path $resources "$language.lproj"
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $microphone = $catalog['App.MicrophonePermission'].Replace('\', '\\').Replace('"', '\"')
    "NSMicrophoneUsageDescription = `"$microphone`";" | Set-Content -LiteralPath (Join-Path $directory 'InfoPlist.strings') -Encoding utf8NoBOM
}
$plist = [IO.File]::ReadAllText((Join-Path $projectRoot 'src/YokoWave.Desktop/Packaging/Info.plist'))
$plist = $plist.Replace('@VERSION@', $Version).Replace('@MICROPHONE_USAGE@', [Security.SecurityElement]::Escape($catalogs['en']['App.MicrophonePermission']))
$plistPath = Join-Path $BundleDirectory 'Contents/Info.plist'
[IO.File]::WriteAllText($plistPath, $plist)
& plutil -lint $plistPath
if ($LASTEXITCODE -ne 0) { throw 'Invalid macOS bundle metadata.' }
& codesign --force --deep --sign - $BundleDirectory
if ($LASTEXITCODE -ne 0) { throw 'Ad-hoc bundle signing failed.' }
& codesign --verify --deep --strict $BundleDirectory
if ($LASTEXITCODE -ne 0) { throw 'Bundle signature verification failed.' }
