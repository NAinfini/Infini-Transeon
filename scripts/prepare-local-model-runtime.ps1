<#
.SYNOPSIS
Assembles the local-model runtime beside a built application so local packages can be installed.

.DESCRIPTION
Three files decide whether the application can offer a local model at all: the signed catalog that
names the packages, the worker process that hosts them, and the native translation library the
worker loads. LocalModelManagementService and LocalModelRuntimeAvailability both look for them
next to the executable and nowhere else, which is correct for a shipped layout and leaves a source
build with no local models at all — the catalog reads as "not included in this release" and every
package, translation and OCR alike, stays unreachable.

Only the release workflow used to assemble that layout. This script is the same assembly step for a
developer build, so what is tested locally has the shape that ships. It copies; it does not sign.
Producing a signed catalog is scripts/build-signed-model-catalog.ps1's job and needs the release
private key, which never leaves the key owner.
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')] [string] $Configuration = 'Release',
    [string] $CatalogPath,
    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot (
        "src/InfiniTranseon.App/bin/$Configuration/net10.0-windows10.0.22621.0/win-x64")
}
if (-not (Test-Path -LiteralPath $OutputDirectory -PathType Container)) {
    throw "Application output directory '$OutputDirectory' does not exist. Build " +
        "src/InfiniTranseon.App in the $Configuration configuration first."
}
$outputPath = (Resolve-Path -LiteralPath $OutputDirectory).Path

if ([string]::IsNullOrWhiteSpace($CatalogPath)) {
    $CatalogPath = Join-Path $repositoryRoot 'packaging/model-catalog.json'
}
if (-not (Test-Path -LiteralPath $CatalogPath -PathType Leaf)) {
    throw "Signed model catalog '$CatalogPath' was not found. The key owner produces one with: " +
        "./scripts/build-signed-model-catalog.ps1 -TemplatePath packaging/model-catalog.template.json " +
        "-OutputPath packaging/model-catalog.json -CatalogSequence 1"
}

$nativeLibraryPath = Join-Path $repositoryRoot (
    "artifacts/cmake/windows-x64/src/InfiniTranseon.ModelRuntime.Native/$Configuration/" +
    'InfiniTranseon.ModelRuntime.Native.dll')
if (-not (Test-Path -LiteralPath $nativeLibraryPath -PathType Leaf)) {
    throw "Native translation runtime '$nativeLibraryPath' was not found. It is off by default; " +
        'build it with: cmake --preset windows-x64 -DINFINI_ENABLE_LOCAL_MODEL_RUNTIME=ON; ' +
        "cmake --build --preset windows-x64-$($Configuration.ToLowerInvariant())"
}

# Published the way the release job publishes it — self-contained and single-file — so a developer
# exercising the worker exercises the executable users will actually run.
$workerPublishPath = Join-Path $repositoryRoot "artifacts/local-model-runtime/$Configuration"
dotnet publish (Join-Path $repositoryRoot 'src/InfiniTranseon.ModelWorker/InfiniTranseon.ModelWorker.csproj') `
    -c $Configuration -r win-x64 --self-contained true -p:PublishSingleFile=true -o $workerPublishPath
if ($LASTEXITCODE -ne 0) { throw 'Publishing InfiniTranseon.ModelWorker failed.' }

$copied = @(
    (Join-Path $workerPublishPath 'InfiniTranseon.ModelWorker.exe'),
    $nativeLibraryPath,
    $CatalogPath
)
foreach ($source in $copied) {
    Copy-Item -LiteralPath $source -Destination $outputPath -Force
}

[pscustomobject]@{
    Configuration = $Configuration
    OutputDirectory = $outputPath
    Files = @($copied | ForEach-Object { Split-Path -Leaf $_ })
}
