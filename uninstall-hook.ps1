# Removes the Hooker hooks from %USERPROFILE%\.claude\settings.json.
# Run it yourself (double-click nothing — just run this .ps1).
# Only Hooker's own entries are removed; any other hooks you added are preserved.

$ErrorActionPreference = 'Stop'

$settings = Join-Path $env:USERPROFILE '.claude\settings.json'
if (-not (Test-Path $settings)) { Write-Host "No settings.json found - nothing to do."; return }

$hookExe = (Join-Path $PSScriptRoot 'dist\hook.exe') -replace '\\', '/'

Copy-Item $settings "$settings.hooker-backup" -Force
$json = Get-Content $settings -Raw | ConvertFrom-Json

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

# Write BOM-less UTF-8 (Set-Content -Encoding utf8 adds a BOM on Windows PowerShell 5.1).
$out = $json | ConvertTo-Json -Depth 10
[System.IO.File]::WriteAllText($settings, $out, (New-Object System.Text.UTF8Encoding $false))
Write-Host "Removed Hooker hooks from $settings"
