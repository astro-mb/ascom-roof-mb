<#
.SYNOPSIS
    Installe le driver ASCOM « Toit coulissant Arduino » sur ce PC.

.DESCRIPTION
    À lancer sur le PC de l'observatoire, dans un terminal ADMINISTRATEUR.

    Le script copie la DLL dans un dossier d'installation STABLE puis l'enregistre auprès de COM
    depuis cet emplacement.

    Pourquoi un dossier stable plutôt que le dossier de compilation : regasm /codebase inscrit un
    chemin ABSOLU dans le registre. Enregistrer depuis un `bin\Release` rendrait le driver
    dépendant de l'arborescence de développement — un dépôt déplacé, nettoyé ou recompilé et le
    driver disparaît en pleine nuit.

    Lors d'une mise à jour, les réglages du driver (port COM, temporisations, reprises, trace) sont
    CONSERVÉS : le script les relit dans le Profile ASCOM avant de désenregistrer l'ancienne
    version, puis les réécrit après l'enregistrement de la nouvelle. Une copie est aussi exportée
    dans le dossier d'installation (reglages-avant-installation.reg), au cas où le script serait
    interrompu entre les deux.

    Prérequis sur le PC cible :
      * ASCOM Platform 7          (https://ascom-standards.org/)
      * .NET Framework 4.8        (intégré à Windows 10 1903+ et Windows 11)
    Aucun SDK .NET n'est nécessaire : la DLL est déjà compilée.

.PARAMETER InstallDir
    Dossier d'installation. Défaut : C:\Program Files\ASCOM ArduinoRoof

.EXAMPLE
    # Dans un terminal ADMINISTRATEUR
    .\install.ps1
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'ASCOM ArduinoRoof')
)

$ErrorActionPreference = 'Stop'

$progId  = 'ASCOM.ArduinoRoof.Dome'
$dllName = 'ASCOM.ArduinoRoof.Dome.dll'
$source  = Join-Path $PSScriptRoot $dllName
$target  = Join-Path $InstallDir $dllName

$regasm32 = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\regasm.exe"
$regasm64 = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"

# Clé du driver dans le Profile ASCOM (la Platform stocke le Profile dans la vue 32 bits).
$profileKey    = "HKLM:\SOFTWARE\WOW6432Node\ASCOM\Dome Drivers\$progId"
$profileKeyReg = "HKLM\SOFTWARE\WOW6432Node\ASCOM\Dome Drivers\$progId"

Write-Host ''
Write-Host '=== Installation du driver ASCOM « Toit coulissant Arduino » ===' -ForegroundColor Cyan
Write-Host ''

# --- Élévation ------------------------------------------------------------------------------
$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Ce script doit être lancé dans un terminal ADMINISTRATEUR.' -ForegroundColor Red
    Write-Host "    powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -ForegroundColor DarkGray
    Write-Host ''
    exit 1
}

# --- Prérequis ------------------------------------------------------------------------------
Write-Host '--- Prérequis ---' -ForegroundColor Cyan

$ascomKey = 'HKLM:\SOFTWARE\WOW6432Node\ASCOM\Platform'
if (Test-Path $ascomKey) {
    Write-Host "  Platform ASCOM : $((Get-ItemProperty $ascomKey).'Platform Version')" -ForegroundColor Green
} else {
    Write-Host '  Platform ASCOM INTROUVABLE.' -ForegroundColor Red
    Write-Host '  Installez-la depuis https://ascom-standards.org/ avant de continuer.' -ForegroundColor Yellow
    Write-Host ''
    exit 1
}

$net48 = (Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full' -ErrorAction SilentlyContinue).Release
if ($net48 -and $net48 -ge 528040) {
    Write-Host '  .NET Framework 4.8 : présent' -ForegroundColor Green
} else {
    Write-Host '  .NET Framework 4.8 absent ou trop ancien.' -ForegroundColor Red
    Write-Host '  https://dotnet.microsoft.com/download/dotnet-framework/net48' -ForegroundColor Yellow
    Write-Host ''
    exit 1
}

if (-not (Test-Path $source)) {
    Write-Host "  DLL introuvable : $source" -ForegroundColor Red
    Write-Host '  Le dossier release/ est-il complet ?' -ForegroundColor Yellow
    Write-Host ''
    exit 1
}
Write-Host "  DLL à installer : $(('{0:N0}' -f (Get-Item $source).Length)) octets" -ForegroundColor Green
Write-Host ''

# --- Sauvegarde des réglages -------------------------------------------------------------------
# Le désenregistrement de l'ancienne DLL exécute sa méthode Dome.UnregisterASCOM, qui retire le
# driver du Chooser en SUPPRIMANT sa clé du Profile ASCOM, valeurs comprises ; l'enregistrement de
# la nouvelle DLL réécrit ensuite les valeurs par défaut. Sans cette sauvegarde, une mise à jour
# ramène le port COM et les temporisations aux valeurs d'usine — constaté le 06/10/2026 : port
# COM3 au lieu de COM4 et PULSE 600 ms, avec lesquels NINA ne se connecte plus et le portail ne
# démarre plus.
$reglages = [ordered]@{}
if (Test-Path $profileKey) {
    $cle = Get-Item $profileKey
    foreach ($nom in $cle.GetValueNames()) {
        # La valeur sans nom est le libellé du Chooser : l'enregistrement la réécrit lui-même.
        if ($nom -ne '') { $reglages[$nom] = [string]$cle.GetValue($nom) }
    }
}

$sauvegarde = $null
if ($reglages.Count -gt 0) {
    Write-Host '--- Sauvegarde des réglages ---' -ForegroundColor Cyan
    # Filet de sécurité sur disque : si le script s'interrompt entre le désenregistrement et la
    # restauration, les réglages restent récupérables par « reg import ».
    if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
    $sauvegarde = Join-Path $InstallDir 'reglages-avant-installation.reg'
    & reg.exe export $profileKeyReg $sauvegarde /y | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "  Export impossible (code $LASTEXITCODE) : installation interrompue, rien n'a été modifié." -ForegroundColor Red
        Write-Host ''
        exit 1
    }
    Write-Host ("  {0} valeurs : port {1}, PULSE {2}, INTER {3}, START {4}, TRAVEL {5}" -f $reglages.Count,
        $reglages['COM Port'], $reglages['Pulse ms'], $reglages['Inter pulse ms'],
        $reglages['Start timeout ms'], $reglages['Travel timeout ms']) -ForegroundColor Green
    Write-Host "  Copie : $sauvegarde" -ForegroundColor DarkGray
    Write-Host ''
}

# --- Désenregistrement d'une installation précédente -----------------------------------------
# Indispensable si le driver avait été enregistré depuis un autre chemin (un bin\Release de
# développement, par exemple) : sans cela deux CodeBase coexisteraient et COM chargerait
# l'ancienne DLL.
$ancien = $null
if (Test-Path "HKLM:\SOFTWARE\Classes\$progId\CLSID") {
    $clsidAncien = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\$progId\CLSID").'(default)'
    $srv = "HKLM:\SOFTWARE\Classes\CLSID\$clsidAncien\InprocServer32"
    if (Test-Path $srv) { $ancien = (Get-ItemProperty $srv).CodeBase }
}

if ($ancien) {
    Write-Host '--- Installation précédente détectée ---' -ForegroundColor Cyan
    Write-Host "  $ancien" -ForegroundColor DarkGray
    $ancienFichier = $null
    try { $ancienFichier = ([Uri]$ancien).LocalPath } catch { }
    if ($ancienFichier -and (Test-Path $ancienFichier)) {
        foreach ($r in @($regasm64, $regasm32)) {
            if (Test-Path $r) { & $r $ancienFichier /unregister | Out-Null }
        }
        Write-Host '  Désenregistrée.' -ForegroundColor Green
    } else {
        Write-Host '  Fichier absent : les clés seront simplement écrasées.' -ForegroundColor Yellow
    }
    Write-Host ''
}

# --- Copie ------------------------------------------------------------------------------------
Write-Host '--- Copie ---' -ForegroundColor Cyan
if (-not (Test-Path $InstallDir)) { New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null }
Copy-Item $source $target -Force
Write-Host "  $target" -ForegroundColor Green
Write-Host ''

# --- Enregistrement COM 32 et 64 bits ---------------------------------------------------------
# Les deux sont nécessaires : NINA et ConformU sont 64 bits, le Chooser ASCOM est 32 bits.
Write-Host '--- Enregistrement COM ---' -ForegroundColor Cyan
$echecs = 0
foreach ($r in @(@{N='32 bits';E=$regasm32}, @{N='64 bits';E=$regasm64})) {
    if (-not (Test-Path $r.E)) {
        Write-Host "  regasm $($r.N) introuvable." -ForegroundColor Red; $echecs++; continue
    }
    & $r.E $target /codebase | Out-Null
    if ($LASTEXITCODE -eq 0) { Write-Host "  $($r.N) : OK" -ForegroundColor Green }
    else { Write-Host "  $($r.N) : ECHEC (code $LASTEXITCODE)" -ForegroundColor Red; $echecs++ }
}
Write-Host '  (l''avertissement RA0000 sur l''assembly non signée est normal)' -ForegroundColor DarkGray
Write-Host ''

# --- Restauration des réglages ------------------------------------------------------------------
# Les valeurs sauvegardées écrasent les défauts que l'enregistrement vient d'écrire. Une valeur
# apparue avec la nouvelle version (nouveau réglage) garde son défaut.
if ($reglages.Count -gt 0) {
    Write-Host '--- Restauration des réglages ---' -ForegroundColor Cyan
    if (Test-Path $profileKey) {
        foreach ($nom in $reglages.Keys) {
            New-ItemProperty -Path $profileKey -Name $nom -Value $reglages[$nom] -PropertyType String -Force | Out-Null
        }
        $relu = Get-ItemProperty $profileKey
        $ecarts = @($reglages.Keys | Where-Object { [string]$relu.$_ -ne $reglages[$_] })
        if ($ecarts.Count -eq 0) {
            Write-Host "  $($reglages.Count) valeurs restaurées (port $($relu.'COM Port'))." -ForegroundColor Green
        } else {
            Write-Host "  Valeurs non restaurées : $($ecarts -join ', ')" -ForegroundColor Red
            Write-Host "  Les récupérer avec : reg import `"$sauvegarde`"" -ForegroundColor Yellow
            $echecs++
        }
    } else {
        # Enregistrement en échec : recréer la clé ferait apparaître au Chooser un driver absent.
        Write-Host '  Clé du Profile absente (enregistrement en échec) : réglages NON restaurés.' -ForegroundColor Red
        Write-Host "  Après correction, les récupérer avec : reg import `"$sauvegarde`"" -ForegroundColor Yellow
        $echecs++
    }
    Write-Host ''
}

# --- Vérification -----------------------------------------------------------------------------
# Le ProgID est une clé PARTAGÉE 32/64 ; seul le CLSID possède une vue WOW6432Node distincte.
Write-Host '--- Vérification ---' -ForegroundColor Cyan
$clsid = $null
if (Test-Path "HKLM:\SOFTWARE\Classes\$progId\CLSID") {
    $clsid = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\$progId\CLSID").'(default)'
}
if ($clsid) {
    foreach ($v in @(@{N='64 bits';K="HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32"},
                     @{N='32 bits';K="HKLM:\SOFTWARE\Classes\WOW6432Node\CLSID\$clsid\InprocServer32"})) {
        if (Test-Path $v.K) {
            Write-Host "  Serveur $($v.N) : $((Get-ItemProperty $v.K).CodeBase)" -ForegroundColor Green
        } else {
            Write-Host "  Serveur $($v.N) ABSENT." -ForegroundColor Red; $echecs++
        }
    }
} else {
    Write-Host '  CLSID non résolu depuis le ProgID.' -ForegroundColor Red; $echecs++
}

$chooser = "HKLM:\SOFTWARE\WOW6432Node\ASCOM\Dome Drivers\$progId"
if (Test-Path $chooser) {
    Write-Host "  Chooser ASCOM : $((Get-ItemProperty $chooser).'(default)')" -ForegroundColor Green
} else {
    Write-Host '  NON inscrit au Chooser ASCOM.' -ForegroundColor Yellow
}
Write-Host ''

if ($echecs -gt 0) {
    Write-Host "Installation incomplète ($echecs problème(s))." -ForegroundColor Red
    Write-Host ''
    exit 1
}

Write-Host 'Installation terminée.' -ForegroundColor Green
Write-Host ''
Write-Host 'Étapes suivantes :' -ForegroundColor Cyan
Write-Host '  1. Flasher l''Arduino          : .\flash-firmware.ps1 -Port COMx'
Write-Host '  2. Dans NINA, Equipment > Dome > Choisir'
Write-Host '       « Drivers Ascom toit Obs principal Version 2026-02 »'
if ($reglages.Count -gt 0) {
    Write-Host '  3. Bouton Setup : vérifier le port COM et les temporisations (conservés)'
} else {
    Write-Host '  3. Bouton Setup : choisir le port COM et régler les temporisations'
}
Write-Host '  4. Dérouler docs\TESTS.md AVANT de rebrancher le moteur'
Write-Host ''
Write-Host 'RAPPEL SECURITE : la résistance de tirage 10 kOhm entre la commande du' -ForegroundColor Yellow
Write-Host 'relais et GND doit etre en place avant tout raccordement du moteur.' -ForegroundColor Yellow
Write-Host ''
exit 0
