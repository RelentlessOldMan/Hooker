# Builds both exes into .\dist  (framework-dependent single-file, needs .NET 8 runtime).
# Run:  ! powershell -ExecutionPolicy Bypass -File C:\Playground\Hooker\build.ps1
$ErrorActionPreference = 'Stop'
Push-Location $PSScriptRoot
try {
    # Regenerate icons only if Python/Pillow is present; otherwise the committed .ico files are used.
    if (Get-Command python -ErrorAction SilentlyContinue) {
        python assets\make_icons.py
    }
    dotnet publish shim\Shim.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
    dotnet publish tray\Tray.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o dist
    Write-Host "`nBuilt: dist\hook.exe and dist\HookerWidget.exe"
}
finally { Pop-Location }
