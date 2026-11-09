param([ValidateSet('Debug', 'Release')][string]$Configuration = 'Release')

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testProject = Join-Path $projectRoot 'tests/YokoWave.Tests'
$testAssembly = Join-Path $testProject "bin/$Configuration/net10.0/YokoWave.Tests.dll"
$assetsPath = Join-Path $testProject 'obj/project.assets.json'
if (-not (Test-Path -LiteralPath $testAssembly) -or -not (Test-Path -LiteralPath $assetsPath)) {
    throw 'Restore and build the test project before collecting coverage.'
}

$assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
$package = $assets.libraries.PSObject.Properties | Where-Object { $_.Name -like 'Microsoft.CodeCoverage/*' } | Select-Object -First 1
if ($null -eq $package) {
    throw 'Microsoft.CodeCoverage was not resolved by Microsoft.NET.Test.Sdk.'
}
$collectorDirectory = $assets.packageFolders.PSObject.Properties.Name | ForEach-Object {
    Join-Path (Join-Path $_ $package.Value.path) 'build/netstandard2.0'
} | Where-Object { Test-Path -LiteralPath (Join-Path $_ 'Microsoft.VisualStudio.TraceDataCollector.dll') } | Select-Object -First 1
if ($null -eq $collectorDirectory) {
    throw 'The resolved code-coverage collector is missing; restore dependencies again.'
}

$reportDirectory = Join-Path $projectRoot ('artifacts/coverage/' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $reportDirectory | Out-Null
& dotnet vstest $testAssembly "--TestAdapterPath:$collectorDirectory" '--collect:Code Coverage;Format=cobertura' "--ResultsDirectory:$reportDirectory" '--Logger:trx;LogFileName=tests.trx'
if ($LASTEXITCODE -ne 0) {
    throw 'The coverage test run failed.'
}
$report = Get-ChildItem -LiteralPath $reportDirectory -Recurse -Filter '*.cobertura.xml' | Select-Object -First 1
if ($null -eq $report) {
    throw 'Tests ran without producing the requested coverage report.'
}

[xml]$coverage = Get-Content -LiteralPath $report.FullName -Raw
$expectedAssemblies = @('YokoWave.Core', 'YokoWave.Pipelines', 'YokoWave.Remote', 'YokoWave.App')
$summary = @($coverage.coverage.packages.package | Where-Object { $_.name -in $expectedAssemblies } | ForEach-Object {
    [PSCustomObject]@{
        Assembly = $_.name
        LineRate = [double]::Parse($_.'line-rate', [Globalization.CultureInfo]::InvariantCulture)
        BranchRate = [double]::Parse($_.'branch-rate', [Globalization.CultureInfo]::InvariantCulture)
    }
})
if ($summary.Count -ne $expectedAssemblies.Count) {
    throw 'Coverage is missing one or more of the Core, Pipelines, Remote, and App assemblies.'
}
$summary | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $reportDirectory 'summary.json') -Encoding utf8
$summary | Format-Table Assembly, @{Name = 'Lines'; Expression = { $_.LineRate.ToString('P2') } }, @{Name = 'Branches'; Expression = { $_.BranchRate.ToString('P2') } }
Write-Output "Coverage report: $($report.FullName)"
