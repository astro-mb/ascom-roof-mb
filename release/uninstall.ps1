<#
.SYNOPSIS
    Désinstalle le driver ASCOM « Toit coulissant Arduino » de ce PC.

.DESCRIPTION
    À lancer dans un terminal ADMINISTRATEUR.

    Le désenregistrement doit se faire AVANT la suppression du fichier : regasm doit pouvoir
    charger l'assembly pour exécuter sa méthode de désenregistrement, qui retire le driver du
    Chooser ASCOM. Le script s'en charge dans le bon ordre.

    Retirer le driver du Chooser SUPPRIME ses réglages du Profile ASCOM (port COM, temporisations,
    reprises, trace). Le script les exporte donc d'abord dans un fichier .reg, dans le dossier
    Documents, pour pouvoir les réimporter après une réinstallation.

.PARAMETER InstallDir
    Dossier d'installation. Défaut : C:\Program Files\ASCOM ArduinoRoof

.PARAMETER KeepFiles
    Désenregistre sans supprimer le dossier d'installation.

.EXAMPLE
    # Dans un terminal ADMINISTRATEUR
    .\uninstall.ps1
#>
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:ProgramFiles 'ASCOM ArduinoRoof'),
    [switch] $KeepFiles
)

$ErrorActionPreference = 'Stop'

$progId  = 'ASCOM.ArduinoRoof.Dome'
$dllName = 'ASCOM.ArduinoRoof.Dome.dll'
$target  = Join-Path $InstallDir $dllName

$regasm32 = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\regasm.exe"
$regasm64 = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\regasm.exe"

Write-Host ''
Write-Host '=== Désinstallation du driver ASCOM « Toit coulissant Arduino » ===' -ForegroundColor Cyan
Write-Host ''

$principal = New-Object Security.Principal.WindowsPrincipal([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Ce script doit être lancé dans un terminal ADMINISTRATEUR.' -ForegroundColor Red
    Write-Host ''
    exit 1
}

# Le CLSID doit être relevé maintenant : la clé ProgID qui permet de le résoudre disparaîtra.
$clsid = $null
if (Test-Path "HKLM:\SOFTWARE\Classes\$progId\CLSID") {
    $clsid = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\$progId\CLSID").'(default)'
}

# On désenregistre le fichier RÉELLEMENT référencé par le registre, qui n'est pas forcément
# celui de $InstallDir si l'installation venait d'ailleurs.
$fichier = $target
if ($clsid) {
    $srv = "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32"
    if (Test-Path $srv) {
        try { $fichier = ([Uri](Get-ItemProperty $srv).CodeBase).LocalPath } catch { }
    }
}

# Sauvegarde des réglages : le désenregistrement va supprimer la clé du Profile ASCOM.
$profileKey = "HKLM:\SOFTWARE\WOW6432Node\ASCOM\Dome Drivers\$progId"
$sauvegarde = $null
if (Test-Path $profileKey) {
    $sauvegarde = Join-Path ([Environment]::GetFolderPath('MyDocuments')) `
        ("ArduinoRoof-reglages-{0}.reg" -f (Get-Date -Format 'yyyyMMdd-HHmmss'))
    & reg.exe export "HKLM\SOFTWARE\WOW6432Node\ASCOM\Dome Drivers\$progId" $sauvegarde /y | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "Réglages sauvegardés : $sauvegarde" -ForegroundColor Green
    } else {
        Write-Host "Export des réglages impossible (code $LASTEXITCODE) : désinstallation interrompue." -ForegroundColor Red
        Write-Host ''
        exit 1
    }
    Write-Host ''
}

if (Test-Path $fichier) {
    Write-Host "--- Désenregistrement de $fichier ---" -ForegroundColor Cyan
    foreach ($r in @(@{N='64 bits';E=$regasm64}, @{N='32 bits';E=$regasm32})) {
        if (Test-Path $r.E) {
            & $r.E $fichier /unregister | Out-Null
            if ($LASTEXITCODE -eq 0) { Write-Host "  $($r.N) : OK" -ForegroundColor Green }
            else { Write-Host "  $($r.N) : ECHEC (code $LASTEXITCODE)" -ForegroundColor Yellow }
        }
    }
} else {
    Write-Host "DLL introuvable ($fichier) : nettoyage du registre uniquement." -ForegroundColor Yellow
}
Write-Host ''

Write-Host '--- Vérification ---' -ForegroundColor Cyan
if ($clsid) {
    foreach ($v in @(@{N='64 bits';K="HKLM:\SOFTWARE\Classes\CLSID\$clsid"},
                     @{N='32 bits';K="HKLM:\SOFTWARE\Classes\WOW6432Node\CLSID\$clsid"})) {
        if (Test-Path $v.K) { Write-Host "  Serveur $($v.N) ENCORE présent." -ForegroundColor Yellow }
        else                { Write-Host "  Serveur $($v.N) retiré." -ForegroundColor Green }
    }
}
if (Test-Path "HKLM:\SOFTWARE\WOW6432Node\ASCOM\Dome Drivers\$progId") {
    Write-Host '  ENCORE inscrit au Chooser ASCOM.' -ForegroundColor Yellow
} else {
    Write-Host '  Retiré du Chooser ASCOM.' -ForegroundColor Green
}
Write-Host ''

if (-not $KeepFiles -and (Test-Path $InstallDir)) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Dossier supprimé : $InstallDir" -ForegroundColor Green
}

Write-Host ''
Write-Host 'Désinstallation terminée.' -ForegroundColor Green
if ($sauvegarde) {
    Write-Host 'Les réglages du driver ont été retirés du Profile ASCOM avec lui. Pour les retrouver' -ForegroundColor DarkGray
    Write-Host 'après une réinstallation (install.ps1), dans un terminal administrateur :' -ForegroundColor DarkGray
    Write-Host "    reg import `"$sauvegarde`"" -ForegroundColor DarkGray
}
Write-Host ''
exit 0
