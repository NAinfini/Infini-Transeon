[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string[]] $BinaryPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$dumpbinCommand = Get-Command dumpbin.exe -ErrorAction SilentlyContinue
$dumpbin = if ($null -ne $dumpbinCommand) { $dumpbinCommand.Source } else { $null }
if ([string]::IsNullOrWhiteSpace($dumpbin)) {
    $dumpbin = Get-ChildItem `
        -Path (Join-Path $env:ProgramFiles 'Microsoft Visual Studio') `
        -Recurse `
        -Filter dumpbin.exe `
        -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -like '*\Hostx64\x64\dumpbin.exe' } |
        Sort-Object FullName -Descending |
        Select-Object -First 1 -ExpandProperty FullName
}
if ([string]::IsNullOrWhiteSpace($dumpbin)) {
    throw 'dumpbin.exe was not found in the installed Visual Studio toolchain.'
}

$forbiddenRuntime = '(?i)\b(?:clang_rt\.[^\s]+|msvcp\d+|vcruntime\d+(?:_1)?|ucrtbased)\.dll\b'
$results = foreach ($path in $BinaryPath) {
    $fullPath = (Resolve-Path -LiteralPath $path).Path
    $output = & $dumpbin /dependents $fullPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "Could not inspect native dependencies for '$fullPath'."
    }
    $matches = @([regex]::Matches(($output -join "`n"), $forbiddenRuntime) |
        ForEach-Object { $_.Value.ToLowerInvariant() } |
        Sort-Object -Unique)
    if ($matches.Count -gt 0) {
        throw "Binary '$fullPath' requires non-system runtime files: $($matches -join ', ')"
    }
    [pscustomobject]@{
        Binary = $fullPath
        ExternalRuntimeDependencyCount = 0
    }
}

$results
