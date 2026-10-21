# Builds the player release: BepInEx 5 + Better Fishing, as a zip you unpack into the Emberville folder.
#   pwsh spike/BetterFishing/package.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/BetterFishing-v<version>.zip  (version = Plugin.Version)
# BepInEx is included so it works on its own; next to the multiplayer mod, unzipping it again changes nothing.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$version = [regex]::Match((Get-Content "$here\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "No version in Plugin.cs" }
$name = "BetterFishing-v$version"
$dist = Join-Path (Split-Path $here) "dist"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Building $name..."
dotnet build "$here\BetterFishing.csproj" -c Release -p:GamePath="$GamePath" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = Join-Path $here "bin\Release"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\plugins\BetterFishing" | Out-Null

# BepInEx 5 (loader + core), console window off for players (the log file stays on)
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GamePath $f) $stage }
Copy-Item (Join-Path $GamePath "BepInEx\core") "$stage\BepInEx\" -Recurse
$cfg = Get-Content (Join-Path $GamePath "BepInEx\config\BepInEx.cfg") -Raw
$cfg = [regex]::Replace($cfg, '(\[Logging\.Console\][^\[]*?Enabled = )true', '${1}false')
Set-Content "$stage\BepInEx\config\BepInEx.cfg" $cfg -NoNewline

Copy-Item (Join-Path $out "BetterFishing.dll") "$stage\BepInEx\plugins\BetterFishing\"

@"
Better Fishing v$version
Fishing that pays, and more places to fish, in the Emberville Demo. Not made or supported by Cygnus Cross.

INSTALL
1. Find the game folder: Steam > right-click Emberville Demo > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).

USE
- Fishing is Terraria's: after the cast, wait for the bite (a splash and a "!", 1-5 seconds), then left-click
  within a second. Too soon or too late and it gets away.
- Every catch brings something up: food or a material, and a quarter of the time (25%) anything at all, up to legendary.
  (The Fishing XP stays as it was.)
- Fish wherever you see fish shadows in the water: walk up to the bank near one and the fishing hint shows.
  The starting area's pond has eight. The game's own spots (the Vitromotus Crystal) pay too.
Settings: BepInEx\config\spike.emberville.betterfishing.cfg ([Fishing] RareChance = 25).

UNINSTALL
Delete BepInEx\plugins\BetterFishing.

PROBLEMS
Send BepInEx\LogOutput.log (from the game folder) with a description.

CREDITS / LICENSES
BepInEx (LGPL-2.1) https://github.com/BepInEx/BepInEx, HarmonyX (MIT).
"@ | Set-Content "$stage\README-BetterFishing.txt"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
