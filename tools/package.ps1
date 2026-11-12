# Builds the Release configuration and assembles dist\SpeechToText-<version>.zip, laid out so its contents can be copied
# straight into the Resonite folder:
#   rml_mods\SpeechToText.dll
#   rml_libs\SpeechToText.Core.dll
#   rml_libs\SpeechToText\<Whisper.net, its dependency, runtimes\win-x64\*>
# plus README.md, docs\USAGE.md, LICENSE (if present) and the recipe package. No models are bundled (the mod downloads them on first use).
param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$src = Join-Path $root 'src'
dotnet build $src -c $Configuration
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

$out = Join-Path $src "bin\$Configuration\net10.0"
[xml]$proj = Get-Content (Join-Path $src 'SpeechToText.csproj')
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$name = "SpeechToText-$version"
$stage = Join-Path $root "dist\$name"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force (Join-Path $stage 'rml_mods'), (Join-Path $stage 'rml_libs\SpeechToText\runtimes\win-x64') | Out-Null

Copy-Item (Join-Path $out 'SpeechToText.dll') (Join-Path $stage 'rml_mods')
Copy-Item (Join-Path $out 'SpeechToText.Core.dll') (Join-Path $stage 'rml_libs')
foreach ($f in 'Whisper.net.dll', 'Microsoft.Extensions.AI.Abstractions.dll') {
    Copy-Item (Join-Path $out $f) (Join-Path $stage 'rml_libs\SpeechToText')
}
Copy-Item (Join-Path $out 'runtimes\win-x64\*') (Join-Path $stage 'rml_libs\SpeechToText\runtimes\win-x64')

Copy-Item (Join-Path $root 'README.md') $stage
New-Item -ItemType Directory -Force (Join-Path $stage 'docs') | Out-Null
Copy-Item (Join-Path $root 'docs\USAGE.md') (Join-Path $stage 'docs')
Copy-Item (Join-Path $root 'docs\INSTALL.md') (Join-Path $stage 'docs')
if (Test-Path (Join-Path $root 'LICENSE')) { Copy-Item (Join-Path $root 'LICENSE') $stage }
$recipes = Join-Path $root 'recipes\SpeechToText-Recipes.resonitepackage'
if (Test-Path $recipes) { Copy-Item $recipes $stage } else { Write-Warning 'recipes\SpeechToText-Recipes.resonitepackage not found; the zip has no recipes.' }

$zip = Join-Path $root "dist\$name.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip
Get-ChildItem $stage -Recurse -File | ForEach-Object { '{0,10:N0}  {1}' -f $_.Length, $_.FullName.Substring($stage.Length + 1) }
"`n$zip  ({0:N1} MB)" -f ((Get-Item $zip).Length / 1MB)
