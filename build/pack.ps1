<#
.SYNOPSIS
    Gera o instalador do Mirante (Velopack) em artifacts/releases.

.EXAMPLE
    ./build/pack.ps1 -Version 0.1.0
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts/publish'
$releases = Join-Path $root 'artifacts/releases'

Push-Location $root
try {
    # Local builds start clean. (A release pipeline would instead download the previous
    # releases here, so Velopack can generate delta updates.)
    foreach ($dir in $publish, $releases) {
        if (Test-Path -LiteralPath $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
    }

    dotnet tool restore
    if ($LASTEXITCODE) { throw 'dotnet tool restore falhou' }

    # Depende do .NET Desktop Runtime 10; o instalador baixa e instala se faltar.
    dotnet publish src/Mirante.App -c Release -r win-x64 --self-contained false -p:Version=$Version -o $publish
    if ($LASTEXITCODE) { throw 'dotnet publish falhou' }

    dotnet vpk pack `
        --packId Mirante `
        --packVersion $Version `
        --packDir $publish `
        --mainExe Mirante.exe `
        --packTitle Mirante `
        --packAuthors Sanfado `
        --icon src/Mirante.App/Assets/mirante.ico `
        --splashImage build/installer/splash.png `
        --splashProgressColor '#5B9BFF' `
        --framework net10.0-x64-desktop `
        --outputDir $releases
    if ($LASTEXITCODE) { throw 'vpk pack falhou' }

    Write-Host "`nInstalador: $releases" -ForegroundColor Green
    Get-ChildItem -LiteralPath $releases | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
}
finally {
    Pop-Location
}
