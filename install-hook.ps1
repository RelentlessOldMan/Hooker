# Installs the Hooker hooks into %USERPROFILE%\.claude\settings.json.
# Run it yourself — double-click "Install Hooker.cmd", or run this .ps1 directly.
#
# It registers hook.exe on PreToolUse (auto-approve when hooking) plus
# UserPromptSubmit / Notification / Stop / SessionStart / SessionEnd (status + lifecycle).
# Your settings are backed up to settings.json.hooker-backup, and any hooks you
# already had are preserved — only a previous Hooker entry is replaced on re-install.

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

$cmd       = [pscustomobject]@{ type = 'command'; command = $hookExe }
$withMatch = [pscustomobject]@{ matcher = '*'; hooks = @($cmd) }
$noMatch   = [pscustomobject]@{ hooks = @($cmd) }

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
    $ourGrp = $events[$e]
    if ($json.hooks.PSObject.Properties.Name -contains $e) {
        # Keep hooks you already had for this event; drop only a prior Hooker entry.
        $kept = @($json.hooks.$e | Where-Object {
            -not ($_.hooks | Where-Object { $_.command -eq $hookExe })
        })
        $json.hooks.$e = @($kept + $ourGrp)
    } else {
        $json.hooks | Add-Member -NotePropertyName $e -NotePropertyValue @($ourGrp)
    }
}

# Write BOM-less UTF-8 (Set-Content -Encoding utf8 adds a BOM on Windows PowerShell 5.1).
$out = $json | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($settings, $out, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Installed Hooker hooks into $settings"
Write-Host "Restart any running Claude Code sessions to pick up the hooks."
