# Removes the Hooker hooks from %USERPROFILE%\.claude\settings.json.
# Run it yourself (double-click nothing - just run this .ps1).
# Only Hooker's own entries are removed; any other hooks you added are preserved. Your
# settings are backed up to a timestamped file, the result is validated as JSON before it
# replaces the live one, and the backup is restored on any failure.

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

# Turn every session's autopilot off for good: Hooker's hooks are going away, and nothing may
# quietly re-arm if Hooker is ever installed again. Stop the widget first - with "Remember
# autopilot" on it would switch remembered sessions straight back on - then clear the switches
# and the remembered list.
if (Get-Process HookerWidget -ErrorAction SilentlyContinue) {
    Stop-Process -Name HookerWidget -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 500
    Write-Host "Stopped the Hooker widget."
}
Remove-Item (Join-Path $env:USERPROFILE '.claude\hooker\sessions\*.state') -Force -ErrorAction SilentlyContinue
$widgetCfg = Join-Path $env:USERPROFILE '.claude\hooker\widget.json'
if (Test-Path $widgetCfg) {
    try {
        $w = Get-Content $widgetCfg -Raw -Encoding UTF8 | ConvertFrom-Json
        foreach ($k in 'RememberAutopilot', 'Autopilot', 'OnTiles') { $w.PSObject.Properties.Remove($k) }
        [System.IO.File]::WriteAllText($widgetCfg, ($w | ConvertTo-Json -Depth 20), (New-Object System.Text.UTF8Encoding $false))
    }
    catch { Remove-Item $widgetCfg -Force -ErrorAction SilentlyContinue }   # unreadable: drop it (just position/order)
}

$settings = Join-Path $env:USERPROFILE '.claude\settings.json'
if (-not (Test-Path $settings)) { Write-Host "No settings.json found - nothing to do."; return }


# Timestamped so re-running never clobbers an earlier good backup.
$backup = "$settings.hooker-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
Copy-Item $settings $backup -Force
$json = Get-Content $settings -Raw -Encoding UTF8 | ConvertFrom-Json   # UTF8: see install-hook.ps1

$tmp = "$settings.hooker-tmp"
try {
    if ($json.PSObject.Properties.Name -contains 'hooks') {
        foreach ($e in @('PreToolUse','UserPromptSubmit','Notification','Stop','SessionStart','SessionEnd')) {
            if ($json.hooks.PSObject.Properties.Name -notcontains $e) { continue }
            $kept = @(Remove-HookerEntries $json.hooks.$e)
            if ($kept.Count -gt 0) { $json.hooks.$e = $kept }
            else { $json.hooks.PSObject.Properties.Remove($e) }
        }
        # Drop the hooks object entirely if it's now empty.
        # @(...).Count: on PS 5.1 a bare .Properties.Count is $null, so this never fired.
        if (@($json.hooks.PSObject.Properties).Count -eq 0) { $json.PSObject.Properties.Remove('hooks') }
    }

    # Write BOM-less UTF-8 to a temp file, verify it parses, then swap it in.
    $out = $json | ConvertTo-Json -Depth 100
    [System.IO.File]::WriteAllText($tmp, $out, (New-Object System.Text.UTF8Encoding $false))
    $null = Get-Content $tmp -Raw -Encoding UTF8 | ConvertFrom-Json   # validate before replacing
    Set-Settings $tmp $settings -Move
}
catch {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    Set-Settings $backup $settings; Write-Warning "Uninstall failed; restored settings from $backup"
    throw
}

Write-Host "Removed Hooker hooks from $settings (backup: $backup)"
