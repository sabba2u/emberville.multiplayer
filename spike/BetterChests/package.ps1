# Builds the player release: BepInEx 5 + Better Chests, as a zip you unpack into the Emberville folder.
#   pwsh spike/BetterChests/package.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/BetterChests-v<version>.zip  (version = Plugin.Version)
# BepInEx is included so it works on its own; next to the multiplayer mod, unzipping it again changes nothing.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$version = [regex]::Match((Get-Content "$here\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "No version in Plugin.cs" }
$name = "BetterChests-v$version"
$dist = Join-Path (Split-Path $here) "dist"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Building $name..."
dotnet build "$here\BetterChests.csproj" -c Release -p:GamePath="$GamePath" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = Join-Path $here "bin\Release"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\plugins\BetterChests" | Out-Null

# BepInEx 5 (loader + core), console window off for players (the log file stays on)
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GamePath $f) $stage }
Copy-Item (Join-Path $GamePath "BepInEx\core") "$stage\BepInEx\" -Recurse
$cfg = Get-Content (Join-Path $GamePath "BepInEx\config\BepInEx.cfg") -Raw
$cfg = [regex]::Replace($cfg, '(\[Logging\.Console\][^\[]*?Enabled = )true', '${1}false')
Set-Content "$stage\BepInEx\config\BepInEx.cfg" $cfg -NoNewline

Copy-Item (Join-Path $out "BetterChests.dll") "$stage\BepInEx\plugins\BetterChests\"

@"
Better Chests v$version
Quick-stack, a shared stash and more for chests in the Emberville Demo. Not made or supported by Cygnus Cross.

INSTALL
1. Find the game folder: Steam > right-click Emberville Demo > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).
   Already have the Emberville multiplayer mod? Then it's fine to unzip this over it; X then works in co-op too.

USE
Walk up to a chest (its arrow shows) and press X: everything in your bag that the chest already holds goes in,
onto its stacks and then into free slots while there's room. Each item shows in the game's list on the right
("Stored Carrot (12)"). It's the chest screen's own "send common" button, without opening the screen.
- Shift+X: the same into every chest nearby.
- V at a chest: its name, colour and what it takes (weapons only, materials only...).
- Search boxes over the chest and your bag while a chest is open.
- Ctrl+click an item while a chest is open: it goes straight across (bag to chest, chest to bag).
- U (or chat /stash): your shared stash, anywhere: 6 tabs of 30 slots, shared by every character on this PC. Right-click
  a tab to rename it and give it a colour. It's kept in its own file next to your saves (with a backup).
- F over an item in your inventory or a chest: a favourite (a gold star in its corner). Favourites never go into a
  chest by themselves (X, Shift+X, the chest screen's send buttons); F again to undo.
- J over an item: junk (a red X in its corner); J again to undo. An item is a favourite or junk, not both.
- Shift+J: drop everything marked junk from your bag at your feet. On the ground its name is red with
  an X; favourites' names shimmer gold behind a star.
Settings: BepInEx\config\spike.emberville.betterchests.cfg ([QuickStack] Key = X).

UNINSTALL
Delete BepInEx\plugins\BetterChests (or winhttp.dll to turn off all mods).

PROBLEMS
Send BepInEx\LogOutput.log (from the game folder) with a description.

CREDITS / LICENSES
BepInEx (LGPL-2.1) https://github.com/BepInEx/BepInEx, HarmonyX (MIT).
"@ | Set-Content "$stage\README-BetterChests.txt"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
