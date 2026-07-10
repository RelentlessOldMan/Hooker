# Builds both exes into .\dist (framework-dependent single-file, needs .NET 8 runtime).
# Auto-bumps the patch version on every build (VERSION file) and stamps it into the exes.
# Run:  powershell -ExecutionPolicy Bypass -File .\build.ps1
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    # Auto-bump patch version.
    $verFile = Join-Path $PSScriptRoot 'VERSION'
    $ver = if (Test-Path $verFile) { (Get-Content $verFile -Raw).Trim() } else { '1.0.0' }
    $p = $ver.Split('.'); $p[2] = [int]$p[2] + 1; $ver = ($p -join '.')
    Set-Content $verFile $ver -Encoding ascii -NoNewline
    Write-Host "Building Hooker v$ver"

    # Regenerate icons if Python/Pillow is present; otherwise the committed tiles are used.
    if (Get-Command python -ErrorAction SilentlyContinue) { python assets\make_icons.py }

    dotnet publish shim\Shim.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Version=$ver -o dist
    dotnet publish tray\Tray.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Version=$ver -o dist
    Write-Host "`nBuilt v${ver}: dist\hook.exe and dist\HookerWidget.exe"
}
finally { Pop-Location }
