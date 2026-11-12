# Closes Resonite gracefully (never kills it), builds + installs the mod into the game's rml_mods / rml_libs, relaunches in
# desktop mode with ResoniteModLoader loaded. Development helper: it restarts your game, so only run it when that is fine.
#   -ResonitePath  the Resonite folder (default: Local.props, then $env:RESONITE_PATH, then the default Steam path)
#   -LaunchArgs    arguments for Resonite.exe
#   -NoLaunch      install only
param(
    [string]$ResonitePath,
    [switch]$NoLaunch,
    [string]$LaunchArgs = '-Screen -LoadAssembly Libraries/ResoniteModLoader.dll',
    [int]$CloseTimeoutSeconds = 90
)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"
$dir = Get-ResonitePath $ResonitePath
if (-not (Test-Path (Join-Path $dir 'Resonite.exe'))) { throw "Resonite.exe not found in '$dir'. Pass -ResonitePath or set RESONITE_PATH / Local.props." }

$names = 'Resonite', 'Renderite.Host', 'Renderite.Renderer'
function Get-Reso { Get-Process -ErrorAction SilentlyContinue | Where-Object { $names -contains $_.Name } }

foreach ($p in @(Get-Reso)) { if ($p.MainWindowHandle -ne 0) { [void]$p.CloseMainWindow() } }
$deadline = (Get-Date).AddSeconds($CloseTimeoutSeconds)
while ((Get-Reso) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 1 }
if (Get-Reso) { throw "Resonite did not exit within $CloseTimeoutSeconds s. Close it by hand and rerun." }

dotnet build (Join-Path (Split-Path -Parent $PSScriptRoot) 'src') -c Release -p:InstallMod=true "-p:ResonitePath=$dir"
if ($LASTEXITCODE -ne 0) { throw 'Build/install failed.' }

if (-not $NoLaunch) { Start-Process -FilePath (Join-Path $dir 'Resonite.exe') -ArgumentList $LaunchArgs -WorkingDirectory $dir }
