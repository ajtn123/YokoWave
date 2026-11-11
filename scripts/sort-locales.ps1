param([switch]$Check)

$ErrorActionPreference = 'Stop'
$localeRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../locales'))
$options = [Text.Json.JsonSerializerOptions]::new()
$options.WriteIndented = $true
$options.Encoder = [Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping
foreach ($file in Get-ChildItem -LiteralPath $localeRoot -Recurse -Filter '*.json' -File) {
    $original = [IO.File]::ReadAllText($file.FullName)
    $document = [Text.Json.JsonDocument]::Parse($original)
    try {
        $catalog = [Collections.Generic.SortedDictionary[string, string]]::new([StringComparer]::Ordinal)
        foreach ($entry in $document.RootElement.EnumerateObject()) {
            if ($entry.Value.ValueKind -ne [Text.Json.JsonValueKind]::String -or $catalog.ContainsKey($entry.Name)) {
                throw "Invalid or duplicate localization key in $($file.Name): $($entry.Name)"
            }
            $catalog.Add($entry.Name, $entry.Value.GetString())
        }
        $formatted = [Text.Json.JsonSerializer]::Serialize($catalog, $options).Replace("`r`n", "`n") + "`n"
        if ($Check) {
            if ($original.Replace("`r`n", "`n") -cne $formatted) {
                throw "Unsorted or unformatted catalog: $($file.FullName). Run scripts/sort-locales.ps1."
            }
        }
        elseif ($original -cne $formatted) {
            [IO.File]::WriteAllText($file.FullName, $formatted, [Text.UTF8Encoding]::new($false))
        }
    }
    finally {
        $document.Dispose()
    }
}
