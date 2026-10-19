# Builds the player release: BepInEx 5 + the mod, as a zip you unpack into the Emberville folder.
#   pwsh spike/EmbervilleGhost/package.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/EmbervilleMultiplayer-v<version>.zip  (version = Plugin.Version)
# BepInEx files are taken from the game folder (where we installed it); the mod from a fresh Release build.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$version = [regex]::Match((Get-Content "$here\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "No version in Plugin.cs" }
$name = "EmbervilleMultiplayer-v$version"
$dist = Join-Path (Split-Path $here) "dist"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Building $name..."
dotnet build "$here\EmbervilleGhost.csproj" -c Release -p:GamePath="$GamePath" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = Join-Path $here "bin\Release"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\plugins\EmbervilleMultiplayer\Assets\Fonts" | Out-Null

# BepInEx 5 (loader + core)
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GamePath $f) $stage }
Copy-Item (Join-Path $GamePath "BepInEx\core") "$stage\BepInEx\" -Recurse
# Config: keep HideManagerGameObject = true (the game destroys the manager otherwise), no console window for players
# (the log file BepInEx\LogOutput.log stays on, for bug reports).
$cfg = Get-Content (Join-Path $GamePath "BepInEx\config\BepInEx.cfg") -Raw
$cfg = [regex]::Replace($cfg, '(\[Logging\.Console\][^\[]*?Enabled = )true', '${1}false')
Set-Content "$stage\BepInEx\config\BepInEx.cfg" $cfg -NoNewline

# The mod: our DLL + what it needs (no game / Unity DLLs)
$plugin = "$stage\BepInEx\plugins\EmbervilleMultiplayer"
foreach ($f in "EmbervilleGhost.dll", "LiteNetLib.dll", "ImGui.NET.dll", "cimgui.dll", "System.Runtime.CompilerServices.Unsafe.dll") { Copy-Item (Join-Path $out $f) $plugin }
Copy-Item (Join-Path $out "Assets\Fonts\*") "$plugin\Assets\Fonts\"

# Read me
@"
$((Get-Content "$here\Plugin.cs" -Raw | Select-String 'ModName = "([^"]+)"').Matches[0].Groups[1].Value) v$version
Unofficial co-op for the Emberville Demo (2-4 players). Not made or supported by Cygnus Cross.

INSTALL
1. Find the game folder: Steam > right-click Emberville Demo > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).
3. Start the game. The title screen shows "MULTIPLAYER" and the mod's version at the bottom.
Everyone playing together needs the same mod version.

PLAY
Host:   load your world, press F8, pick the rules (shared XP, instanced loot, PvP), HOST NOW.
        Copy your address with the Copy button and send it to your friends.
        Over the internet: forward UDP port 9060 on your router to your PC (same network: not needed).
Join:   on the title screen, press Insert, click "Paste address" (or type it), press F9.
        Wait for "downloaded", pick your character, press "Connect now".
        You play your own character in the host's world; the host's world is never changed on your PC.
Keys:   F8 host, F9 join, F10 disconnect, Insert menu (teleport to a teammate, rules, log), Enter chat.

UNINSTALL
Delete winhttp.dll (the game then runs without mods), or also the BepInEx folder, doorstop_config.ini, .doorstop_version.

PROBLEMS
Send BepInEx\LogOutput.log (from the game folder) with a description.

CREDITS / LICENSES
BepInEx (LGPL-2.1) https://github.com/BepInEx/BepInEx, HarmonyX (MIT), LiteNetLib (MIT),
Dear ImGui / cimgui / ImGui.NET (MIT).
"@ | Set-Content "$stage\README-EmbervilleMultiplayer.txt"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
