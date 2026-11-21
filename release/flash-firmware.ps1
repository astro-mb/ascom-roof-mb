<#
.SYNOPSIS
    Téléverse le firmware pré-compilé (.hex) sur l'Arduino Nano du toit, sans compiler.

.DESCRIPTION
    Destiné au PC de l'observatoire, où ni le SDK .NET ni les sources ne sont nécessaires.
    Le fichier RoofController.ino.hex livré dans ce dossier a été compilé depuis les sources du
    même commit.

    IDENTIFICATION DES CARTES
    Sur un observatoire, plusieurs Arduino sont souvent connectés (focuser, roue à filtres,
    contrôleur d'alimentation...). Des clones à puce CH340 se présentent tous sous le même nom et
    ne portent aucun numéro de série : rien ne les distingue dans la liste des ports.

    Téléverser sur la mauvaise carte écraserait le firmware d'un autre équipement, de façon
    irréversible. Le script prend donc deux précautions :

      * -ListPorts interroge chaque port et indique lequel répond « contrôleur de toit » ;
      * avant tout téléversement, le port cible est interrogé. Si le contrôleur de toit ne s'y
        identifie pas, le script REFUSE, sauf -Force explicite.

    L'interrogation ouvre brièvement chaque port sans activer DTR ni RTS, ce qui évite de
    redémarrer les cartes sondées.

    PREMIER TELEVERSEMENT
    Une carte neuve ne contient pas encore le firmware : elle ne peut donc pas s'identifier et
    -Force sera nécessaire. Pour être certain du port, la méthode la plus fiable :

        1. .\flash-firmware.ps1 -ListPorts       (carte du toit DEBRANCHEE)
        2. brancher la carte du toit
        3. .\flash-firmware.ps1 -ListPorts       (le port APPARU est le bon)

    Le script cherche un téléverseur dans cet ordre :
      1. arduino-cli (fourni avec l'IDE Arduino)   -> méthode préférée
      2. avrdude (fourni avec le coeur arduino:avr) -> repli

.PARAMETER Port
    Port COM de l'Arduino du toit. Obligatoire pour téléverser.

.PARAMETER OldBootloader
    Clones Nano à ancien bootloader (fréquent avec une puce CH340) : téléversement à 57600 bauds
    au lieu de 115200. À essayer si le téléversement échoue.

.PARAMETER ListPorts
    Énumère les ports série, interroge chacun d'eux et quitte. Ne téléverse rien.

.PARAMETER Force
    Autorise le téléversement sur un port où le contrôleur de toit ne s'identifie pas.
    Nécessaire pour une carte neuve. À n'utiliser qu'après avoir vérifié le port.

.EXAMPLE
    .\flash-firmware.ps1 -ListPorts
    .\flash-firmware.ps1 -Port COM5
    .\flash-firmware.ps1 -Port COM5 -Force            # carte neuve
    .\flash-firmware.ps1 -Port COM5 -OldBootloader
#>
[CmdletBinding()]
param(
    [string] $Port,
    [switch] $OldBootloader,
    [switch] $ListPorts,
    [switch] $Force
)

$ErrorActionPreference = 'Stop'

$hex = Join-Path $PSScriptRoot 'RoofController.ino.hex'

# Interroge un port : renvoie la ligne de réponse au PING, ou $null.
# DTR et RTS restent inactifs afin de ne pas redémarrer la carte sondée — elle peut appartenir
# à un autre équipement en service.
function Test-Carte([string] $nomPort) {
    $sp = $null
    try {
        $sp = New-Object System.IO.Ports.SerialPort($nomPort, 115200, 'None', 8, 'One')
        $sp.ReadTimeout  = 1200
        $sp.WriteTimeout = 1000
        $sp.NewLine      = "`n"
        $sp.DtrEnable    = $false
        $sp.RtsEnable    = $false
        $sp.Open()
        Start-Sleep -Milliseconds 2500      # marge si la carte a malgré tout redémarré
        $sp.DiscardInBuffer()

        foreach ($tentative in 1..2) {
            $sp.WriteLine('PING')
            $limite = [DateTime]::UtcNow.AddMilliseconds(1500)
            while ([DateTime]::UtcNow -lt $limite) {
                try { $ligne = $sp.ReadLine().Trim() } catch { break }
                if ($ligne -eq '' -or $ligne.StartsWith('#')) { continue }
                return $ligne
            }
        }
        return $null
    } catch {
        return "(port inaccessible : $($_.Exception.Message))"
    } finally {
        if ($sp -and $sp.IsOpen) { $sp.Close() }
        if ($sp) { $sp.Dispose() }
    }
}

function Est-ControleurToit([string] $reponse) {
    return ($reponse -and $reponse -match '^PONG\s+ROOF\s')
}

Write-Host ''
Write-Host '=== Téléversement du firmware du contrôleur de toit ===' -ForegroundColor Cyan
Write-Host ''

# --- Énumération -------------------------------------------------------------------------------
if ($ListPorts) {
    $ports = [System.IO.Ports.SerialPort]::GetPortNames() | Sort-Object
    if (-not $ports) {
        Write-Host 'Aucun port série détecté.' -ForegroundColor Yellow
        Write-Host ''
        exit 0
    }

    $noms = @{}
    Get-WmiObject Win32_PnPEntity -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '\((COM\d+)\)' } |
        ForEach-Object { $noms[$Matches[1]] = $_.Name }

    Write-Host "Interrogation de $($ports.Count) port(s), quelques secondes..." -ForegroundColor DarkGray
    Write-Host ''

    foreach ($p in $ports) {
        $desc = if ($noms.ContainsKey($p)) { $noms[$p] } else { $p }
        $rep  = Test-Carte $p
        if (Est-ControleurToit $rep) {
            Write-Host ("  {0,-8} {1}" -f $p, $desc) -ForegroundColor Green
            Write-Host ("           -> CONTROLEUR DE TOIT : {0}" -f $rep) -ForegroundColor Green
        } elseif ($rep) {
            Write-Host ("  {0,-8} {1}" -f $p, $desc) -ForegroundColor Yellow
            Write-Host ("           -> repond, mais ce n'est PAS le toit : {0}" -f $rep) -ForegroundColor Yellow
        } else {
            Write-Host ("  {0,-8} {1}" -f $p, $desc) -ForegroundColor Gray
            Write-Host  "           -> muet (carte neuve, autre firmware, ou port occupe)" -ForegroundColor Gray
        }
    }

    Write-Host ''
    Write-Host 'Si aucun port ne s''identifie comme contrôleur de toit (carte neuve), procéder ainsi :' -ForegroundColor Cyan
    Write-Host '  1. carte du toit DEBRANCHEE  -> .\flash-firmware.ps1 -ListPorts'
    Write-Host '  2. brancher la carte du toit'
    Write-Host '  3. relancer -ListPorts : le port APPARU est le bon'
    Write-Host ''
    exit 0
}

if (-not $Port) {
    Write-Host 'Paramètre -Port obligatoire : le script ne devine jamais la carte.' -ForegroundColor Red
    Write-Host '  .\flash-firmware.ps1 -ListPorts      pour identifier les cartes' -ForegroundColor DarkGray
    Write-Host ''
    exit 1
}

if (-not (Test-Path $hex)) {
    Write-Host "Firmware introuvable : $hex" -ForegroundColor Red
    Write-Host ''
    exit 1
}

Write-Host ("Firmware : {0}  ({1:N0} octets)" -f $hex, (Get-Item $hex).Length)
Write-Host "Port     : $Port"
Write-Host ''

# --- Garde-fou : identification de la carte cible ----------------------------------------------
Write-Host '--- Identification de la carte cible ---' -ForegroundColor Cyan
$reponse = Test-Carte $Port

if (Est-ControleurToit $reponse) {
    Write-Host "  Contrôleur de toit confirmé : $reponse" -ForegroundColor Green
} elseif ($Force) {
    if ($reponse) {
        Write-Host "  La carte répond : $reponse" -ForegroundColor Yellow
        Write-Host '  Ce N''EST PAS le contrôleur de toit, mais -Force a été passé.' -ForegroundColor Yellow
        Write-Host '  Son firmware actuel va etre DEFINITIVEMENT ECRASE.' -ForegroundColor Red
    } else {
        Write-Host '  Carte muette (neuve ou autre firmware) — -Force a été passé.' -ForegroundColor Yellow
    }
} else {
    Write-Host ''
    if ($reponse) {
        Write-Host "  REFUS : la carte sur $Port répond, mais ce n'est pas le contrôleur de toit." -ForegroundColor Red
        Write-Host "  Elle a répondu : $reponse" -ForegroundColor Red
        Write-Host ''
        Write-Host '  Téléverser ici ECRASERAIT DEFINITIVEMENT le firmware de cet équipement.' -ForegroundColor Red
    } else {
        Write-Host "  REFUS : aucune carte ne s'identifie comme contrôleur de toit sur $Port." -ForegroundColor Red
        Write-Host '  Elle est peut-etre neuve, sous un autre firmware, ou le port est occupé' -ForegroundColor Yellow
        Write-Host '  (NINA, boîte de configuration du driver, moniteur série...).' -ForegroundColor Yellow
    }
    Write-Host ''
    Write-Host '  Vérifier le port :  .\flash-firmware.ps1 -ListPorts' -ForegroundColor Cyan
    Write-Host '  Puis, une fois CERTAIN du port :' -ForegroundColor Cyan
    Write-Host "      .\flash-firmware.ps1 -Port $Port -Force" -ForegroundColor DarkGray
    Write-Host ''
    exit 1
}
Write-Host ''

Write-Host 'AVERTISSEMENT : le téléversement reset la carte. Le relais peut battre pendant' -ForegroundColor Yellow
Write-Host 'cette seconde si la résistance de tirage 10 kOhm n''est pas en place. Le moteur' -ForegroundColor Yellow
Write-Host 'doit etre débranché ou hors tension.' -ForegroundColor Yellow
Write-Host ''

# --- Recherche d'un téléverseur ----------------------------------------------------------------
$cli = $null
foreach ($c in @(
    "$env:LOCALAPPDATA\Programs\Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe",
    "$env:ProgramFiles\Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe",
    "${env:ProgramFiles(x86)}\Arduino IDE\resources\app\lib\backend\resources\arduino-cli.exe")) {
    if (Test-Path $c) { $cli = $c; break }
}
if (-not $cli) {
    $g = Get-Command arduino-cli -ErrorAction SilentlyContinue
    if ($g) { $cli = $g.Source }
}

if ($cli) {
    $fqbn = if ($OldBootloader) { 'arduino:avr:nano:cpu=atmega328old' } else { 'arduino:avr:nano' }
    Write-Host 'Téléverseur : arduino-cli' -ForegroundColor Green
    Write-Host "FQBN        : $fqbn"
    Write-Host ''
    & $cli upload --input-file $hex --fqbn $fqbn --port $Port
    $code = $LASTEXITCODE
} else {
    $avrdude = Get-ChildItem "$env:LOCALAPPDATA\Arduino15\packages\arduino\tools\avrdude" -Recurse `
                             -Filter 'avrdude.exe' -ErrorAction SilentlyContinue |
               Select-Object -First 1 -ExpandProperty FullName
    $conf    = Get-ChildItem "$env:LOCALAPPDATA\Arduino15\packages\arduino\tools\avrdude" -Recurse `
                             -Filter 'avrdude.conf' -ErrorAction SilentlyContinue |
               Select-Object -First 1 -ExpandProperty FullName

    if (-not $avrdude -or -not $conf) {
        Write-Host 'Aucun téléverseur trouvé sur ce PC.' -ForegroundColor Red
        Write-Host ''
        Write-Host 'Installez l''IDE Arduino : https://www.arduino.cc/en/software' -ForegroundColor Yellow
        Write-Host 'Il fournit le téléverseur ET le moniteur série, indispensable pour la' -ForegroundColor Yellow
        Write-Host 'mise en service (commandes PING, STATE, DIAG, CFG?).' -ForegroundColor Yellow
        Write-Host ''
        exit 1
    }

    $baud = if ($OldBootloader) { '57600' } else { '115200' }
    Write-Host 'Téléverseur : avrdude' -ForegroundColor Green
    Write-Host "Débit       : $baud bauds"
    Write-Host ''
    & $avrdude -C $conf -p atmega328p -c arduino -P $Port -b $baud -D -U "flash:w:$hex`:i"
    $code = $LASTEXITCODE
}

Write-Host ''
if ($code -ne 0) {
    Write-Host "ECHEC du téléversement (code $code)." -ForegroundColor Red
    Write-Host ''
    Write-Host 'Pistes, dans l''ordre :' -ForegroundColor Yellow
    Write-Host '  1. Bootloader ancien (fréquent sur les clones CH340) : relancer avec -OldBootloader'
    Write-Host '  2. Port occupé : fermer NINA, la boîte de configuration du driver et le moniteur série'
    Write-Host '  3. Ne pas passer par un concentrateur USB : brancher le Nano directement sur le PC'
    Write-Host '  4. Mauvais port : .\flash-firmware.ps1 -ListPorts'
    Write-Host '  5. Câble USB de charge seule, sans fils de données : en essayer un autre'
    Write-Host ''
    exit $code
}

Write-Host 'Téléversement terminé.' -ForegroundColor Green
Write-Host ''
Write-Host '--- Contrôle après téléversement ---' -ForegroundColor Cyan
Start-Sleep -Milliseconds 1500
$apres = Test-Carte $Port
if (Est-ControleurToit $apres) {
    Write-Host "  La carte s'identifie : $apres" -ForegroundColor Green
} else {
    Write-Host "  La carte ne répond pas encore au PING (reçu : $apres)." -ForegroundColor Yellow
    Write-Host '  Débrancher / rebrancher l''USB puis vérifier au moniteur série.' -ForegroundColor Yellow
}
Write-Host ''
Write-Host 'Vérification au moniteur série (115200 bauds, fin de ligne « Nouvelle ligne ») :' -ForegroundColor Cyan
Write-Host '  au reset la carte émet   # ROOF CONTROLLER 1.1.0 BOOT'
Write-Host '  PING   ->  PONG ROOF 1.0 1.1.0'
Write-Host '  DIAG   ->  état des capteurs, du relais et de l''EEPROM'
Write-Host ''
exit 0
