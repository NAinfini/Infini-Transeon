[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Version,
    [Parameter(Mandatory)] [string] $Publisher,
    [Parameter(Mandatory)] [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$versionText = $Version.TrimStart('v')
if ($versionText -notmatch '^(\d+)\.(\d+)\.(\d+)(?:-[0-9A-Za-z.-]+)?$') {
    throw "Invalid release version '$Version'."
}
$versionMajor = $Matches[1]
$versionMinor = $Matches[2]
$versionPatch = $Matches[3]
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null
$packagePath = Join-Path $outputPath 'package'
New-Item -ItemType Directory -Path $packagePath -Force | Out-Null

$identityTemplate = Join-Path $root 'packaging\identity\Package.appxmanifest'
$applicationTemplate = Join-Path $root 'src\InfiniTranseon.App\app.manifest'
[xml] $identity = Get-Content -LiteralPath $identityTemplate -Raw
[xml] $application = Get-Content -LiteralPath $applicationTemplate -Raw

$identityNode = $identity.SelectSingleNode(
    "/*[local-name()='Package']/*[local-name()='Identity']")
if ($null -eq $identityNode) { throw 'Identity package manifest has no Identity element.' }
$publisherText = $Publisher.Trim()
if ($publisherText -notmatch '^CN=[^,=]+(?:,\s*(?:CN|L|O|OU|E|C|S|STREET|T|G|I|SN|DC|SERIALNUMBER|Description|PostalCode|POBox|Phone|X21Address|dnQualifier|OID\.[0-9]+(?:\.[0-9]+)+)=[^,=]+)*$') {
    throw "Publisher '$Publisher' is not a supported certificate subject."
}
$identityNode.SetAttribute('Publisher', $publisherText)
$identityNode.SetAttribute('Version', "$versionMajor.$versionMinor.$versionPatch.0")

$assemblyNode = $application.DocumentElement
if ($null -eq $assemblyNode) { throw 'Application manifest has no assembly element.' }
$assemblyIdentityNode = $application.SelectSingleNode(
    "/*[local-name()='assembly']/*[local-name()='assemblyIdentity']")
if ($null -eq $assemblyIdentityNode) { throw 'Application manifest has no assemblyIdentity element.' }
$msixNode = $application.CreateElement('msix', 'urn:schemas-microsoft-com:msix.v1')
$null = $msixNode.SetAttribute('publisher', $publisherText)
$null = $msixNode.SetAttribute('packageName', $identityNode.GetAttribute('Name'))
$applicationNode = $identity.SelectSingleNode(
    "/*[local-name()='Package']/*[local-name()='Applications']/*[local-name()='Application']")
if ($null -eq $applicationNode) { throw 'Identity package manifest has no Application element.' }
$null = $msixNode.SetAttribute('applicationId', $applicationNode.GetAttribute('Id'))
$null = $assemblyNode.InsertAfter($msixNode, $assemblyIdentityNode)
$utf8 = [Text.UTF8Encoding]::new($false)
$settings = [Xml.XmlWriterSettings]::new()
$settings.Encoding = $utf8
$settings.Indent = $true
$settings.OmitXmlDeclaration = $false

$identityOutput = Join-Path $packagePath 'AppxManifest.xml'
$writer = [Xml.XmlWriter]::Create($identityOutput, $settings)
try { $identity.Save($writer) } finally { $writer.Dispose() }

$applicationOutput = Join-Path $outputPath 'app.manifest'
$writer = [Xml.XmlWriter]::Create($applicationOutput, $settings)
try { $application.Save($writer) } finally { $writer.Dispose() }

$assetSource = Join-Path $root 'packaging\identity\Assets'
$assetDestination = Join-Path $packagePath 'Assets'
$requiredAssets = @(
    'StoreLogo.png',
    'Square150x150Logo.png',
    'Square44x44Logo.png'
)
New-Item -ItemType Directory -Path $assetDestination -Force | Out-Null
foreach ($asset in $requiredAssets) {
    $source = Join-Path $assetSource $asset
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Identity package asset '$source' is missing."
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $assetDestination $asset) -Force
}

[pscustomobject]@{
    ApplicationManifest = $applicationOutput
    IdentityPackageDirectory = $packagePath
    PackageManifest = $identityOutput
}
