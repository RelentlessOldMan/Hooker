# Removes the Hooker hooks from %USERPROFILE%\.claude\settings.json.
# Run it yourself (double-click nothing - just run this .ps1).
# Only Hooker's own entries are removed; any other hooks you added are preserved. Your
# settings are backed up to a timestamped file, the result is validated as JSON before it
# replaces the live one, and the backup is restored on any failure.

$ErrorActionPreference = 'Stop'

$settings = Join-Path $env:USERPROFILE '.claude\settings.json'
if (-not (Test-Path $settings)) { Write-Host "No settings.json found - nothing to do."; return }

$hookExe = (Join-Path $PSScriptRoot 'dist\hook.exe') -replace '\\', '/'

# Timestamped so re-running never clobbers an earlier good backup.
$backup = "$settings.hooker-backup-" + (Get-Date -Format 'yyyyMMdd-HHmmss')
Copy-Item $settings $backup -Force
$json = Get-Content $settings -Raw | ConvertFrom-Json

$tmp = "$settings.hooker-tmp"
try {
    if ($json.PSObject.Properties.Name -contains 'hooks') {
        foreach ($e in @('PreToolUse','UserPromptSubmit','Notification','Stop','SessionStart','SessionEnd')) {
            if ($json.hooks.PSObject.Properties.Name -notcontains $e) { continue }
            # Keep every group except Hooker's (identified by our hook.exe command).
            $kept = @($json.hooks.$e | Where-Object {
                -not ($_.hooks | Where-Object { $_.command -eq $hookExe })
            })
            if ($kept.Count -gt 0) { $json.hooks.$e = $kept }
            else { $json.hooks.PSObject.Properties.Remove($e) }
        }
        # Drop the hooks object entirely if it's now empty.
        if ($json.hooks.PSObject.Properties.Count -eq 0) { $json.PSObject.Properties.Remove('hooks') }
    }

    # Write BOM-less UTF-8 to a temp file, verify it parses, then swap it in.
    $out = $json | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText($tmp, $out, (New-Object System.Text.UTF8Encoding $false))
    $null = Get-Content $tmp -Raw | ConvertFrom-Json   # validate before replacing
    Move-Item $tmp $settings -Force
}
catch {
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    Copy-Item $backup $settings -Force; Write-Warning "Uninstall failed; restored settings from $backup"
    throw
}

Write-Host "Removed Hooker hooks from $settings (backup: $backup)"
