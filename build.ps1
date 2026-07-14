# Builds both exes into .\dist (framework-dependent single-file, needs .NET 8 runtime).
# Each project is published to a TEMP folder first, then its finished single-file exe is
# swapped into dist\ with retry — so a running widget, or a hook.exe firing mid-build,
# can't lock the output and leave a half-published (broken) exe in dist\.
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

    $dist = Join-Path $PSScriptRoot 'dist'
    New-Item -ItemType Directory -Force $dist | Out-Null

    # Publish one project to a temp dir, then copy its exe into dist\ (retrying past a
    # transient lock from the running app or a firing hook). Publishing outside dist\
    # means a lock can never corrupt the live exe — the swap is all-or-nothing.
    function Publish-One($proj, $exe) {
        $tmp = Join-Path $env:TEMP ('hooker-build-' + [IO.Path]::GetFileNameWithoutExtension($proj))
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
        dotnet publish $proj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:Version=$ver -o $tmp
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed for $proj" }

        $src = Join-Path $tmp $exe
        $dst = Join-Path $dist $exe
        for ($i = 1; $i -le 20; $i++) {
            try { Copy-Item $src $dst -Force -ErrorAction Stop; break }
            catch {
                if ($i -eq 20) { throw "could not swap $exe into dist (still locked after 20 tries): $_" }
                Start-Sleep -Milliseconds 300
            }
        }
        Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }

    Publish-One 'shim\Shim.csproj' 'hook.exe'
    Publish-One 'tray\Tray.csproj' 'HookerWidget.exe'
    Write-Host "`nBuilt v${ver}: dist\hook.exe and dist\HookerWidget.exe"
}
finally { Pop-Location }
