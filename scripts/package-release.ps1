param(
    [Parameter(Mandatory)][ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string]$Runtime,
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputDirectory = 'artifacts/release'
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw 'Expected a MAJOR.MINOR.PATCH version.' }
[xml]$props = Get-Content (Join-Path $projectRoot 'Directory.Build.props') -Raw
if ($props.Project.PropertyGroup.Version -ne $Version) { throw 'Package and project version do not match.' }
if ($Runtime.StartsWith('win-') -and !$IsWindows -or $Runtime.StartsWith('linux-') -and !$IsLinux -or $Runtime.StartsWith('osx-') -and !$IsMacOS) {
    throw 'Create the package on its target operating system; cross-architecture packaging is supported.'
}

$outputPath = [IO.Path]::GetFullPath($OutputDirectory, $projectRoot)
$packageName = "YokoWave-v$Version-$Runtime"
$extension = if ($Runtime.StartsWith('win-')) { '.zip' } else { '.tar.gz' }
$archivePath = Join-Path $outputPath ($packageName + $extension)
if (Test-Path -LiteralPath $archivePath) { throw "Package already exists: $archivePath" }
$workPath = Join-Path $outputPath "work/$Runtime-$([Guid]::NewGuid().ToString('N'))"
$payload = Join-Path $workPath $packageName
$applicationPath = if ($Runtime.StartsWith('osx-')) { Join-Path $payload 'YokoWave.app/Contents/MacOS' } else { $payload }
New-Item -ItemType Directory -Force -Path $applicationPath | Out-Null

Push-Location $projectRoot
try {
    & dotnet publish src/YokoWave.Desktop -c Release -r $Runtime --self-contained true --nologo -o $applicationPath `
        -p:Version=$Version -p:ContinuousIntegrationBuild=true -p:PublishTrimmed=false -p:PublishSingleFile=false -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed.' }

    # Dependencies can ship prebuilt symbols despite DebugSymbols=false. Remove them before macOS signing.
    foreach ($symbols in Get-ChildItem -LiteralPath $applicationPath -Recurse -File -Force | Where-Object { $_.Extension -ieq '.pdb' }) {
        Remove-Item -LiteralPath $symbols.FullName -Force
    }

    $executable = if ($Runtime.StartsWith('win-')) { 'YokoWave.exe' } else { 'YokoWave' }
    $nativeAudio = if ($Runtime.StartsWith('win-')) { 'miniaudio.dll' } elseif ($Runtime.StartsWith('osx-')) { 'libminiaudio.dylib' } else { 'libminiaudio.so' }
    foreach ($file in @($executable, 'YokoWave.dll', 'System.Private.CoreLib.dll', 'Microsoft.AspNetCore.Server.Kestrel.Core.dll', $nativeAudio)) {
        if (!(Test-Path -LiteralPath (Join-Path $applicationPath $file))) { throw "Missing package dependency: $file" }
    }
    if (!$IsWindows) {
        & chmod +x (Join-Path $applicationPath $executable)
        if ($LASTEXITCODE -ne 0) { throw 'Could not set executable permissions.' }
    }
    if ($Runtime.StartsWith('osx-')) {
        & (Join-Path $PSScriptRoot 'package-macos.ps1') -BundleDirectory (Join-Path $payload 'YokoWave.app') -WorkDirectory $workPath -Version $Version
    }
    Copy-Item -LiteralPath LICENSE, THIRD-PARTY-NOTICES.md -Destination $payload
    $launch = if ($Runtime.StartsWith('osx-')) { 'Open YokoWave.app.' } elseif ($Runtime.StartsWith('win-')) { 'Run YokoWave.exe.' } else { 'Run ./YokoWave.' }
    @"
YokoWave $Version ($Runtime)

$launch
Keep all files together. The .NET and ASP.NET Core runtimes are included.
The macOS bundle is ad-hoc signed; no developer certificate or notarization is provided.
Native Bluetooth is available on Windows. macOS/Linux support simulation and remote devices.

User guide: open Help in the application.
Downloads and platform details: https://github.com/ajtn123/YokoWave
Release notes: https://github.com/ajtn123/YokoWave/releases/tag/v$Version
"@ | Set-Content -LiteralPath (Join-Path $payload 'README.txt') -Encoding utf8NoBOM

    if ($IsWindows) {
        Compress-Archive -LiteralPath $payload -DestinationPath $archivePath -CompressionLevel Optimal
    }
    else {
        & tar -czf $archivePath -C $workPath $packageName
        if ($LASTEXITCODE -ne 0) { throw 'Archive creation failed.' }
    }
    Write-Output $archivePath
}
finally {
    Pop-Location
}
