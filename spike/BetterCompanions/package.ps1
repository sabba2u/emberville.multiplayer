# Builds the player release: BepInEx 5 + Better Companions, as a zip you unpack into the Emberville folder.
#   pwsh spike/BetterCompanions/package.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/BetterCompanions-v<version>.zip  (version = Plugin.Version)
# BepInEx is included so it works on its own; next to the multiplayer mod, unzipping it again changes nothing.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$version = [regex]::Match((Get-Content "$here\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "No version in Plugin.cs" }
$name = "BetterCompanions-v$version"
$dist = Join-Path (Split-Path $here) "dist"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Building $name..."
dotnet build "$here\BetterCompanions.csproj" -c Release -p:GamePath="$GamePath" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = Join-Path $here "bin\Release"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\plugins\BetterCompanions" | Out-Null

# BepInEx 5 (loader + core), console window off for players (the log file stays on)
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GamePath $f) $stage }
Copy-Item (Join-Path $GamePath "BepInEx\core") "$stage\BepInEx\" -Recurse
$cfg = Get-Content (Join-Path $GamePath "BepInEx\config\BepInEx.cfg") -Raw
$cfg = [regex]::Replace($cfg, '(\[Logging\.Console\][^\[]*?Enabled = )true', '${1}false')
Set-Content "$stage\BepInEx\config\BepInEx.cfg" $cfg -NoNewline

Copy-Item (Join-Path $out "BetterCompanions.dll") "$stage\BepInEx\plugins\BetterCompanions\"

@"
Better Companions v$version
An adventurer who travels and fights beside you, made from one of your other characters, for the Emberville Demo.
Not made or supported by Cygnus Cross.

INSTALL
1. Find the game folder: Steam > right-click Emberville Demo > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).
   With the Emberville multiplayer mod, unzip this over it: then the chat command below works and your friends see
   your companion (and you theirs).

USE
- Chat (with the multiplayer mod): /companion 2 copies save slot 2 into your companion (its looks, gear, bag, level).
  Your real save slot is only read, never changed; the companion keeps its own save (Saves\companion_slotN.evc).
  An empty slot gives a brand-new adventurer. /companion new, /companion dismiss, /companion call, /companion (status).
  Without the multiplayer mod: set [Companion] CopyFromSlot in the settings file below, then load your character.
- It follows you and fights: a sword, axe or fists up close; a bow or staff from range. With one weapon of each kind
  it swaps between them by itself and dashes in or out of a fight.
- Middle click sends it somewhere: it goes there and fights.
- Enemies fight it back. At zero health it's down: stand next to it and hold F for 2 seconds to get it up.
- F next to it opens the inventory with ITS gear on top (tinted) and your bag below: drag gear up to dress it, drag
  it back down to take it. The character panel shows its attributes while that's open: spend its points there.
- It earns XP from the kills it helps with, and levels up.
Settings: BepInEx\config\spike.emberville.bettercompanions.cfg

UNINSTALL
Delete BepInEx\plugins\BetterCompanions. (Your save slots were never touched; the companion's own save stays in the
Saves folder until you delete companion_slot*.evc / .json.)

PROBLEMS
Send BepInEx\LogOutput.log (from the game folder) with a description.

CREDITS / LICENSES
BepInEx (LGPL-2.1) https://github.com/BepInEx/BepInEx, HarmonyX (MIT).
"@ | Set-Content "$stage\README-BetterCompanions.txt"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
