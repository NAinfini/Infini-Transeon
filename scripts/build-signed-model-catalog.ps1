[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $TemplatePath,
    [Parameter(Mandatory)] [string] $OutputPath,
    [Parameter(Mandatory)] [long] $CatalogSequence
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($CatalogSequence -lt 1) { throw 'Catalog sequence must be positive.' }
foreach ($name in @('RELEASE_ED25519_PRIVATE_KEY', 'RELEASE_ED25519_KEY_ID')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Release secret '$name' is required."
    }
}

$template = Get-Content -LiteralPath $TemplatePath -Raw | ConvertFrom-Json
if ($template.schemaVersion -ne 1 -or @($template.models).Count -eq 0) {
    throw 'Model catalog template is invalid or empty.'
}
$unsigned = [ordered]@{
    catalogSequence = $CatalogSequence
    models = @($template.models)
    publishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    schemaVersion = 1
}

$temporaryBase = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    [IO.Path]::GetTempPath()
} else {
    $env:RUNNER_TEMP
}
$temporaryRoot = Join-Path $temporaryBase (
    'infini-model-catalog-signing-' + [Guid]::NewGuid().ToString('N'))
$privateKeyPath = Join-Path $temporaryRoot 'release-ed25519.pem'
$unsignedPath = Join-Path $temporaryRoot 'model-catalog-unsigned.json'
$signaturePath = Join-Path $temporaryRoot 'model-catalog.sig'

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    [IO.File]::WriteAllText(
        $privateKeyPath,
        [Text.Encoding]::UTF8.GetString(
            [Convert]::FromBase64String($env:RELEASE_ED25519_PRIVATE_KEY)),
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        $unsignedPath,
        ($unsigned | ConvertTo-Json -Depth 12 -Compress),
        [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'convert-to-canonical-json.ps1') `
        -InputPath $unsignedPath `
        -OutputPath $unsignedPath

    $openSsl = 'C:\Program Files\Git\usr\bin\openssl.exe'
    if (-not (Test-Path -LiteralPath $openSsl -PathType Leaf)) {
        throw 'OpenSSL from Git for Windows was not found.'
    }
    & $openSsl pkeyutl -sign -rawin -inkey $privateKeyPath `
        -in $unsignedPath -out $signaturePath
    if ($LASTEXITCODE -ne 0) { throw 'Ed25519 model catalog signing failed.' }

    $signed = [ordered]@{}
    foreach ($entry in $unsigned.GetEnumerator()) { $signed[$entry.Key] = $entry.Value }
    $signed.signatures = @([ordered]@{
        algorithm = 'Ed25519'
        keyId = $env:RELEASE_ED25519_KEY_ID
        signature = [Convert]::ToBase64String(
            [IO.File]::ReadAllBytes($signaturePath))
    })
    $outputDirectory = Split-Path -Parent $OutputPath
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
    }
    [IO.File]::WriteAllText(
        $OutputPath,
        ($signed | ConvertTo-Json -Depth 12),
        [Text.UTF8Encoding]::new($false))
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
