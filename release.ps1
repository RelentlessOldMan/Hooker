# Cut a Hooker release in one shot: build (bumps VERSION) -> commit + push the bump
# -> package the prebuilt zip -> create the GitHub release with it attached.
#
# Commit your actual changes first (this only commits the version bump), then run:
#   powershell -ExecutionPolicy Bypass -File .\release.ps1
#
# Needs: gh (authenticated) and a clean working tree.

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# 1. Require a clean tree so the release tag captures your committed work.
$dirty = git status --porcelain
if ($dirty) { throw "Working tree not clean - commit or stash your changes first, then re-run." }

# 2. Stop the widget (it locks its own exe), build (bumps VERSION), remember to relaunch.
$wasRunning = [bool](Get-Process HookerWidget -ErrorAction SilentlyContinue)
Stop-Process -Name HookerWidget -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400
& "$PSScriptRoot\build.ps1"
$ver = (Get-Content "$PSScriptRoot\VERSION" -Raw).Trim()

# 3. Commit the version bump and push.
git add VERSION
git commit --quiet -m "Release v$ver"
git push --quiet origin main

# 4. Package the prebuilt zip (scripts at root, exes under dist\).
$root  = Join-Path $env:TEMP 'hooker-rel'
$stage = Join-Path $root 'Hooker'
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $stage 'dist') | Out-Null
Copy-Item 'Install Hooker.cmd','install-hook.ps1','uninstall-hook.ps1','README.md' $stage
Copy-Item 'dist\hook.exe','dist\HookerWidget.exe' (Join-Path $stage 'dist')
$zip = Join-Path $PSScriptRoot "Hooker-v$ver.zip"
Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

# 5. Create the GitHub release with the zip attached.
# Pass notes via a temp file (--notes-file), NOT --notes: Windows PowerShell 5.1 mangles a
# native-command argument that contains embedded double-quotes (the notes' *"Run desktop apps"*),
# splitting it into stray words that gh then tries to attach as asset files ("CreateFile desktop").
$notes = @'
**Prebuilt Windows build** (framework-dependent, x64).

Requires the free [.NET Desktop Runtime 8](https://dotnet.microsoft.com/download/dotnet/8.0) — under *"Run desktop apps"*, the **Windows x64** installer.

**Install:** extract the zip, double-click `Install Hooker.cmd`, restart Claude Code, then run `HookerWidget.exe`.

WARNING: putting a session on autopilot (salmon tile) auto-approves every prompt for it — read the README security note first.
'@
$notesFile = Join-Path $env:TEMP "hooker-relnotes-$ver.md"
Set-Content -LiteralPath $notesFile -Value $notes -Encoding utf8
try { gh release create "v$ver" "$zip" --title "Hooker v$ver" --notes-file $notesFile }
finally { Remove-Item $notesFile -Force -ErrorAction SilentlyContinue }

# 6. Clean up and relaunch the widget if it had been running.
Remove-Item $zip -ErrorAction SilentlyContinue
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
if ($wasRunning) { Start-Process "$PSScriptRoot\dist\HookerWidget.exe" }

Write-Host "`nReleased v$ver -> https://github.com/RelentlessOldMan/Hooker/releases/tag/v$ver"
