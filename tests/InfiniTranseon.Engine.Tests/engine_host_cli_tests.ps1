param(
    [Parameter(Mandatory = $true)]
    [string]$EngineHostPath
)

$errorFile = [System.IO.Path]::GetTempFileName()
try {
    & $EngineHostPath 2> $errorFile
    $exitCode = $LASTEXITCODE
    $errorText = [System.IO.File]::ReadAllText($errorFile)
    if ($exitCode -ne 64) {
        throw "Expected exit code 64, received $exitCode."
    }
    if ($errorText -notmatch 'Invalid EngineHost startup arguments') {
        throw "Unexpected diagnostic: $errorText"
    }
}
finally {
    Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue
}
