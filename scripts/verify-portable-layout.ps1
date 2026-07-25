[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PublishDirectory,
    [string] $ManifestPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$publishPath = (Resolve-Path -LiteralPath $PublishDirectory).Path
if ([string]::IsNullOrWhiteSpace($ManifestPath)) {
    $ManifestPath = Join-Path $publishPath 'portable-manifest.json'
}
$manifestFullPath = (Resolve-Path -LiteralPath $ManifestPath).Path
$manifest = Get-Content -LiteralPath $manifestFullPath -Raw | ConvertFrom-Json

if ($manifest.schemaVersion -ne 1 -or $manifest.platform -ne 'windows' -or
    $manifest.architecture -ne 'x64' -or $manifest.minimumWindowsBuild -lt 22621) {
    throw 'Portable manifest platform contract is invalid.'
}
if ([string]::IsNullOrWhiteSpace($manifest.entryPoint)) {
    throw 'Portable manifest entry point is missing.'
}

$required = @($manifest.requiredFiles)
if ($required.Count -eq 0 -or $required -notcontains $manifest.entryPoint) {
    throw 'Portable manifest required files must include the entry point.'
}
foreach ($relativePath in $required) {
    if ([string]::IsNullOrWhiteSpace($relativePath) -or
        [IO.Path]::IsPathRooted($relativePath) -or
        $relativePath.Contains('..') -or
        $relativePath.Contains('/') -or
        $relativePath.Contains('\')) {
        throw "Portable required file '$relativePath' is not a root file name."
    }
    if (-not (Test-Path -LiteralPath (Join-Path $publishPath $relativePath) -PathType Leaf)) {
        throw "Portable required file '$relativePath' is missing."
    }
}

$relativeFiles = @(Get-ChildItem -LiteralPath $publishPath -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($publishPath, $_.FullName).Replace('\', '/')
})
foreach ($pattern in @($manifest.excludedPatterns)) {
    if ([string]::IsNullOrWhiteSpace($pattern)) {
        throw 'Portable manifest contains an empty exclusion pattern.'
    }
    $matches = @($relativeFiles | Where-Object { $_ -like $pattern })
    if ($matches.Count -gt 0) {
        throw "Portable layout contains excluded content for '$pattern': $($matches -join ', ')"
    }
}

[pscustomobject]@{
    FileCount = $relativeFiles.Count
    RequiredFileCount = $required.Count
    PublishDirectory = $publishPath
}
