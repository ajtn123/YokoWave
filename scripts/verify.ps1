param([switch]$Offline)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    $restoreArgs = @('restore', 'YokoWave.slnx', '--nologo', '-m:1')
    if ($Offline) { $restoreArgs += @('--ignore-failed-sources', '-p:NuGetAudit=false') }
    & dotnet @restoreArgs
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }

    & dotnet build YokoWave.slnx -c Release --no-restore --nologo -m:1 -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

    $reportDirectory = Join-Path $projectRoot 'artifacts'
    New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
    & dotnet tests/YokoWave.Tests/bin/Release/net10.0/YokoWave.Tests.dll -xml (Join-Path $reportDirectory 'test-results.xml')
    if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
}
finally {
    Pop-Location
}
