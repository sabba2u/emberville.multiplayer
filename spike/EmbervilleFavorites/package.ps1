# Builds the player release: BepInEx 5 + Emberville Favourite Weapons, as a zip you unpack into the Emberville folder.
#   pwsh spike/EmbervilleFavorites/package.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/EmbervilleFavorites-v<version>.zip  (version = Plugin.Version)
# BepInEx is included so it works on its own; next to the multiplayer mod, unzipping it again changes nothing.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$version = [regex]::Match((Get-Content "$here\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "No version in Plugin.cs" }
$name = "EmbervilleFavorites-v$version"
$dist = Join-Path (Split-Path $here) "dist"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Building $name..."
dotnet build "$here\EmbervilleFavorites.csproj" -c Release -p:GamePath="$GamePath" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = Join-Path $here "bin\Release"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\plugins\EmbervilleFavorites" | Out-Null

# BepInEx 5 (loader + core), console window off for players (the log file stays on)
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GamePath $f) $stage }
Copy-Item (Join-Path $GamePath "BepInEx\core") "$stage\BepInEx\" -Recurse
$cfg = Get-Content (Join-Path $GamePath "BepInEx\config\BepInEx.cfg") -Raw
$cfg = [regex]::Replace($cfg, '(\[Logging\.Console\][^\[]*?Enabled = )true', '${1}false')
Set-Content "$stage\BepInEx\config\BepInEx.cfg" $cfg -NoNewline

Copy-Item (Join-Path $out "EmbervilleFavorites.dll") "$stage\BepInEx\plugins\EmbervilleFavorites\"
# the how-to popup's video, if it's been made
$videos = Get-ChildItem "$here\Tutorials" -Include *.webm, *.mp4 -File -Recurse -ErrorAction SilentlyContinue
if ($videos) {
    New-Item -ItemType Directory -Force "$stage\BepInEx\plugins\EmbervilleFavorites\Tutorials" | Out-Null
    $videos | Copy-Item -Destination "$stage\BepInEx\plugins\EmbervilleFavorites\Tutorials\"
}

@"
Emberville Favourite Weapons v$version
Swap between up to four favourite weapons with F5-F8 or a weapon wheel in the Emberville Demo. Not made or supported by Cygnus Cross.

INSTALL
1. Find the game folder: Steam > right-click Emberville Demo > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).
   Already have the Emberville multiplayer mod? Then it's fine to unzip this over it.

USE
In the inventory: hover a weapon and press F5, F6, F7 or F8 to put it on that key (the same key again clears it).
Its tooltip then says "Favourite: F6".
While playing: press F5-F8 (or click a slot on the bar above the belt) to wield that weapon; the one in your hand
goes back into your bag. The weapon in hand is highlighted on the bar.
Weapon wheel: hold Left Alt (gamepad: hold LT), point the mouse (or right stick) at a weapon, let go to wield it.
Hover a slot on the bar to see the weapon's tooltip and how it compares with the one in your hand.
Shift+F5 shows the how-to window again (it pops up by itself the first time).
Favourites are remembered per character (a small .egfav file next to the save).
Settings: BepInEx\config\spike.emberville.favorites.cfg ([Bar] Show = false hides the bar).

UNINSTALL
Delete BepInEx\plugins\EmbervilleFavorites (or winhttp.dll to turn off all mods).

PROBLEMS
Send BepInEx\LogOutput.log (from the game folder) with a description.

CREDITS / LICENSES
BepInEx (LGPL-2.1) https://github.com/BepInEx/BepInEx, HarmonyX (MIT).
"@ | Set-Content "$stage\README-EmbervilleFavorites.txt"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
