param([string]$OutputDirectory = 'artifacts/sdk')

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot 'artifacts')) + [IO.Path]::DirectorySeparatorChar
$outputPath = [IO.Path]::GetFullPath((Join-Path $projectRoot $OutputDirectory))
if (!$outputPath.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) { throw 'SDK output must be inside artifacts/.' }
$version = [IO.File]::ReadAllText((Join-Path $projectRoot 'modules/sdk-version.txt')).Trim()
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { throw 'Expected a stable SDK version.' }
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
Push-Location $projectRoot
try {
    foreach ($project in @('src/YokoWave.Core/YokoWave.Core.csproj', 'modules/csharp-sdk/YokoWave.Sdk.csproj')) {
        & dotnet pack $project -c Release --nologo -o $outputPath -p:PackageVersion=$version -p:UseSharedCompilation=false
        if ($LASTEXITCODE -ne 0) { throw "Packing failed: $project" }
    }
    foreach ($name in @('YokoWave.Core', 'YokoWave.Sdk')) {
        $archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $outputPath "$name.$version.nupkg"))
        try {
            if (!$archive.GetEntry("lib/net10.0/$name.dll")) { throw "Missing SDK assembly: $name" }
            if ($archive.Entries.FullName -match '\.pdb$') { throw 'SDK packages must not contain PDB files.' }
            if ($name -eq 'YokoWave.Core') {
                foreach ($notice in @('LICENSE', 'THIRD-PARTY-NOTICES.md', 'licenses/dglab-kit-GPL-3.0.txt')) {
                    if (!$archive.GetEntry($notice)) { throw "Missing bundled data notice: $notice" }
                }
            }
        }
        finally { $archive.Dispose() }
    }
    $consumer = Join-Path $outputPath ('consumer-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $consumer | Out-Null
    if ((Get-Item -LiteralPath $consumer).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Consumer directory cannot be a link.' }
    [IO.File]::WriteAllText((Join-Path $consumer 'Consumer.csproj'), @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><NuGetAudit>false</NuGetAudit></PropertyGroup>
  <ItemGroup><PackageReference Include="YokoWave.Sdk" Version="$version" /></ItemGroup>
</Project>
"@)
    [IO.File]::WriteAllText((Join-Path $consumer 'Program.cs'), @'
using YokoWave.Core;
using YokoWave.Sdk;
using YokoWave.Sdk.DgLab.V4;
await using var client = new RemoteControlClient("ws://127.0.0.1:1/ws/test", new string('x', 32));
_ = OutputFrame.Zero;
_ = typeof(RemoteEventClient);
_ = typeof(WebSocketGattAdapter);
_ = typeof(DgLabV4Controller);
Console.WriteLine("SDK NuGet consumer verified without hosting or UI dependencies.");

'@)
    & dotnet restore (Join-Path $consumer 'Consumer.csproj') --source $outputPath --packages (Join-Path $consumer 'packages') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'SDK package consumer restore failed.' }
    & dotnet run --project (Join-Path $consumer 'Consumer.csproj') -c Release --no-restore -p:UseSharedCompilation=false
    if ($LASTEXITCODE -ne 0) { throw 'SDK package consumer failed.' }
    $runtime = Get-Content (Join-Path $consumer 'bin/Release/net10.0/Consumer.runtimeconfig.json') -Raw | ConvertFrom-Json
    foreach ($framework in @($runtime.runtimeOptions.framework) + @($runtime.runtimeOptions.frameworks)) {
        if ($framework -and $framework.name -ne 'Microsoft.NETCore.App') { throw 'SDK consumer requires an unexpected shared framework.' }
    }
}
finally { Pop-Location }
