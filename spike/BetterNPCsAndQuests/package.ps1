# Builds the player release: BepInEx 5 + Better NPCs and Quests, as a zip you unpack into the Emberville folder.
#   pwsh spike/BetterNPCsAndQuests/package.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/BetterNPCsAndQuests-v<version>.zip  (version = Plugin.Version)
# BepInEx is included so it works on its own; next to the multiplayer mod, unzipping it again changes nothing.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$version = [regex]::Match((Get-Content "$here\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "No version in Plugin.cs" }
$name = "BetterNPCsAndQuests-v$version"
$dist = Join-Path (Split-Path $here) "dist"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Building $name..."
dotnet build "$here\BetterNPCsAndQuests.csproj" -c Release -p:GamePath="$GamePath" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = Join-Path $here "bin\Release"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\plugins\BetterNPCsAndQuests" | Out-Null

# BepInEx 5 (loader + core), console window off for players (the log file stays on)
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GamePath $f) $stage }
Copy-Item (Join-Path $GamePath "BepInEx\core") "$stage\BepInEx\" -Recurse
$cfg = Get-Content (Join-Path $GamePath "BepInEx\config\BepInEx.cfg") -Raw
$cfg = [regex]::Replace($cfg, '(\[Logging\.Console\][^\[]*?Enabled = )true', '${1}false')
Set-Content "$stage\BepInEx\config\BepInEx.cfg" $cfg -NoNewline

Copy-Item (Join-Path $out "BetterNPCsAndQuests.dll") "$stage\BepInEx\plugins\BetterNPCsAndQuests\"
# Ellie, the template, the README for modders, the example rules
Copy-Item "$here\Content\*" "$stage\BepInEx\plugins\BetterNPCsAndQuests\" -Recurse

@"
Better NPCs and Quests v$version
NPCs, conversations and quests from text files, for the Emberville Demo. Not made or supported by Cygnus Cross.
Comes with Ellie, a quest giver near the descent pad in the starting area.

INSTALL
1. Find the game folder: Steam > right-click Emberville Demo > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).
   With the Emberville multiplayer mod, the host's NPCs and quests are shared with everyone who joins, and the Insert
   menu has an NPCs & Quests tab.

USE
- Walk up to an NPC and press the interact key (F) to talk. Their quests show in the game's journal and quest list.
- Make your own: see BepInEx\plugins\BetterNPCsAndQuests\README.txt (copy Ellie's folder to start).
- Insert: read the files again, copy your position for a new NPC, and see any problems in the files.
Your story (flags and quest progress) is saved per world in its own file (<world>.bnq); the game's save is untouched.

UNINSTALL
Delete BepInEx\plugins\BetterNPCsAndQuests. Your worlds load as before.

PROBLEMS
Send BepInEx\LogOutput.log (from the game folder) with a description.

CREDITS / LICENSES
BepInEx (LGPL-2.1) https://github.com/BepInEx/BepInEx, HarmonyX (MIT).
"@ | Set-Content "$stage\README-BetterNPCsAndQuests.txt"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
