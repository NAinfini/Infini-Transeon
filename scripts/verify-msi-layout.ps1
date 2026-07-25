[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MsiPath,
    [string] $WixPath = 'wix'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$msiFullPath = (Resolve-Path -LiteralPath $MsiPath).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'infini-msi-layout-' + [Guid]::NewGuid().ToString('N'))
$decompiledPath = Join-Path $temporaryRoot 'installer.wxs'

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    & $WixPath msi decompile $msiFullPath -o $decompiledPath
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not decompile the MSI for layout verification.'
    }

    [xml] $document = Get-Content -LiteralPath $decompiledPath -Raw
    $fileNames = @($document.SelectNodes("//*[local-name()='File']") | ForEach-Object {
        $_.GetAttribute('Name')
    })
    $requiredFiles = @(
        'InfiniTranseon.App.exe',
        'InfiniTranseon.EngineHost.exe',
        'InfiniTranseon.ModelWorker.exe',
        'InfiniTranseon.ModelRuntime.Native.dll',
        # Local OCR runs in-process on ONNX Runtime. Without this native library the PP-OCR
        # backend throws DllNotFoundException on the user's machine, and only for the languages
        # Windows OCR cannot read, so a dropped payload would ship unnoticed.
        'onnxruntime.dll',
        'model-catalog.json',
        'resources.pri',
        'LICENSE',
        'NOTICE',
        'THIRD-PARTY-NOTICES.json',
        'sbom.json'
    )
    $missing = @($requiredFiles | Where-Object { $fileNames -notcontains $_ })
    if ($missing.Count -gt 0) {
        throw "MSI is missing required payload files: $($missing -join ', ')"
    }

    [pscustomobject]@{
        FileCount = $fileNames.Count
        RequiredFileCount = $requiredFiles.Count
        MsiPath = $msiFullPath
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
