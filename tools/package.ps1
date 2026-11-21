<#
.SYNOPSIS
    Régénère le paquet de déploiement du dossier release\.

.DESCRIPTION
    À lancer sur le PC de DÉVELOPPEMENT, après toute modification du driver ou du firmware.

    Le script recompile les deux, puis copie dans release\ :
      * ASCOM.ArduinoRoof.Dome.dll   driver compilé
      * RoofController.ino.hex       firmware compilé

    Ces deux binaires sont VERSIONNÉS, contrairement à tout le reste des sorties de compilation.
    C'est délibéré : le PC de l'observatoire n'a ni SDK .NET ni sources, un simple `git pull` doit
    donc lui apporter des binaires cohérents avec le commit qui les a produits.

    Il faut donc penser à COMMITER release\ après avoir lancé ce script.

.PARAMETER SkipFirmware
    Ne recompile pas le firmware (utile si seul le driver a changé).

.EXAMPLE
    .\package.ps1
#>
[CmdletBinding()]
param(
    [switch] $SkipFirmware
)

$ErrorActionPreference = 'Stop'

$repoRoot   = Split-Path -Parent $PSScriptRoot
$releaseDir = Join-Path $repoRoot 'release'
$solution   = Join-Path $repoRoot 'driver\ASCOM.ArduinoRoof.sln'
$sketchDir  = Join-Path $repoRoot 'firmware\RoofController'
$buildDir   = Join-Path $repoRoot 'build\firmware'

Write-Host ''
Write-Host '=== Fabrication du paquet de déploiement ===' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path $releaseDir)) { New-Item -ItemType Directory -Path $releaseDir -Force | Out-Null }

# --- Driver -----------------------------------------------------------------------------------
Write-Host '--- Driver ---' -ForegroundColor Cyan
$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    Write-Host '  SDK .NET introuvable : winget install Microsoft.DotNet.SDK.8' -ForegroundColor Red
    exit 1
}

# --no-incremental : une compilation incrémentale peut laisser une DLL périmée en place, et un
# paquet de déploiement contenant un binaire périmé est un piège difficile à diagnostiquer.
& dotnet build $solution -c Release --nologo --no-incremental
if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '  ECHEC de compilation du driver.' -ForegroundColor Red
    Write-Host '  Si la DLL est verrouillée : fermer NINA complètement (déconnecter le dôme ne' -ForegroundColor Yellow
    Write-Host '  suffit pas, .NET ne décharge pas un assembly d''un domaine d''application).' -ForegroundColor Yellow
    exit 1
}

$dllSource = Join-Path $repoRoot 'driver\ASCOM.ArduinoRoof.Dome\bin\Release\net48\ASCOM.ArduinoRoof.Dome.dll'
if (-not (Test-Path $dllSource)) {
    Write-Host "  DLL introuvable : $dllSource" -ForegroundColor Red
    exit 1
}
Copy-Item $dllSource $releaseDir -Force
$dll = Get-Item (Join-Path $releaseDir 'ASCOM.ArduinoRoof.Dome.dll')
Write-Host ("  ASCOM.ArduinoRoof.Dome.dll   {0,9:N0} octets" -f $dll.Length) -ForegroundColor Green

# --- Firmware ---------------------------------------------------------------------------------
Write-Host ''
Write-Host '--- Firmware ---' -ForegroundColor Cyan
if ($SkipFirmware) {
    Write-Host '  ignoré (-SkipFirmware)' -ForegroundColor DarkGray
} else {
    $cli = $null
    foreach ($c in @(
        "$env:LOCALAPPDATA\Programs\Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe",
        "$env:ProgramFiles\Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe")) {
        if (Test-Path $c) { $cli = $c; break }
    }
    if (-not $cli) {
        $g = Get-Command arduino-cli -ErrorAction SilentlyContinue
        if ($g) { $cli = $g.Source }
    }
    if (-not $cli) {
        Write-Host '  arduino-cli introuvable (IDE Arduino non installé ?).' -ForegroundColor Red
        Write-Host '  Relancer avec -SkipFirmware pour ne mettre à jour que le driver.' -ForegroundColor Yellow
        exit 1
    }

    & $cli compile --fqbn arduino:avr:nano --warnings all --output-dir $buildDir $sketchDir
    if ($LASTEXITCODE -ne 0) {
        Write-Host '  ECHEC de compilation du firmware.' -ForegroundColor Red
        exit 1
    }

    $hexSource = Join-Path $buildDir 'RoofController.ino.hex'
    if (-not (Test-Path $hexSource)) {
        Write-Host "  .hex introuvable : $hexSource" -ForegroundColor Red
        exit 1
    }
    Copy-Item $hexSource $releaseDir -Force
    $hex = Get-Item (Join-Path $releaseDir 'RoofController.ino.hex')
    Write-Host ("  RoofController.ino.hex       {0,9:N0} octets" -f $hex.Length) -ForegroundColor Green
}

# --- Contenu du paquet -------------------------------------------------------------------------
Write-Host ''
Write-Host '--- Contenu de release\ ---' -ForegroundColor Cyan
Get-ChildItem $releaseDir -File | Sort-Object Name | ForEach-Object {
    Write-Host ("  {0,-34} {1,9:N0} octets" -f $_.Name, $_.Length)
}

Write-Host ''
Write-Host 'Paquet prêt.' -ForegroundColor Green
Write-Host ''
Write-Host 'Ne pas oublier de le commiter : les binaires de release\ sont versionnés' -ForegroundColor Yellow
Write-Host 'pour que le PC de l''observatoire les récupère par un simple git pull.' -ForegroundColor Yellow
Write-Host ''
exit 0
