param(
    [Parameter(Mandatory = $true)]
    [string]$BomPath,
    [string]$ModelCatalogPath
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
    'MS-PL'
) | ForEach-Object { [void]$allowed.Add($_) }

function Assert-CompatibleLicense {
    param(
        [Parameter(Mandatory = $true)] [string]$Component,
        [Parameter(Mandatory = $true)] [string[]]$Expressions
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
    Assert-CompatibleLicense -Component ([string]$component.name) -Expressions $expressions.ToArray()
}

if (-not [string]::IsNullOrWhiteSpace($ModelCatalogPath) -and
    (Test-Path -LiteralPath $ModelCatalogPath)) {
    $catalog = Get-Content -LiteralPath $ModelCatalogPath -Raw | ConvertFrom-Json
    foreach ($model in @($catalog.models)) {
        Assert-CompatibleLicense -Component ([string]$model.modelId) -Expressions @([string]$model.licenseSpdx)
    }
}

Write-Output 'SBOM and model licenses satisfy the explicit allowlist.'
