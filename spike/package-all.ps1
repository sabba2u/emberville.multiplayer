# Builds the all-in-one player download: BepInEx + the multiplayer mod + Favourite Weapons + Better Chests + Better Combat
# + Better Looting + Better Pets and Summons + Better Fishing + Better NPCs and Quests + Better Weather + Better Companions + Better Streaming + Better Classes, in one zip
# (Better Translations is parked: docs/research/2026-10-25-translations-and-weblate.md)
# you unpack into the Emberville folder.
#   pwsh spike/package-all.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/EmbervilleModPack-v<multiplayer version>.zip
# It runs each mod's own package.ps1, then merges their folders (BepInEx is the same in each, so it's taken once).
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$dist = Join-Path $here "dist"
$mods = "EmbervilleGhost", "EmbervilleFavorites", "BetterChests", "BetterCombat", "BetterLooting", "BetterPetsAndSummons", "BetterFishing", "BetterNPCsAndQuests", "BetterWeather", "BetterCompanions", "BetterStreaming", "BetterClasses"
$version = [regex]::Match((Get-Content "$here\EmbervilleGhost\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
$name = "EmbervilleModPack-v$version"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

foreach ($mod in $mods) {
    & "$here\$mod\package.ps1" -GamePath $GamePath | Out-Host
    # each package leaves its staging folder next to its zip: the newest folder for this mod
    $prefix = @{ EmbervilleGhost = "EmbervilleMultiplayer"; EmbervilleFavorites = "EmbervilleFavorites"; BetterChests = "BetterChests"; BetterCombat = "BetterCombat"; BetterLooting = "BetterLooting"; BetterPetsAndSummons = "BetterPetsAndSummons"; BetterFishing = "BetterFishing"; BetterNPCsAndQuests = "BetterNPCsAndQuests"; BetterWeather = "BetterWeather"; BetterCompanions = "BetterCompanions"; BetterStreaming = "BetterStreaming"; BetterClasses = "BetterClasses" }[$mod]
    $built = Get-ChildItem $dist -Directory | Where-Object { $_.Name -like "$prefix-v*" } | Sort-Object LastWriteTime | Select-Object -Last 1
    if (-not $built) { throw "No build folder for $mod" }
    Copy-Item "$($built.FullName)\*" $stage -Recurse -Force
}

# the monster art (MonsterArt.dll, its sheets) is bought art: never in the pack
$art = Get-ChildItem $stage -Recurse -File | Where-Object { $_.Name -like "MonsterArt*" -or $_.FullName -match "Tiny ?Swords|Bumblebee" }
if ($art) { throw "Monster art in the pack: $($art.FullName -join ', ')" }

@"
Emberville Mod Pack v$version
Everything in one download: the co-op multiplayer mod, Favourite Weapons (F5-F8, the weapon wheel), Better Chests
(X / Shift+X / V at a chest, Ctrl+click, the shared stash on U), Better Combat (lock-on on G, bow lock-on, move while attacking, a centred camera, spell keys that answer every press, no stun lock), Better Looting (magnet, green arrows) and
Better Pets and Summons (/pet, the Staff of Clucking, the Staff of the Risen), Better Fishing (a catch from every
cast, spots at the fish shadows) and Better NPCs and Quests (Ellie and her quest; add your own NPCs and quests with
text files) and Better Weather (a rain storm: /rain) and Better Companions (an adventurer from your other save slot: /companion 2) and Better Streaming (your Twitch chat sets off effects: MODS > Streaming) and Better Classes (the Ninja: /ninja or the class screen). Not made or supported by Cygnus Cross.
The Tiny Swords monsters' art is a paid pack and isn't included: without it you get the game's own creatures in their
place (the GitHub README says how to add it).

INSTALL
1. Find the game folder: Steam > right-click Emberville Demo > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).
3. Start the game. Everyone playing together needs the same version.

Each mod's own README is in the readme folder: README-EmbervilleMultiplayer.txt, README-EmbervilleFavorites.txt,
README-BetterChests.txt, README-BetterCombat.txt, README-BetterLooting.txt, README-BetterPetsAndSummons.txt, README-BetterFishing.txt,
README-BetterNPCsAndQuests.txt, README-BetterWeather.txt, README-BetterCompanions.txt, README-BetterStreaming.txt,
README-BetterClasses.txt.
To leave one out, delete its folder in BepInEx\plugins.
"@ | Set-Content "$stage\README-EmbervilleModPack.txt"

# the mods' own READMEs in a readme folder (only the pack's README stays next to the game)
New-Item -ItemType Directory -Force "$stage\readme" | Out-Null
Get-ChildItem $stage -Filter "README-*.txt" | Where-Object { $_.Name -ne "README-EmbervilleModPack.txt" } | Move-Item -Destination "$stage\readme" -Force

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
