<#
.SYNOPSIS
    Rejoue les scénarios de panne sur le simulateur intégré (port « SIM ») et vérifie les reprises
    automatiques du driver.

.DESCRIPTION
    Aucun matériel n'est nécessaire, et le vrai toit n'est jamais commandé : le driver est réglé sur
    le port SIM pendant le test. Les réglages modifiés dans le Profile ASCOM (port, temporisations,
    reprises) sont sauvegardés au début et restaurés à la fin, même en cas d'échec.

    Par défaut la DLL compilée est chargée directement : aucun enregistrement COM n'est nécessaire.
    Avec -UseCom, le test passe par le ProgID enregistré, comme NINA.

    Scénarios :
      1. Ouverture et fermeture nominales, ShutterStatus à jour dès le retour de la commande
      2. Impulsion ignorée par le portail (TIMEOUT_START) : reprise, toit fermé
      3. Obstacle à mi-course (TIMEOUT_TRAVEL) : RECOVER, mauvais sens, puis fermé
      4. Rebond du capteur de départ : aucune erreur (firmware 1.1)
      5. Capteur qui frémit 0,5 s à l'arrêt : aucune erreur ; 4 s : SENSOR_LOST, puis fermeture reprise
      6. BOTH_SENSORS : aucune reprise, CloseShutter échoue
      7. Reprises désactivées (0) : la première erreur est rapportée telle quelle
      8. Double CloseShutter pendant la fermeture : accepté
      9. OpenShutter pendant une fermeture : inversion
     10. AbortSlew pendant une mission : arrêt, aucune reprise
     11. Reset du Nano en pleine fermeture (INTERRUPTED) : toit fermé quand même
     12. Coupure à mi-course puis reconnexion (INTERRUPTED perdu) : avertissement de connexion,
         puis fermeture reprise par RECOVER
     13. SENSOR_LOST puis retour en butée (toit déplacé à la main) : erreur acquittée par le driver

.PARAMETER UseCom
    Instancie le driver par COM (ProgID ASCOM.ArduinoRoof.Dome) au lieu de charger la DLL.

.PARAMETER DllPath
    DLL à tester. Défaut : sortie Release de tools\build.ps1.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\test-sim.ps1
#>
[CmdletBinding()]
param(
    [switch] $UseCom,
    [string] $DllPath
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DllPath) {
    $DllPath = Join-Path $repoRoot 'driver\ASCOM.ArduinoRoof.Dome\bin\Release\net48\ASCOM.ArduinoRoof.Dome.dll'
}

$progId = 'ASCOM.ArduinoRoof.Dome'

# --- Réglages de test : course simulée de 8 s, délais serrés pour que la suite reste courte ---
$testSettings = [ordered]@{
    'COM Port'          = 'SIM'
    'Start timeout ms'  = '3000'
    'Travel timeout ms' = '12000'
    'Retry count'       = '3'
    'Auto recover'      = 'True'
    'Poll period ms'    = '1000'
}

$ascomProfile = New-Object -ComObject ASCOM.Utilities.Profile
$ascomProfile.DeviceType = 'Dome'
$backup = @{}
foreach ($key in $testSettings.Keys) {
    $backup[$key] = $ascomProfile.GetValue($progId, $key, '', '<absent>')
}

$script:results = New-Object System.Collections.Generic.List[object]
$script:dome = $null

function Set-TestSetting([string] $key, [string] $value) {
    $ascomProfile.WriteValue($progId, $key, $value)
}

function New-Driver {
    if ($UseCom) { return New-Object -ComObject $progId }
    return New-Object ASCOM.ArduinoRoof.Dome
}

function Status { return [string]$script:dome.ShutterStatus }

function Sim([string] $command) { return $script:dome.Action('Sim:Fault', $command) }

function Mission { return $script:dome.Action('Roof:Mission', '') }

function Unwrap($err) {
    $e = $err.Exception
    while ($e.InnerException) { $e = $e.InnerException }
    return $e
}

# Attend un état final. Retourne la liste des états observés (pour vérifier qu'aucune erreur n'a fui).
function Wait-Status([string[]] $expected, [int] $timeoutS) {
    $seen = New-Object System.Collections.Generic.List[string]
    $deadline = (Get-Date).AddSeconds($timeoutS)
    while ((Get-Date) -lt $deadline) {
        $s = Status
        if ($seen.Count -eq 0 -or $seen[$seen.Count - 1] -ne $s) { $seen.Add($s) }
        if ($expected -contains $s) { return $seen }
        Start-Sleep -Milliseconds 250
    }
    throw ("délai de $timeoutS s dépassé en attendant " + ($expected -join '/') + " — états vus : " + ($seen -join ' > ') + " — " + (Mission))
}

function Assert([bool] $condition, [string] $message) {
    if (-not $condition) { throw $message }
}

# Remet le toit simulé dans une position connue, sans panne.
function Reset-Roof([string] $target) {
    Sim 'clear' | Out-Null
    $s = Status
    if ($s -eq 'shutterError') {
        $script:dome.Action('Roof:ClearError', '') | Out-Null
        Start-Sleep -Milliseconds 600
    }
    if ($target -eq 'open') { $script:dome.OpenShutter() } else { $script:dome.CloseShutter() }
    $final = if ($target -eq 'open') { 'shutterOpen' } else { 'shutterClosed' }
    Wait-Status @($final) 90 | Out-Null
}

function Run-Scenario([string] $name, [scriptblock] $body) {
    Write-Host ''
    Write-Host "--- $name" -ForegroundColor Cyan
    $chrono = [Diagnostics.Stopwatch]::StartNew()
    try {
        & $body
        Write-Host ("    OK ({0:0} s) — {1}" -f $chrono.Elapsed.TotalSeconds, (Mission)) -ForegroundColor Green
        $script:results.Add([pscustomobject]@{ Scenario = $name; Result = 'OK' })
    }
    catch {
        Write-Host ("    ECHEC : " + (Unwrap $_).Message) -ForegroundColor Red
        $script:results.Add([pscustomobject]@{ Scenario = $name; Result = 'ECHEC' })
    }
}

try {
    foreach ($key in $testSettings.Keys) { Set-TestSetting $key $testSettings[$key] }

    if (-not $UseCom) {
        if (-not (Test-Path $DllPath)) { throw "DLL introuvable : $DllPath — lancer tools\build.ps1" }
        [Reflection.Assembly]::LoadFrom($DllPath) | Out-Null
    }

    $script:dome = New-Driver
    $script:dome.Connected = $true
    Sim 'travel 8000' | Out-Null
    Sim 'startdelay 1000' | Out-Null
    Write-Host ("Driver " + $script:dome.DriverVersion + " connecté — " + $script:dome.Action('Roof:Status', ''))

    Run-Scenario '1. Ouverture et fermeture nominales' {
        Reset-Roof 'closed'
        $script:dome.OpenShutter()
        $s = Status
        Assert ($s -eq 'shutterOpening') "ShutterStatus au retour d'OpenShutter : $s (attendu shutterOpening)"
        Wait-Status @('shutterOpen') 30 | Out-Null
        $script:dome.CloseShutter()
        $s = Status
        Assert ($s -eq 'shutterClosing') "ShutterStatus au retour de CloseShutter : $s (attendu shutterClosing)"
        $seen = Wait-Status @('shutterClosed') 30
        Assert (-not ($seen -contains 'shutterError')) ("erreur vue pendant la course : " + ($seen -join ' > '))
    }

    Run-Scenario '2. Impulsion ignorée (TIMEOUT_START) puis reprise' {
        Reset-Roof 'open'
        Sim 'ignore-pulse 1' | Out-Null
        $script:dome.CloseShutter()
        $seen = Wait-Status @('shutterClosed', 'shutterError') 60
        Assert ((Status) -eq 'shutterClosed') ("toit non fermé : " + ($seen -join ' > '))
        Assert (-not ($seen -contains 'shutterError')) ("NINA aurait vu une erreur : " + ($seen -join ' > '))
        Assert ((Mission) -match 'reprises=1/') "une reprise attendue"
    }

    Run-Scenario '3. Obstacle à mi-course (TIMEOUT_TRAVEL, RECOVER)' {
        Reset-Roof 'open'
        Sim 'obstacle 0.5' | Out-Null
        $script:dome.CloseShutter()
        $seen = Wait-Status @('shutterClosed', 'shutterError') 150
        Assert ((Status) -eq 'shutterClosed') ("toit non fermé : " + ($seen -join ' > '))
        Assert (-not ($seen -contains 'shutterError')) ("NINA aurait vu une erreur : " + ($seen -join ' > '))
        Assert ((Mission) -match 'RECOVER') "un RECOVER était attendu"
    }

    Run-Scenario '4. Rebond du capteur de départ' {
        Reset-Roof 'open'
        Sim 'bounce' | Out-Null
        $script:dome.CloseShutter()
        $seen = Wait-Status @('shutterClosed', 'shutterError') 40
        Assert ((Status) -eq 'shutterClosed') ("toit non fermé : " + ($seen -join ' > '))
        Assert ((Mission) -match 'reprises=0/') "aucune reprise ne devait être nécessaire"
    }

    Run-Scenario '5. Capteur qui frémit à l''arrêt, puis SENSOR_LOST' {
        Reset-Roof 'open'
        Sim 'flicker open 500' | Out-Null
        Start-Sleep -Seconds 2
        Assert ((Status) -eq 'shutterOpen') ("un frémissement de 0,5 s ne doit lever aucune erreur : " + (Status))
        Sim 'flicker open 6000' | Out-Null
        Start-Sleep -Milliseconds 4800
        $st = $script:dome.Action('Roof:Status', '')
        Assert ($st -match 'SENSOR_LOST') "SENSOR_LOST attendu après 4 s de capteur relâché : $st"
        $script:dome.CloseShutter()
        $seen = Wait-Status @('shutterClosed', 'shutterError') 60
        Assert ((Status) -eq 'shutterClosed') ("fermeture non reprise : " + ($seen -join ' > '))
    }

    Run-Scenario '6. BOTH_SENSORS : aucune reprise' {
        Reset-Roof 'open'
        Sim 'both on' | Out-Null
        Start-Sleep -Seconds 1
        $failed = $false
        try { $script:dome.CloseShutter() } catch { $failed = $true; Write-Host ('    refus attendu : ' + (Unwrap $_).Message) }
        Assert $failed "CloseShutter aurait dû échouer"
        Assert ((Status) -eq 'shutterError') ("état attendu shutterError : " + (Status))
        Sim 'both off' | Out-Null
        Start-Sleep -Milliseconds 500
        $script:dome.Action('Roof:ClearError', '') | Out-Null
        Start-Sleep -Seconds 1
        Assert ((Status) -eq 'shutterOpen') ("après acquittement, toit ouvert attendu : " + (Status))
    }

    Run-Scenario '7. Reprises désactivées : erreur rapportée telle quelle' {
        Reset-Roof 'open'
        $script:dome.Dispose()
        Set-TestSetting 'Retry count' '0'
        $script:dome = New-Driver
        $script:dome.Connected = $true
        try {
            Sim 'ignore-pulse 1' | Out-Null
            $script:dome.CloseShutter()
            $seen = Wait-Status @('shutterClosed', 'shutterError') 30
            Assert ((Status) -eq 'shutterError') ("erreur attendue sans reprise : " + ($seen -join ' > '))
            Assert ((Mission) -match 'TimeoutStart') "TIMEOUT_START attendu"
        }
        finally {
            $script:dome.Dispose()
            Set-TestSetting 'Retry count' '3'
            $script:dome = New-Driver
            $script:dome.Connected = $true
        }
    }

    Run-Scenario '8. Double CloseShutter pendant la fermeture' {
        Reset-Roof 'open'
        $script:dome.CloseShutter()
        Start-Sleep -Seconds 2
        $script:dome.CloseShutter()
        Assert ((Status) -eq 'shutterClosing') ("toujours en fermeture attendu : " + (Status))
        Wait-Status @('shutterClosed') 30 | Out-Null
    }

    Run-Scenario '9. OpenShutter pendant une fermeture : inversion' {
        Reset-Roof 'open'
        $script:dome.CloseShutter()
        Start-Sleep -Seconds 4
        $script:dome.OpenShutter()
        Assert ((Status) -eq 'shutterOpening') ("ouverture attendue au retour : " + (Status))
        $seen = Wait-Status @('shutterOpen', 'shutterError') 40
        Assert ((Status) -eq 'shutterOpen') ("toit non rouvert : " + ($seen -join ' > '))
    }

    Run-Scenario '10. AbortSlew pendant une mission : aucune reprise' {
        Reset-Roof 'open'
        $script:dome.CloseShutter()
        Start-Sleep -Seconds 4
        $script:dome.AbortSlew()
        Start-Sleep -Seconds 8
        Assert ((Mission) -match 'Cancelled') ("mission annulée attendue : " + (Mission))
        Assert ((Status) -eq 'shutterError') ("toit arrêté à mi-course attendu (shutterError) : " + (Status))
    }

    Run-Scenario '11. Reset du Nano en pleine fermeture (INTERRUPTED)' {
        Reset-Roof 'open'
        $script:dome.CloseShutter()
        Start-Sleep -Seconds 3
        Sim 'reset' | Out-Null
        $seen = Wait-Status @('shutterClosed', 'shutterError') 60
        Assert ((Status) -eq 'shutterClosed') ("toit non fermé : " + ($seen -join ' > '))
    }

    Run-Scenario '12. Coupure à mi-course puis reconnexion (INTERRUPTED perdu)' {
        Reset-Roof 'open'
        # Course lancée hors mission (aucune reprise), arrêtée à mi-course par un obstacle.
        Sim 'obstacle 0.5' | Out-Null
        $script:dome.Action('Roof:RawCommand', 'CLOSE') | Out-Null
        Start-Sleep -Seconds 6
        # Premier redémarrage : INTERRUPTED, et « plus de mouvement en cours » est enregistré.
        Sim 'reset' | Out-Null
        Start-Sleep -Seconds 1
        # Reconnexion : l'ouverture du port redémarre la carte une seconde fois.
        $script:dome.Connected = $false
        $script:dome.Connected = $true
        $st = $script:dome.Action('Roof:Status', '')
        Assert ($st -match '^STATE UNKNOWN IDLE .* NONE$') "position inconnue sans erreur attendue : $st"
        Assert ((Mission) -match 'CONNEXION .*position du toit inconnue') ("avertissement de connexion attendu : " + (Mission))
        $script:dome.CloseShutter()
        $seen = Wait-Status @('shutterClosed', 'shutterError') 150
        Assert ((Status) -eq 'shutterClosed') ("fermeture non reprise : " + ($seen -join ' > '))
    }

    Run-Scenario '13. SENSOR_LOST puis retour en butée : acquittement automatique' {
        Reset-Roof 'open'
        # Capteur relâché 6 s (toit déplacé à la main) : SENSOR_LOST à 3 s, retour en butée à 6 s.
        Sim 'flicker open 6000' | Out-Null
        Start-Sleep -Milliseconds 4800
        $st = $script:dome.Action('Roof:Status', '')
        Assert ($st -match 'SENSOR_LOST') "SENSOR_LOST attendu toit hors butée : $st"
        $seen = Wait-Status @('shutterOpen') 10
        $st = $script:dome.Action('Roof:Status', '')
        Assert ($st -match ' NONE$') "erreur non acquittée une fois en butée : $st"
    }
}
finally {
    if ($script:dome) {
        try { $script:dome.Connected = $false } catch { }
        try { $script:dome.Dispose() } catch { }
    }
    foreach ($key in $backup.Keys) {
        if ($backup[$key] -eq '<absent>') { $ascomProfile.DeleteValue($progId, $key, '') }
        else { $ascomProfile.WriteValue($progId, $key, $backup[$key]) }
    }
    Write-Host ''
    Write-Host 'Réglages du Profile ASCOM restaurés.'
}

Write-Host ''
$script:results | Format-Table -AutoSize
$failures = @($script:results | Where-Object { $_.Result -ne 'OK' }).Count
if ($failures -gt 0) {
    Write-Host "$failures scénario(s) en échec." -ForegroundColor Red
    exit 1
}
Write-Host 'Tous les scénarios sont passés.' -ForegroundColor Green
