[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $MsiPath,
    [string] $WixPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$msiFullPath = (Resolve-Path -LiteralPath $MsiPath).Path
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'infini-msi-layout-' + [Guid]::NewGuid().ToString('N'))
$decompiledPath = Join-Path $temporaryRoot 'installer.wxs'

try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    if ([string]::IsNullOrWhiteSpace($WixPath)) {
        & dotnet tool run wix msi decompile $msiFullPath -o $decompiledPath
    }
    else {
        & $WixPath msi decompile $msiFullPath -o $decompiledPath
    }
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

    $majorUpgrade = $document.SelectSingleNode("//*[local-name()='MajorUpgrade']")
    if ($null -eq $majorUpgrade -or
        [string]::IsNullOrWhiteSpace($majorUpgrade.GetAttribute('DowngradeErrorMessage'))) {
        throw 'MSI must block downgrades.'
    }

    # WiX 5 decompilation can reconstruct a different MajorUpgrade.Schedule from the
    # compiled action order. Read the actual MSI table before asserting rollback safety.
    $installer = $database = $view = $null
    try {
        $installer = New-Object -ComObject WindowsInstaller.Installer
        $database = $installer.OpenDatabase($msiFullPath, 0)
        $view = $database.OpenView('SELECT `Action`, `Sequence` FROM `InstallExecuteSequence`')
        $view.Execute()
        $sequences = @{}
        while ($record = $view.Fetch()) {
            $sequences[$record.StringData(1)] = $record.IntegerData(2)
            [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($record)
        }
        $view.Close()
        if (-not $sequences.ContainsKey('RemoveExistingProducts') -or
            -not $sequences.ContainsKey('InstallInitialize') -or
            -not $sequences.ContainsKey('InstallFiles') -or
            $sequences.RemoveExistingProducts -le $sequences.InstallInitialize -or
            $sequences.RemoveExistingProducts -ge $sequences.InstallFiles) {
            throw 'MSI must remove the previous version inside the rollback transaction before installing new files.'
        }
    }
    finally {
        foreach ($comObject in @($view, $database, $installer)) {
            if ($null -ne $comObject) {
                [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($comObject)
            }
        }
    }

    [pscustomobject]@{
        FileCount = $fileNames.Count
        RequiredFileCount = $requiredFiles.Count
        UpgradeRollbackEnabled = $true
        MsiPath = $msiFullPath
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
