# Installs the Hooker hooks into %USERPROFILE%\.claude\settings.json.
# Run this yourself:  ! powershell -ExecutionPolicy Bypass -File C:\Playground\Hooker\install-hook.ps1
#
# It registers hook.exe for PreToolUse (auto-approve when hooking) plus
# UserPromptSubmit / Notification / Stop (drive the working/waiting status light).
# Existing settings are backed up to settings.json.hooker-backup and any other
# hook events you already have are preserved.

$ErrorActionPreference = 'Stop'

$settings = Join-Path $env:USERPROFILE '.claude\settings.json'
$hookExe  = Join-Path $PSScriptRoot 'dist\hook.exe'

if (-not (Test-Path $hookExe)) { throw "hook.exe not found at $hookExe - build the project first (see README)." }

# Claude Code runs hook commands through bash, which treats backslashes as escape
# characters (C:\Playground -> C:Playground). Use forward slashes, which bash
# passes through untouched and Windows still accepts when launching the exe.
$hookExe = $hookExe -replace '\\', '/'

if (Test-Path $settings) {
    Copy-Item $settings "$settings.hooker-backup" -Force
    $json = Get-Content $settings -Raw | ConvertFrom-Json
    Write-Host "Backed up existing settings to $settings.hooker-backup"
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
    $json = [pscustomobject]@{}
}

$cmd        = [pscustomobject]@{ type = 'command'; command = $hookExe }
$withMatch  = [pscustomobject]@{ matcher = '*'; hooks = @($cmd) }
$noMatch    = [pscustomobject]@{ hooks = @($cmd) }

if (-not ($json.PSObject.Properties.Name -contains 'hooks')) {
    $json | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{})
}

$events = @{
    PreToolUse       = $withMatch
    UserPromptSubmit = $noMatch
    Notification     = $noMatch
    Stop             = $noMatch
    SessionStart     = $noMatch
    SessionEnd       = $noMatch
}
foreach ($e in $events.Keys) {
    $grp = @($events[$e])
    if ($json.hooks.PSObject.Properties.Name -contains $e) { $json.hooks.$e = $grp }
    else { $json.hooks | Add-Member -NotePropertyName $e -NotePropertyValue $grp }
}

$json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding utf8
Write-Host "Installed Hooker hooks into $settings"
Write-Host "Restart any running Claude Code sessions to pick up the hooks."
