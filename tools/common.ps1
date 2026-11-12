# Shared helpers for the scripts in this folder. Dot-source it:  . "$PSScriptRoot\common.ps1"

function Get-ResonitePath([string]$Override) {
    # Same order as src\SpeechToText.csproj: explicit value, Local.props, RESONITE_PATH, the default Steam location.
    if ($Override) { return $Override }

    $localProps = Join-Path (Split-Path -Parent $PSScriptRoot) 'Local.props'
    if (Test-Path $localProps) {
        [xml]$xml = Get-Content $localProps
        $value = ($xml.Project.PropertyGroup | Where-Object { $_.ResonitePath } | Select-Object -First 1).ResonitePath
        if ($value) { return $value }
    }
    if ($env:RESONITE_PATH) { return $env:RESONITE_PATH }
    return 'C:\Program Files (x86)\Steam\steamapps\common\Resonite'
}
