# Builds the player release: BepInEx 5 + Better Streaming, as a zip you unpack into the Emberville folder.
#   pwsh spike/BetterStreaming/package.ps1 [-GamePath "K:\...\Emberville Demo"]
# Output: spike/dist/BetterStreaming-v<version>.zip  (version = Plugin.Version)
# BepInEx is included so it works on its own; next to the multiplayer mod, unzipping it again changes nothing.
param([string]$GamePath = "K:\__games\steamapps\common\Emberville Demo")
$ErrorActionPreference = "Stop"

$here = $PSScriptRoot
$version = [regex]::Match((Get-Content "$here\Plugin.cs" -Raw), 'const string Version = "([^"]+)"').Groups[1].Value
if (-not $version) { throw "No version in Plugin.cs" }
$name = "BetterStreaming-v$version"
$dist = Join-Path (Split-Path $here) "dist"
$stage = Join-Path $dist $name
$zip = Join-Path $dist "$name.zip"

Write-Host "Building $name..."
dotnet build "$here\BetterStreaming.csproj" -c Release -p:GamePath="$GamePath" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
$out = Join-Path $here "bin\Release"

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force "$stage\BepInEx\config", "$stage\BepInEx\plugins\BetterStreaming" | Out-Null

# BepInEx 5 (loader + core), console window off for players (the log file stays on)
foreach ($f in "winhttp.dll", "doorstop_config.ini", ".doorstop_version") { Copy-Item (Join-Path $GamePath $f) $stage }
Copy-Item (Join-Path $GamePath "BepInEx\core") "$stage\BepInEx\" -Recurse
$cfg = Get-Content (Join-Path $GamePath "BepInEx\config\BepInEx.cfg") -Raw
$cfg = [regex]::Replace($cfg, '(\[Logging\.Console\][^\[]*?Enabled = )true', '${1}false')
Set-Content "$stage\BepInEx\config\BepInEx.cfg" $cfg -NoNewline

Copy-Item (Join-Path $out "BetterStreaming.dll") "$stage\BepInEx\plugins\BetterStreaming\"

@"
Better Streaming v$version
Your Twitch chat plays along: cheers (and later channel points and votes) set off effects in your Emberville game,
with the viewer's name on screen. Not made or supported by Cygnus Cross or Twitch.

INSTALL
1. Find the game folder: Steam > right-click Emberville > Manage > Browse local files.
2. Unzip everything here into that folder (winhttp.dll and the BepInEx folder end up next to Emberville.exe).
   Best with the Emberville mod pack: it draws the Streaming tab (Insert or MODS) and the raids need it.

USE
- MODS > Streaming > Connect Twitch: the game shows a code; enter it at the link on twitch.tv. No password in the game.
- Bits need a Twitch Affiliate or Partner channel.
- Every effect has a Test button (free, offline) and you can Simulate a cheer before going live.
Settings: BepInEx\config\spike.emberville.betterstreaming.cfg

UNINSTALL
Delete BepInEx\plugins\BetterStreaming, and sign out from the Streaming tab first (or revoke it on twitch.tv:
Settings > Connections).
"@ | Set-Content "$stage\README-BetterStreaming.txt"

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$kb = [math]::Round((Get-Item $zip).Length / 1KB)
Write-Host "Done: $zip ($kb KB)"
