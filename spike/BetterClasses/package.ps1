# Builds the player release: BepInEx 5 + Better Classes, as a zip you unpack into the Emberville folder.
#   pwsh spike/BetterClasses/package.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/BetterClasses-v<version>.zip  (version = Plugin.Version)
# BepInEx is included so it works on its own; next to the multiplayer mod, unzipping it again changes nothing.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$version = [regex]::Match((Get-Content "$here\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "No version in Plugin.cs" }
$name = "BetterClasses-v$version"
$dist = Join-Path (Split-Path $here) "dist"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Building $name..."
dotnet build "$here\BetterClasses.csproj" -c Release -p:GamePath="$GamePath" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = Join-Path $here "bin\Release"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\plugins\BetterClasses" | Out-Null

# BepInEx 5 (loader + core), console window off for players (the log file stays on)
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GamePath $f) $stage }
Copy-Item (Join-Path $GamePath "BepInEx\core") "$stage\BepInEx\" -Recurse
$cfg = Get-Content (Join-Path $GamePath "BepInEx\config\BepInEx.cfg") -Raw
$cfg = [regex]::Replace($cfg, '(\[Logging\.Console\][^\[]*?Enabled = )true', '${1}false')
Set-Content "$stage\BepInEx\config\BepInEx.cfg" $cfg -NoNewline

Copy-Item (Join-Path $out "BetterClasses.dll") "$stage\BepInEx\plugins\BetterClasses\"

@"
Better Classes v$version
New classes for the Emberville Demo. Not made or supported by Cygnus Cross.

INSTALL
1. Find the game folder: Steam > right-click Emberville Demo > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).

USE
- EARLY ALPHA: it works, but it's rough; borrowed art, numbers still changing.
- The Ninja is on the class screen: learn it there like any class (or /ninja in chat, with the multiplayer mod:
  that also learns every Ninja skill, for testing). As a Ninja you get 20 extra skill points.
- Fast (+15% move speed). Bare hands throw poisoned arrows (half a creature's health over 5 s; arrows until there
  are throwing stars). The dodge vanishes in smoke, 1.5x as far, through walls and doors.
- Skill tree: Hunter's Hook (a foe it catches is dragged to you and stunned), Fire Palm (hold it: a cone of fire;
  burning creatures panic and spread the fire), Chain Lightning (everything on screen, stunned 5 s) and Spike Trap
  (up to three; the first creature on one is spiked and held).
- It takes the game's empty Archer slot: without this mod, a Ninja save just has no class on.

UNINSTALL
Delete BepInEx\plugins\BetterClasses.

PROBLEMS
Send BepInEx\LogOutput.log (from the game folder) with a description.

CREDITS / LICENSES
BepInEx (LGPL-2.1) https://github.com/BepInEx/BepInEx, HarmonyX (MIT).
"@ | Set-Content "$stage\README-BetterClasses.txt"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
