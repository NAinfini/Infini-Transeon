param(
    [Parameter(Mandatory = $true)]
    [string]$BomPath,
    [string]$ModelCatalogPath,
    [string]$NativeNoticesPath,
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
    'LicenseRef-Public-Domain-SQLite',
    'LicenseRef-Microsoft-Software-License-Terms'
) | ForEach-Object { [void]$allowed.Add($_) }

$reviewedLicenseOverrides = @{
    # SQLite declares a public-domain dedication rather than an SPDX expression.
    # Pin the exact binary package version so every upgrade requires a fresh review.
    'SourceGear.sqlite3@3.50.4.5' = 'LicenseRef-Public-Domain-SQLite'

    # Windows App SDK / WebView2 / Windows SDK first-party packages ship the
    # "Microsoft Software License Terms" as a license FILE (no SPDX expression in
    # the nuspec). Those terms explicitly permit redistribution with the
    # application (self-contained WinUI deployment), reviewed 2026-07-22.
    # Versions are pinned so every upgrade forces a fresh review here.
    'Microsoft.Web.WebView2@1.0.3719.77' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.Windows.AI.MachineLearning@2.1.70' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.Windows.SDK.BuildTools@10.0.26100.4654' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.Windows.SDK.BuildTools.MSIX@1.7.251221100' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.WindowsAppSDK@2.2.0' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.WindowsAppSDK.AI@2.2.3' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.WindowsAppSDK.Base@2.0.4' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.WindowsAppSDK.DWrite@2.1.0' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.WindowsAppSDK.Foundation@2.1.0' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.WindowsAppSDK.InteractiveExperiences@2.0.15' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.WindowsAppSDK.ML@2.1.70' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.WindowsAppSDK.Runtime@2.2.0' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.WindowsAppSDK.Widgets@2.0.5' = 'LicenseRef-Microsoft-Software-License-Terms'
    'Microsoft.WindowsAppSDK.WinUI@2.2.1' = 'LicenseRef-Microsoft-Software-License-Terms'

    # ONNX Runtime ships an MIT license file in both NuGet packages, but CycloneDX
    # does not convert file-based license metadata into an SPDX expression.
    # Reviewed against the packaged license files on 2026-07-28. Versions are pinned
    # so every upgrade forces a fresh review.
    'Microsoft.ML.OnnxRuntime@1.27.1' = 'MIT'
    'Microsoft.ML.OnnxRuntime.Managed@1.27.1' = 'MIT'
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

if (-not [string]::IsNullOrWhiteSpace($NativeNoticesPath)) {
    $native = Get-Content -LiteralPath $NativeNoticesPath -Raw | ConvertFrom-Json
    if ($native.schemaVersion -ne 1 -or @($native.components).Count -eq 0) {
        throw 'Native third-party notices are invalid or empty.'
    }
    foreach ($component in @($native.components)) {
        $expressions = @($component.licenses | ForEach-Object { [string]$_ })
        Assert-CompatibleLicense `
            -Component ([string]$component.name) `
            -Expressions $expressions
        $notices.Add([ordered]@{
            name = [string]$component.name
            version = [string]$component.version
            licenses = @($expressions | Sort-Object -Unique)
            licenseFile = [string]$component.licenseFile
        })
    }
}

if (-not [string]::IsNullOrWhiteSpace($ModelCatalogPath) -and
    (Test-Path -LiteralPath $ModelCatalogPath)) {
    $catalog = Get-Content -LiteralPath $ModelCatalogPath -Raw | ConvertFrom-Json
    foreach ($model in @($catalog.models)) {
        $expression = [string]$model.licenseSpdx
        Assert-CompatibleLicense -Component ([string]$model.modelId) -Expressions @($expression)
        # Downloadable models are redistributed by this application, so Apache-2.0 attribution
        # applies to them exactly as it does to the linked libraries. Checking the licence
        # without emitting a notice left the shipped file silent about every model.
        $notices.Add([ordered]@{
            name = [string]$model.modelId
            version = [string]$model.version
            licenses = @($expression)
            downloadOrigins = if ($model.PSObject.Properties.Name -contains 'downloadOrigins') {
                @($model.downloadOrigins | ForEach-Object { [string]$_ })
            } else {
                @()
            }
        })
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
