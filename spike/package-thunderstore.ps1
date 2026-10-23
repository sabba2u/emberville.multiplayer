# Builds the mod-manager package (Thunderstore format, for emberville.hexium.gg and the Gale mod manager): every mod of
# the all-in-one pack, without BepInEx (the mod manager installs that itself).
#   pwsh spike/package-thunderstore.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/Thunderstore-EmbervilleModPack-v<multiplayer version>.zip, with at its root:
#   manifest.json, icon.png (256x256, from spike/thunderstore), README.md (the repo's README, links made absolute),
#   CHANGELOG.md (spike/thunderstore), and plugins/<each mod's folder>.
# It runs package-all.ps1 first (each mod built and staged) and takes the mods from its staging folder.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$repo = Split-Path $here -Parent
$dist = Join-Path $here "dist"
$version = [regex]::Match((Get-Content "$here\EmbervilleGhost\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
$github = "https://github.com/sabba2u/emberville.multiplayer"
$raw = "https://raw.githubusercontent.com/sabba2u/emberville.multiplayer/main"

& "$here\package-all.ps1" -GamePath $GamePath | Out-Host
$pack = Join-Path $dist "EmbervilleModPack-v$version"
if (-not (Test-Path "$pack\BepInEx\plugins")) { throw "No pack staging folder at $pack" }

$name = "Thunderstore-EmbervilleModPack-v$version"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\plugins" | Out-Null

# the mods (each its own folder, as in BepInEx\plugins); no BepInEx
Copy-Item "$pack\BepInEx\plugins\*" "$stage\plugins" -Recurse -Force
Copy-Item "$here\thunderstore\icon.png" "$stage\icon.png"
Copy-Item "$here\thunderstore\CHANGELOG.md" "$stage\CHANGELOG.md"

# manifest.json (name: letters, digits, underscores; description: at most 256 characters)
$description = "Online co-op (host by IP or Steam friends) and gameplay mods: our own random dungeon, a boss rush arena, lock-on, a shared stash, better chests and looting, pets and summon staffs, weather, fishing, NPCs and quests."
if ($description.Length -gt 256) { throw "The description is $($description.Length) characters (256 at most)" }
[ordered]@{
    name           = "EmbervilleModPack"
    description    = $description
    version_number = $version
    website_url    = $github
    dependencies   = @()
} | ConvertTo-Json | Set-Content "$stage\manifest.json" -Encoding utf8NoBOM

# README.md: the repo's README for players (the build notes left out), its links pointing at GitHub, and installing
# through the mod manager
$md = Get-Content "$repo\README.md" -Raw
$md = $md.Substring(0, $md.IndexOf("`n## Build"))
$start = $md.IndexOf("- **Download:**"); $end = $md.IndexOf("- **Commands and API:**")
if ($start -ge 0 -and $end -gt $start) {
    $md = $md.Substring(0, $start) + @"
- **Install:** with a mod manager (Gale): install this package; it brings BepInEx. By hand: the full download (BepInEx
  included) is on the [GitHub releases]($github/releases) page: unzip it into the Emberville Demo folder.
- Everyone playing together needs the same version. To leave one of the mods out, delete its folder in the plugins.

"@ + $md.Substring($end)
}
$md = [regex]::Replace($md, '\]\(([A-Za-z0-9_\-]+\.(gif|png))\)', "]($raw/`$1)")
$md = $md.Replace("](API.md", "]($github/blob/main/API.md").Replace("](todo.md)", "]($github/blob/main/todo.md)").Replace("](../../releases)", "]($github/releases)")
Set-Content "$stage\README.md" $md -Encoding utf8NoBOM

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
