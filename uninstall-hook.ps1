# Removes the Hooker hooks from %USERPROFILE%\.claude\settings.json.
# Run this yourself:  ! powershell -ExecutionPolicy Bypass -File C:\Playground\Hooker\uninstall-hook.ps1

$ErrorActionPreference = 'Stop'

$settings = Join-Path $env:USERPROFILE '.claude\settings.json'
if (-not (Test-Path $settings)) { Write-Host "No settings.json found - nothing to do."; return }

Copy-Item $settings "$settings.hooker-backup" -Force
$json = Get-Content $settings -Raw | ConvertFrom-Json

if ($json.PSObject.Properties.Name -contains 'hooks') {
    foreach ($e in @('PreToolUse','UserPromptSubmit','Notification','Stop','SessionStart','SessionEnd')) {
        if ($json.hooks.PSObject.Properties.Name -contains $e) { $json.hooks.PSObject.Properties.Remove($e) }
    }
    # Drop the hooks object entirely if it's now empty.
    if ($json.hooks.PSObject.Properties.Count -eq 0) { $json.PSObject.Properties.Remove('hooks') }
}

$json | ConvertTo-Json -Depth 10 | Set-Content $settings -Encoding utf8
Write-Host "Removed Hooker hooks from $settings"
