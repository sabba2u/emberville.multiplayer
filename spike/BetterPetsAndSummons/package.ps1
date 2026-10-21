# Builds the player release: BepInEx 5 + Better Pets and Summons, as a zip you unpack into the Emberville folder.
#   pwsh spike/BetterPetsAndSummons/package.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/BetterPetsAndSummons-v<version>.zip  (version = Plugin.Version)
# BepInEx is included so it works on its own; next to the multiplayer mod, unzipping it again changes nothing.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$version = [regex]::Match((Get-Content "$here\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "No version in Plugin.cs" }
$name = "BetterPetsAndSummons-v$version"
$dist = Join-Path (Split-Path $here) "dist"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Building $name..."
dotnet build "$here\BetterPetsAndSummons.csproj" -c Release -p:GamePath="$GamePath" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = Join-Path $here "bin\Release"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\plugins\BetterPetsAndSummons" | Out-Null

# BepInEx 5 (loader + core), console window off for players (the log file stays on)
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GamePath $f) $stage }
Copy-Item (Join-Path $GamePath "BepInEx\core") "$stage\BepInEx\" -Recurse
$cfg = Get-Content (Join-Path $GamePath "BepInEx\config\BepInEx.cfg") -Raw
$cfg = [regex]::Replace($cfg, '(\[Logging\.Console\][^\[]*?Enabled = )true', '${1}false')
Set-Content "$stage\BepInEx\config\BepInEx.cfg" $cfg -NoNewline

Copy-Item (Join-Path $out "BetterPetsAndSummons.dll") "$stage\BepInEx\plugins\BetterPetsAndSummons\"

@"
Better Pets and Summons v$version
A pet, the Staff of Clucking and the Staff of the Risen, for the Emberville Demo. Not made or supported by Cygnus Cross.

INSTALL
1. Find the game folder: Steam > right-click Emberville Demo > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).
   Already have the Emberville multiplayer mod? Unzip this over it: then the chat commands below work, the Insert menu
   has a Pets tab, name tags and health bars show, teammates see your pet and summons, and descent guardians drop the
   staffs.

USE
- Insert opens the pets menu (on its own; with the multiplayer mod it's the Pets tab of its Insert menu): pets on,
  befriend or switch to a creature (a chicken, a Glowbat, a Lilshroom...), its name and colour, call it or send it home,
  and buttons for the two staffs. Your pet follows you, fights, levels up.
- With the multiplayer mod, chat works too: /pet, /pet list, /pet glowbat, /pet name Fang, /pet color red.
- The Staff of Clucking (menu button, or /clucking). Every attack with it summons a battle chicken; the more you use it, the stronger.
- The Staff of the Risen (menu button, or /risen). Creatures that fall near you glow green: right-click near one to raise it.
- Holding either staff, a middle click sends your summons there.
Settings: BepInEx\config\spike.emberville.betterpetsandsummons.cfg ([Pet]).

UNINSTALL
Delete BepInEx\plugins\BetterPetsAndSummons. (The staffs stay in your bag as ordinary staffs.)

PROBLEMS
Send BepInEx\LogOutput.log (from the game folder) with a description.

CREDITS / LICENSES
BepInEx (LGPL-2.1) https://github.com/BepInEx/BepInEx, HarmonyX (MIT).
"@ | Set-Content "$stage\README-BetterPetsAndSummons.txt"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
