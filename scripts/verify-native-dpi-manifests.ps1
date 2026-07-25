[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $BinaryPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$mtCommand = Get-Command mt.exe -ErrorAction SilentlyContinue
$mt = if ($null -ne $mtCommand) { $mtCommand.Source } else { $null }
if ([string]::IsNullOrWhiteSpace($mt)) {
    $mt = Get-ChildItem `
        -Path (Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin') `
        -Recurse `
        -Filter mt.exe `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\x64\mt.exe' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($mt)) {
    throw 'mt.exe was not found in the installed Windows SDK.'
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) (
    'infini-native-manifest-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $results = foreach ($path in $BinaryPath) {
        $fullPath = (Resolve-Path -LiteralPath $path).Path
        $manifestPath = Join-Path $temporaryRoot (
            [IO.Path]::GetFileNameWithoutExtension($fullPath) + '.manifest')
        & $mt "-inputresource:$fullPath;#1" "-out:$manifestPath"
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $manifestPath)) {
            throw "Could not extract the application manifest from '$fullPath'."
        }

        [xml] $manifest = Get-Content -LiteralPath $manifestPath -Raw
        $dpiNode = $manifest.SelectSingleNode("//*[local-name()='dpiAwareness']")
        if ($null -eq $dpiNode -or
            $dpiNode.InnerText.Trim() -notmatch '^(?i)PerMonitorV2(?:,\s*unaware)?$') {
            throw "Binary '$fullPath' is not declared Per-Monitor-V2 DPI aware."
        }

        [pscustomobject]@{
            Binary = $fullPath
            DpiAwareness = $dpiNode.InnerText.Trim()
        }
    }
    $results
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
