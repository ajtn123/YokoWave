param([switch]$Fix)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Push-Location $projectRoot
try {
    if ($Fix) {
        & (Join-Path $PSScriptRoot 'sort-locales.ps1')
    }
    else {
        & (Join-Path $PSScriptRoot 'sort-locales.ps1') -Check
    }
    $formatArguments = @('YokoWave.slnx', '--no-restore')
    if (-not $Fix) {
        $formatArguments += '--verify-no-changes'
    }

    & dotnet format style @formatArguments --severity warn
    if ($LASTEXITCODE -ne 0) {
        throw 'Code-style checks failed. Run scripts/check-style.ps1 -Fix, then review the diff.'
    }

    & dotnet format whitespace @formatArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'Whitespace checks failed. Run scripts/check-style.ps1 -Fix, then review the diff.'
    }
}
finally {
    Pop-Location
}
