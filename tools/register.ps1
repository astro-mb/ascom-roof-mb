<#
.SYNOPSIS
    Enregistre le driver ASCOM « Toit coulissant Arduino » auprès de COM (32 et 64 bits).

.DESCRIPTION
    Le driver est une DLL COM in-process en .NET Framework 4.8, compilée en AnyCPU.

    Elle doit être enregistrée AVEC LES DEUX regasm — celui du Framework 32 bits et celui du
    Framework 64 bits — car les deux vues du registre sont distinctes :

      * NINA et ConformU sont en 64 bits    -> ont besoin de l'enregistrement 64 bits
      * le Chooser ASCOM et de nombreux outils historiques sont en 32 bits
                                            -> ont besoin de l'enregistrement 32 bits

    Les deux pointent vers le MÊME fichier DLL grâce à l'option /codebase.

    L'enregistrement déclenche la méthode [ComRegisterFunction] du driver, qui l'inscrit dans
    le Chooser ASCOM via le Profile de la Platform.

.NOTES
    regasm émet un avertissement RA0000 « Registering an unsigned assembly with /codebase... ».
    C'est NORMAL et sans conséquence : c'est le cas de la quasi-totalité des drivers ASCOM,
    qui ne sont pas signés en nom fort.

.PARAMETER Configuration
    Release (défaut) ou Debug — détermine la DLL à enregistrer.

.EXAMPLE
    # Dans un terminal ADMINISTRATEUR
    .\register.ps1
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
Write-Host '=== Enregistrement COM du driver ASCOM ===' -ForegroundColor Cyan
Write-Host ''

# --- Élévation administrateur ---------------------------------------------------------------
$identity  = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Host "Ce script doit être lancé dans un terminal ADMINISTRATEUR." -ForegroundColor Red
    Write-Host ''
    Write-Host "L'enregistrement COM écrit dans HKEY_CLASSES_ROOT : les droits administrateur" -ForegroundColor Yellow
    Write-Host "sont indispensables." -ForegroundColor Yellow
    Write-Host ''
    Write-Host 'Ouvrez PowerShell en tant qu''administrateur, puis relancez :' -ForegroundColor Yellow
    Write-Host "    powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`"" -ForegroundColor DarkGray
    Write-Host ''
    exit 1
}

# --- Présence de la DLL ---------------------------------------------------------------------
if (-not (Test-Path $dllPath)) {
    Write-Host "DLL introuvable : $dllPath" -ForegroundColor Red
    Write-Host ''
    Write-Host 'Compilez d''abord le driver :' -ForegroundColor Yellow
    Write-Host "    powershell -ExecutionPolicy Bypass -File `"$(Join-Path $PSScriptRoot 'build.ps1')`"" -ForegroundColor DarkGray
    Write-Host ''
    exit 1
}

$dll = Get-Item $dllPath
Write-Host "DLL       : $($dll.FullName)"
Write-Host ("Taille    : {0:N0} octets" -f $dll.Length)
Write-Host "Modifiée  : $($dll.LastWriteTime)"
Write-Host ''

# --- Les deux regasm ------------------------------------------------------------------------
$regasmTargets = @(
    @{ Nom = '32 bits'; Exe = "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\regasm.exe"   },
    @{ Nom = '64 bits'; Exe = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\regasm.exe" }
)

$echecs = 0
foreach ($t in $regasmTargets) {
    Write-Host "--- regasm $($t.Nom) ---" -ForegroundColor Cyan

    if (-not (Test-Path $t.Exe)) {
        Write-Host "  regasm introuvable : $($t.Exe)" -ForegroundColor Red
        $echecs++
        continue
    }

    # 2>&1 volontairement omis : la sortie d'erreur est déjà capturée par l'hôte, et la
    # rediriger sur un exécutable natif fausserait le code de retour en PowerShell 5.1.
    & $t.Exe $dll.FullName /codebase
    if ($LASTEXITCODE -eq 0) {
        Write-Host "  Enregistrement $($t.Nom) réussi." -ForegroundColor Green
    } else {
        Write-Host "  ÉCHEC de l'enregistrement $($t.Nom) (code $LASTEXITCODE)." -ForegroundColor Red
        $echecs++
    }
    Write-Host ''
}

# --- Vérification ---------------------------------------------------------------------------
# ATTENTION au piège WOW64 : sous HKLM\SOFTWARE\Classes, seules certaines sous-clés (CLSID,
# Interface, TypeLib, Record...) possèdent une vue 32 bits séparée sous WOW6432Node. Un ProgID
# est une clé PARTAGÉE entre les deux architectures. Le chercher sous WOW6432Node produirait donc
# systématiquement un faux échec : c'est le CLSID qu'il faut tester pour prouver que les clients
# 32 et 64 bits voient bien le driver.
Write-Host '--- Vérification ---' -ForegroundColor Cyan

$progIdKey = "HKLM:\SOFTWARE\Classes\$progId"
if (Test-Path $progIdKey) {
    Write-Host "  ProgID $progId présent (clé partagée 32/64)." -ForegroundColor Green
} else {
    Write-Host "  ProgID $progId ABSENT." -ForegroundColor Red
    $echecs++
}

# Le CLSID est lu depuis le ProgID plutôt que codé en dur : pas de GUID à maintenir ici.
$clsid = $null
if (Test-Path "$progIdKey\CLSID") {
    $clsid = (Get-ItemProperty "$progIdKey\CLSID").'(default)'
}

if ($clsid) {
    Write-Host "  CLSID  $clsid"
    $vues = @(
        @{ Nom = '64 bits'; Cle = "HKLM:\SOFTWARE\Classes\CLSID\$clsid\InprocServer32" },
        @{ Nom = '32 bits'; Cle = "HKLM:\SOFTWARE\Classes\WOW6432Node\CLSID\$clsid\InprocServer32" }
    )
    foreach ($v in $vues) {
        if (Test-Path $v.Cle) {
            Write-Host "  Serveur $($v.Nom) enregistré." -ForegroundColor Green
            Write-Host "      $((Get-ItemProperty $v.Cle).CodeBase)" -ForegroundColor DarkGray
        } else {
            Write-Host "  Serveur $($v.Nom) ABSENT." -ForegroundColor Red
            $echecs++
        }
    }
} else {
    Write-Host '  Impossible de résoudre le CLSID depuis le ProgID.' -ForegroundColor Red
    $echecs++
}

$chooserKey = "HKLM:\SOFTWARE\WOW6432Node\ASCOM\Dome Drivers\$progId"
if (Test-Path $chooserKey) {
    Write-Host "  Inscrit au Chooser ASCOM : $((Get-ItemProperty $chooserKey).'(default)')" -ForegroundColor Green
} else {
    Write-Host '  NON inscrit au Chooser ASCOM.' -ForegroundColor Yellow
}
Write-Host ''

if ($echecs -gt 0) {
    Write-Host "$echecs enregistrement(s) en échec." -ForegroundColor Red
    exit 1
}

Write-Host 'Driver enregistré.' -ForegroundColor Green
Write-Host ''
Write-Host 'Il apparaît désormais dans le Chooser ASCOM (liste des Dome) sous :' -ForegroundColor Cyan
Write-Host '    Drivers Ascom toit Obs principal Version 2026-02' -ForegroundColor DarkGray
Write-Host ''
Write-Host 'Rappel : l''avertissement RA0000 sur l''assembly non signée est normal.' -ForegroundColor DarkGray
Write-Host ''
exit 0
