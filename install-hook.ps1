# Installs the Hooker hooks into %USERPROFILE%\.claude\settings.json.
# Run it yourself - double-click "Install Hooker.cmd", or run this .ps1 directly.
#
# It registers hook.exe on PreToolUse (auto-approve when hooking) plus
# UserPromptSubmit / Notification / Stop / SessionStart / SessionEnd (status + lifecycle).
# Your settings are backed up to a timestamped settings.json.hooker-backup-<stamp>, any hooks
# you already had are preserved (only a previous Hooker entry is replaced), the new file is
# validated as JSON before it replaces the live one, and the backup is restored on any failure.

$ErrorActionPreference = 'Stop'

# Hooker's own entries: a dist/hook.exe that sits next to HookerWidget.exe, wherever the folder
# lives - so a re-install from a moved or newer release folder replaces the old entry instead of
# leaving both registered (both would run on every event) - or one whose exe is gone (a deleted
# old folder, which would only error on every event). Another tool's dist/hook.exe is left alone.
# The command is written quoted (see install-hook.ps1); older installs wrote it bare - match both.
function Test-HookerCommand([string]$c) {
    if ($c -notmatch '^"?(.*[\\/]dist[\\/]hook\.exe)"?$') { return $false }
    $exe = $Matches[1]
    if (-not (Test-Path -LiteralPath $exe)) { return $true }
    return (Test-Path -LiteralPath (Join-Path (Split-Path $exe) 'HookerWidget.exe'))
}

# Put $src's content in place as settings.json. A symlinked settings.json (kept in a dotfiles repo)
# is written through to its target - swapping the file in would replace the link with a copy.
function Set-Settings([string]$src, [string]$dst, [switch]$Move) {
    $item = Get-Item -LiteralPath $dst -Force -ErrorAction SilentlyContinue
    if ($Move -and -not ($item -and $item.LinkType)) { Move-Item $src $dst -Force; return }
    [System.IO.File]::WriteAllBytes($dst, [System.IO.File]::ReadAllBytes($src))
    if ($Move) { Remove-Item $src -Force -ErrorAction SilentlyContinue }
}

# An event's hook groups minus Hooker's own commands: a group holding only Hooker goes, but a
# group where you'd also put hooks of your own keeps them.
function Remove-HookerEntries($groups) {
    foreach ($g in @($groups)) {
        $all    = @($g.hooks)
        $others = @($all | Where-Object { -not (Test-HookerCommand $_.command) })
        if ($others.Count -eq $all.Count) { $g }
        elseif ($others.Count -gt 0) { $g.hooks = $others; $g }
    }
}

$settings = Join-Path $env:USERPROFILE '.claude\settings.json'
$hookExe  = Join-Path $PSScriptRoot 'dist\hook.exe'

if (-not (Test-Path $hookExe)) { throw "hook.exe not found at $hookExe - build the project first (see README)." }

# Claude Code runs hook commands through bash, which treats backslashes as escape
# characters (C:\Playground -> C:Playground). Use forward slashes, which bash
# passes through untouched and Windows still accepts when launching the exe - and quote
# the path, or a space ("C:/Users/John Smith/...") or parentheses ("Hooker (1)") break it.
$hookExe = $hookExe -replace '\\', '/'
if ($hookExe -match '["$`\\]') { throw "Hooker's folder path contains a character a hook command can't quote safely: $hookExe" }

$backup = $null
if (Test-Path $settings) {
    # Timestamped so re-running never clobbers an earlier good backup.
    $backup = "$settings.hooker-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
    Copy-Item $settings $backup -Force
    # -Encoding UTF8: Windows PowerShell 5.1 otherwise reads a BOM-less file as the ANSI
    # codepage and would mangle any non-ASCII text in your settings on the way back out.
    $json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json
    Write-Host "Backed up existing settings to $backup"
} else {
    New-Item -ItemType Directory -Force -Path (Split-Path $settings) | Out-Null
    $json = [pscustomobject]@{}
}

$tmp = "$settings.hooker-tmp"
try {
    $cmd       = [pscustomobject]@{ type = 'command'; command = '"' + $hookExe + '"' }
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
            $kept = @(Remove-HookerEntries $json.hooks.$e)
            $json.hooks.$e = @($kept + $ourGrp)
        } else {
            $json.hooks | Add-Member -NotePropertyName $e -NotePropertyValue @($ourGrp)
        }
    }

    # Write BOM-less UTF-8 (Set-Content -Encoding utf8 adds a BOM on Windows PowerShell 5.1)
    # to a temp file, verify it parses as JSON, then swap it in - so a bad write can never
    # leave the live settings.json corrupt.
    $out = $json | ConvertTo-Json -Depth 100   # deep enough that nothing of yours gets flattened
    [System.IO.File]::WriteAllText($tmp, $out, (New-Object System.Text.UTF8Encoding $false))
    $null = Get-Content $tmp -Raw -Encoding UTF8 | ConvertFrom-Json   # validate before replacing
    Set-Settings $tmp $settings -Move
}
catch {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    if ($backup) { Set-Settings $backup $settings; Write-Warning "Install failed; restored settings from $backup" }
    throw
}

Write-Host "Installed Hooker hooks into $settings"
Write-Host "Restart any running Claude Code sessions to pick up the hooks."
