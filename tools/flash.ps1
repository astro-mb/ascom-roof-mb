#Requires -Version 5.1
<#
.SYNOPSIS
    Compile et téléverse le firmware du contrôleur de toit d'observatoire sur un Arduino Nano.

.DESCRIPTION
    Ce script s'appuie sur l'arduino-cli embarqué dans l'IDE Arduino (aucune installation
    supplémentaire n'est nécessaire) pour :

      1. compiler le croquis firmware\RoofController avec « --warnings all » ;
      2. afficher l'occupation flash et RAM ;
      3. téléverser le binaire sur la carte (sauf si -CompileOnly est présent).

    Le binaire produit est déposé dans build\firmware à la racine du dépôt
    (répertoire déjà ignoré par git).

    ---------------------------------------------------------------------------
    BOOTLOADER ANCIEN DES CLONES NANO
    ---------------------------------------------------------------------------
    Beaucoup de clones d'Arduino Nano (puces CH340) sont livrés avec l'ANCIEN
    bootloader, qui tourne à 57600 bauds au lieu de 115200. Avec le FQBN par
    défaut le téléversement échoue alors sur une erreur du type :

        avrdude: stk500_recv(): programmer is not responding
        avrdude: stk500_getsync() attempt 1 of 10: not in sync: resp=0x00

    Dans ce cas, relancer le script en forçant l'ancien bootloader :

        .\flash.ps1 -Port COM4 -Fqbn arduino:avr:nano:cpu=atmega328old

    Le firmware lui-même est strictement identique : seul le protocole de
    téléversement change. En cas de doute, essayer les deux.
    ---------------------------------------------------------------------------

    AUTRES CAUSES D'ÉCHEC DE TÉLÉVERSEMENT

      * Le port est déjà ouvert par un autre logiciel : fermer NINA, le driver
        ASCOM et le moniteur série de l'IDE Arduino avant de téléverser.
      * Un condensateur de 10 µF a été installé entre RESET et GND pour
        neutraliser l'auto-reset DTR (voir docs\CABLAGE.md) : il DOIT être
        retiré pendant le téléversement, sinon la carte ne redémarre jamais en
        bootloader.
      * Mauvais port COM : utiliser -ListPorts pour énumérer les cartes vues.

.PARAMETER Port
    Port série de la carte, par exemple COM4. Obligatoire pour le téléversement,
    inutile avec -CompileOnly. Utiliser -ListPorts pour le découvrir.

.PARAMETER Fqbn
    Identifiant de carte arduino-cli. Défaut : arduino:avr:nano.
    Pour un clone à bootloader ancien : arduino:avr:nano:cpu=atmega328old

.PARAMETER CompileOnly
    Compile et affiche l'occupation mémoire, sans rien téléverser.
    C'est le mode à utiliser quand la carte n'est pas branchée.

.PARAMETER ListPorts
    Énumère les ports série et les cartes détectées, puis s'arrête.

.PARAMETER ArduinoCli
    Chemin explicite vers arduino-cli.exe. Par défaut le script cherche
    l'exécutable embarqué dans l'IDE Arduino, puis un arduino-cli du PATH.

.EXAMPLE
    .\flash.ps1 -CompileOnly
    Vérifie que le firmware compile, carte débranchée.

.EXAMPLE
    .\flash.ps1 -ListPorts
    Affiche les ports COM disponibles pour identifier le Nano.

.EXAMPLE
    .\flash.ps1 -Port COM4
    Compile puis téléverse sur la carte branchée sur COM4.

.EXAMPLE
    .\flash.ps1 -Port COM4 -Fqbn arduino:avr:nano:cpu=atmega328old
    Idem pour un clone Nano équipé de l'ancien bootloader.
#>

[CmdletBinding()]
param(
    [string] $Port,
    [string] $Fqbn = 'arduino:avr:nano',
    [switch] $CompileOnly,
    [switch] $ListPorts,
    [string] $ArduinoCli
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------
# Petites aides d'affichage
# ---------------------------------------------------------------------------

function Write-Step { param([string] $Message) Write-Host ""; Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok   { param([string] $Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn { param([string] $Message) Write-Host "    $Message" -ForegroundColor Yellow }
function Write-Fail { param([string] $Message) Write-Host "    $Message" -ForegroundColor Red }

# ---------------------------------------------------------------------------
# Localisation de arduino-cli
# ---------------------------------------------------------------------------

function Resolve-ArduinoCli {
    param([string] $Explicit)

    if ($Explicit) {
        if (Test-Path -LiteralPath $Explicit) {
            return (Resolve-Path -LiteralPath $Explicit).Path
        }
        return $null
    }

    $candidates = @()
    if ($env:LOCALAPPDATA) {
        $candidates += (Join-Path $env:LOCALAPPDATA 'Programs\Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe')
    }
    if ($env:ProgramFiles) {
        $candidates += (Join-Path $env:ProgramFiles 'Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe')
    }
    if (${env:ProgramFiles(x86)}) {
        $candidates += (Join-Path ${env:ProgramFiles(x86)} 'Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe')
    }

    foreach ($c in $candidates) {
        if (Test-Path -LiteralPath $c) {
            return (Resolve-Path -LiteralPath $c).Path
        }
    }

    # Dernier recours : un arduino-cli installé séparément et présent dans le PATH.
    $onPath = $null
    try { $onPath = Get-Command 'arduino-cli.exe' -ErrorAction Stop } catch { $onPath = $null }
    if ($onPath) { return $onPath.Source }

    return $null
}

$cli = Resolve-ArduinoCli -Explicit $ArduinoCli
if (-not $cli) {
    Write-Host ""
    Write-Fail "arduino-cli introuvable."
    Write-Host ""
    Write-Host "  Ce script a besoin de l'arduino-cli fourni avec l'IDE Arduino. Emplacement attendu :"
    Write-Host "    $env:LOCALAPPDATA\Programs\Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe"
    Write-Host ""
    Write-Host "  Solutions :"
    Write-Host "    - installer l'IDE Arduino 2.x depuis https://www.arduino.cc/en/software ;"
    Write-Host "    - ou indiquer le chemin explicitement :"
    Write-Host "        .\flash.ps1 -CompileOnly -ArduinoCli 'D:\chemin\vers\arduino-cli.exe'"
    Write-Host ""
    exit 1
}

# ---------------------------------------------------------------------------
# Localisation du croquis
# ---------------------------------------------------------------------------

$repoRoot  = Split-Path -Parent $PSScriptRoot
$sketchDir = Join-Path $repoRoot 'firmware\RoofController'
$sketchIno = Join-Path $sketchDir 'RoofController.ino'
$outputDir = Join-Path $repoRoot 'build\firmware'

if (-not (Test-Path -LiteralPath $sketchIno)) {
    Write-Host ""
    Write-Fail "Croquis introuvable : $sketchIno"
    Write-Host "    Le script doit rester dans le répertoire tools\ du dépôt."
    Write-Host ""
    exit 1
}

Write-Host ""
Write-Host "  Contrôleur de toit - compilation / téléversement du firmware" -ForegroundColor White
Write-Host "  arduino-cli : $cli" -ForegroundColor DarkGray
Write-Host "  croquis     : $sketchDir" -ForegroundColor DarkGray
Write-Host "  FQBN        : $Fqbn" -ForegroundColor DarkGray

# ---------------------------------------------------------------------------
# -ListPorts : énumération et sortie
# ---------------------------------------------------------------------------

if ($ListPorts) {
    Write-Step "Ports série détectés"
    & $cli board list
    Write-Host ""
    Write-Host "  Relancer ensuite avec :  .\flash.ps1 -Port COMx" -ForegroundColor DarkGray
    Write-Host ""
    exit 0
}

# ---------------------------------------------------------------------------
# Vérification des arguments
# ---------------------------------------------------------------------------

if (-not $CompileOnly -and -not $Port) {
    Write-Host ""
    Write-Fail "Paramètre -Port manquant."
    Write-Host "    -Port est obligatoire pour téléverser. Options :"
    Write-Host "      .\flash.ps1 -ListPorts       pour trouver le port de la carte"
    Write-Host "      .\flash.ps1 -Port COM4       pour compiler et téléverser"
    Write-Host "      .\flash.ps1 -CompileOnly     pour compiler sans carte branchée"
    Write-Host ""
    exit 1
}

if ($Port -and $Port -notmatch '^(?i)COM\d+$') {
    Write-Warn "Le port « $Port » ne ressemble pas à un port COM Windows (COM1, COM4, ...). Tentative quand même."
}

# ---------------------------------------------------------------------------
# Compilation
# ---------------------------------------------------------------------------

Write-Step "Compilation (--warnings all)"

if (-not (Test-Path -LiteralPath $outputDir)) {
    New-Item -ItemType Directory -Force -Path $outputDir | Out-Null
}

# --format json place toute la sortie du compilateur dans la structure JSON :
# on la réaffiche nous-mêmes pour que les avertissements restent visibles.
$rawJson = & $cli compile `
    --fqbn $Fqbn `
    --warnings all `
    --output-dir $outputDir `
    --format json `
    $sketchDir | Out-String

$result = $null
try {
    $result = $rawJson | ConvertFrom-Json
} catch {
    Write-Fail "Sortie d'arduino-cli illisible (JSON invalide). Sortie brute :"
    Write-Host $rawJson
    exit 1
}

if ($result.compiler_out -and $result.compiler_out.Trim()) {
    Write-Host $result.compiler_out.TrimEnd()
}
if ($result.compiler_err -and $result.compiler_err.Trim()) {
    # avr-gcc envoie ses avertissements ET ses erreurs sur ce flux.
    Write-Host $result.compiler_err.TrimEnd() -ForegroundColor Yellow
}

if (-not $result.success) {
    Write-Host ""
    Write-Fail "Échec de la compilation - rien n'a été téléversé."
    Write-Host ""
    exit 1
}

Write-Ok "Compilation réussie."

# ---------------------------------------------------------------------------
# Occupation mémoire
# ---------------------------------------------------------------------------

function Format-Usage {
    param(
        [string] $Label,
        [string] $Detail,
        $Section
    )
    if (-not $Section) { return }

    $used = [int64] $Section.size
    $max  = [int64] $Section.max_size
    if ($max -le 0) { return }

    $pct  = [math]::Round(($used * 100.0) / $max, 1)
    $free = $max - $used

    $color = 'Green'
    if ($pct -ge 90) { $color = 'Red' } elseif ($pct -ge 75) { $color = 'Yellow' }

    $line = "{0,-20} {1,6} / {2,6} octets  ({3,5} %)  - libre : {4} octets" -f $Label, $used, $max, $pct, $free
    Write-Host "    $line" -ForegroundColor $color
    if ($Detail) { Write-Host "    $(' ' * 20) $Detail" -ForegroundColor DarkGray }
}

$sections = $result.builder_result.executable_sections_size
$textSect = $sections | Where-Object { $_.name -eq 'text' }
$dataSect = $sections | Where-Object { $_.name -eq 'data' }

Write-Step "Occupation mémoire"
Format-Usage -Label 'Flash (programme)' -Section $textSect -Detail 'code + chaînes constantes stockées en F()/PSTR'
Format-Usage -Label 'RAM (variables)'   -Section $dataSect -Detail 'globales ; le reste sert à la pile et aux tampons'

if ($dataSect) {
    $ramPct = ([int64] $dataSect.size * 100.0) / [int64] $dataSect.max_size
    if ($ramPct -ge 75) {
        Write-Warn "RAM globale au-delà de 75 % : risque de collision pile/tas sur ATmega328P."
    }
}

Write-Host ""
Write-Host "    Binaire : $(Join-Path $outputDir 'RoofController.ino.hex')" -ForegroundColor DarkGray

if ($CompileOnly) {
    Write-Step "Terminé (-CompileOnly : aucun téléversement)"
    Write-Host ""
    exit 0
}

# ---------------------------------------------------------------------------
# Téléversement
# ---------------------------------------------------------------------------

Write-Step "Téléversement sur $Port"
Write-Warn "Le relais peut battre brièvement pendant le reset : le toit doit être"
Write-Warn "sous surveillance, ou le moteur de portail hors tension."
Write-Host ""

& $cli upload `
    --fqbn $Fqbn `
    --port $Port `
    --input-dir $outputDir `
    $sketchDir

$uploadExit = $LASTEXITCODE

if ($uploadExit -ne 0) {
    Write-Host ""
    Write-Fail "Échec du téléversement (code $uploadExit)."
    Write-Host ""
    Write-Host "  Pistes, dans l'ordre :" -ForegroundColor White
    Write-Host "   1. Bootloader ancien (très fréquent sur les clones CH340). Relancer avec :"
    Write-Host "        .\flash.ps1 -Port $Port -Fqbn arduino:avr:nano:cpu=atmega328old" -ForegroundColor Cyan
    Write-Host "   2. Port occupé : fermer NINA, le driver ASCOM et le moniteur série de l'IDE."
    Write-Host "   3. Condensateur 10 µF entre RESET et GND encore en place : le retirer"
    Write-Host "      pendant le téléversement (voir docs\CABLAGE.md), puis le remettre."
    Write-Host "   4. Mauvais port : .\flash.ps1 -ListPorts"
    Write-Host "   5. Câble USB de charge seule, sans fils de données : en essayer un autre."
    Write-Host ""
    exit $uploadExit
}

Write-Host ""
Write-Ok "Téléversement terminé."
Write-Host ""
Write-Host "  Vérification rapide au moniteur série (115200 bauds, fin de ligne « Nouvelle ligne ») :" -ForegroundColor White
Write-Host "    au reset la carte doit émettre    # ROOF CONTROLLER 1.1.0 BOOT" -ForegroundColor DarkGray
Write-Host "    PING   doit répondre              PONG ROOF 1.0 1.1.0"          -ForegroundColor DarkGray
Write-Host "    STATE  doit répondre              STATE <pos> <motion> ..."     -ForegroundColor DarkGray
Write-Host "    DIAG   affiche l'état des capteurs et de l'EEPROM"              -ForegroundColor DarkGray
Write-Host ""
exit 0
