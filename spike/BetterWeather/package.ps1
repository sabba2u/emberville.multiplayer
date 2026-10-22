# Builds the player release: BepInEx 5 + Better Weather, as a zip you unpack into the Emberville folder.
#   pwsh spike/BetterWeather/package.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/BetterWeather-v<version>.zip  (version = Plugin.Version)
# BepInEx is included so it works on its own; next to the multiplayer mod, unzipping it again changes nothing.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$version = [regex]::Match((Get-Content "$here\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "No version in Plugin.cs" }
$name = "BetterWeather-v$version"
$dist = Join-Path (Split-Path $here) "dist"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Building $name..."
dotnet build "$here\BetterWeather.csproj" -c Release -p:GamePath="$GamePath" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = Join-Path $here "bin\Release"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\plugins\BetterWeather" | Out-Null

# BepInEx 5 (loader + core), console window off for players (the log file stays on)
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GamePath $f) $stage }
Copy-Item (Join-Path $GamePath "BepInEx\core") "$stage\BepInEx\" -Recurse
$cfg = Get-Content (Join-Path $GamePath "BepInEx\config\BepInEx.cfg") -Raw
$cfg = [regex]::Replace($cfg, '(\[Logging\.Console\][^\[]*?Enabled = )true', '${1}false')
Set-Content "$stage\BepInEx\config\BepInEx.cfg" $cfg -NoNewline

Copy-Item (Join-Path $out "BetterWeather.dll") "$stage\BepInEx\plugins\BetterWeather\"

@"
Better Weather v$version
A rain storm for the Emberville Demo. Not made or supported by Cygnus Cross.

INSTALL
1. Find the game folder: Steam > right-click Emberville Demo > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).

USE
- /rain in chat (with the multiplayer mod) or Insert > Weather: the storm on / off.
- Pixel rain over the whole screen, slanted by a wind that shifts and gusts; a splash where each drop lands, a
  ripple on water; lightning striking the ground now and then; the world darker while it lasts. Outside only.
- With the multiplayer mod, the storm is everyone's, and everyone sees the same lightning.
Settings: BepInEx\config\spike.emberville.betterweather.cfg ([Rain] Density, Darkness, LightningEvery, OutdoorScenes).

UNINSTALL
Delete BepInEx\plugins\BetterWeather.

PROBLEMS
Send BepInEx\LogOutput.log (from the game folder) with a description.

CREDITS / LICENSES
BepInEx (LGPL-2.1) https://github.com/BepInEx/BepInEx, HarmonyX (MIT).
"@ | Set-Content "$stage\README-BetterWeather.txt"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
