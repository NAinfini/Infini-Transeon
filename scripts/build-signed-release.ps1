[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ReleaseDirectory,
    [Parameter(Mandatory)] [string] $Version,
    [long] $ReleaseSequence = [long]$env:GITHUB_RUN_NUMBER
)

$ErrorActionPreference = 'Stop'
$releasePath = (Resolve-Path -LiteralPath $ReleaseDirectory).Path
$versionText = $Version.TrimStart('v')
if ($versionText -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid release version '$Version'."
}
if ($ReleaseSequence -lt 1) { throw 'Release sequence must be positive.' }

$requiredSecrets = @(
    'RELEASE_ED25519_PRIVATE_KEY',
    'RELEASE_ED25519_KEY_ID'
)
foreach ($name in $requiredSecrets) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Release secret '$name' is required."
    }
}

$temporaryBase = if ([string]::IsNullOrWhiteSpace($env:RUNNER_TEMP)) {
    [IO.Path]::GetTempPath()
} else {
    $env:RUNNER_TEMP
}
$tempRoot = Join-Path $temporaryBase (
    "infini-release-signing-" + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tempRoot | Out-Null
$privateKeyPath = Join-Path $tempRoot 'release-ed25519.pem'
$unsignedManifestPath = Join-Path $tempRoot 'manifest-unsigned.json'
$signaturePath = Join-Path $tempRoot 'manifest.sig'

try {
    [IO.File]::WriteAllText(
        $privateKeyPath,
        [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($env:RELEASE_ED25519_PRIVATE_KEY)),
        [Text.UTF8Encoding]::new($false))

    $msiPath = Join-Path $releasePath 'Infini-Transeon.msi'
    $zipPath = Join-Path $releasePath 'Infini-Transeon-portable.zip'
    $sourcePath = Join-Path $releasePath 'Infini-Transeon-source.zip'
    foreach ($path in @($msiPath, $zipPath, $sourcePath)) {
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Release artifact '$path' is missing." }
    }

    $artifacts = @(
        [ordered]@{
            byteSize = (Get-Item -LiteralPath $msiPath).Length
            codeSigning = 'unsigned'
            fileName = 'Infini-Transeon.msi'
            sha256 = (Get-FileHash -LiteralPath $msiPath -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            byteSize = (Get-Item -LiteralPath $zipPath).Length
            codeSigning = 'not-applicable'
            fileName = 'Infini-Transeon-portable.zip'
            sha256 = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
        },
        [ordered]@{
            byteSize = (Get-Item -LiteralPath $sourcePath).Length
            codeSigning = 'not-applicable'
            fileName = 'Infini-Transeon-source.zip'
            sha256 = (Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    )
    $unsigned = [ordered]@{
        architecture = 'win-x64'
        artifacts = $artifacts
        channel = 'stable'
        minimumWindowsBuild = 22621
        publishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        releaseSequence = $ReleaseSequence
        releaseVersion = $versionText
        schemaVersion = 1
    }
    $unsignedJson = $unsigned | ConvertTo-Json -Depth 8 -Compress
    [IO.File]::WriteAllText($unsignedManifestPath, $unsignedJson, [Text.UTF8Encoding]::new($false))
    & (Join-Path $PSScriptRoot 'convert-to-canonical-json.ps1') `
        -InputPath $unsignedManifestPath -OutputPath $unsignedManifestPath
    $openSsl = 'C:\Program Files\Git\usr\bin\openssl.exe'
    if (-not (Test-Path -LiteralPath $openSsl -PathType Leaf)) { throw 'OpenSSL from Git for Windows was not found.' }
    & $openSsl pkeyutl -sign -rawin -inkey $privateKeyPath -in $unsignedManifestPath -out $signaturePath
    if ($LASTEXITCODE -ne 0) { throw 'Ed25519 manifest signing failed.' }
    $signature = [Convert]::ToBase64String([IO.File]::ReadAllBytes($signaturePath))
    $signed = [ordered]@{}
    foreach ($entry in $unsigned.GetEnumerator()) { $signed[$entry.Key] = $entry.Value }
    $signed.signatures = @([ordered]@{
        algorithm = 'Ed25519'
        keyId = $env:RELEASE_ED25519_KEY_ID
        signature = $signature
    })
    $manifestPath = Join-Path $releasePath 'release-manifest.json'
    [IO.File]::WriteAllText(
        $manifestPath,
        ($signed | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
}
finally {
    if (Test-Path -LiteralPath $tempRoot) {
        Remove-Item -LiteralPath $tempRoot -Recurse -Force
    }
}
