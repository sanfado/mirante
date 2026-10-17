<#
.SYNOPSIS
    Gera o instalador do Mirante (Velopack) em artifacts/releases.

.EXAMPLE
    ./build/pack.ps1 -Version 0.1.0

.EXAMPLE
    # Release no CI: mantém os releases anteriores já baixados, para gerar atualizações delta.
    ./build/pack.ps1 -Version 0.2.0 -KeepReleases
#>
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    # Debug + outro PackId servem para testar atualizações sem tocar na instalação real.
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release',

    [ValidatePattern('^[A-Za-z][A-Za-z0-9.]*$')]
    [string] $PackId = 'Mirante',

    [string] $Title = 'Mirante',

    [string] $Shortcuts = 'Desktop,StartMenuRoot',

    [switch] $KeepReleases
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$publish = Join-Path $root 'artifacts/publish'
$releases = Join-Path $root 'artifacts/releases'

Push-Location $root
try {
    if (Test-Path -LiteralPath $publish) { Remove-Item -LiteralPath $publish -Recurse -Force }
    if (-not $KeepReleases -and (Test-Path -LiteralPath $releases)) { Remove-Item -LiteralPath $releases -Recurse -Force }

    dotnet tool restore
    if ($LASTEXITCODE) { throw 'dotnet tool restore falhou' }

    # Depende do .NET Desktop Runtime 10; o instalador baixa e instala se faltar.
    dotnet publish src/Mirante.App -c $Configuration -r win-x64 --self-contained false -p:Version=$Version -o $publish
    if ($LASTEXITCODE) { throw 'dotnet publish falhou' }

    dotnet vpk pack `
        --packId $PackId `
        --packVersion $Version `
        --packDir $publish `
        --mainExe Mirante.exe `
        --packTitle $Title `
        --packAuthors Sanfado `
        --icon src/Mirante.App/Assets/mirante.ico `
        --splashImage build/installer/splash.png `
        --splashProgressColor '#5B9BFF' `
        --framework net10.0-x64-desktop `
        --shortcuts $Shortcuts `
        --outputDir $releases
    if ($LASTEXITCODE) { throw 'vpk pack falhou' }

    Write-Host "`nInstalador: $releases" -ForegroundColor Green
    Get-ChildItem -LiteralPath $releases | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } }
}
finally {
    Pop-Location
}
