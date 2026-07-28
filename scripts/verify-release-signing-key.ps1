[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ManifestPath,
    [string] $TrustRootSource = (Join-Path $PSScriptRoot `
        '..\src\InfiniTranseon.App\Presentation\Services\ProductionReleaseTrustRoot.cs')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
foreach ($name in @('RELEASE_ED25519_PRIVATE_KEY', 'RELEASE_ED25519_KEY_ID')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Release secret '$name' is required."
    }
}

$manifestFullPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$trustRootFullPath = (Resolve-Path -LiteralPath $TrustRootSource).Path
$source = Get-Content -LiteralPath $trustRootFullPath -Raw
$sourceKeyIdMatch = [regex]::Match(
    $source,
    'CurrentKeyId\s*=\s*"(?<value>[a-z0-9._-]+)"')
$sourceKeyMatch = [regex]::Match(
    $source,
    'Convert\.FromHexString\(\s*"(?<value>[0-9a-f]{64})"\s*\)')
if (-not $sourceKeyIdMatch.Success -or -not $sourceKeyMatch.Success) {
    throw 'The embedded release trust root could not be parsed.'
}
$sourceKeyId = $sourceKeyIdMatch.Groups['value'].Value
$sourcePublicKey = $sourceKeyMatch.Groups['value'].Value
if ($sourceKeyId -ne $env:RELEASE_ED25519_KEY_ID) {
    throw 'The release key ID does not match the embedded trust root.'
}

$openSslCandidates = @(
    (Join-Path $env:ProgramFiles 'Git\usr\bin\openssl.exe'),
    (Join-Path ${env:ProgramFiles(x86)} 'Git\usr\bin\openssl.exe')
)
$openSsl = $openSslCandidates |
    Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } |
    Select-Object -First 1
if ([string]::IsNullOrWhiteSpace($openSsl)) {
    throw 'OpenSSL from Git for Windows was not found.'
}

$temporaryBase = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    [IO.Path]::GetTempPath()
} else {
    $env:RUNNER_TEMP
}
$temporaryRoot = Join-Path $temporaryBase (
    'infini-release-verification-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$privateKeyPath = Join-Path $temporaryRoot 'release-ed25519.pem'
$publicDerPath = Join-Path $temporaryRoot 'release-ed25519-public.der'
$publicPemPath = Join-Path $temporaryRoot 'release-ed25519-public.pem'
$unsignedPath = Join-Path $temporaryRoot 'manifest-unsigned.json'
$signaturePath = Join-Path $temporaryRoot 'manifest.sig'
$privateBytes = $null
try {
    $privateBytes = [Convert]::FromBase64String($env:RELEASE_ED25519_PRIVATE_KEY)
    [IO.File]::WriteAllBytes($privateKeyPath, $privateBytes)
    & $openSsl pkey -in $privateKeyPath -pubout -outform DER -out $publicDerPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not derive the release public key.' }
    & $openSsl pkey -in $privateKeyPath -pubout -out $publicPemPath
    if ($LASTEXITCODE -ne 0) { throw 'Could not export the release public key.' }
    $publicDer = [IO.File]::ReadAllBytes($publicDerPath)
    if ($publicDer.Length -ne 44) {
        throw 'The release public key has an unexpected encoding.'
    }
    [byte[]] $rawPublicKey = $publicDer[12..43]
    $derivedPublicKey = [Convert]::ToHexString($rawPublicKey).ToLowerInvariant()
    if ($derivedPublicKey -ne $sourcePublicKey) {
        throw 'The release private key does not match the public key embedded in the application.'
    }

    $manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json
    $matchingSignatures = @($manifest.signatures |
        Where-Object {
            $_.algorithm -eq 'Ed25519' -and
            $_.keyId -eq $env:RELEASE_ED25519_KEY_ID
        })
    if ($matchingSignatures.Count -ne 1) {
        throw 'The release manifest does not contain exactly one signature from the embedded key.'
    }
    [IO.File]::WriteAllBytes(
        $signaturePath,
        [Convert]::FromBase64String($matchingSignatures[0].signature))
    Copy-Item -LiteralPath $manifestFullPath -Destination $unsignedPath
    & (Join-Path $PSScriptRoot 'convert-to-canonical-json.ps1') `
        -InputPath $unsignedPath `
        -OutputPath $unsignedPath
    & $openSsl pkeyutl -verify -pubin -rawin `
        -inkey $publicPemPath `
        -in $unsignedPath `
        -sigfile $signaturePath
    if ($LASTEXITCODE -ne 0) {
        throw 'The release manifest signature does not verify with the embedded trust root.'
    }

    [pscustomobject]@{
        KeyId = $sourceKeyId
        Manifest = $manifestFullPath
        Verified = $true
    }
}
finally {
    if ($null -ne $privateBytes) {
        [Array]::Clear($privateBytes, 0, $privateBytes.Length)
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
