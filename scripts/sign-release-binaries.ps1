[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
foreach ($name in @('AUTHENTICODE_CERTIFICATE', 'AUTHENTICODE_PASSWORD', 'AUTHENTICODE_PUBLISHER')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Release secret '$name' is required."
    }
}

$required = @(
    'InfiniTranseon.App.exe',
    'InfiniTranseon.EngineHost.exe',
    'InfiniTranseon.ModelWorker.exe',
    'InfiniTranseon.Identity.msix'
)
foreach ($name in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $name) -PathType Leaf)) {
        throw "Required release binary '$name' is missing."
    }
}

$temporaryRoot = Join-Path $env:RUNNER_TEMP (
    'infini-binary-signing-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
$certificatePath = Join-Path $temporaryRoot 'codesign.pfx'
$importedCertificate = $null
try {
    [IO.File]::WriteAllBytes(
        $certificatePath,
        [Convert]::FromBase64String($env:AUTHENTICODE_CERTIFICATE))
    $password = ConvertTo-SecureString $env:AUTHENTICODE_PASSWORD -AsPlainText -Force
    $importedCertificate = Import-PfxCertificate `
        -FilePath $certificatePath `
        -CertStoreLocation Cert:\CurrentUser\My `
        -Password $password
    if (-not [String]::Equals(
            $importedCertificate.Subject,
            $env:AUTHENTICODE_PUBLISHER,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Authenticode certificate subject does not match AUTHENTICODE_PUBLISHER.'
    }
    $signTool = (Get-ChildItem -Path "${env:ProgramFiles(x86)}\Windows Kits\10\bin" `
        -Recurse -Filter signtool.exe |
        Where-Object { $_.FullName -like '*\x64\signtool.exe' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1).FullName
    if ([string]::IsNullOrWhiteSpace($signTool)) { throw 'signtool.exe was not found.' }

    $binaries = @(Get-ChildItem -LiteralPath $publishPath -Recurse -File |
        Where-Object { $_.Extension -in @('.exe', '.dll', '.msix') })
    if ($binaries.Count -eq 0) { throw 'No release binaries were found to sign.' }
    foreach ($binary in $binaries) {
        & $signTool sign /sha1 $importedCertificate.Thumbprint /fd SHA256 /td SHA256 `
            /tr http://timestamp.digicert.com $binary.FullName
        if ($LASTEXITCODE -ne 0) { throw "Authenticode signing failed for '$($binary.Name)'." }
        & $signTool verify /pa /all $binary.FullName
        if ($LASTEXITCODE -ne 0) { throw "Authenticode verification failed for '$($binary.Name)'." }
    }
}
finally {
    if ($null -ne $importedCertificate) {
        Remove-Item -LiteralPath (
            'Cert:\CurrentUser\My\' + $importedCertificate.Thumbprint) `
            -Force -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
