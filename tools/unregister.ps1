<#
.SYNOPSIS
    Désenregistre le driver ASCOM « Toit coulissant Arduino » de COM (32 et 64 bits).

.DESCRIPTION
    Symétrique de register.ps1. Le désenregistrement déclenche la méthode
    [ComUnregisterFunction] du driver, qui le retire du Chooser ASCOM — et SUPPRIME ses réglages
    du Profile ASCOM (port COM, temporisations…). Un register.ps1 ultérieur repartira des valeurs
    par défaut. Pour changer de DLL sans perdre les réglages, lancer register.ps1 directement :
    il réécrit le CodeBase sans désenregistrer.

    IMPORTANT : lancez ce script AVANT de supprimer ou de déplacer la DLL. regasm doit pouvoir
    charger l'assembly pour exécuter sa méthode de désenregistrement. Si la DLL a déjà disparu,
    le driver restera listé dans le Chooser et il faudra le retirer à la main via le
    « Profile Explorer » de la Platform ASCOM.

.PARAMETER Configuration
    Release (défaut) ou Debug — détermine la DLL à désenregistrer.

.EXAMPLE
    # Dans un terminal ADMINISTRATEUR
    .\unregister.ps1
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')]
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$dllPath  = Join-Path $repoRoot "driver\ASCOM.ArduinoRoof.Dome\bin\$Configuration\net48\ASCOM.ArduinoRoof.Dome.dll"
$progId   = 'ASCOM.ArduinoRoof.Dome'

Write-Host ''
Write-Host '=== Désenregistrement COM du driver ASCOM ===' -ForegroundColor Cyan
Write-Host ''

# --- Élévation administrateur ---------------------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host 'Ce script doit être lancé dans un terminal ADMINISTRATEUR.' -ForegroundColor Red
    Write-Host ''
    Write-Host 'Ouvrez PowerShell en tant qu''administrateur, puis relancez :' -ForegroundColor Yellow
    Write-Host "    powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -ForegroundColor DarkGray
    Write-Host ''
    exit 1
}

if (-not (Test-Path $dllPath)) {
    Write-Host "DLL introuvable : $dllPath" -ForegroundColor Red
    Write-Host ''
    Write-Host 'La DLL doit être présente pour que regasm puisse exécuter la méthode de' -ForegroundColor Yellow
    Write-Host 'désenregistrement du driver. Si elle a déjà été supprimée, retirez l''entrée' -ForegroundColor Yellow
    Write-Host 'à la main avec le « Profile Explorer » de la Platform ASCOM.' -ForegroundColor Yellow
    Write-Host ''
    exit 1
}

$dll = Get-Item $dllPath
Write-Host "DLL : $($dll.FullName)"

# Le CLSID doit être relevé MAINTENANT : après le désenregistrement, la clé ProgID qui permet
# de le résoudre aura disparu et la vérification finale serait impossible.
$clsid = $null
if (Test-Path "HKLM:\SOFTWARE\Classes\$progId\CLSID") {
    $clsid = (Get-ItemProperty "HKLM:\SOFTWARE\Classes\$progId\CLSID").'(default)'
    Write-Host "CLSID : $clsid"
}
Write-Host ''

# --- Les deux regasm ------------------------------------------------------------------------
$regasmTargets = @(
    @{ Nom = '64 bits'; Exe = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" },
    @{ Nom = '32 bits'; Exe = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\regasm.exe"   }
)

$echecs = 0
foreach ($t in $regasmTargets) {
    Write-Host "--- regasm $($t.Nom) /unregister ---" -ForegroundColor Cyan

    if (-not (Test-Path $t.Exe)) {
        Write-Host "  regasm introuvable : $($t.Exe)" -ForegroundColor Red
        $echecs++
        continue
    }

    & $t.Exe $dll.FullName /unregister
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Désenregistrement $($t.Nom) réussi." -ForegroundColor Green
    } else {
        Write-Host "  ÉCHEC du désenregistrement $($t.Nom) (code $LASTEXITCODE)." -ForegroundColor Red
        $echecs++
    }
    Write-Host ''
}

# --- Vérification ---------------------------------------------------------------------------
# Comme dans register.ps1 : le ProgID est une clé partagée, seul le CLSID possède une vue 32 bits
# distincte sous WOW6432Node. C'est donc lui qui prouve le désenregistrement des deux côtés.
Write-Host '--- Vérification ---' -ForegroundColor Cyan

if (Test-Path "HKLM:\SOFTWARE\Classes\$progId") {
    Write-Host "  ProgID $progId ENCORE présent." -ForegroundColor Yellow
} else {
    Write-Host "  ProgID $progId retiré." -ForegroundColor Green
}

if ($clsid) {
    $vues = @(
        @{ Nom = '64 bits'; Cle = "HKLM:\SOFTWARE\Classes\CLSID\$clsid" },
        @{ Nom = '32 bits'; Cle = "HKLM:\SOFTWARE\Classes\WOW6432Node\CLSID\$clsid" }
    )
    foreach ($v in $vues) {
        if (Test-Path $v.Cle) {
            Write-Host "  Serveur $($v.Nom) ENCORE enregistré." -ForegroundColor Yellow
        } else {
            Write-Host "  Serveur $($v.Nom) retiré." -ForegroundColor Green
        }
    }
} else {
    Write-Host '  CLSID inconnu : le driver n''était probablement pas enregistré.' -ForegroundColor DarkGray
}

if (Test-Path "HKLM:\SOFTWARE\WOW6432Node\ASCOM\Dome Drivers\$progId") {
    Write-Host '  ENCORE inscrit au Chooser ASCOM.' -ForegroundColor Yellow
} else {
    Write-Host '  Retiré du Chooser ASCOM.' -ForegroundColor Green
}
Write-Host ''

if ($echecs -gt 0) {
    Write-Host "$echecs désenregistrement(s) en échec." -ForegroundColor Red
    exit 1
}

Write-Host 'Driver désenregistré.' -ForegroundColor Green
Write-Host ''
exit 0
