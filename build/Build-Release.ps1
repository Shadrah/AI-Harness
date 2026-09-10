[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version = '1.0.1',

    [string] $SigningMetadataPath,

    [switch] $SkipChecks
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$releaseRoot = Join-Path $repositoryRoot ".artifacts\release\$Version"
$publishRoot = Join-Path $releaseRoot 'publish\win-x64'
$installerRoot = Join-Path $releaseRoot 'installer'

if (Test-Path -LiteralPath $releaseRoot) {
    $resolvedRelease = (Resolve-Path -LiteralPath $releaseRoot).Path
    $expectedParent = (Join-Path $repositoryRoot '.artifacts\release')
    if (-not $resolvedRelease.StartsWith($expectedParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove unexpected release path: $resolvedRelease"
    }
    Remove-Item -LiteralPath $resolvedRelease -Recurse -Force
}
New-Item -ItemType Directory -Path $publishRoot -Force | Out-Null
New-Item -ItemType Directory -Path $installerRoot -Force | Out-Null

Push-Location $repositoryRoot
try {
    dotnet restore Harness.sln --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Solution restore failed.' }
    dotnet build Harness.sln -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }

    dotnet restore src/Harness.App/Harness.App.csproj -r win-x64 --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Windows x64 publish restore failed.' }

    if (-not $SkipChecks) {
        dotnet run --project tools/Harness.ApiCheck -c Release --no-build
        if ($LASTEXITCODE -ne 0) { throw 'Harness API checks failed.' }
        dotnet run --project tools/Harness.ApiCheck -c Release --no-build -- --startup-check
        if ($LASTEXITCODE -ne 0) { throw 'Harness startup responsiveness check failed.' }
    }

    dotnet publish src/Harness.App/Harness.App.csproj -c Release -r win-x64 --self-contained true --no-restore --nologo -o $publishRoot -p:Version=$Version -p:DebugType=None -p:DebugSymbols=false
    if ($LASTEXITCODE -ne 0) { throw 'Self-contained publish failed.' }

    $forbidden = Get-ChildItem -LiteralPath $publishRoot -File -Recurse | Where-Object {
        $_.Extension -in '.pdb', '.db', '.log' -or $_.Name -match 'token|credential|secret'
    }
    if ($forbidden) {
        throw "Publish output contains forbidden development or user-data files: $($forbidden.Name -join ', ')"
    }

    $appExecutable = Join-Path $publishRoot 'Harness.exe'
    $webViewLoader = Join-Path $publishRoot 'WebView2Loader.dll'
    if (-not (Test-Path -LiteralPath $webViewLoader)) {
        throw 'Self-contained publish is missing the root WebView2Loader.dll required by the embedded browser.'
    }
    if (-not [string]::IsNullOrWhiteSpace($SigningMetadataPath)) {
        & (Join-Path $PSScriptRoot 'Sign-Artifact.ps1') -ArtifactPath $appExecutable -MetadataPath $SigningMetadataPath
    }

    dotnet build installer/Harness.Installer/Harness.Installer.wixproj -c Release --nologo -p:HarnessPublishDir=$publishRoot -p:ProductVersion=$Version -p:OutputPath=$installerRoot
    if ($LASTEXITCODE -ne 0) { throw 'MSI build failed.' }

    $installer = Get-ChildItem -LiteralPath $installerRoot -Filter '*.msi' -File -Recurse | Select-Object -First 1
    if (-not $installer) { throw 'The MSI build completed without producing an installer.' }
    & (Join-Path $PSScriptRoot 'Test-InstallerStructure.ps1') -InstallerPath $installer.FullName -ExpectedVersion $Version
    if (-not [string]::IsNullOrWhiteSpace($SigningMetadataPath)) {
        & (Join-Path $PSScriptRoot 'Sign-Artifact.ps1') -ArtifactPath $installer.FullName -MetadataPath $SigningMetadataPath
    }

    $hashTargets = @($installer.FullName, $appExecutable)
    $checksums = foreach ($target in $hashTargets) {
        $hash = Get-FileHash -LiteralPath $target -Algorithm SHA256
        "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($target))"
    }
    $checksumPath = Join-Path $releaseRoot 'SHA256SUMS.txt'
    $checksums | Set-Content -LiteralPath $checksumPath -Encoding ascii

    $signatureState = if ([string]::IsNullOrWhiteSpace($SigningMetadataPath)) { 'UNSIGNED' } else { 'SIGNED AND VERIFIED' }
    Write-Host "Release $Version complete · $signatureState"
    Write-Host "Installer: $($installer.FullName)"
    Write-Host "Checksums: $checksumPath"
}
finally {
    Pop-Location
}
