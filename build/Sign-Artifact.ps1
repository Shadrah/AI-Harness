[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string[]] $ArtifactPath,

    [Parameter(Mandatory = $true)]
    [string] $MetadataPath
)

$ErrorActionPreference = 'Stop'
$signingProject = Join-Path $PSScriptRoot 'SigningTools\SigningTools.csproj'
$resolvedMetadata = (Resolve-Path -LiteralPath $MetadataPath).Path
$metadata = Get-Content -LiteralPath $resolvedMetadata -Raw | ConvertFrom-Json
foreach ($requiredName in 'Endpoint', 'CodeSigningAccountName', 'CertificateProfileName') {
    $value = $metadata.$requiredName
    if ([string]::IsNullOrWhiteSpace($value) -or $value -match '^YOUR-') {
        throw "Artifact Signing metadata is missing a real $requiredName value."
    }
}

$tenantId = [string] $metadata.TenantId
if (-not [string]::IsNullOrWhiteSpace($tenantId)) {
    $parsedTenantId = [Guid]::Empty
    if (-not [Guid]::TryParse($tenantId, [ref] $parsedTenantId)) {
        throw 'Artifact Signing TenantId must be a valid Azure directory GUID.'
    }
}

# TenantId configures DefaultAzureCredential through the process environment, but
# is not a field understood by the native signing library. Write a short-lived
# metadata document containing only the library's supported fields.
$dlibMetadata = [ordered]@{
    Endpoint = [string] $metadata.Endpoint
    CodeSigningAccountName = [string] $metadata.CodeSigningAccountName
    CertificateProfileName = [string] $metadata.CertificateProfileName
}
if (-not [string]::IsNullOrWhiteSpace([string] $metadata.CorrelationId)) {
    $dlibMetadata.CorrelationId = [string] $metadata.CorrelationId
}
if ($null -ne $metadata.ExcludeCredentials) {
    $dlibMetadata.ExcludeCredentials = @($metadata.ExcludeCredentials)
}
$temporaryMetadata = Join-Path ([IO.Path]::GetTempPath()) "harness-artifact-signing-$([Guid]::NewGuid().ToString('N')).json"
$dlibMetadata | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $temporaryMetadata -Encoding utf8

dotnet restore $signingProject --nologo
if ($LASTEXITCODE -ne 0) { throw 'Could not restore the Microsoft signing tools.' }

$nugetLine = dotnet nuget locals global-packages --list
$nugetRoot = ($nugetLine -replace '^global-packages:\s*', '').Trim()
if (-not (Test-Path -LiteralPath $nugetRoot)) { throw 'The NuGet global package directory could not be located.' }

$sdkRoot = Join-Path $nugetRoot 'microsoft.windows.sdk.buildtools\10.0.26100.9169'
$clientRoot = Join-Path $nugetRoot 'microsoft.artifactsigning.client\1.0.128'
$signTool = Get-ChildItem -LiteralPath $sdkRoot -Filter 'signtool.exe' -File -Recurse |
    Where-Object { $_.FullName -match '[\\/]x64[\\/]' } |
    Select-Object -First 1
$signingLibrary = Get-ChildItem -LiteralPath $clientRoot -Filter 'Azure.CodeSigning.Dlib.dll' -File -Recurse |
    Where-Object { $_.FullName -match '[\\/]x64[\\/]' } |
    Select-Object -First 1
if (-not $signTool) { throw 'The x64 Windows SDK SignTool was not found after restore.' }
if (-not $signingLibrary) { throw 'The x64 Artifact Signing client library was not found after restore.' }

if ([string]::IsNullOrWhiteSpace($tenantId)) {
    $previousTenantId = $null
} else {
    $previousTenantId = [Environment]::GetEnvironmentVariable('AZURE_TENANT_ID', 'Process')
    [Environment]::SetEnvironmentVariable('AZURE_TENANT_ID', $tenantId, 'Process')
}

try {
    foreach ($path in $ArtifactPath) {
        $resolvedArtifact = (Resolve-Path -LiteralPath $path).Path
        & $signTool.FullName sign /v /fd SHA256 /tr 'http://timestamp.acs.microsoft.com' /td SHA256 /dlib $signingLibrary.FullName /dmdf $temporaryMetadata $resolvedArtifact
        if ($LASTEXITCODE -ne 0) { throw "Artifact Signing failed for $resolvedArtifact" }
        & $signTool.FullName verify /pa /all /v $resolvedArtifact
        if ($LASTEXITCODE -ne 0) { throw "Signature verification failed for $resolvedArtifact" }
    }
}
finally {
    Remove-Item -LiteralPath $temporaryMetadata -Force -ErrorAction SilentlyContinue
    if (-not [string]::IsNullOrWhiteSpace($tenantId)) {
        [Environment]::SetEnvironmentVariable('AZURE_TENANT_ID', $previousTenantId, 'Process')
    }
}
