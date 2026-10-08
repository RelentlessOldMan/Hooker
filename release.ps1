# Cut a Hooker release: set the new version -> build -> package zip + checksums -> commit &
# push the bump -> create the GitHub release with the assets attached.
#
# The version changes ONLY here (a plain build.ps1 never bumps). Commit your feature work
# first (this commits only the version bump), then run:
#   powershell -ExecutionPolicy Bypass -File .\release.ps1                # patch bump
#   powershell -ExecutionPolicy Bypass -File .\release.ps1 -Version 1.1.0 # explicit
#
# Needs: gh (authenticated) and a clean working tree.
param([string]$Version)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

# 1. Preflight BEFORE anything is changed or published: clean tree + authenticated gh, so a
#    misconfigured environment fails fast instead of half-way through a release.
$dirty = git status --porcelain
if ($dirty) { throw "Working tree not clean - commit or stash your changes first, then re-run." }
# try/catch: under 'Stop', PS 5.1 turns gh's stderr into a terminating error before the check runs.
$ghOk = $false
try { gh auth status *> $null; $ghOk = ($LASTEXITCODE -eq 0) } catch { }
if (-not $ghOk) { throw "gh is not authenticated - run 'gh auth login' first." }
$branch = (git rev-parse --abbrev-ref HEAD | Out-String).Trim()
if ($branch -ne 'main') { throw "Releases are cut from main; you're on '$branch'." }

# 2. Decide the new version: explicit -Version, else bump the patch.
$verFile = Join-Path $PSScriptRoot 'VERSION'
$cur = (Get-Content $verFile -Raw).Trim()
if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+$') { throw "-Version must be M.M.P (e.g. 1.1.0); got '$Version'." }
    $ver = $Version
} else {
    $p = $cur.Split('.'); $p[2] = [int]$p[2] + 1; $ver = ($p -join '.')
}
if ([version]$ver -le [version]$cur) { throw "v$ver is not newer than the current v$cur." }
# The tag must be free, and main must not be behind GitHub: either would only fail AFTER the
# release commit was made (push rejected, or 'gh release create' on a taken tag).
git rev-parse --quiet --verify "refs/tags/v$ver" > $null
if ($LASTEXITCODE -eq 0) { throw "Tag v$ver already exists." }
$remoteTag = git ls-remote --tags origin "refs/tags/v$ver"
if ($LASTEXITCODE -ne 0) { throw "Can't reach origin (git ls-remote failed)." }
if ($remoteTag) { throw "Tag v$ver already exists on GitHub." }
git fetch --quiet origin main
if ($LASTEXITCODE -ne 0) { throw "git fetch origin main failed." }
$behind = [int]((git rev-list --count HEAD..origin/main | Out-String).Trim())
if ($behind -gt 0) { throw "main is $behind commit(s) behind origin/main - pull first." }
Write-Host "Releasing v$cur -> v$ver"

# 3. Stop the widget (it locks its own exe); remember to relaunch at the end.
$wasRunning = [bool](Get-Process HookerWidget -ErrorAction SilentlyContinue)
Stop-Process -Name HookerWidget -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 400
try {   # finally (at the end) relaunches the widget whether the release succeeds or fails

# 4. Write the new version, then build + package + checksums. If ANY of this fails, roll
#    VERSION back so a failed release never leaves a half-bumped, uncommitted file behind,
#    and nothing has been committed or pushed yet.
Set-Content $verFile $ver -Encoding ascii -NoNewline
$root  = Join-Path $env:TEMP 'hooker-rel'
$stage = Join-Path $root 'Hooker'
$zip   = Join-Path $PSScriptRoot "Hooker-v$ver.zip"
$sums  = Join-Path $PSScriptRoot 'SHA256SUMS.txt'
try {
    & "$PSScriptRoot\build.ps1"

    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force (Join-Path $stage 'dist') | Out-Null
    Copy-Item 'Install Hooker.cmd','install-hook.ps1','uninstall-hook.ps1','README.md' $stage
    Copy-Item 'dist\hook.exe','dist\HookerWidget.exe' (Join-Path $stage 'dist')
    Remove-Item $zip -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zip

    # Checksum of what's actually attached (the zip), so `sha256sum -c` next to the download passes.
    Remove-Item $sums -ErrorAction SilentlyContinue
    Get-FileHash $zip -Algorithm SHA256 |
        ForEach-Object { "{0}  {1}" -f $_.Hash.ToLower(), (Split-Path $_.Path -Leaf) } |
        Set-Content $sums -Encoding ascii
}
catch {
    Set-Content $verFile $cur -Encoding ascii -NoNewline    # roll the bump back
    Remove-Item $zip, $sums -ErrorAction SilentlyContinue
    Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue
    throw "Release aborted before any commit; VERSION restored to $cur. $_"
}

# 5. Commit the bump and push -- only now that a good build + package exist.
git add VERSION
if ($LASTEXITCODE -eq 0) { git commit --quiet -m "Release v$ver" }
if ($LASTEXITCODE -ne 0) {
    # Nothing is published yet: undo the bump so the tree is back where it started.
    git reset --quiet -- VERSION
    Set-Content $verFile $cur -Encoding ascii -NoNewline
    Remove-Item $zip, $sums -ErrorAction SilentlyContinue
    throw "git add/commit failed (a hook?); VERSION restored to $cur and nothing was pushed."
}
git push --quiet origin main
if ($LASTEXITCODE -ne 0) {
    throw "git push failed - the 'Release v$ver' commit is local. Fix the remote, 'git push', then finish with: gh release create v$ver `"$zip`" `"$sums`" --title `"Hooker v$ver`" --notes-file <notes>"
}

# 6. Create the GitHub release with the zip + checksums attached.
# Pass notes via a temp file (--notes-file), NOT --notes: Windows PowerShell 5.1 mangles a
# native-command argument that contains embedded double-quotes, splitting it into stray words
# that gh then tries to attach as asset files.
$notes = @'
**Prebuilt Windows build** (framework-dependent, x64).

Requires the free [.NET Desktop Runtime 8](https://dotnet.microsoft.com/download/dotnet/8.0) - under *"Run desktop apps"*, the **Windows x64** installer.

**Install:** extract the zip, double-click `Install Hooker.cmd`, restart Claude Code, then run `HookerWidget.exe`.

Verify your download against `SHA256SUMS.txt`.

WARNING: putting a session on autopilot (salmon tile) auto-approves every prompt for it - read the README security note first.
'@
$notesFile = Join-Path $env:TEMP "hooker-relnotes-$ver.md"
[System.IO.File]::WriteAllText($notesFile, $notes, (New-Object System.Text.UTF8Encoding $false))   # no BOM
$head = (git rev-parse HEAD | Out-String).Trim()
gh release create "v$ver" "$zip" "$sums" --title "Hooker v$ver" --notes-file $notesFile --target $head
if ($LASTEXITCODE -ne 0) {
    Write-Warning "v$ver was committed & pushed, but 'gh release create' failed."
    Write-Warning "Finish manually: gh release create v$ver `"$zip`" `"$sums`" --title `"Hooker v$ver`" --notes-file `"$notesFile`""
    throw "gh release create failed (assets kept at $zip and $sums)."
}
Remove-Item $notesFile -Force -ErrorAction SilentlyContinue

# 7. Clean up and relaunch the widget if it had been running.
Remove-Item $zip, $sums -ErrorAction SilentlyContinue
Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "`nReleased v$ver -> https://github.com/RelentlessOldMan/Hooker/releases/tag/v$ver"
}
finally {
    if ($wasRunning -and -not (Get-Process HookerWidget -ErrorAction SilentlyContinue)) {
        Start-Process "$PSScriptRoot\dist\HookerWidget.exe"
    }
}
