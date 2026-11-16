$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$fixture = Join-Path $projectRoot "artifacts/release-version-tests/$([Guid]::NewGuid().ToString('N'))"
foreach ($directory in @('scripts', 'modules/minecraft', 'docs/releases')) {
    New-Item -ItemType Directory -Force -Path (Join-Path $fixture $directory) | Out-Null
}
$scriptPath = Join-Path $fixture 'scripts/get-release-version.ps1'
Copy-Item -LiteralPath (Join-Path $projectRoot 'scripts/get-release-version.ps1') -Destination $scriptPath
$propsPath = Join-Path $fixture 'Directory.Build.props'
$sdkPath = Join-Path $fixture 'modules/sdk-version.txt'
$minecraftPath = Join-Path $fixture 'modules/minecraft/gradle.properties'
$notesPath = Join-Path $fixture 'docs/releases/1.2.3.md'
$outputPath = Join-Path $fixture 'github-output.txt'
$checks = 0

function Invoke-FixtureGit {
    & git -C $fixture -c user.name=codex -c user.email=codex@openai.com -c commit.gpgsign=false -c tag.gpgsign=false -c core.autocrlf=false @args
    if ($LASTEXITCODE -ne 0) { throw "Fixture Git command failed: $args" }
}

function Assert-Equal($Actual, $Expected) {
    if ($Actual -cne $Expected) { throw "Expected '$Expected', received '$Actual'." }
    $script:checks++
}

function Assert-Rejected([scriptblock]$Action, [string]$Message) {
    $before = [IO.File]::ReadAllText($outputPath)
    $failure = $null
    try { & $Action | Out-Null }
    catch { $failure = $_ }
    if (!$failure -or !$failure.Exception.Message.Contains($Message, [StringComparison]::Ordinal)) {
        throw "Expected rejection containing '$Message', received '$failure'."
    }
    Assert-Equal ([IO.File]::ReadAllText($outputPath)) $before
}

Set-Content -LiteralPath $propsPath '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>'
Set-Content -LiteralPath $sdkPath '1.2.3'
Set-Content -LiteralPath $minecraftPath "loader=fabric`nmod_version=1.2.3`nminecraft_version=1.20.1"
Set-Content -LiteralPath $notesPath "## Changes`nRelease notes."
Invoke-FixtureGit init --quiet
Invoke-FixtureGit add Directory.Build.props modules docs
Invoke-FixtureGit commit --quiet --author='codex <codex@openai.com>' -m 'Release version fixture'
$commit = Invoke-FixtureGit rev-parse HEAD
Invoke-FixtureGit tag -a v1.2.3 -m 'Annotated desktop tag'
Invoke-FixtureGit tag sdk-v1.2.3
Invoke-FixtureGit tag minecraft-v1.2.3

# Run outside the fixture so version sources and Git commands cannot depend on the caller's directory.
Push-Location $PSScriptRoot
try {
    foreach ($product in @('Sdk', 'Minecraft')) {
        $result = & $scriptPath -Product $product
        Assert-Equal $result.version '1.2.3'
        Assert-Equal $result.commit ''
    }
    foreach ($release in @(
        @{ Product = 'Desktop'; Tag = 'v1.2.3' },
        @{ Product = 'Sdk'; Tag = 'sdk-v1.2.3' },
        @{ Product = 'Minecraft'; Tag = 'minecraft-v1.2.3' }
    )) {
        Set-Content -LiteralPath $outputPath 'existing=value'
        $result = & $scriptPath @release -OutputFile $outputPath
        Assert-Equal $result.version '1.2.3'
        Assert-Equal $result.tag $release.Tag
        Assert-Equal $result.commit $commit
        Assert-Equal ((Get-Content -LiteralPath $outputPath) -join '|') "existing=value|version=1.2.3|tag=$($release.Tag)|commit=$commit"
    }

    Assert-Rejected { & $scriptPath -Product Desktop -OutputFile $outputPath } 'requires an existing version tag'
    Assert-Rejected { & $scriptPath -Product Sdk -Tag 'v1.2.3' -OutputFile $outputPath } 'Expected the tag sdk-v1.2.3'
    Assert-Rejected { & $scriptPath -Product Sdk -Tag 'sdk-v1.2.4' -OutputFile $outputPath } 'Expected the tag sdk-v1.2.3'
    Assert-Rejected { & $scriptPath -Product Sdk -Tag 'SDK-v1.2.3' -OutputFile $outputPath } 'Expected the tag sdk-v1.2.3'
    Assert-Rejected { & $scriptPath -Product Sdk -Tag "sdk-v1.2.3`nother=value" -OutputFile $outputPath } 'Expected the tag sdk-v1.2.3'

    foreach ($invalidVersion in @('01.2.3', '1.2', '1.2.3-beta', "1.2.3`nother=value")) {
        Set-Content -LiteralPath $sdkPath $invalidVersion
        Assert-Rejected { & $scriptPath -Product Sdk -OutputFile $outputPath } 'Expected a stable'
    }
    Set-Content -LiteralPath $sdkPath '1.2.4'
    Assert-Rejected { & $scriptPath -Product Sdk -Tag 'sdk-v1.2.4' -OutputFile $outputPath } 'tag must already exist'
    Set-Content -LiteralPath $sdkPath '1.2.3'

    Set-Content -LiteralPath $propsPath '<Project><PropertyGroup><Version>1.2.3</Version><Version>1.2.3</Version></PropertyGroup></Project>'
    Assert-Rejected { & $scriptPath -Product Desktop -Tag 'v1.2.3' -OutputFile $outputPath } 'Expected one desktop'
    Set-Content -LiteralPath $propsPath '<Project><PropertyGroup><Version>1.2.3</Version></PropertyGroup></Project>'
    Set-Content -LiteralPath $minecraftPath "mod_version=1.2.3`nmod_version=1.2.3"
    Assert-Rejected { & $scriptPath -Product Minecraft -OutputFile $outputPath } 'Expected one Minecraft'

    foreach ($heading in @('# Duplicate title', '   # Indented title', '#')) {
        Set-Content -LiteralPath $notesPath $heading
        Assert-Rejected { & $scriptPath -Product Desktop -Tag 'v1.2.3' -OutputFile $outputPath } 'level-one heading'
    }
    Move-Item -LiteralPath $notesPath -Destination "$notesPath.saved"
    Assert-Rejected { & $scriptPath -Product Desktop -Tag 'v1.2.3' -OutputFile $outputPath } 'Release notes are missing'
    Move-Item -LiteralPath "$notesPath.saved" -Destination $notesPath

    Invoke-FixtureGit commit --quiet --allow-empty --author='codex <codex@openai.com>' -m 'Different checkout'
    Assert-Rejected { & $scriptPath -Product Sdk -Tag 'sdk-v1.2.3' -OutputFile $outputPath } 'Checkout does not match'
}
finally { Pop-Location }

Write-Output "Release version checks passed: $checks assertions."
