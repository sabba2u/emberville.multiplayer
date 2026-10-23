# Builds the player release: BepInEx 5 + Better Combat, as a zip you unpack into the Emberville folder.
#   pwsh spike/BetterCombat/package.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/BetterCombat-v<version>.zip  (version = Plugin.Version)
# BepInEx is included so it works on its own; next to the multiplayer mod, unzipping it again changes nothing.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$version = [regex]::Match((Get-Content "$here\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "No version in Plugin.cs" }
$name = "BetterCombat-v$version"
$dist = Join-Path (Split-Path $here) "dist"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Building $name..."
dotnet build "$here\BetterCombat.csproj" -c Release -p:GamePath="$GamePath" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = Join-Path $here "bin\Release"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\plugins\BetterCombat" | Out-Null

# BepInEx 5 (loader + core), console window off for players (the log file stays on)
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GamePath $f) $stage }
Copy-Item (Join-Path $GamePath "BepInEx\core") "$stage\BepInEx\" -Recurse
$cfg = Get-Content (Join-Path $GamePath "BepInEx\config\BepInEx.cfg") -Raw
$cfg = [regex]::Replace($cfg, '(\[Logging\.Console\][^\[]*?Enabled = )true', '${1}false')
Set-Content "$stage\BepInEx\config\BepInEx.cfg" $cfg -NoNewline

Copy-Item (Join-Path $out "BetterCombat.dll") "$stage\BepInEx\plugins\BetterCombat\"

@"
Better Combat v$version
Lock-on, bow lock-on and moving while you attack, for the Emberville Demo. Not made or supported by Cygnus Cross.

INSTALL
1. Find the game folder: Steam > right-click Emberville Demo > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).
   Already have the Emberville multiplayer mod? Unzip this over it: its Insert menu then shows these switches
   (Options) and its chat takes the commands.

USE
- Lock-on (G): point at an enemy and press G: your character faces it whatever the mouse does, so you can circle and
  strafe round it, and swings, spells and arrows go at it. A ring under it shows the lock. G on it again lets go; G
  pointing at nothing locks the nearest enemy, and again the next one. When it dies the lock moves to the next one
  close by. Only enemies (creatures, spider eggs), never doors, barrels or furniture. The key: [LockOn] NextKey.
- Bow lock-on (off to start): arrows from a bow / crossbow curve onto the creature nearest your aim.
  Chat /bowlock (on / off), /bowlock 15 (cone of 15 degrees either side). True aim stays on: arrows leave exactly where
  you aim.
- Move while attacking (off to start): melee and magic at full speed, bows slower while aiming. Chat /moveattack,
  /moveattack 70 (bow speed 70%).
Without the multiplayer mod: switch them in BepInEx\config\spike.emberville.bettercombat.cfg ([Combat]).

UNINSTALL
Delete BepInEx\plugins\BetterCombat.

PROBLEMS
Send BepInEx\LogOutput.log (from the game folder) with a description.

CREDITS / LICENSES
BepInEx (LGPL-2.1) https://github.com/BepInEx/BepInEx, HarmonyX (MIT).
"@ | Set-Content "$stage\README-BetterCombat.txt"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
