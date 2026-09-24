#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidatePattern('^[A-Za-z0-9][A-Za-z0-9._-]*$')]
    [string]$Version = '0.1.7.5-illusion.1',
    [string]$OutputDirectory = 'artifacts',
    [switch]$NoRestore
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$gitSafeRoot = $repositoryRoot.Replace('\', '/')
$outputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
}
$packageName = "ZDPS-IllusionEnergy-v$Version-win-x64"
$packageDirectory = Join-Path $outputRoot $packageName
$binaryZip = Join-Path $outputRoot "$packageName.zip"
$sourceName = "ZDPS-IllusionEnergy-v$Version-source.zip"
$sourceZip = Join-Path $outputRoot $sourceName
foreach ($path in @($packageDirectory, $binaryZip, $sourceZip)) {
    if (Test-Path -LiteralPath $path) { throw "Output already exists: $path. Use a new version or output directory." }
}

function Invoke-DotNetLogged {
    param([string]$Label, [string[]]$Arguments, [string]$LogPath)
    & dotnet @Arguments *> $LogPath
    if ($LASTEXITCODE -ne 0) {
        Get-Content -LiteralPath $LogPath -Tail 50 | Write-Host
        throw "$Label failed. See $LogPath"
    }
    Write-Host "$Label passed."
}

Push-Location $repositoryRoot
try {
    $commit = & git -c "safe.directory=$gitSafeRoot" rev-parse HEAD
    if ($LASTEXITCODE -ne 0) { throw 'A Git checkout is required.' }
    $dirty = & git -c "safe.directory=$gitSafeRoot" status --porcelain
    if ($LASTEXITCODE -ne 0 -or $dirty) { throw 'Commit source changes before packaging so the source ZIP matches the binary.' }
    New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
    $restoreArgs = if ($NoRestore) { @('--no-restore') } else { @() }
    $testsLog = Join-Path $outputRoot "tests-$Version.log"
    $uiLog = Join-Path $outputRoot "ui-$Version.log"
    $publishLog = Join-Path $outputRoot "publish-$Version.log"

    Invoke-DotNetLogged -Label 'Factor tests' -LogPath $testsLog -Arguments (
        @('run', '--project', 'tests/FactorEnergy.Tests', '-c', 'Release', '--verbosity', 'quiet') + $restoreArgs)
    Invoke-DotNetLogged -Label 'Headless UI' -LogPath $uiLog -Arguments (
        @('run', '--project', 'tests/FactorEnergy.UiSmoke', '-c', 'Release', '--verbosity', 'quiet') + $restoreArgs)
    $testsText = Get-Content -LiteralPath $testsLog -Raw
    $uiText = Get-Content -LiteralPath $uiLog -Raw
    $testSummary = [regex]::Match($testsText, '(\d+) passed, 0 failed')
    $uiSummary = [regex]::Match($uiText, '(\d+) UI scenarios passed')
    if (-not $testSummary.Success -or -not $uiSummary.Success) { throw 'Expected test summaries were not found.' }

    Invoke-DotNetLogged -Label 'Windows x64 publish' -LogPath $publishLog -Arguments (
        @('publish', 'BPSR-ZDPS/BPSR-ZDPS.csproj', '-c', 'Release', '-r', 'win-x64',
          '--self-contained', 'true', '-p:PublishSingleFile=true', '-p:PublishTrimmed=false',
          '-p:DebugType=None', '-p:DebugSymbols=false', "-p:InformationalVersion=$Version",
          '-o', $packageDirectory, '--verbosity', 'quiet') + $restoreArgs)

    foreach ($name in @('README.FactorEnergy.ja.md', 'JAPANESE-NAMES.md', 'CHANGELOG.FactorEnergy.md')) {
        Copy-Item -LiteralPath (Join-Path $repositoryRoot $name) -Destination $packageDirectory
    }
    Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.FactorEnergy.ja.md') -Destination (Join-Path $packageDirectory 'README.ja.md')
    foreach ($name in @('LICENSE', 'LICENSES/BPSR-ZDPS-MIT.txt', 'THIRD-PARTY-NOTICES.md')) {
        if ((Get-FileHash -LiteralPath (Join-Path $repositoryRoot $name)).Hash -ne
            (Get-FileHash -LiteralPath (Join-Path $packageDirectory $name)).Hash) {
            throw "Distribution notice missing or different: $name"
        }
    }
    $exe = Join-Path $packageDirectory 'BPSR-ZDPS.exe'
    $factorRoot = Join-Path $packageDirectory 'Data/FactorEnergy'
    $dataHashes = [ordered]@{}
    foreach ($file in (Get-ChildItem -LiteralPath $factorRoot -File -Recurse -Filter '*.json' | Sort-Object FullName)) {
        $relative = [IO.Path]::GetRelativePath($packageDirectory, $file.FullName).Replace('\', '/')
        $fileHash = (Get-FileHash -LiteralPath $file.FullName).Hash.ToLowerInvariant()
        $expected = (Get-FileHash -LiteralPath (Join-Path $repositoryRoot "BPSR-ZDPS/$relative")).Hash.ToLowerInvariant()
        if ($fileHash -ne $expected) { throw "Published factor data differs: $relative" }
        $dataHashes[$relative] = $fileHash
    }
    if ($dataHashes.Count -ne 6) { throw 'Expected six factor data files.' }
    $provenance = Get-Content -LiteralPath (Join-Path $factorRoot 'provenance.json') -Raw | ConvertFrom-Json
    $info = [ordered]@{
        name = 'ZDPS Illusion Energy'; version = $Version
        sourceRepository = 'https://github.com/norezark/BPSR-ZDPS'; sourceCommit = $commit
        sourceArchive = $sourceName; upstreamCommit = $provenance.targetBaseCommit
        donorCommit = $provenance.sourceCommit; license = 'AGPL-3.0-only'
        createdUtc = [DateTime]::UtcNow.ToString('o'); sdk = (& dotnet --version)
        runtimeIdentifier = 'win-x64'; selfContained = $true
        unitAndIntegrationTests = @{ passed = [int]$testSummary.Groups[1].Value; failed = 0 }
        headlessUiScenarios = @{ passed = [int]$uiSummary.Groups[1].Value; failed = 0 }
        liveGameComparison = 'not performed'; desktopAppearanceReview = 'not performed'
        exeSha256 = (Get-FileHash -LiteralPath $exe).Hash.ToLowerInvariant()
        factorDataSha256 = $dataHashes
    }
    $info | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $packageDirectory 'BUILD-INFO.json') -Encoding utf8
    & git -c "safe.directory=$gitSafeRoot" archive --format=zip --prefix=ZDPS-IllusionEnergy-source/ "--output=$sourceZip" $commit
    if ($LASTEXITCODE -ne 0) { throw 'Source archive failed.' }
    [IO.Compression.ZipFile]::CreateFromDirectory($packageDirectory, $binaryZip, [IO.Compression.CompressionLevel]::Optimal, $true)
    @($binaryZip, $sourceZip) | ForEach-Object {
        '{0}  {1}' -f (Get-FileHash -LiteralPath $_).Hash.ToLowerInvariant(), [IO.Path]::GetFileName($_)
    } | Set-Content -LiteralPath (Join-Path $outputRoot "SHA256SUMS-$Version.txt") -Encoding utf8
    @(
        "ZDPS Illusion Energy v$Version", "Source commit: $commit", $testSummary.Value, $uiSummary.Value,
        'Windows x64 self-contained publish: passed', 'Factor data and license copies: matched',
        'Live game comparison: not performed', 'Desktop appearance review: not performed'
    ) | Set-Content -LiteralPath (Join-Path $outputRoot "verification-$Version.txt") -Encoding utf8
    Write-Host "Binary: $binaryZip"
    Write-Host "Source: $sourceZip"
} finally {
    Pop-Location
}
