# Installs the Hooker hooks into %USERPROFILE%\.claude\settings.json.
# Run it yourself - double-click "Install Hooker.cmd", or run this .ps1 directly.
#
# It registers hook.exe on PreToolUse (auto-approve when hooking) plus
# UserPromptSubmit / Notification / Stop / SessionStart / SessionEnd (status + lifecycle).
# Your settings are backed up to a timestamped settings.json.hooker-backup-<stamp>, any hooks
# you already had are preserved (only a previous Hooker entry is replaced), the new file is
# validated as JSON before it replaces the live one, and the backup is restored on any failure.

$ErrorActionPreference = 'Stop'

$settings = Join-Path $env:USERPROFILE '.claude\settings.json'
$hookExe  = Join-Path $PSScriptRoot 'dist\hook.exe'

if (-not (Test-Path $hookExe)) { throw "hook.exe not found at $hookExe - build the project first (see README)." }

# Claude Code runs hook commands through bash, which treats backslashes as escape
# characters (C:\Playground -> C:Playground). Use forward slashes, which bash
# passes through untouched and Windows still accepts when launching the exe.
$hookExe = $hookExe -replace '\\', '/'

$backup = $null
if (Test-Path $settings) {
    # Timestamped so re-running never clobbers an earlier good backup.
    $backup = "$settings.hooker-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item $settings $backup -Force
    $json = Get-Content $settings -Raw | ConvertFrom-Json
    Write-Host "Backed up existing settings to $backup"
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
    $json = [pscustomobject]@{}
}

$tmp = "$settings.hooker-tmp"
try {
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

    # Write BOM-less UTF-8 (Set-Content -Encoding utf8 adds a BOM on Windows PowerShell 5.1)
    # to a temp file, verify it parses as JSON, then swap it in - so a bad write can never
    # leave the live settings.json corrupt.
    $out = $json | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($tmp, $out, (New-Object System.Text.UTF8Encoding $false))
    $null = Get-Content $tmp -Raw | ConvertFrom-Json   # validate before replacing
    Move-Item $tmp $settings -Force
}
catch {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    if ($backup) { Copy-Item $backup $settings -Force; Write-Warning "Install failed; restored settings from $backup" }
    throw
}

Write-Host "Installed Hooker hooks into $settings"
Write-Host "Restart any running Claude Code sessions to pick up the hooks."
