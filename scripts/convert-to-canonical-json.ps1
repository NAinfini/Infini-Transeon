[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $InputPath,
    [Parameter(Mandatory)] [string] $OutputPath
)

$ErrorActionPreference = 'Stop'

function Write-CanonicalElement {
    param(
        [Parameter(Mandatory)] [System.Text.Json.Utf8JsonWriter] $Writer,
        [Parameter(Mandatory)] [System.Text.Json.JsonElement] $Element,
        [bool] $IsRoot = $false
    )

    switch ($Element.ValueKind) {
        ([System.Text.Json.JsonValueKind]::Object) {
            $Writer.WriteStartObject()
            $properties = @($Element.EnumerateObject()) | Sort-Object -Property Name -CaseSensitive
            foreach ($property in $properties) {
                if ($IsRoot -and [string]::Equals(
                        $property.Name, 'signatures', [StringComparison]::Ordinal)) {
                    continue
                }
                $Writer.WritePropertyName($property.Name)
                Write-CanonicalElement -Writer $Writer -Element $property.Value
            }
            $Writer.WriteEndObject()
        }
        ([System.Text.Json.JsonValueKind]::Array) {
            $Writer.WriteStartArray()
            foreach ($item in $Element.EnumerateArray()) {
                Write-CanonicalElement -Writer $Writer -Element $item
            }
            $Writer.WriteEndArray()
        }
        ([System.Text.Json.JsonValueKind]::String) { $Writer.WriteStringValue($Element.GetString()) }
        ([System.Text.Json.JsonValueKind]::Number) {
            $integer = 0L
            $decimalValue = 0D
            if ($Element.TryGetInt64([ref]$integer)) {
                $Writer.WriteNumberValue($integer)
            }
            elseif ($Element.TryGetDecimal([ref]$decimalValue)) {
                $Writer.WriteNumberValue($decimalValue)
            }
            else {
                $Writer.WriteNumberValue($Element.GetDouble())
            }
        }
        ([System.Text.Json.JsonValueKind]::True) { $Writer.WriteBooleanValue($true) }
        ([System.Text.Json.JsonValueKind]::False) { $Writer.WriteBooleanValue($false) }
        ([System.Text.Json.JsonValueKind]::Null) { $Writer.WriteNullValue() }
        default { throw "Unsupported JSON token '$($Element.ValueKind)'." }
    }
}

$json = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $InputPath).Path, [Text.Encoding]::UTF8)
$document = [System.Text.Json.JsonDocument]::Parse([string]$json)
$stream = [IO.MemoryStream]::new()
$writer = [System.Text.Json.Utf8JsonWriter]::new($stream)
try {
    Write-CanonicalElement -Writer $writer -Element $document.RootElement -IsRoot $true
    $writer.Flush()
    $destination = [IO.Path]::GetFullPath($OutputPath)
    $directory = [IO.Path]::GetDirectoryName($destination)
    if ([string]::IsNullOrWhiteSpace($directory)) { throw 'Canonical JSON output has no directory.' }
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    [IO.File]::WriteAllBytes($destination, $stream.ToArray())
}
finally {
    $writer.Dispose()
    $stream.Dispose()
    $document.Dispose()
}
