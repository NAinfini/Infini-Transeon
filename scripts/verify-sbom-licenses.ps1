param(
    [Parameter(Mandatory = $true)]
    [string]$BomPath,
    [string]$ModelCatalogPath,
    [string]$NoticesOutputPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$allowed = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::OrdinalIgnoreCase)
@(
    'Apache-2.0',
    'MIT',
    'BSD-2-Clause',
    'BSD-3-Clause',
    'ISC',
    'Zlib',
    'Unicode-3.0',
    'MS-PL',
    'LicenseRef-Public-Domain-SQLite'
) | ForEach-Object { [void]$allowed.Add($_) }

$reviewedLicenseOverrides = @{
    # SQLite declares a public-domain dedication rather than an SPDX expression.
    # Pin the exact binary package version so every upgrade requires a fresh review.
    'SourceGear.sqlite3@3.50.4.5' = 'LicenseRef-Public-Domain-SQLite'
}
$notices = [System.Collections.Generic.List[object]]::new()

function Assert-CompatibleLicense {
    param(
        [Parameter(Mandatory = $true)] [string]$Component,
        [Parameter(Mandatory = $true)] [AllowEmptyCollection()] [string[]]$Expressions
    )

    if ($Expressions.Count -eq 0) {
        throw "Component '$Component' has no machine-readable license."
    }
    foreach ($expression in $Expressions) {
        if (-not $allowed.Contains($expression)) {
            throw "Component '$Component' uses unknown or incompatible license '$expression'."
        }
    }
}

$bom = Get-Content -LiteralPath $BomPath -Raw | ConvertFrom-Json
foreach ($component in @($bom.components)) {
    $expressions = [System.Collections.Generic.List[string]]::new()
    $licenseEntries = if ($component.PSObject.Properties.Name -contains 'licenses') {
        @($component.licenses)
    } else {
        @()
    }
    foreach ($entry in $licenseEntries) {
        if (($entry.PSObject.Properties.Name -contains 'expression') -and
            -not [string]::IsNullOrWhiteSpace($entry.expression)) {
            $expressions.Add([string]$entry.expression)
        } elseif (($entry.PSObject.Properties.Name -contains 'license') -and
            $null -ne $entry.license -and
            ($entry.license.PSObject.Properties.Name -contains 'id') -and
            -not [string]::IsNullOrWhiteSpace($entry.license.id)) {
            $expressions.Add([string]$entry.license.id)
        }
    }
    $componentVersion = if ($component.PSObject.Properties.Name -contains 'version') {
        [string]$component.version
    } else {
        ''
    }
    $overrideKey = "$([string]$component.name)@$componentVersion"
    if ($expressions.Count -eq 0 -and $reviewedLicenseOverrides.ContainsKey($overrideKey)) {
        $expressions.Add([string]$reviewedLicenseOverrides[$overrideKey])
    }
    Assert-CompatibleLicense -Component ([string]$component.name) -Expressions $expressions.ToArray()
    $notices.Add([ordered]@{
        name = [string]$component.name
        version = $componentVersion
        licenses = @($expressions.ToArray() | Sort-Object -Unique)
        packageUrl = if ($component.PSObject.Properties.Name -contains 'purl') { [string]$component.purl } else { $null }
    })
}

if (-not [string]::IsNullOrWhiteSpace($ModelCatalogPath) -and
    (Test-Path -LiteralPath $ModelCatalogPath)) {
    $catalog = Get-Content -LiteralPath $ModelCatalogPath -Raw | ConvertFrom-Json
    foreach ($model in @($catalog.models)) {
        Assert-CompatibleLicense -Component ([string]$model.modelId) -Expressions @([string]$model.licenseSpdx)
    }
}

if (-not [string]::IsNullOrWhiteSpace($NoticesOutputPath)) {
    $noticeDirectory = Split-Path -Parent $NoticesOutputPath
    if (-not [string]::IsNullOrWhiteSpace($noticeDirectory)) {
        New-Item -ItemType Directory -Force -Path $noticeDirectory | Out-Null
    }
    $document = [ordered]@{
        schemaVersion = 1
        generatedFrom = 'CycloneDX SBOM'
        components = @($notices | Sort-Object -Property name,version)
    }
    [IO.File]::WriteAllText(
        $NoticesOutputPath,
        ($document | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
}

Write-Output 'SBOM and model licenses satisfy the explicit allowlist.'
